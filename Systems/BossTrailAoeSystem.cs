using System;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Boss Path Trail AoE System (Round 124 Direction 1).
    /// Some bosses (e.g. Mountain Wyrm, Frost Colossus) leave a damaging trail along the
    /// path as they advance. When the boss's path progress (EnemyPathNodeIndex / waypoint
    /// count) advances by BossTrailProgressInterval, a trail AoE event is queued at the
    /// boss's current position. At end-of-frame the events are drained serially:
    ///   (a) Damage to the player (matches codebase convention — see SuicideBombSystem
    ///       ApplyDamageToTowers, where "enemy AoE damaging towers" reduces PlayerHealth).
    ///   (b) Slow to all nearby enemies in the trail radius.
    ///
    /// This system holds NO per-frame state of its own beyond the event queue. The actual
    /// 触发检测位于 EnemyMovementSystem 的并行段，每个活跃敌人索引独占一个事件槽。
    /// 并行屏障后由 Resolve 串行按活跃索引顺序提交。
    /// </summary>
    public class BossTrailAoeSystem : global::BattleSystemECS.Content.Contracts.IBossTrailCollector
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        private BossTrailEvent[] _events=Array.Empty<BossTrailEvent>();
        private bool[] _hasEvent=Array.Empty<bool>();
        private int _collectCount;

        // Reused buffer for the "find the current path waypoint count" lookup. We ask the
        // store to compute the total waypoint count once per enemy (EnemyPathId is an int,
        // the count is looked up via a small helper). For the trail trigger we need the
        // boss's *current* path progress, which is EnemyPathNodeIndex / totalWaypoints.
        // The total waypoint count comes from the PathfindingSystem (path[pathId].Waypoints.Count)
        // — to keep this system decoupled from PathfindingSystem internals, we accept the
        // current node index AND the current progress value (already pre-computed) as
        // inputs to the trigger check. The progress is computed by the parallel pass in
        // EnemyMovementSystem using its existing pathfinding reference.

        public BossTrailAoeSystem(ComponentStore store, int playerId)
        {
            this.store = store;
            this.playerId = playerId;
        }

        /// <summary>
        /// 在移动并行段开始前准备 active-index 独占槽。
        /// </summary>
        public void BeginCollect(int count)
        {
            EnsureCapacity(count);
            Array.Clear(_hasEvent,0,count);
            _collectCount=count;
        }

        public void TryQueueTrail(int enemyId,float progress)
        {
            int slot=enemyId;
            EnsureCapacity(slot+1);
            if(_collectCount<slot+1)_collectCount=slot+1;
            TryQueueTrail(slot,enemyId,progress);
        }

        /// <summary>
        /// Called from EnemyMovementSystem's Parallel.For body on each enemy. If the enemy
        /// is a boss with a trail configured, and the current path progress has advanced
        /// by ≥ BossTrailProgressInterval since the last trigger, queue a trail event.
        ///
        /// Arguments:
        ///   enemyId      — the enemy to evaluate
        ///   progress     — current path progress in [0, 1] (nodeIndex / totalWaypoints).
        ///                  Pass -1 if the enemy is not on a path (no trail possible).
        /// </summary>
        public void TryQueueTrail(int activeIndex,int enemyId, float progress)
        {
            if (!store.EnemyIsBossTrail[enemyId]) return;
            if (progress < 0f) return;
            float interval = store.EnemyBossTrailProgressInterval[enemyId];
            if (interval <= 0f) return;
            float radius = store.EnemyBossTrailRadius[enemyId];
            if (radius <= 0f) return;
            float dmg = store.EnemyBossTrailDamage[enemyId];
            if (dmg <= 0f) return;

            float last = store.EnemyBossTrailLastTriggerProgress[enemyId];
            // Trigger when progress has advanced by at least `interval` since last trigger.
            // progress is monotonic along a path, so a single signed difference is enough.
            if (progress - last < interval) return;

            _events[activeIndex]=new BossTrailEvent
            {
                EnemyId = enemyId,
                X = store.PositionX[enemyId],
                Y = store.PositionY[enemyId],
                Radius = radius,
                Damage = dmg,
                Slow = store.EnemyBossTrailSlow[enemyId],
            };
            _hasEvent[activeIndex]=true;

            // Update last-trigger so we don't fire again until progress advances by another
            // interval. We anchor to the threshold we just crossed (not to `progress`),
            // so a single frame that advances by 0.3 with interval 0.1 only fires 3 times
            // (one per crossed threshold), then waits for the next interval.
            // We do this by walking the intervals since last. Cap iterations to prevent
            // runaway loops in degenerate cases (e.g. progress jumps from 0 to 1 instantly).
            float newLast = last + interval;
            // Cap at progress itself (otherwise we'd skip past our current position).
            if (newLast > progress) newLast = progress;
            // Safety: in case of floating-point drift, ensure forward-only progress.
            if (newLast < last) newLast = last;
            store.EnemyBossTrailLastTriggerProgress[enemyId] = newLast;
        }

        /// <summary>
        /// Drain all per-thread event lists serially. Applies (a) damage to player, (b) slow
        /// to all nearby enemies. Called once per frame from EnemyMovementSystem.Update()
        /// 在 Parallel.For 屏障后串行提交，并清理活跃索引标记供下一帧复用。
        /// </summary>
        public void ResolveTrailEvents()
        {
            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            int enemyCount = activeEnemyIds.Count;

            for(int eventIndex=0;eventIndex<_collectCount;eventIndex++)
            {
                    if(!_hasEvent[eventIndex])continue;
                    BossTrailEvent evt=_events[eventIndex];

                    // (a) Damage to player if within radius. No "is player alive" gate —
                    // matches SuicideBombSystem convention (always apply damage if in range;
                    // dead players naturally clamp at 0). The trail damage respects the
                    // same convention as other enemy-AoE systems.
                    float r2 = evt.Radius * evt.Radius;
                    float px = store.PositionX[playerId];
                    float py = store.PositionY[playerId];
                    float dxp = px - evt.X;
                    float dyp = py - evt.Y;
                    float distSqP = dxp * dxp + dyp * dyp;
                    if (distSqP <= r2)
                    {
                        store.ApplyPlayerResourceAuthority(store.GetEntityHandle(evt.EnemyId).Index, playerId, new Core.GAS.AttributeKey(3), -evt.Damage);
                    }

                    // (b) Slow all active enemies within the trail radius (excluding self).
                    if (evt.Slow > 0f && evt.Slow < 1f)
                    {
                        float slowR2 = evt.Radius * evt.Radius;
                        for (int j = 0; j < enemyCount; j++)
                        {
                            int victimId = activeEnemyIds[j];
                            if (victimId == evt.EnemyId) continue;
                            if (!store.EnemyActive[victimId]) continue;
                            float vx = store.PositionX[victimId];
                            float vy = store.PositionY[victimId];
                            float dxv = vx - evt.X;
                            float dyv = vy - evt.Y;
                            float d2 = dxv * dxv + dyv * dyv;
                            if (d2 > slowR2) continue;
                            // Apply 1-frame slow via the existing slow duration timer.
                            // Setting duration to 1f means the slow decays in 1 frame
                            // (the parallel pass decrements EnemySlowDurationLeft by 1f
                            // each frame at the top of EnemyMovementSystem.Update).
                            // 1 frame ≈ the boss just passed the victim's tile.
                            store.ApplyEnemySlow(victimId, evt.Slow, 1);
                        }
                    }
            }
            Array.Clear(_hasEvent,0,_collectCount);
            _collectCount=0;
        }

        private void EnsureCapacity(int count)
        {
            if(_events.Length>=count)return;
            int capacity=Math.Max(count,Math.Max(16,_events.Length*2));
            Array.Resize(ref _events,capacity);
            Array.Resize(ref _hasEvent,capacity);
        }

        /// <summary>
        /// Helper for tests / external systems to compute the current path progress
        /// (EnemyPathNodeIndex / total waypoints) for an enemy on a known path. Returns -1
        /// if the enemy has no path or no waypoints.
        /// </summary>
        public float GetPathProgress(int enemyId, int totalWaypoints)
        {
            if (totalWaypoints <= 0) return -1f;
            int nodeIdx = store.EnemyPathNodeIndex[enemyId];
            if (nodeIdx < 0) return 1f; // past last waypoint = fully progressed
            return (float)nodeIdx / totalWaypoints;
        }

        private readonly struct BossTrailEvent
        {
            public int EnemyId { get; init; }
            public float X { get; init; }
            public float Y { get; init; }
            public float Radius { get; init; }
            public float Damage { get; init; }
            public float Slow { get; init; }
        }
    }
}

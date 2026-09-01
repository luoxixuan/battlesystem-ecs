#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Reflect Tower System — when a tower with TowerReflectRatio > 0 is attacked,
    /// it reflects a fraction of the damage back to the attacking enemy.
    /// 
    /// Design goals:
    /// - Tower takes damage normally (HP reduced), but a fraction is "returned" to the attacker.
    /// - Two-phase: parallel collection in EnemyAISystem attack execution, serial apply here.
    /// - TowerReflectCap prevents runaway from large hits (e.g. Boss charge).
    /// - TowerReflectAuraRadius: when a reflect tower is hit, nearby towers also reflect.
    /// 
    /// Anti-loop: reflected damage goes to enemy HP directly (not through attack flow),
    /// so it does NOT trigger another reflect. The enemy takes the damage and either
    /// lives or dies — no second tower is involved so no infinite loop.
    /// 
    /// Retaliate (co-tenant of the same queue): a tower with TowerRetaliateChance > 0 rolls
    /// a per-hit probability and queues a single independent strike back at the attacker
    /// scaled by TowerBaseDamage × TowerRetaliateDamageMult. Retaliate and Reflect can both
    /// fire on the same hit — they're orthogonal (Reflect scales with the incoming hit,
    /// Retaliate scales with the tower's base damage).
    /// 
    /// Execution: CombatGroup (after Combat/TowerAttack where towers are hit).
    /// </summary>
    public class ReflectTowerSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        // Ping-pong queue: (attackingEnemyId, reflectDamage) — applied serial at frame end.
        // Both Reflect and Retaliate events share this queue — they are unified at the apply
        // stage, since the semantic is "damage to attacking enemy". Differentiating them at
        // the queue stage would require duplicating the apply pass for no behavioral gain.
        private readonly List<ReflectEvent>[] _reflectQueue = { new List<ReflectEvent>(64), new List<ReflectEvent>(64) };
        private int _queueIdx = 0;
        public int RejectedReflectCount { get; private set; }
        public Core.GAS.DamageRejectionReason LastReflectRejection { get; private set; }

        public ReflectTowerSystem(ComponentStore store, int playerId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
        }

        /// <summary>
        /// Queue a reflect event when a tower is hit by an enemy.
        /// 由串行攻击提交路径调用，请求在 graph 边界统一结算。
        /// </summary>
        public void QueueReflect(int towerId, int attackingEnemyId, float damageReceived)
        {
            if (towerId < 0 || attackingEnemyId < 0 || damageReceived <= 0f) return;
            if (!store.TowerActive[towerId]) return;

            float ratio = store.TowerReflectRatio[towerId];
            if (ratio <= 0f) return;

            float reflectDamage = damageReceived * ratio;
            float cap = store.TowerReflectCap[towerId];
            if (cap > 0f)
                reflectDamage = Math.Min(reflectDamage, cap);

            if (reflectDamage <= 0f) return;

            // 收集原始反伤请求，后续由串行边界稳定提交。
            _reflectQueue[_queueIdx].Add(new ReflectEvent
            {
                TowerId = towerId,
                AttackingEnemyId = attackingEnemyId,
                ReflectDamage = reflectDamage,
                ParentSequence = store.AllocateGameplaySequence(attackingEnemyId),
                SourceHandle = store.GetEntityHandle(towerId),
                ProvenanceId = store.CurrentFrame,
                ProvenanceDepth = 1
            });
        }

        /// <summary>
        /// Queue a retaliate event when a tower is hit. Retaliate fires a single independent
        /// strike back at the attacker based on the tower's base damage, independent of the
        /// incoming hit size. Caller is expected to have rolled the chance already (e.g.
        /// `if (rng.NextDouble() < store.TowerRetaliateChance[towerId])`).
        ///
        /// Skipped when:
        /// - any id is invalid
        /// - tower inactive
        /// - damage ≤ 0
        /// - tower has no base damage (e.g. support tower) — nothing to retaliate with
        /// </summary>
        public void QueueRetaliate(int towerId, int attackingEnemyId, float damage)
        {
            if (towerId < 0 || attackingEnemyId < 0 || damage <= 0f) return;
            if (!store.TowerActive[towerId]) return;
            if (!store.EnemyActive[attackingEnemyId]) return;
            float baseDmg = store.TowerBaseDamage[towerId];
            if (baseDmg <= 0f) return;

            _reflectQueue[_queueIdx].Add(new ReflectEvent
            {
                TowerId = towerId,
                AttackingEnemyId = attackingEnemyId,
                ReflectDamage = damage,
                ParentSequence = store.AllocateGameplaySequence(attackingEnemyId),
                SourceHandle = store.GetEntityHandle(towerId)
            });
        }

        /// <summary>
        /// Apply all queued reflect events serially.
        /// Called once per frame after Combat/TowerAttack.
        /// </summary>
        public void ResolveReflect()
        {
            int readIdx = _queueIdx;
            int writeIdx = 1 - _queueIdx;
            _reflectQueue[writeIdx].Clear();

            // 原始反伤请求与光环派生请求共同进入 prepared 缓冲，不能只保留派生项。
            foreach (var evt in _reflectQueue[readIdx])
                _reflectQueue[writeIdx].Add(evt);
            _queueIdx = writeIdx;
            ResolveAuraReflect(readIdx);
            _reflectQueue[readIdx].Clear();
        }

        private void ResolveAuraReflect(int readIdx)
        {
            // For each tower that was hit, check if it has a reflect aura
            // and propagate to nearby towers (they reflect too)
            foreach (var evt in _reflectQueue[readIdx])
            {
                int towerId = evt.TowerId;
                if (!store.TowerActive[towerId]) continue;

                float auraRadius = store.TowerReflectAuraRadius[towerId];
                if (auraRadius <= 0f) continue;

                // Find all towers within aura radius that also have reflect
                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];
                float auraRadiusSq = auraRadius * auraRadius;

                foreach (int nearbyId in store.ActiveTowerIds)
                {
                    if (nearbyId == towerId) continue;
                    if (!store.TowerActive[nearbyId]) continue;
                    if (store.TowerReflectRatio[nearbyId] <= 0f) continue;

                    float dx = store.PositionX[nearbyId] - towerX;
                    float dy = store.PositionY[nearbyId] - towerY;
                    if (dx * dx + dy * dy > auraRadiusSq) continue;

                    // This nearby tower also reflects the same damage
                    float ratio = store.TowerReflectRatio[nearbyId];
                    float reflectDamage = evt.ReflectDamage * ratio;
                    float cap = store.TowerReflectCap[nearbyId];
                    if (cap > 0f)
                        reflectDamage = Math.Min(reflectDamage, cap);

                    if (reflectDamage > 0f)
                        _reflectQueue[_queueIdx].Add(new ReflectEvent
                        {
                            TowerId = nearbyId,
                            AttackingEnemyId = evt.AttackingEnemyId,
                            ReflectDamage = reflectDamage,
                            ParentSequence = evt.ParentSequence,
                            SourceHandle = store.GetEntityHandle(nearbyId),
                            ProvenanceId = evt.ProvenanceId,
                            ProvenanceDepth = evt.ProvenanceDepth + 1
                        });
                }
            }
        }

        /// <summary>
        /// Phase 2 (serial): apply all reflect damage from the queue to the attacking enemies.
        /// Direct HP reduction — no thorns involvement, no infinite loop.
        /// </summary>
        public void ApplyReflectDamage()
        {
            int readIdx = _queueIdx;
            int writeIdx = 1 - _queueIdx;

            foreach (var evt in _reflectQueue[readIdx])
            {
                int enemyId = evt.AttackingEnemyId;
                if (!store.EnemyActive[enemyId]) continue;

                float dmg = evt.ReflectDamage;
                var source = evt.SourceHandle.IsValid ? evt.SourceHandle : store.GetEntityHandle(evt.TowerId);
                var target = store.GetEntityHandle(enemyId);
                if (source.IsValid && target.IsValid)
                {
                    var result = store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, dmg, Components.DamageType.True, Components.ElementType.None, Core.GAS.DamageFlags.Reflect, Core.GAS.DamageAmountStage.Raw, Core.GAS.DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(enemyId), parentSequence: evt.ParentSequence, ownerPlayerId: playerId, provenanceId: evt.ProvenanceId, provenanceDepth: evt.ProvenanceDepth));
                    if (!result.Accepted) { RejectedReflectCount++; LastReflectRejection = result.Reason; }
                }
            }
            _reflectQueue[readIdx].Clear();
            _queueIdx = writeIdx;
        }

        private readonly struct ReflectEvent
        {
            public int TowerId { get; init; }
            public int AttackingEnemyId { get; init; }
            public float ReflectDamage { get; init; }
            public long ParentSequence { get; init; }
            public Core.GAS.EntityHandle SourceHandle { get; init; }
            public long ProvenanceId { get; init; }
            public int ProvenanceDepth { get; init; }
        }
    }
}

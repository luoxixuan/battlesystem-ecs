using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Bosses
{
    /// <summary>
    /// Tests for Round 124 Direction 1: Boss Path Trail AoE.
    /// Verifies:
    ///   1. MonsterConfig.BossTrail* fields default to 0 (zero-overhead opt-out)
    ///   2. ComponentStore SOA fields zero-init on AddEnemy (no trail by default)
    ///   3. ComponentStore SOA fields reset on DestroyEntity (no ID-reuse leak)
    ///   4. TryQueueTrail is no-op when EnemyIsBossTrail is false
    ///   5. TryQueueTrail is no-op when BossTrailProgressInterval is 0
    ///   6. TryQueueTrail queues an event when progress advances past interval
    ///   7. TryQueueTrail does NOT re-fire before progress advances by another interval
    ///   8. TryQueueTrail anchors last-trigger to threshold (caps runaway loops)
    ///   9. ResolveTrailEvents deals damage to player in range
    ///  10. ResolveTrailEvents does NOT damage player out of range
    ///  11. ResolveTrailEvents slows nearby enemies (within radius) for 1 frame
    ///  12. ResolveTrailEvents does NOT slow enemies out of range
    ///  13. ResolveTrailEvents does NOT slow the trail-boss itself
    ///  14. ResolveTrailEvents is no-op when no events were queued
    ///  15. PathfindingSystem.GetPathWaypointCount returns the correct count for known paths
    /// </summary>
    public class BossPathTrailAoeTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        /// <summary>文件内共享构造：已启用 BossTrail 的敌人。</summary>
        private int CreateTrailBoss(
            float x, float y, float radius, float damage, float slow,
            float interval, float lastTrigger = 0f)
        {
            int eid = Enemy(e =>
            {
                e.X = x;
                e.Y = y;
                e.MoveSpeed = 1f;
                e.Damage = 10f;
                e.Name = "TrailBoss";
            });
            Store.EnemyIsBossTrail[eid] = true;
            Store.EnemyBossTrailRadius[eid] = radius;
            Store.EnemyBossTrailDamage[eid] = damage;
            Store.EnemyBossTrailSlow[eid] = slow;
            Store.EnemyBossTrailProgressInterval[eid] = interval;
            Store.EnemyBossTrailLastTriggerProgress[eid] = lastTrigger;
            return eid;
        }

        // ── Config defaults ─────────────────────────────────────────────

        [Fact]
        public void MonsterConfig_BossTrail_DefaultsToZero()
        {
            // All four fields default 0 → no trail (zero-overhead on hot path).
            var mc = new MonsterConfig();
            Assert.Equal(0f, mc.BossTrailProgressInterval);
            Assert.Equal(0f, mc.BossTrailRadius);
            Assert.Equal(0f, mc.BossTrailDamage);
            Assert.Equal(0f, mc.BossTrailSlow);
        }

        // ── SOA field lifecycle ──────────────────────────────────────────

        [Fact]
        public void ComponentStore_BossTrailFields_DefaultToZero_OnAddEnemy()
        {
            // Adding an enemy without opting in to boss trail must leave all 6 fields at 0/false.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Test"; });
            Assert.False(Store.EnemyIsBossTrail[eid]);
            Assert.Equal(0f, Store.EnemyBossTrailRadius[eid]);
            Assert.Equal(0f, Store.EnemyBossTrailDamage[eid]);
            Assert.Equal(0f, Store.EnemyBossTrailSlow[eid]);
            Assert.Equal(0f, Store.EnemyBossTrailProgressInterval[eid]);
            Assert.Equal(0f, Store.EnemyBossTrailLastTriggerProgress[eid]);
        }

        [Fact]
        public void ComponentStore_BossTrailFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a boss-trail enemy and spawning a
            // new one in the recycled slot, the new enemy must NOT inherit the trail config.
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Test"; });
            Store.EnemyIsBossTrail[eid] = true;
            Store.EnemyBossTrailRadius[eid] = 5f;
            Store.EnemyBossTrailDamage[eid] = 20f;
            Store.EnemyBossTrailSlow[eid] = 0.5f;
            Store.EnemyBossTrailProgressInterval[eid] = 0.25f;
            Store.EnemyBossTrailLastTriggerProgress[eid] = 0.75f;
            Store.DestroyEntity(eid);
            // AddEnemy re-uses the same id (entity recycling).
            int eid2 = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test2");
            Assert.Equal(eid, eid2); // same slot
            Assert.False(Store.EnemyIsBossTrail[eid2]);
            Assert.Equal(0f, Store.EnemyBossTrailRadius[eid2]);
            Assert.Equal(0f, Store.EnemyBossTrailDamage[eid2]);
            Assert.Equal(0f, Store.EnemyBossTrailSlow[eid2]);
            Assert.Equal(0f, Store.EnemyBossTrailProgressInterval[eid2]);
            Assert.Equal(0f, Store.EnemyBossTrailLastTriggerProgress[eid2]);
        }

        // ── TryQueueTrail early-out paths ────────────────────────────────

        [Fact]
        public void TryQueueTrail_NoOp_WhenNotBossTrail()
        {
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Test"; });
            var sys = new BossTrailAoeSystem(Store, PlayerId);
            // No trail flag → no-op even at high progress
            sys.TryQueueTrail(eid, 0.9f);
            sys.ResolveTrailEvents();
            // No event means no player damage
            Assert.Equal(0f, Store.PlayerCurrentHealth[PlayerId]);
        }

        [Fact]
        public void TryQueueTrail_NoOp_WhenIntervalZero()
        {
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Test"; });
            Store.EnemyIsBossTrail[eid] = true;
            Store.EnemyBossTrailRadius[eid] = 5f;
            Store.EnemyBossTrailDamage[eid] = 10f;
            Store.EnemyBossTrailProgressInterval[eid] = 0f; // disabled
            var sys = new BossTrailAoeSystem(Store, PlayerId);
            sys.TryQueueTrail(eid, 0.9f);
            sys.ResolveTrailEvents();
            Assert.Equal(0f, Store.PlayerCurrentHealth[PlayerId]);
        }

        [Fact]
        public void TryQueueTrail_NoOp_WhenRadiusZero()
        {
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Test"; });
            Store.EnemyIsBossTrail[eid] = true;
            Store.EnemyBossTrailRadius[eid] = 0f; // no AoE radius
            Store.EnemyBossTrailDamage[eid] = 10f;
            Store.EnemyBossTrailProgressInterval[eid] = 0.1f;
            var sys = new BossTrailAoeSystem(Store, PlayerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(0f, Store.PlayerCurrentHealth[PlayerId]);
        }

        [Fact]
        public void TryQueueTrail_NoOp_WhenDamageZero()
        {
            int eid = Enemy(e => { e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Test"; });
            Store.EnemyIsBossTrail[eid] = true;
            Store.EnemyBossTrailRadius[eid] = 5f;
            Store.EnemyBossTrailDamage[eid] = 0f; // no damage
            Store.EnemyBossTrailProgressInterval[eid] = 0.1f;
            var sys = new BossTrailAoeSystem(Store, PlayerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(0f, Store.PlayerCurrentHealth[PlayerId]);
        }

        // ── Core trigger logic ──────────────────────────────────────────

        [Fact]
        public void TryQueueTrail_Fires_WhenProgressAdvancesPastInterval()
        {
            int playerId = Player(p => { p.Health = 200f; p.X = 0f; p.Y = 0f; });
            int eid = CreateTrailBoss( 0f, 5f, radius: 10f, damage: 15f, slow: 0f, interval: 0.25f);

            var sys = new BossTrailAoeSystem(Store, playerId);
            // First call at progress=0.5 → advances by 0.5 (well past 0.25) → fires
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(200f - 15f, Store.PlayerCurrentHealth[playerId]);
        }

        [Fact]
        public void TryQueueTrail_DoesNotFire_BeforeProgressAdvancesByInterval()
        {
            int playerId = Player(p => { p.Health = 200f; p.X = 0f; p.Y = 0f; });
            int eid = CreateTrailBoss( 0f, 5f, radius: 10f, damage: 15f, slow: 0f, interval: 0.25f, lastTrigger: 0.20f);

            var sys = new BossTrailAoeSystem(Store, playerId);
            // progress=0.30 → only 0.10 advance (< 0.25) → does NOT fire
            sys.TryQueueTrail(eid, 0.30f);
            sys.ResolveTrailEvents();
            // Player takes no damage
            Assert.Equal(200f, Store.PlayerCurrentHealth[playerId]);
        }

        [Fact]
        public void TryQueueTrail_AnchorsLastTriggerToThreshold_NotProgress()
        {
            // Edge case: progress jumps from 0 to 1 in a single frame with interval=0.25.
            // The implementation fires exactly 1 event per TryQueueTrail call (subsequent
            // thresholds are detected on the next frame's call as the boss continues to
            // advance). This test documents that single-fire behavior + verifies the
            // last-trigger anchor advances to the crossed threshold (so the same threshold
            // doesn't re-fire on the next frame).
            int playerId = Player(p => { p.Health = 1000f; p.X = 100f; p.Y = 100f; }); // far from boss — no damage
            int eid = CreateTrailBoss( 0f, 0f, radius: 3f, damage: 10f, slow: 0f, interval: 0.25f);

            var sys = new BossTrailAoeSystem(Store, playerId);
            sys.TryQueueTrail(eid, 1.0f); // jumped from 0 to 1
            sys.ResolveTrailEvents();
            // Single fire: last-trigger anchored to 0.25 (the first threshold crossed).
            Assert.Equal(0.25f, Store.EnemyBossTrailLastTriggerProgress[eid]);
        }

        // ── ResolveTrailEvents damage to player ─────────────────────────

        [Fact]
        public void ResolveTrailEvents_DamagesPlayer_WhenInRange()
        {
            int playerId = Player(p => { p.Health = 200f; p.X = 5f; p.Y = 5f; });
            int eid = CreateTrailBoss( 5f, 5f, radius: 2f, damage: 12f, slow: 0f, interval: 0.1f);

            var sys = new BossTrailAoeSystem(Store, playerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(200f - 12f, Store.PlayerCurrentHealth[playerId]);
        }

        [Fact]
        public void ResolveTrailEvents_DoesNotDamagePlayer_WhenOutOfRange()
        {
            int playerId = Player(p => { p.Health = 200f; p.X = 50f; p.Y = 50f; });
            int eid = CreateTrailBoss( 0f, 0f, radius: 2f, damage: 12f, slow: 0f, interval: 0.1f);

            var sys = new BossTrailAoeSystem(Store, playerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(200f, Store.PlayerCurrentHealth[playerId]);
        }

        // ── ResolveTrailEvents slow to enemies ──────────────────────────

        [Fact]
        public void ResolveTrailEvents_SlowsNearbyEnemies()
        {
            // The boss must have damage > 0 for the trail to fire (BossTrailAoeSystem
            // requires dmg > 0 as a sanity check). We test the slow effect by placing the
            // player far from the boss so the player takes no damage.
            int playerId = Player(p => { p.Health = 1000f; p.X = 100f; p.Y = 100f; }); // far from boss
            int boss = CreateTrailBoss( 0f, 0f, radius: 3f, damage: 5f, slow: 0.5f, interval: 0.1f);
            int victim = Enemy(e => { e.X = 1f; e.Y = 0f; e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Victim"; }); // 1 unit away

            var sys = new BossTrailAoeSystem(Store, playerId);
            sys.TryQueueTrail(boss, 0.5f);
            sys.ResolveTrailEvents();
            // Victim should now have an active slow of factor 0.5 for 1 frame.
            Assert.Equal(0.5f, Store.EnemySlowFactor[victim]);
            Assert.Equal(1f, Store.EnemySlowDurationLeft[victim]);
        }

        [Fact]
        public void ResolveTrailEvents_DoesNotSlowEnemiesOutOfRange()
        {
            int playerId = Player(p => { p.Health = 1000f; p.X = 100f; p.Y = 0f; });
            int boss = CreateTrailBoss( 0f, 0f, radius: 3f, damage: 5f, slow: 0.5f, interval: 0.1f);
            int victim = Enemy(e => { e.X = 20f; e.Y = 20f; e.MoveSpeed = 1f; e.Damage = 10f; e.Name = "Victim"; }); // far away

            var sys = new BossTrailAoeSystem(Store, playerId);
            sys.TryQueueTrail(boss, 0.5f);
            sys.ResolveTrailEvents();
            // Victim is out of range → no slow applied (defaults to 0 = no slow)
            Assert.Equal(0f, Store.EnemySlowFactor[victim]);
        }

        [Fact]
        public void ResolveTrailEvents_DoesNotSlowBossItself()
        {
            // The trail-boss should not slow itself. Slow excludes the boss's own id.
            int playerId = Player(p => { p.Health = 1000f; p.X = 100f; p.Y = 0f; });
            int boss = CreateTrailBoss( 0f, 0f, radius: 3f, damage: 5f, slow: 0.5f, interval: 0.1f);

            var sys = new BossTrailAoeSystem(Store, playerId);
            sys.TryQueueTrail(boss, 0.5f);
            sys.ResolveTrailEvents();
            // Boss itself is not slowed
            Assert.Equal(0f, Store.EnemySlowFactor[boss]);
        }

        [Fact]
        public void ResolveTrailEvents_NoOp_WhenNoEventsQueued()
        {
            // Calling ResolveTrailEvents without any prior TryQueueTrail must be a no-op
            // (the per-thread event dictionary is empty).
            int playerId = Player(p => { p.Health = 200f; p.X = 0f; p.Y = 0f; });
            var sys = new BossTrailAoeSystem(Store, playerId);
            sys.ResolveTrailEvents();
            Assert.Equal(200f, Store.PlayerCurrentHealth[playerId]);
        }

        // ── PathfindingSystem.GetPathWaypointCount ──────────────────────

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsFiveForDefault()
        {
            // The "default" path has 5 waypoints (init in PathfindingSystem.InitDefaultPaths).
            var pfs = new PathfindingSystem(Store);
            Assert.Equal(5, pfs.GetPathWaypointCount(0)); // 0 = "default"
        }

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsFourForForks()
        {
            // fork_left and fork_right have 4 waypoints each.
            var pfs = new PathfindingSystem(Store);
            Assert.Equal(4, pfs.GetPathWaypointCount(1)); // 1 = "fork_left"
            Assert.Equal(4, pfs.GetPathWaypointCount(2)); // 2 = "fork_right"
        }

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsFiveForRing()
        {
            // Ring path has 5 waypoints (closed loop).
            var pfs = new PathfindingSystem(Store);
            Assert.Equal(5, pfs.GetPathWaypointCount(3)); // 3 = "ring"
        }

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsFiveForUnknown()
        {
            // Unknown path ids fall through to "default" mapping → 5, NOT 0.
            // The implementation explicitly maps unknown ids to "default" via GetPathKey's
            // switch default case. This test documents the current behavior so any future
            // change (e.g. returning 0 for unknown ids) is a conscious decision.
            var pfs = new PathfindingSystem(Store);
            int count = pfs.GetPathWaypointCount(99);
            Assert.Equal(5, count);
        }
    }
}
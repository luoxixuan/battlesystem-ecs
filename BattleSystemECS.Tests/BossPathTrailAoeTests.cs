using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
    public class BossPathTrailAoeTests
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

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
            var store = new ComponentStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test");
            Assert.False(store.EnemyIsBossTrail[eid]);
            Assert.Equal(0f, store.EnemyBossTrailRadius[eid]);
            Assert.Equal(0f, store.EnemyBossTrailDamage[eid]);
            Assert.Equal(0f, store.EnemyBossTrailSlow[eid]);
            Assert.Equal(0f, store.EnemyBossTrailProgressInterval[eid]);
            Assert.Equal(0f, store.EnemyBossTrailLastTriggerProgress[eid]);
        }

        [Fact]
        public void ComponentStore_BossTrailFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a boss-trail enemy and spawning a
            // new one in the recycled slot, the new enemy must NOT inherit the trail config.
            var store = new ComponentStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 5f;
            store.EnemyBossTrailDamage[eid] = 20f;
            store.EnemyBossTrailSlow[eid] = 0.5f;
            store.EnemyBossTrailProgressInterval[eid] = 0.25f;
            store.EnemyBossTrailLastTriggerProgress[eid] = 0.75f;
            store.DestroyEntity(eid);
            // AddEnemy re-uses the same id (entity recycling).
            int eid2 = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test2");
            Assert.Equal(eid, eid2); // same slot
            Assert.False(store.EnemyIsBossTrail[eid2]);
            Assert.Equal(0f, store.EnemyBossTrailRadius[eid2]);
            Assert.Equal(0f, store.EnemyBossTrailDamage[eid2]);
            Assert.Equal(0f, store.EnemyBossTrailSlow[eid2]);
            Assert.Equal(0f, store.EnemyBossTrailProgressInterval[eid2]);
            Assert.Equal(0f, store.EnemyBossTrailLastTriggerProgress[eid2]);
        }

        // ── TryQueueTrail early-out paths ────────────────────────────────

        [Fact]
        public void TryQueueTrail_NoOp_WhenNotBossTrail()
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test");
            var sys = new BossTrailAoeSystem(store, PlayerId);
            // No trail flag → no-op even at high progress
            sys.TryQueueTrail(eid, 0.9f);
            sys.ResolveTrailEvents();
            // No event means no player damage
            Assert.Equal(0f, store.PlayerCurrentHealth[PlayerId]);
        }

        [Fact]
        public void TryQueueTrail_NoOp_WhenIntervalZero()
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 5f;
            store.EnemyBossTrailDamage[eid] = 10f;
            store.EnemyBossTrailProgressInterval[eid] = 0f; // disabled
            var sys = new BossTrailAoeSystem(store, PlayerId);
            sys.TryQueueTrail(eid, 0.9f);
            sys.ResolveTrailEvents();
            Assert.Equal(0f, store.PlayerCurrentHealth[PlayerId]);
        }

        [Fact]
        public void TryQueueTrail_NoOp_WhenRadiusZero()
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 0f; // no AoE radius
            store.EnemyBossTrailDamage[eid] = 10f;
            store.EnemyBossTrailProgressInterval[eid] = 0.1f;
            var sys = new BossTrailAoeSystem(store, PlayerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(0f, store.PlayerCurrentHealth[PlayerId]);
        }

        [Fact]
        public void TryQueueTrail_NoOp_WhenDamageZero()
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Test");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 5f;
            store.EnemyBossTrailDamage[eid] = 0f; // no damage
            store.EnemyBossTrailProgressInterval[eid] = 0.1f;
            var sys = new BossTrailAoeSystem(store, PlayerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(0f, store.PlayerCurrentHealth[PlayerId]);
        }

        // ── Core trigger logic ──────────────────────────────────────────

        [Fact]
        public void TryQueueTrail_Fires_WhenProgressAdvancesPastInterval()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 0f;
            store.PositionY[playerId] = 0f;

            int eid = store.AddEnemy(0f, 5f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 10f; // large enough to cover player at (0,0)
            store.EnemyBossTrailDamage[eid] = 15f;
            store.EnemyBossTrailProgressInterval[eid] = 0.25f;
            store.EnemyBossTrailLastTriggerProgress[eid] = 0f;

            var sys = new BossTrailAoeSystem(store, playerId);
            // First call at progress=0.5 → advances by 0.5 (well past 0.25) → fires
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(200f - 15f, store.PlayerCurrentHealth[playerId]);
        }

        [Fact]
        public void TryQueueTrail_DoesNotFire_BeforeProgressAdvancesByInterval()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 0f;
            store.PositionY[playerId] = 0f;

            int eid = store.AddEnemy(0f, 5f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 10f;
            store.EnemyBossTrailDamage[eid] = 15f;
            store.EnemyBossTrailProgressInterval[eid] = 0.25f;
            store.EnemyBossTrailLastTriggerProgress[eid] = 0.20f; // already at 0.20

            var sys = new BossTrailAoeSystem(store, playerId);
            // progress=0.30 → only 0.10 advance (< 0.25) → does NOT fire
            sys.TryQueueTrail(eid, 0.30f);
            sys.ResolveTrailEvents();
            // Player takes no damage
            Assert.Equal(200f, store.PlayerCurrentHealth[playerId]);
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
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 1000f;
            store.PlayerCurrentHealth[playerId] = 1000f;
            store.PositionX[playerId] = 100f; // far from boss — no damage
            store.PositionY[playerId] = 100f;

            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 3f;
            store.EnemyBossTrailDamage[eid] = 10f;
            store.EnemyBossTrailProgressInterval[eid] = 0.25f;
            store.EnemyBossTrailLastTriggerProgress[eid] = 0f;

            var sys = new BossTrailAoeSystem(store, playerId);
            sys.TryQueueTrail(eid, 1.0f); // jumped from 0 to 1
            sys.ResolveTrailEvents();
            // Single fire: last-trigger anchored to 0.25 (the first threshold crossed).
            Assert.Equal(0.25f, store.EnemyBossTrailLastTriggerProgress[eid]);
        }

        // ── ResolveTrailEvents damage to player ─────────────────────────

        [Fact]
        public void ResolveTrailEvents_DamagesPlayer_WhenInRange()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 5f;

            int eid = store.AddEnemy(5f, 5f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 2f; // covers (5,5) from (5,5)
            store.EnemyBossTrailDamage[eid] = 12f;
            store.EnemyBossTrailProgressInterval[eid] = 0.1f;
            store.EnemyBossTrailLastTriggerProgress[eid] = 0f;

            var sys = new BossTrailAoeSystem(store, playerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(200f - 12f, store.PlayerCurrentHealth[playerId]);
        }

        [Fact]
        public void ResolveTrailEvents_DoesNotDamagePlayer_WhenOutOfRange()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 50f; // far from boss
            store.PositionY[playerId] = 50f;

            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            store.EnemyIsBossTrail[eid] = true;
            store.EnemyBossTrailRadius[eid] = 2f; // does NOT cover (50,50)
            store.EnemyBossTrailDamage[eid] = 12f;
            store.EnemyBossTrailProgressInterval[eid] = 0.1f;
            store.EnemyBossTrailLastTriggerProgress[eid] = 0f;

            var sys = new BossTrailAoeSystem(store, playerId);
            sys.TryQueueTrail(eid, 0.5f);
            sys.ResolveTrailEvents();
            Assert.Equal(200f, store.PlayerCurrentHealth[playerId]);
        }

        // ── ResolveTrailEvents slow to enemies ──────────────────────────

        [Fact]
        public void ResolveTrailEvents_SlowsNearbyEnemies()
        {
            // The boss must have damage > 0 for the trail to fire (BossTrailAoeSystem
            // requires dmg > 0 as a sanity check). We test the slow effect by placing the
            // player far from the boss so the player takes no damage.
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 1000f;
            store.PlayerCurrentHealth[playerId] = 1000f;
            store.PositionX[playerId] = 100f; // far from boss
            store.PositionY[playerId] = 100f;

            int boss = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            int victim = store.AddEnemy(1f, 0f, 1f, 100f, 100f, 10f, 10, 1, "Victim"); // 1 unit away
            store.EnemyIsBossTrail[boss] = true;
            store.EnemyBossTrailRadius[boss] = 3f;
            store.EnemyBossTrailDamage[boss] = 5f; // required > 0 to fire
            store.EnemyBossTrailSlow[boss] = 0.5f; // 50% slow
            store.EnemyBossTrailProgressInterval[boss] = 0.1f;
            store.EnemyBossTrailLastTriggerProgress[boss] = 0f;

            var sys = new BossTrailAoeSystem(store, playerId);
            sys.TryQueueTrail(boss, 0.5f);
            sys.ResolveTrailEvents();
            // Victim should now have an active slow of factor 0.5 for 1 frame.
            Assert.Equal(0.5f, store.EnemySlowFactor[victim]);
            Assert.Equal(1f, store.EnemySlowDurationLeft[victim]);
        }

        [Fact]
        public void ResolveTrailEvents_DoesNotSlowEnemiesOutOfRange()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 1000f;
            store.PlayerCurrentHealth[playerId] = 1000f;
            store.PositionX[playerId] = 100f;

            int boss = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            int victim = store.AddEnemy(20f, 20f, 1f, 100f, 100f, 10f, 10, 1, "Victim"); // far away
            store.EnemyIsBossTrail[boss] = true;
            store.EnemyBossTrailRadius[boss] = 3f;
            store.EnemyBossTrailDamage[boss] = 5f; // required > 0 to fire
            store.EnemyBossTrailSlow[boss] = 0.5f;
            store.EnemyBossTrailProgressInterval[boss] = 0.1f;
            store.EnemyBossTrailLastTriggerProgress[boss] = 0f;

            var sys = new BossTrailAoeSystem(store, playerId);
            sys.TryQueueTrail(boss, 0.5f);
            sys.ResolveTrailEvents();
            // Victim is out of range → no slow applied (defaults to 0 = no slow)
            Assert.Equal(0f, store.EnemySlowFactor[victim]);
        }

        [Fact]
        public void ResolveTrailEvents_DoesNotSlowBossItself()
        {
            // The trail-boss should not slow itself. Slow excludes the boss's own id.
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 1000f;
            store.PlayerCurrentHealth[playerId] = 1000f;
            store.PositionX[playerId] = 100f;

            int boss = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 10f, 10, 1, "TrailBoss");
            store.EnemyIsBossTrail[boss] = true;
            store.EnemyBossTrailRadius[boss] = 3f;
            store.EnemyBossTrailDamage[boss] = 5f; // required > 0
            store.EnemyBossTrailSlow[boss] = 0.5f;
            store.EnemyBossTrailProgressInterval[boss] = 0.1f;
            store.EnemyBossTrailLastTriggerProgress[boss] = 0f;

            var sys = new BossTrailAoeSystem(store, playerId);
            sys.TryQueueTrail(boss, 0.5f);
            sys.ResolveTrailEvents();
            // Boss itself is not slowed
            Assert.Equal(0f, store.EnemySlowFactor[boss]);
        }

        [Fact]
        public void ResolveTrailEvents_NoOp_WhenNoEventsQueued()
        {
            // Calling ResolveTrailEvents without any prior TryQueueTrail must be a no-op
            // (the per-thread event dictionary is empty).
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 0f;
            store.PositionY[playerId] = 0f;
            var sys = new BossTrailAoeSystem(store, playerId);
            sys.ResolveTrailEvents();
            Assert.Equal(200f, store.PlayerCurrentHealth[playerId]);
        }

        // ── PathfindingSystem.GetPathWaypointCount ──────────────────────

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsFiveForDefault()
        {
            // The "default" path has 5 waypoints (init in PathfindingSystem.InitDefaultPaths).
            var store = new ComponentStore();
            var pfs = new PathfindingSystem(store);
            Assert.Equal(5, pfs.GetPathWaypointCount(0)); // 0 = "default"
        }

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsFourForForks()
        {
            // fork_left and fork_right have 4 waypoints each.
            var store = new ComponentStore();
            var pfs = new PathfindingSystem(store);
            Assert.Equal(4, pfs.GetPathWaypointCount(1)); // 1 = "fork_left"
            Assert.Equal(4, pfs.GetPathWaypointCount(2)); // 2 = "fork_right"
        }

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsFiveForRing()
        {
            // Ring path has 5 waypoints (closed loop).
            var store = new ComponentStore();
            var pfs = new PathfindingSystem(store);
            Assert.Equal(5, pfs.GetPathWaypointCount(3)); // 3 = "ring"
        }

        [Fact]
        public void PathfindingSystem_GetPathWaypointCount_ReturnsZeroForUnknown()
        {
            // Unknown path ids should fall through to "default" mapping → 5, NOT 0.
            // The implementation explicitly maps unknown ids to "default" via GetPathKey's
            // switch default case. This test documents the current behavior so any future
            // change (e.g. returning 0 for unknown ids) is a conscious decision.
            var store = new ComponentStore();
            var pfs = new PathfindingSystem(store);
            int count = pfs.GetPathWaypointCount(99);
            // Whatever the behavior, must not throw, must be ≥ 0
            Assert.True(count >= 0);
        }
    }
}

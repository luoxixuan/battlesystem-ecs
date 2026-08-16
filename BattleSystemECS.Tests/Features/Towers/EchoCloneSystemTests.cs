using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 201 Direction 8: Echo Clone / Spectral Tower System.
    /// Verifies that:
    ///   1. Default state: all Echo fields are 0/-1/1f/0.6f (zero-overhead, opt-out sentinel)
    ///   2. ForceSpawnEcho on a parent with SpawnsEcho>0 spawns a clone (TowerIsEcho=true, parent id, damage)
    ///   3. ForceSpawnEcho on a parent with SpawnsEcho=0 returns -1 (opt-out sentinel respected)
    ///   4. ForceSpawnEcho on an echo tower (parent is itself an echo) returns -1 (no recursion)
    ///   5. ForceSpawnEcho on an inactive parent returns -1
    ///   6. ForceSpawnEcho on a parent with EchoDuration=0 returns -1
    ///   7. ForceSpawnEcho resets the parent's spawn cooldown to TowerEchoMaxCooldown
    ///   8. IsEcho: true for a fresh live echo, false after expiry / destroy / non-echo tower
    ///   9. DestroyEcho: removes the live echo and returns true; returns false for non-echo
    ///  10. Update: with no opt-in parent, sentinel stays false (fast path) and no echo spawns
    ///  11. Update: with an opt-in parent, throttled re-scan arms the sentinel so dice rolls run
    ///  12. Update: echo expires after EchoDuration elapses (TowerActive → false)
    /// </summary>
    public class EchoCloneSystemTests
    {
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private static (EchoCloneSystem system, ComponentStore store) MakeSystem()
        {
            var store = new ComponentStore();
            store.AddPlayer(0, attackRange: 1f, attackSpeed: 1f, attackDamage: 1f, currentLevel: 1);
            var system = new EchoCloneSystem(store);
            return (system, store);
        }

        /// <summary>Spawn a basic tower at (50, 50) with the given echo settings.</summary>
        private static int MakeEchoTower(
            ComponentStore store,
            float x = 50f,
            float y = 50f,
            float damage = 10f,
            float chance = 1f,           // 1.0 = always spawn (deterministic test)
            float duration = 5f,
            float maxCooldown = 5f)
        {
            int tid = 1;
            store.AddTower(tid, Components.TowerType.Basic, damage, 5, 1f, 1, 50f);
            store.PositionX[tid] = x;
            store.PositionY[tid] = y;
            store.TowerCanSpawnEcho[tid] = chance > 0f && duration > 0f;
            store.TowerEchoChance[tid] = chance;
            store.TowerEchoDuration[tid] = duration;
            store.TowerEchoDamageMult[tid] = 0.6f;
            store.TowerEchoSpawnCooldown[tid] = 0f;
            store.TowerEchoMaxCooldown[tid] = maxCooldown;
            return tid;
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllEchoFields_InertSentinels()
        {
            var store = new ComponentStore();
            // Fresh ComponentStore: C# array defaults are 0/false. The reset
            // hooks in AddTower / DestroyEntity set the -1 sentinels and the
            // 1f default for TowerEchoDamageMult.
            Assert.False(store.TowerIsEcho[0]);
            Assert.Equal(0, store.TowerEchoParentId[0]); // raw default, before reset
            Assert.Equal(0f, store.TowerEchoDamageMult[0]); // raw default, before reset
            Assert.Equal(0, store.TowerEchoExpireTurn[0]); // raw default, before reset
            Assert.False(store.TowerCanSpawnEcho[0]);
            Assert.Equal(0f, store.TowerEchoChance[0]);
            Assert.Equal(0f, store.TowerEchoDuration[0]);
            Assert.Equal(0f, store.TowerEchoSpawnCooldown[0]);
            Assert.Equal(0f, store.TowerEchoMaxCooldown[0]);

            // After AddTower → DestroyEntity the reset hook should populate
            // the -1 sentinels for parent id + expire turn and the 1f default
            // for the damage multiplier.
            int tid = 1;
            store.AddTower(tid, Components.TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.DestroyEntity(tid);
            Assert.False(store.TowerIsEcho[tid]);
            Assert.Equal(-1, store.TowerEchoParentId[tid]);
            Assert.Equal(1f, store.TowerEchoDamageMult[tid]);
            Assert.Equal(-1, store.TowerEchoExpireTurn[tid]);
        }

        // ── 2. ForceSpawnEcho happy path ────────────────────────────────
        [Fact]
        public void ForceSpawnEcho_OnOptInParent_CreatesEcho()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 5f, maxCooldown: 5f);

            int echoId = sys.ForceSpawnEcho(parent);

            Assert.True(echoId >= 0);
            Assert.NotEqual(parent, echoId);
            Assert.True(store.TowerActive[echoId]);
            Assert.True(store.TowerIsEcho[echoId]);
            Assert.Equal(parent, store.TowerEchoParentId[echoId]);
            // Damage: 10 * 0.6 = 6
            Assert.Equal(6f, store.TowerAttackDamage[echoId]);
            // Position inherited
            Assert.Equal(50f, store.PositionX[echoId]);
            Assert.Equal(50f, store.PositionY[echoId]);
            // Echo can never spawn another echo (no recursion)
            Assert.False(store.TowerCanSpawnEcho[echoId]);
            // ExpireTurn stores the duration in seconds (int ceiling of 5f = 5)
            Assert.Equal(5, store.TowerEchoExpireTurn[echoId]);
            Assert.Equal(1, sys.TotalEchoesSpawned);
        }

        // ── 3. ForceSpawnEcho on opt-out parent returns -1 ─────────────
        [Fact]
        public void ForceSpawnEcho_OnNonOptInParent_ReturnsMinusOne()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 0f, duration: 5f); // opt-out
            // chance=0 → TowerCanSpawnEcho stays false per MakeEchoTower logic

            int echoId = sys.ForceSpawnEcho(parent);

            Assert.Equal(-1, echoId);
            Assert.Equal(0, sys.TotalEchoesSpawned);
        }

        // ── 4. ForceSpawnEcho on echo tower returns -1 (no recursion) ──
        [Fact]
        public void ForceSpawnEcho_OnEchoParent_ReturnsMinusOne()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 5f);
            int echoId = sys.ForceSpawnEcho(parent);
            Assert.True(echoId >= 0);
            // Try to spawn a phantom-of-phantom. TowerIsEcho[echo] is true, so it must refuse.
            int echo2 = sys.ForceSpawnEcho(echoId);
            Assert.Equal(-1, echo2);
        }

        // ── 5. ForceSpawnEcho on inactive parent returns -1 ───────────
        [Fact]
        public void ForceSpawnEcho_OnInactiveParent_ReturnsMinusOne()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store);
            store.DestroyEntity(parent);

            int echoId = sys.ForceSpawnEcho(parent);
            Assert.Equal(-1, echoId);
        }

        // ── 6. ForceSpawnEcho on parent with EchoDuration=0 returns -1 ─
        [Fact]
        public void ForceSpawnEcho_ZeroDuration_ReturnsMinusOne()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 0f);

            int echoId = sys.ForceSpawnEcho(parent);
            Assert.Equal(-1, echoId);
        }

        // ── 7. ForceSpawnEcho resets parent cooldown ───────────────────
        [Fact]
        public void ForceSpawnEcho_ResetsParentCooldownToMax()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 5f, maxCooldown: 7.5f);
            // Pre-set cooldown to 0 to mimic "ready to spawn"
            store.TowerEchoSpawnCooldown[parent] = 0f;

            int echoId = sys.ForceSpawnEcho(parent);
            Assert.True(echoId >= 0);
            // After successful spawn, parent cooldown is reset to max (7.5s)
            Assert.Equal(7.5f, store.TowerEchoSpawnCooldown[parent]);
        }

        // ── 8. IsEcho helper ──────────────────────────────────────────
        [Fact]
        public void IsEcho_LiveEcho_True_DestroyedEcho_False()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 5f);
            int echoId = sys.ForceSpawnEcho(parent);

            Assert.True(sys.IsEcho(echoId));
            Assert.False(sys.IsEcho(parent));

            // Manually destroy the echo
            sys.DestroyEcho(echoId);
            Assert.False(sys.IsEcho(echoId));
        }

        // ── 9. DestroyEcho removes echo ───────────────────────────────
        [Fact]
        public void DestroyEcho_OnLiveEcho_ReturnsTrue_AndDeactivates()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 5f);
            int echoId = sys.ForceSpawnEcho(parent);

            Assert.True(sys.DestroyEcho(echoId));
            Assert.False(store.TowerActive[echoId]);
            // Re-destroy is a no-op (returns false)
            Assert.False(sys.DestroyEcho(echoId));
        }

        // ── 10. Update fast-path with no opt-in parent ────────────────
        [Fact]
        public void Update_NoOptInParent_FastPath_NoSpawns()
        {
            var (sys, store) = MakeSystem();
            // No towers placed — sentinel stays false, Update is O(1)
            sys.Update(DeltaTime);
            Assert.False(sys.HasAnyEchoCapableParent);
            Assert.False(sys.HasAnyLiveEcho);
            Assert.Equal(0, sys.TotalEchoesSpawned);
        }

        // ── 11. Throttled re-scan arms the sentinel for opt-in parent ──
        [Fact]
        public void Update_OptInParent_ThrottledRescanArmsSentinel()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 5f, maxCooldown: 0.1f);

            // Initially sentinel is false (no echo spawned yet)
            Assert.False(sys.HasAnyEchoCapableParent);

            // Pump 65 frames: the throttled re-scan (Phase 3) arms the
            // sentinel on frame 60. After that, Phase 2 will spawn echoes
            // (chance=1, cooldown=0.1s = ~6 spawns per second).
            float origTime = Time.TotalTime;
            try
            {
                for (int i = 0; i < 65; i++)
                {
                    Time.TotalTime += DeltaTime;
                    sys.Update(DeltaTime);
                }
            }
            finally
            {
                Time.TotalTime = origTime;
            }

            Assert.True(sys.HasAnyEchoCapableParent,
                "Throttled re-scan should have armed the sentinel within 60 frames");
            Assert.True(sys.TotalEchoesSpawned >= 1,
                $"Expected at least 1 echo spawn after re-scan, got {sys.TotalEchoesSpawned}");
        }

        // ── 12. Update expires echo after duration ────────────────────
        [Fact]
        public void Update_ExpiresEcho_AfterDurationElapses()
        {
            var (sys, store) = MakeSystem();
            int parent = MakeEchoTower(store, chance: 1f, duration: 5f, maxCooldown: 60f);
            // Skip the throttled re-scan: force-spawn directly
            int echoId = sys.ForceSpawnEcho(parent);
            Assert.True(echoId >= 0);

            // Advance Time.TotalTime by 10 seconds (past the 5s duration)
            float origTime = Time.TotalTime;
            try
            {
                Time.TotalTime = origTime + 10f;
                sys.Update(DeltaTime); // Phase 1: expire the echo
                Assert.False(store.TowerActive[echoId]);
                Assert.Equal(1, sys.TotalEchoesExpired);
            }
            finally
            {
                Time.TotalTime = origTime;
            }
        }
    }
}
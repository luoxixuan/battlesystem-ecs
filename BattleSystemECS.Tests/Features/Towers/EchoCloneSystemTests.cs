using System;
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
    public class EchoCloneSystemTests : BattleTestBase
    {
        private const float DeltaTime = 1f / 60f;

        // ── Test helpers ────────────────────────────────────────────────

        private EchoCloneSystem MakeSystem()
        {
            Player();
            return new EchoCloneSystem(Store);
        }

        /// <summary>Spawn a basic tower at (x, y) with the given echo settings.</summary>
        private int MakeEchoTower(
            float x = 50f,
            float y = 50f,
            float damage = 10f,
            float chance = 1f,           // 1.0 = always spawn (deterministic test)
            float duration = 5f,
            float maxCooldown = 5f,
            float damageMult = 0.6f)
        {
            int tid = RawTower(0, 0, Components.TowerType.Basic, damage, 5, 1f, 1, 50f);
            Store.PositionX[tid] = x;
            Store.PositionY[tid] = y;
            Store.TowerCanSpawnEcho[tid] = chance > 0f && duration > 0f;
            Store.TowerEchoChance[tid] = chance;
            Store.TowerEchoDuration[tid] = duration;
            Store.TowerEchoDamageMult[tid] = damageMult;
            Store.TowerEchoSpawnCooldown[tid] = 0f;
            Store.TowerEchoMaxCooldown[tid] = maxCooldown;
            return tid;
        }

        // ── 1. Default state ────────────────────────────────────────────
        [Fact]
        public void DefaultState_AllEchoFields_InertSentinels()
        {
            // Fresh ComponentStore: C# array defaults are 0/false. The reset
            // hooks in AddTower / DestroyEntity set the -1 sentinels and the
            // 1f default for TowerEchoDamageMult.
            Assert.False(Store.TowerIsEcho[0]);
            Assert.Equal(0, Store.TowerEchoParentId[0]); // raw default, before reset
            Assert.Equal(0f, Store.TowerEchoDamageMult[0], 3); // raw default, before reset
            Assert.Equal(0, Store.TowerEchoExpireTurn[0]); // raw default, before reset
            Assert.False(Store.TowerCanSpawnEcho[0]);
            Assert.Equal(0f, Store.TowerEchoChance[0], 3);
            Assert.Equal(0f, Store.TowerEchoDuration[0], 3);
            Assert.Equal(0f, Store.TowerEchoSpawnCooldown[0], 3);
            Assert.Equal(0f, Store.TowerEchoMaxCooldown[0], 3);

            // After AddTower → DestroyEntity the reset hook should populate
            // the -1 sentinels for parent id + expire turn and the 1f default
            // for the damage multiplier.
            int tid = 1;
            Store.AddTower(tid, Components.TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Store.DestroyEntity(tid);
            Assert.False(Store.TowerIsEcho[tid]);
            Assert.Equal(-1, Store.TowerEchoParentId[tid]);
            Assert.Equal(1f, Store.TowerEchoDamageMult[tid], 3);
            Assert.Equal(-1, Store.TowerEchoExpireTurn[tid]);
        }

        // ── 2. ForceSpawnEcho happy path ────────────────────────────────
        [Fact]
        public void ForceSpawnEcho_OnOptInParent_CreatesEcho()
        {
            const float parentDamage = 10f;
            const float damageMult = 0.6f;
            const float duration = 5f;
            const float parentX = 50f;
            const float parentY = 50f;
            var sys = MakeSystem();
            int parent = MakeEchoTower(x: parentX, y: parentY, damage: parentDamage,
                chance: 1f, duration: duration, maxCooldown: 5f, damageMult: damageMult);

            int echoId = sys.ForceSpawnEcho(parent);

            Assert.True(echoId >= 0);
            Assert.NotEqual(parent, echoId);
            Assert.True(Store.TowerActive[echoId]);
            Assert.True(Store.TowerIsEcho[echoId]);
            Assert.Equal(parent, Store.TowerEchoParentId[echoId]);
            // Damage: parentDamage * 显式注入的伤害倍率（不复制生产公式，仅推导期望）。
            Assert.Equal(parentDamage * damageMult, Store.TowerAttackDamage[echoId], 3);
            // Position inherited
            Assert.Equal(parentX, Store.PositionX[echoId], 3);
            Assert.Equal(parentY, Store.PositionY[echoId], 3);
            // Echo can never spawn another echo (no recursion)
            Assert.False(Store.TowerCanSpawnEcho[echoId]);
            // ExpireTurn stores the duration in seconds (int ceiling of duration)
            Assert.Equal((int)Math.Ceiling(duration), Store.TowerEchoExpireTurn[echoId]);
            Assert.Equal(1, sys.TotalEchoesSpawned);
        }

        // ── 3-6. ForceSpawnEcho 对非法父塔统一返回 -1（场景合并）───────
        [Theory(DisplayName = "ForceSpawnEcho 对 opt-out/echo 本体/非活跃/零持续时间的父塔返回 -1")]
        // scenario: 0=opt-out(chance=0) / 1=echo 本体 / 2=非活跃 / 3=零持续时间
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ForceSpawnEcho_OnInvalidParentKind_ReturnsMinusOne(int scenario)
        {
            var sys = MakeSystem();
            int parent = scenario switch
            {
                0 => MakeEchoTower(chance: 0f, duration: 5f),  // opt-out：chance=0 → CanSpawnEcho=false
                2 => MakeEchoTower(),                            // 先正常放置，随后销毁
                3 => MakeEchoTower(chance: 1f, duration: 0f),  // 零持续时间
                _ => MakeEchoTower(chance: 1f, duration: 5f)   // 场景 1：先生成 echo 本体
            };
            int setupEcho = -1;
            if (scenario == 1)
            {
                setupEcho = sys.ForceSpawnEcho(parent);
                Assert.True(setupEcho >= 0);
            }
            else if (scenario == 2)
            {
                Store.DestroyEntity(parent);
            }

            int echoId = scenario == 1 ? sys.ForceSpawnEcho(setupEcho) : sys.ForceSpawnEcho(parent);

            Assert.Equal(-1, echoId);
            Assert.Equal(scenario == 1 ? 1 : 0, sys.TotalEchoesSpawned);
        }

        // ── 7. ForceSpawnEcho resets parent cooldown ───────────────────
        [Fact]
        public void ForceSpawnEcho_ResetsParentCooldownToMax()
        {
            var sys = MakeSystem();
            int parent = MakeEchoTower(chance: 1f, duration: 5f, maxCooldown: 7.5f);
            // Pre-set cooldown to 0 to mimic "ready to spawn"
            Store.TowerEchoSpawnCooldown[parent] = 0f;

            int echoId = sys.ForceSpawnEcho(parent);
            Assert.True(echoId >= 0);
            // After successful spawn, parent cooldown is reset to max (读取注入的 max cooldown 推导)。
            Assert.Equal(Store.TowerEchoMaxCooldown[parent], Store.TowerEchoSpawnCooldown[parent], 3);
        }

        // ── 8. IsEcho helper ──────────────────────────────────────────
        [Fact]
        public void IsEcho_LiveEcho_True_DestroyedEcho_False()
        {
            var sys = MakeSystem();
            int parent = MakeEchoTower(chance: 1f, duration: 5f);
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
            var sys = MakeSystem();
            int parent = MakeEchoTower(chance: 1f, duration: 5f);
            int echoId = sys.ForceSpawnEcho(parent);

            Assert.True(sys.DestroyEcho(echoId));
            Assert.False(Store.TowerActive[echoId]);
            // Re-destroy is a no-op (returns false)
            Assert.False(sys.DestroyEcho(echoId));
        }

        // ── 10. Update fast-path with no opt-in parent ────────────────
        [Fact]
        public void Update_NoOptInParent_FastPath_NoSpawns()
        {
            var sys = MakeSystem();
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
            var sys = MakeSystem();
            int parent = MakeEchoTower(chance: 1f, duration: 5f, maxCooldown: 0.1f);

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
            var sys = MakeSystem();
            int parent = MakeEchoTower(chance: 1f, duration: 5f, maxCooldown: 60f);
            // Skip the throttled re-scan: force-spawn directly
            int echoId = sys.ForceSpawnEcho(parent);
            Assert.True(echoId >= 0);

            // Advance Time.TotalTime by 10 seconds (past the 5s duration)
            float origTime = Time.TotalTime;
            try
            {
                Time.TotalTime = origTime + 10f;
                sys.Update(DeltaTime); // Phase 1: expire the echo
                Assert.False(Store.TowerActive[echoId]);
                Assert.Equal(1, sys.TotalEchoesExpired);
            }
            finally
            {
                Time.TotalTime = origTime;
            }
        }
    }
}

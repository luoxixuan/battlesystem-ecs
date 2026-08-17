using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Features.Skills
{
    /// <summary>
    /// Tests for Round 137 Direction 6: Themed Boss Summon.
    /// Verifies that:
    ///   - BossPhaseDef.BossElementAffinity defaults to "" (no affinity, opt-out)
    ///   - MonsterConfig.ElementAffinity defaults to "" (no affinity, opt-out)
    ///   - EnemyPhaseElementAffinityFlat defaults to 0 (None) for all phases
    ///   - SetEnemyPhaseElementAffinity / GetEnemyPhaseElementAffinity boundaries
    ///   - DestroyEntity resets per-phase element affinity (no ID-reuse leakage)
    ///   - 4-arg SpawnMinionNearPosition backward-compat (no bonus)
    ///   - 6-arg SpawnMinionNearPosition with bossElementAffinity=0 = no bonus
    ///   - 6-arg SpawnMinionNearPosition with matching element = +10% HP bonus
    ///   - 6-arg SpawnMinionNearPosition with mismatched element = no bonus
    ///   - 6-arg SpawnMinionNearPosition with case-insensitive match = +10% HP bonus
    ///   - 6-arg SpawnMinionNearPosition with empty minion affinity = no bonus
    /// </summary>
    public class ThemedSummonTests : BattleTestBase
    {
        // ── Config field defaults ─────────────────────────────────────────

        [Fact]
        public void BossPhaseDef_DefaultBossElementAffinity_IsEmpty()
        {
            // Default must opt-out (empty string) so existing boss JSON without the field
            // behaves identically to pre-Dir-6 code.
            var def = new BossPhaseDef();
            Assert.Equal(string.Empty, def.BossElementAffinity);
        }

        [Fact]
        public void MonsterConfig_DefaultElementAffinity_IsEmpty()
        {
            // Default must opt-out (empty string) so existing monster JSON without the field
            // behaves identically to pre-Dir-6 code.
            var cfg = new MonsterConfig { Type = "Test" };
            Assert.Equal(string.Empty, cfg.ElementAffinity);
        }

        // ── SOA defaults & accessors ──────────────────────────────────────

        [Fact]
        public void DefaultState_AllElementAffinitySlotsAreZero()
        {
            int eid = 0;
            // All 4 phase slots should default to 0 (None = no themed bonus).
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(eid, ph));
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_NegativeValue_NormalizesToZero()
        {
            int eid = 0;
            // Negative value (defensive) is clamped to 0 (None) to keep "no bonus" a clean
            // sentinel for the spawn path.
            Store.SetEnemyPhaseElementAffinity(eid, 0, -5);
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_InvalidEnemyId_IsNoOp()
        {
            int eid = 0;
            // 先写入合法槽位的已知值作为对照，再用无效 id 调用并断言合法槽位不变。
            Store.SetEnemyPhaseElementAffinity(eid, 0, (int)ElementType.Fire);
            Assert.Equal((int)ElementType.Fire, Store.GetEnemyPhaseElementAffinity(eid, 0));

            Store.SetEnemyPhaseElementAffinity(-1, 0, (int)ElementType.Ice);
            Store.SetEnemyPhaseElementAffinity(ComponentStore.MAX_ENTITIES, 0, (int)ElementType.Ice);

            Assert.Equal((int)ElementType.Fire, Store.GetEnemyPhaseElementAffinity(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_InvalidPhase_IsNoOp()
        {
            int eid = 0;
            // Out-of-range phase is silently ignored.
            Store.SetEnemyPhaseElementAffinity(eid, -1, (int)ElementType.Ice);
            Store.SetEnemyPhaseElementAffinity(eid, ComponentStore.BOSS_PHASE_MAX, (int)ElementType.Ice);
            // Slot 0 should still be at its default.
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_Valid_StoresValue()
        {
            int eid = 0;
            Store.SetEnemyPhaseElementAffinity(eid, 1, (int)ElementType.Lightning);
            Assert.Equal((int)ElementType.Lightning, Store.GetEnemyPhaseElementAffinity(eid, 1));
            // Adjacent slots remain at 0.
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(eid, 0));
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(eid, 2));
        }

        [Fact]
        public void GetEnemyPhaseElementAffinity_OutOfBounds_ReturnsZero()
        {
            // Invalid enemyId / phase returns 0 (None) — safe default for the spawn path.
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(-1, 0));
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(ComponentStore.MAX_ENTITIES, 0));
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(0, -1));
            Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(0, ComponentStore.BOSS_PHASE_MAX));
        }

        // ── DestroyEntity ID-reuse safety ────────────────────────────────

        [Fact]
        public void DestroyEntity_ResetsElementAffinitySlots()
        {
            int eid = Store.CreateEntity();
            Store.AddActiveEnemyId(eid);
            Store.EnemyActive[eid] = true;
            // Fill all 4 phase slots with distinct elements.
            Store.SetEnemyPhaseElementAffinity(eid, 0, (int)ElementType.Fire);
            Store.SetEnemyPhaseElementAffinity(eid, 1, (int)ElementType.Ice);
            Store.SetEnemyPhaseElementAffinity(eid, 2, (int)ElementType.Lightning);
            Store.SetEnemyPhaseElementAffinity(eid, 3, (int)ElementType.Poison);
            // Sanity: values are set.
            Assert.Equal((int)ElementType.Fire, Store.GetEnemyPhaseElementAffinity(eid, 0));

            Store.DestroyEntity(eid);
            // After destroy, all 4 phase slots must be reset to 0 (None) to prevent
            // a recycled ID from carrying the previous boss's element affinity into
            // a freshly-spawned unit.
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Equal(0, Store.GetEnemyPhaseElementAffinity(eid, ph));
        }

        // ── SpawnMinionNearPosition: themed HP bonus ─────────────────────

        /// <summary>共享 setup：注入单一 minion 配置并构造 WaveSpawningSystem。</summary>
        private (WaveSpawningSystem Wave, int TypeId) CreateSpawnEnv(
            string minionAffinity, float baseHealth)
        {
            Config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "ThemedMinion",
                Health = baseHealth,
                MaxHealth = baseHealth,
                Damage = 5,
                MoveSpeed = 1f,
                ElementAffinity = minionAffinity,
            });
            int typeId = Config.MonsterTypes.Count - 1;
            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            return (wave, typeId);
        }

        /// <summary>
        /// 六个同构用例合并：legacy 4-arg、匹配、失配、空亲和、大小写不敏感 + AllFourElements。
        /// 期望血量由显式注入的 baseHealth 推导（bonus 时 +10%）。
        /// </summary>
        public static IEnumerable<object[]> SpawnBonusCases()
        {
            yield return new object[] { "LegacyFourArg_NoBonus", true, 0, "Fire", 100f, 100f };
            yield return new object[] { "MatchingFire_AppliesBonus", false, (int)ElementType.Fire, "Fire", 100f, 110f };
            yield return new object[] { "MismatchedElement_NoBonus", false, (int)ElementType.Ice, "Fire", 100f, 100f };
            yield return new object[] { "EmptyMinionAffinity_NoBonus", false, (int)ElementType.Fire, "", 100f, 100f };
            yield return new object[] { "CaseInsensitiveMatch_AppliesBonus", false, (int)ElementType.Fire, "fire", 50f, 55f };
            foreach (ElementType elem in new[] { ElementType.Fire, ElementType.Ice, ElementType.Lightning, ElementType.Poison })
            {
                yield return new object[] { "AllFourElements_" + elem + "_AppliesBonus", false, (int)elem, elem.ToString(), 200f, 220f };
            }
        }

        [Theory(DisplayName = "SpawnMinionNearPosition 主题加成：{0}")]
        [MemberData(nameof(SpawnBonusCases))]
        public void SpawnMinionNearPosition_ThemedBonus(
            string caseName, bool useLegacyOverload, int bossElement, string minionAffinity,
            float baseHealth, float expectedHealth)
        {
            var (wave, typeId) = CreateSpawnEnv(minionAffinity, baseHealth);

            int spawned = useLegacyOverload
                ? wave.SpawnMinionNearPosition(typeId, 1, 5f, 5f)
                : wave.SpawnMinionNearPosition(typeId, 1, 5f, 5f, bossElement);
            Assert.True(spawned == 1, caseName + "：应生成 1 个 minion，实际 " + spawned);

            var active = Store.GetCachedActiveEnemyIds();
            Assert.Single(active);
            int minionId = active[0];
            Assert.Equal(expectedHealth, Store.EnemyHealth[minionId], 0.001f);
            Assert.Equal(expectedHealth, Store.EnemyMaxHealth[minionId], 0.001f);
        }
    }
}
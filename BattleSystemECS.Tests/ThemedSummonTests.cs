using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests
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
    public class ThemedSummonTests
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
            var store = new ComponentStore();
            int eid = 0;
            // All 4 phase slots should default to 0 (None = no themed bonus).
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Equal(0, store.GetEnemyPhaseElementAffinity(eid, ph));
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_NegativeValue_NormalizesToZero()
        {
            var store = new ComponentStore();
            int eid = 0;
            // Negative value (defensive) is clamped to 0 (None) to keep "no bonus" a clean
            // sentinel for the spawn path.
            store.SetEnemyPhaseElementAffinity(eid, 0, -5);
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_InvalidEnemyId_IsNoOp()
        {
            var store = new ComponentStore();
            // Out-of-range enemyId is silently ignored.
            store.SetEnemyPhaseElementAffinity(-1, 0, (int)ElementType.Fire);
            store.SetEnemyPhaseElementAffinity(ComponentStore.MAX_ENTITIES, 0, (int)ElementType.Fire);
            // No exception = pass.
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_InvalidPhase_IsNoOp()
        {
            var store = new ComponentStore();
            int eid = 0;
            // Out-of-range phase is silently ignored.
            store.SetEnemyPhaseElementAffinity(eid, -1, (int)ElementType.Ice);
            store.SetEnemyPhaseElementAffinity(eid, ComponentStore.BOSS_PHASE_MAX, (int)ElementType.Ice);
            // Slot 0 should still be at its default.
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseElementAffinity_Valid_StoresValue()
        {
            var store = new ComponentStore();
            int eid = 0;
            store.SetEnemyPhaseElementAffinity(eid, 1, (int)ElementType.Lightning);
            Assert.Equal((int)ElementType.Lightning, store.GetEnemyPhaseElementAffinity(eid, 1));
            // Adjacent slots remain at 0.
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(eid, 0));
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(eid, 2));
        }

        [Fact]
        public void GetEnemyPhaseElementAffinity_OutOfBounds_ReturnsZero()
        {
            var store = new ComponentStore();
            // Invalid enemyId / phase returns 0 (None) — safe default for the spawn path.
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(-1, 0));
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(ComponentStore.MAX_ENTITIES, 0));
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(0, -1));
            Assert.Equal(0, store.GetEnemyPhaseElementAffinity(0, ComponentStore.BOSS_PHASE_MAX));
        }

        // ── DestroyEntity ID-reuse safety ────────────────────────────────

        [Fact]
        public void DestroyEntity_ResetsElementAffinitySlots()
        {
            var store = new ComponentStore();
            int eid = store.CreateEntity();
            store.AddActiveEnemyId(eid);
            store.EnemyActive[eid] = true;
            // Fill all 4 phase slots with distinct elements.
            store.SetEnemyPhaseElementAffinity(eid, 0, (int)ElementType.Fire);
            store.SetEnemyPhaseElementAffinity(eid, 1, (int)ElementType.Ice);
            store.SetEnemyPhaseElementAffinity(eid, 2, (int)ElementType.Lightning);
            store.SetEnemyPhaseElementAffinity(eid, 3, (int)ElementType.Poison);
            // Sanity: values are set.
            Assert.Equal((int)ElementType.Fire, store.GetEnemyPhaseElementAffinity(eid, 0));

            store.DestroyEntity(eid);
            // After destroy, all 4 phase slots must be reset to 0 (None) to prevent
            // a recycled ID from carrying the previous boss's element affinity into
            // a freshly-spawned unit.
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Equal(0, store.GetEnemyPhaseElementAffinity(eid, ph));
        }

        // ── SpawnMinionNearPosition: themed HP bonus ─────────────────────

        [Fact]
        public void SpawnMinionNearPosition_LegacyFourArg_NoBonus()
        {
            // The 4-arg overload delegates to the 6-arg form with bossElementAffinity=0.
            // bossElementAffinity=0 means no bonus, even if the minion has an ElementAffinity.
            var store = new ComponentStore();
            var config = new GameConfig();
            config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "FireImp", Health = 100, MaxHealth = 100, Damage = 5, MoveSpeed = 1f,
                ElementAffinity = "Fire"
            });
            int testTypeId = config.MonsterTypes.Count - 1; // points to the FireImp we just added
            var renderer = new MockRenderer();
            var wave = new WaveSpawningSystem(store, renderer, config);

            int spawned = wave.SpawnMinionNearPosition(testTypeId, 1, 5f, 5f);
            Assert.Equal(1, spawned);

            var active = store.GetCachedActiveEnemyIds();
            Assert.Single(active);
            int minionId = active[0];
            // No boss affinity → no themed bonus → Health stays at base 100.
            Assert.Equal(100f, store.EnemyHealth[minionId]);
        }

        [Fact]
        public void SpawnMinionNearPosition_MatchingElement_AppliesTenPercentHpBonus()
        {
            var store = new ComponentStore();
            var config = new GameConfig();
            config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "FireImp", Health = 100, MaxHealth = 100, Damage = 5, MoveSpeed = 1f,
                ElementAffinity = "Fire"
            });
            int testTypeId = config.MonsterTypes.Count - 1;
            var renderer = new MockRenderer();
            var wave = new WaveSpawningSystem(store, renderer, config);

            // bossElementAffinity = Fire (1) matches minion's "Fire" affinity.
            int spawned = wave.SpawnMinionNearPosition(testTypeId, 1, 5f, 5f, (int)ElementType.Fire);
            Assert.Equal(1, spawned);

            var active = store.GetCachedActiveEnemyIds();
            Assert.Single(active);
            int minionId = active[0];
            // +10% HP bonus: 100 * 1.10 = 110.
            Assert.Equal(110f, store.EnemyHealth[minionId]);
            Assert.Equal(110f, store.EnemyMaxHealth[minionId]);
        }

        [Fact]
        public void SpawnMinionNearPosition_MismatchedElement_NoBonus()
        {
            var store = new ComponentStore();
            var config = new GameConfig();
            config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "FireImp", Health = 100, MaxHealth = 100, Damage = 5, MoveSpeed = 1f,
                ElementAffinity = "Fire"
            });
            int testTypeId = config.MonsterTypes.Count - 1;
            var renderer = new MockRenderer();
            var wave = new WaveSpawningSystem(store, renderer, config);

            // bossElementAffinity = Ice (2) does NOT match minion's "Fire" affinity.
            int spawned = wave.SpawnMinionNearPosition(testTypeId, 1, 5f, 5f, (int)ElementType.Ice);
            Assert.Equal(1, spawned);

            var active = store.GetCachedActiveEnemyIds();
            Assert.Single(active);
            int minionId = active[0];
            // No bonus → Health stays at base 100.
            Assert.Equal(100f, store.EnemyHealth[minionId]);
        }

        [Fact]
        public void SpawnMinionNearPosition_EmptyMinionAffinity_NoBonus()
        {
            var store = new ComponentStore();
            var config = new GameConfig();
            config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "NeutralGrunt", Health = 100, MaxHealth = 100, Damage = 5, MoveSpeed = 1f,
                ElementAffinity = "" // no affinity
            });
            int testTypeId = config.MonsterTypes.Count - 1;
            var renderer = new MockRenderer();
            var wave = new WaveSpawningSystem(store, renderer, config);

            // bossElementAffinity = Fire but minion has empty affinity → no bonus.
            int spawned = wave.SpawnMinionNearPosition(testTypeId, 1, 5f, 5f, (int)ElementType.Fire);
            Assert.Equal(1, spawned);

            var active = store.GetCachedActiveEnemyIds();
            Assert.Single(active);
            int minionId = active[0];
            Assert.Equal(100f, store.EnemyHealth[minionId]);
        }

        [Fact]
        public void SpawnMinionNearPosition_CaseInsensitiveMatch_AppliesBonus()
        {
            var store = new ComponentStore();
            var config = new GameConfig();
            // Minion uses lowercase "fire" — match should still work (OrdinalIgnoreCase).
            config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "FireImp", Health = 50, MaxHealth = 50, Damage = 5, MoveSpeed = 1f,
                ElementAffinity = "fire"
            });
            int testTypeId = config.MonsterTypes.Count - 1;
            var renderer = new MockRenderer();
            var wave = new WaveSpawningSystem(store, renderer, config);

            int spawned = wave.SpawnMinionNearPosition(testTypeId, 1, 5f, 5f, (int)ElementType.Fire);
            Assert.Equal(1, spawned);

            var active = store.GetCachedActiveEnemyIds();
            Assert.Single(active);
            int minionId = active[0];
            // +10% HP bonus: 50 * 1.10 = 55.
            Assert.Equal(55f, store.EnemyHealth[minionId]);
        }

        [Fact]
        public void SpawnMinionNearPosition_AllFourElementsMatch_BonusApplies()
        {
            // Sanity: verify bonus works for all 4 ElementType values (Fire, Ice, Lightning, Poison).
            var renderer = new MockRenderer();
            foreach (ElementType elem in new[] { ElementType.Fire, ElementType.Ice, ElementType.Lightning, ElementType.Poison })
            {
                var store = new ComponentStore();
                var config = new GameConfig();
                config.MonsterTypes.Add(new MonsterConfig
                {
                    Type = elem.ToString() + "Minion",
                    Health = 200, MaxHealth = 200, Damage = 5, MoveSpeed = 1f,
                    ElementAffinity = elem.ToString()
                });
                int testTypeId = config.MonsterTypes.Count - 1;
                var wave = new WaveSpawningSystem(store, renderer, config);

                int spawned = wave.SpawnMinionNearPosition(testTypeId, 1, 5f, 5f, (int)elem);
                Assert.Equal(1, spawned);

                var active = store.GetCachedActiveEnemyIds();
                Assert.Single(active);
                int minionId = active[0];
                // +10% HP bonus: 200 * 1.10 = 220.
                Assert.Equal(220f, store.EnemyHealth[minionId]);
            }
        }
    }
}

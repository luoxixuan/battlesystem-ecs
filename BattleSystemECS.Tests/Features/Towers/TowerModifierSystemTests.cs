using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using System;
using System.IO;
using System.Linq;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Components;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 145 Direction 3: Per-Tower Modifier Pool (塔类型专精重随).
    ///
    /// Verifies:
    ///   - Default state: all fields are -1 / 0f / 0 (zero-overhead fast path)
    ///   - RollAtPlacement: rolls a modifier when pool is non-empty
    ///   - RollAtPlacement: no-op when pool is empty (returns -1, fields stay -1)
    ///   - RollAtPlacement: invalid towerId returns -1
    ///   - RollAtPlacement: weight distribution rolls each eligible index over many trials
    ///   - RerollModifier: drops the existing assignment and re-rolls
    ///   - ClearModifier: resets the assignment
    ///   - GetModifierStat / GetModifierName: read helpers resolve index → def fields
    ///   - SetMinRarity: restricts roll to >= minRarity
    ///   - 4 reset paths: AddTower / AddTower (long sig) / DestroyEntity tower branch / SetTowerModifier sentinel
    ///   - Inert when GameConfig is null (safe no-op)
    ///   - GameConfig.GetTowerModifierDef(int) returns null for out-of-range index
    ///   - GameConfig.GetTowerModifierDef(string) returns null for unknown id
    ///   - GameConfig.GetTowerModifierIndex returns -1 for unknown id
    /// </summary>
    public class TowerModifierSystemTests : BattleTestBase
    {
        // ── helpers ────────────────────────────────────────────────────

        private void SetupModifiers()
        {
            Config.TowerModifiers = new[]
            {
                new GameConfig.TowerModifierDef
                {
                    ModifierId = "crit_master",
                    Name = "Crit Master",
                    Stat = "CritChance",
                    Magnitude = 0.15f,
                    Rarity = 0,
                    Weight = 30
                },
                new GameConfig.TowerModifierDef
                {
                    ModifierId = "vampire",
                    Name = "Vampire",
                    Stat = "LifeOnKill",
                    Magnitude = 3.0f,
                    Rarity = 1,
                    Weight = 20
                },
                new GameConfig.TowerModifierDef
                {
                    ModifierId = "treasure_hunter",
                    Name = "Treasure Hunter",
                    Stat = "GoldOnKill",
                    Magnitude = 2.0f,
                    Rarity = 1,
                    Weight = 20
                },
                new GameConfig.TowerModifierDef
                {
                    ModifierId = "annihilator",
                    Name = "Annihilator",
                    Stat = "CritMultiplier",
                    Magnitude = 0.75f,
                    Rarity = 4,
                    Weight = 2
                }
            };
        }

        private int MakeTower(TowerType type = TowerType.Basic)
        {
            return RawTower(0, 0, type, 5f, 3, 1f, 1, 50f);
        }

        // ── Default state ──────────────────────────────────────────────

        [Fact]
        public void DefaultState_AllFieldsInert()
        {
            // C# array default = 0; the -1 sentinel is applied per-entity via
            // AddTower / AddTower-long-sig / DestroyEntity. A fresh ComponentStore
            // with no entities ever spawned has all-zero arrays.
            Assert.Equal(0, Store.TowerModifierId[0]);
            Assert.Equal(0f, Store.TowerModifierMagnitude[0], 3);
            Assert.Equal(0, Store.TowerModifierRarity[0]);

            // After AddTower the per-entity defaults kick in (sentinel = -1 / 0f / 0).
            int tid = MakeTower();
            Assert.Equal(-1, Store.TowerModifierId[tid]);
            Assert.Equal(0f, Store.TowerModifierMagnitude[tid], 3);
            Assert.Equal(0, Store.TowerModifierRarity[tid]);
        }

        // ── RollAtPlacement basics ─────────────────────────────────────

        [Fact]
        public void RollAtPlacement_InvalidTower_ReturnsMinusOne()
        {
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            Assert.Equal(-1, sys.RollAtPlacement(-1));
            Assert.Equal(-1, sys.RollAtPlacement(ComponentStore.MAX_ENTITIES));
            Assert.Equal(-1, sys.RollAtPlacement(ComponentStore.MAX_ENTITIES + 100));
        }

        [Fact]
        public void RollAtPlacement_InactiveTower_ReturnsMinusOne()
        {
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            // Slot 0 was never occupied by a tower — TowerActive is false.
            Assert.Equal(-1, sys.RollAtPlacement(0));
        }

        [Fact]
        public void RollAtPlacement_EmptyPool_StaysMinusOne()
        {
            var sys = new TowerModifierSystem(Store, Config); // no modifiers loaded
            int tid = MakeTower();
            int idx = sys.RollAtPlacement(tid);
            Assert.Equal(-1, idx);
            Assert.Equal(-1, Store.TowerModifierId[tid]);
            Assert.Equal(0f, Store.TowerModifierMagnitude[tid], 3);
            Assert.Equal(0, Store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void RollAtPlacement_NullGameConfig_StaysMinusOne()
        {
            var sys = new TowerModifierSystem(Store, gameConfig: null);
            int tid = MakeTower();
            int idx = sys.RollAtPlacement(tid);
            Assert.Equal(-1, idx);
            Assert.Equal(-1, Store.TowerModifierId[tid]);
        }

        [Fact]
        public void RollAtPlacement_NonEmptyPool_AssignsModifier()
        {
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            int tid = MakeTower();
            int idx = sys.RollAtPlacement(tid);
            // The pool has 4 entries — idx should be 0..3 (valid range).
            Assert.InRange(idx, 0, Config.TowerModifiers.Length - 1);
            Assert.Equal(idx, Store.TowerModifierId[tid]);
            // The cached magnitude/rarity match the rolled def.
            var def = Config.TowerModifiers[idx];
            Assert.Equal(def.Magnitude, Store.TowerModifierMagnitude[tid], 3);
            Assert.Equal(def.Rarity, Store.TowerModifierRarity[tid]);
            // Read helpers see the same data.
            Assert.Equal(def.Name, sys.GetModifierName(tid));
            Assert.Equal(def.Stat, sys.GetModifierStat(tid));
            Assert.Equal(def.Magnitude, sys.GetModifierMagnitude(tid), 3);
            Assert.Equal(def.Rarity, sys.GetModifierRarity(tid));
            Assert.True(sys.HasModifier(tid));
        }

        [Fact]
        public void RollAtPlacement_Distribution_HitsAllEntriesOverManyRolls()
        {
            // 1000 trials; each index should appear at least once given the weight
            // distribution. We don't assert exact percentages (RNG-dependent) — only
            // that the roll function is reaching all entries.
            SetupModifiers();
            Store.Determinism.Reset(42);
            var sys = new TowerModifierSystem(Store, Config);
            int[] counts = new int[Config.TowerModifiers.Length];
            for (int i = 0; i < 1000; i++)
            {
                int tid = MakeTower();
                int idx = sys.RollAtPlacement(tid);
                Assert.InRange(idx, 0, Config.TowerModifiers.Length - 1);
                counts[idx]++;
            }
            // All 4 indices should appear (statistically, ~1000 * 2/72 = 28 expected for
            // the rarest "annihilator" — well above zero with the deterministic seed).
            for (int i = 0; i < counts.Length; i++)
            {
                Assert.True(counts[i] > 0, $"Index {i} never rolled in 1000 trials (counts={string.Join(",", counts)})");
            }
        }

        // ── RerollModifier ─────────────────────────────────────────────

        [Fact]
        public void RerollModifier_DropsExisting_AndRollsNew()
        {
            SetupModifiers();
            Store.Determinism.Reset(7);
            var sys = new TowerModifierSystem(Store, Config);
            int tid = MakeTower();

            int first = sys.RollAtPlacement(tid);
            Assert.InRange(first, 0, Config.TowerModifiers.Length - 1);
            Assert.True(sys.HasModifier(tid));

            int second = sys.RerollModifier(tid);
            Assert.InRange(second, 0, Config.TowerModifiers.Length - 1);
            // Reroll sets the field to the new value (which may equal first by chance).
            Assert.Equal(second, Store.TowerModifierId[tid]);
        }

        [Fact]
        public void RerollModifier_InvalidTower_ReturnsMinusOne()
        {
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            Assert.Equal(-1, sys.RerollModifier(-1));
            Assert.Equal(-1, sys.RerollModifier(ComponentStore.MAX_ENTITIES));
        }

        // ── ClearModifier ──────────────────────────────────────────────

        [Fact]
        public void ClearModifier_ResetsAssignment()
        {
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            int tid = MakeTower();
            sys.RollAtPlacement(tid);
            Assert.True(sys.HasModifier(tid));

            sys.ClearModifier(tid);
            Assert.False(sys.HasModifier(tid));
            Assert.Equal(-1, Store.TowerModifierId[tid]);
            Assert.Equal(0f, Store.TowerModifierMagnitude[tid], 3);
            Assert.Equal(0, Store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void ClearModifier_NoOpOnUnrolledTower()
        {
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            int tid = MakeTower();
            // No roll yet — clear should be a safe no-op.
            sys.ClearModifier(tid);
            Assert.Equal(-1, Store.TowerModifierId[tid]);
        }

        // ── SetMinRarity gate ──────────────────────────────────────────

        [Fact]
        public void SetMinRarity_FiltersOutLowerRarityEntries()
        {
            SetupModifiers();
            // Pool has rarities 0, 1, 1, 4：minRarity=2 时只有 rarity>=2 的条目可被抽中。
            // 期望索引从注入的 config 推导，而不是钉死“annihilator 在第 3 位”。
            const int minRarity = 2;
            var eligible = Config.TowerModifiers
                .Select((def, index) => (def, index))
                .Where(pair => pair.def.Rarity >= minRarity)
                .ToArray();
            Assert.NotEmpty(eligible);

            var sys = new TowerModifierSystem(Store, Config);
            sys.SetMinRarity(minRarity);
            for (int i = 0; i < 50; i++)
            {
                int tid = MakeTower();
                int idx = sys.RollAtPlacement(tid);
                Assert.Contains(idx, eligible.Select(pair => pair.index));
                Assert.True(Config.TowerModifiers[idx].Rarity >= minRarity);
            }
        }

        [Fact]
        public void SetMinRarity_ClampedToRange()
        {
            SetupModifiers();
            int maxRarity = Config.TowerModifiers.Max(def => def.Rarity);
            var sys = new TowerModifierSystem(Store, Config);
            sys.SetMinRarity(-5);
            Assert.Equal(0, sys.GetMinRarity());
            sys.SetMinRarity(99);
            Assert.Equal(maxRarity, sys.GetMinRarity());
        }

        // ── ComponentStore accessors ───────────────────────────────────

        [Fact]
        public void ComponentStore_HasTowerModifier_DefaultFalse_TrueAfterRoll()
        {
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            int tid = MakeTower();
            Assert.False(Store.HasTowerModifier(tid));
            sys.RollAtPlacement(tid);
            Assert.True(Store.HasTowerModifier(tid));
        }

        [Fact]
        public void ComponentStore_SetTowerModifier_ClampsRarity()
        {
            int tid = MakeTower();
            Store.SetTowerModifier(tid, 0, 1f, 99);
            Assert.Equal(4, Store.TowerModifierRarity[tid]); // clamped to 4
            Store.SetTowerModifier(tid, 0, 1f, -3);
            Assert.Equal(0, Store.TowerModifierRarity[tid]); // clamped to 0
        }

        [Fact]
        public void ComponentStore_Accessors_InvalidEntity_SafeDefaults()
        {
            Assert.False(Store.HasTowerModifier(-1));
            Assert.Equal(-1, Store.GetTowerModifierId(-1));
            Assert.Equal(0f, Store.GetTowerModifierMagnitude(-1));
            Assert.Equal(0, Store.GetTowerModifierRarity(-1));
        }

        // ── Reset paths ────────────────────────────────────────────────

        [Theory(DisplayName = "两种 AddTower 重载都把 modifier 重置为 -1/0/0")]
        [InlineData(false)]
        [InlineData(true)]
        public void ResetPath_AddTowerOverloads_DefaultTowerModifierFields(bool useLongSignature)
        {
            int tid = Store.CreateEntity();
            if (useLongSignature)
            {
                // AddTower with explicit debuff params + damage-type / turn-rate overload
                Store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f, "standard",
                    0.1f /*StunChance*/, 0.2f /*SlowAmount*/, 0.3f /*SlowDuration*/);
            }
            else
            {
                Store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            }
            // The default state must be -1 / 0f / 0.
            Assert.Equal(-1, Store.TowerModifierId[tid]);
            Assert.Equal(0f, Store.TowerModifierMagnitude[tid], 3);
            Assert.Equal(0, Store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void ResetPath_DestroyEntity_RollsNewModifier_DoesNotLeakStale()
        {
            // The critical anti-leak test: place a tower, roll a modifier, destroy it,
            // then place another tower. The recycled slot must NOT carry the previous
            // tower's rolled modifier.
            SetupModifiers();
            var sys = new TowerModifierSystem(Store, Config);
            int tid1 = MakeTower();
            int rolled1 = sys.RollAtPlacement(tid1);
            Assert.InRange(rolled1, 0, Config.TowerModifiers.Length - 1);
            Assert.NotEqual(-1, Store.TowerModifierId[tid1]);

            Store.DestroyEntity(tid1);
            // After destroy, the slot is reset.
            Assert.Equal(-1, Store.TowerModifierId[tid1]);
            Assert.Equal(0f, Store.TowerModifierMagnitude[tid1], 3);
            Assert.Equal(0, Store.TowerModifierRarity[tid1]);

            // Place a new tower at the recycled slot — its modifier must be -1
            // until RollAtPlacement is invoked. (CreateEntity pops the recycled id
            // from the free list, so we may get a new id, but the test still
            // validates that DestroyEntity's reset ran.)
            int tid2 = MakeTower();
            Assert.Equal(-1, Store.TowerModifierId[tid2]);
        }

        // ── GameConfig lookups ─────────────────────────────────────────

        [Fact]
        public void GameConfig_GetTowerModifierDef_ByIndex_OutOfRange_ReturnsNull()
        {
            Config.TowerModifiers = new[] { new GameConfig.TowerModifierDef { ModifierId = "x" } };
            Assert.Null(Config.GetTowerModifierDef(-1));
            Assert.Null(Config.GetTowerModifierDef(1));
            Assert.Null(Config.GetTowerModifierDef(999));
        }

        [Fact]
        public void GameConfig_GetTowerModifierDef_ById_Unknown_ReturnsNull()
        {
            Config.TowerModifiers = new[] { new GameConfig.TowerModifierDef { ModifierId = "x" } };
            Assert.Null(Config.GetTowerModifierDef(""));
            Assert.Null(Config.GetTowerModifierDef("missing"));
        }

        [Fact]
        public void GameConfig_GetTowerModifierIndex_Unknown_ReturnsMinusOne()
        {
            Config.TowerModifiers = new[] { new GameConfig.TowerModifierDef { ModifierId = "x" } };
            Assert.Equal(-1, Config.GetTowerModifierIndex(""));
            Assert.Equal(-1, Config.GetTowerModifierIndex("nope"));
        }

        // ── SetGameConfig late binding ─────────────────────────────────

        [Fact]
        public void SetGameConfig_LateBinding_EnablesPool()
        {
            var sys = new TowerModifierSystem(Store, gameConfig: null);
            int tid = MakeTower();
            // First roll: no config → -1.
            Assert.Equal(-1, sys.RollAtPlacement(tid));

            // Late-bind the config — the next roll must succeed.
            Config.TowerModifiers = new[]
            {
                new GameConfig.TowerModifierDef { ModifierId = "x", Name = "X", Stat = "Damage", Magnitude = 0.5f, Rarity = 0, Weight = 1 }
            };
            sys.SetGameConfig(Config);

            int tid2 = MakeTower();
            int idx = sys.RollAtPlacement(tid2);
            Assert.Equal(0, idx);
            Assert.Equal("X", sys.GetModifierName(tid2));
        }
    }
}

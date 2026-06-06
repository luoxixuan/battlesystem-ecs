using Xunit;
using System;
using System.IO;
using System.Linq;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Components;

namespace BattleSystemECS.Tests
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
    public class TowerModifierSystemTests
    {
        // ── helpers ────────────────────────────────────────────────────

        private static (ComponentStore store, GameConfig config) MakeStoreAndConfig()
        {
            var store = new ComponentStore();
            var config = new GameConfig();
            config.TowerModifiers = new[]
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
            return (store, config);
        }

        private static int MakeTower(ComponentStore store, TowerType type = TowerType.Basic)
        {
            int tid = store.CreateEntity();
            store.AddTower(tid, type, 5f, 3, 1f, 1, 50f);
            return tid;
        }

        // ── Default state ──────────────────────────────────────────────

        [Fact]
        public void DefaultState_AllFieldsInert()
        {
            var store = new ComponentStore();
            // C# array default = 0; the -1 sentinel is applied per-entity via
            // AddTower / AddTower-long-sig / DestroyEntity. A fresh ComponentStore
            // with no entities ever spawned has all-zero arrays.
            Assert.Equal(0, store.TowerModifierId[0]);
            Assert.Equal(0f, store.TowerModifierMagnitude[0]);
            Assert.Equal(0, store.TowerModifierRarity[0]);

            // After AddTower the per-entity defaults kick in (sentinel = -1 / 0f / 0).
            int tid = MakeTower(store);
            Assert.Equal(-1, store.TowerModifierId[tid]);
            Assert.Equal(0f, store.TowerModifierMagnitude[tid]);
            Assert.Equal(0, store.TowerModifierRarity[tid]);
        }

        // ── RollAtPlacement basics ─────────────────────────────────────

        [Fact]
        public void RollAtPlacement_InvalidTower_ReturnsMinusOne()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            Assert.Equal(-1, sys.RollAtPlacement(-1));
            Assert.Equal(-1, sys.RollAtPlacement(ComponentStore.MAX_ENTITIES));
            Assert.Equal(-1, sys.RollAtPlacement(ComponentStore.MAX_ENTITIES + 100));
        }

        [Fact]
        public void RollAtPlacement_InactiveTower_ReturnsMinusOne()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            // Slot 0 was never occupied by a tower — TowerActive is false.
            Assert.Equal(-1, sys.RollAtPlacement(0));
        }

        [Fact]
        public void RollAtPlacement_EmptyPool_StaysMinusOne()
        {
            var store = new ComponentStore();
            var config = new GameConfig(); // no modifiers loaded
            var sys = new TowerModifierSystem(store, config);
            int tid = MakeTower(store);
            int idx = sys.RollAtPlacement(tid);
            Assert.Equal(-1, idx);
            Assert.Equal(-1, store.TowerModifierId[tid]);
            Assert.Equal(0f, store.TowerModifierMagnitude[tid]);
            Assert.Equal(0, store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void RollAtPlacement_NullGameConfig_StaysMinusOne()
        {
            var store = new ComponentStore();
            var sys = new TowerModifierSystem(store, gameConfig: null);
            int tid = MakeTower(store);
            int idx = sys.RollAtPlacement(tid);
            Assert.Equal(-1, idx);
            Assert.Equal(-1, store.TowerModifierId[tid]);
        }

        [Fact]
        public void RollAtPlacement_NonEmptyPool_AssignsModifier()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            int tid = MakeTower(store);
            int idx = sys.RollAtPlacement(tid);
            // The pool has 4 entries — idx should be 0..3 (valid range).
            Assert.InRange(idx, 0, config.TowerModifiers.Length - 1);
            Assert.Equal(idx, store.TowerModifierId[tid]);
            // The cached magnitude/rarity match the rolled def.
            var def = config.TowerModifiers[idx];
            Assert.Equal(def.Magnitude, store.TowerModifierMagnitude[tid]);
            Assert.Equal(def.Rarity, store.TowerModifierRarity[tid]);
            // Read helpers see the same data.
            Assert.Equal(def.Name, sys.GetModifierName(tid));
            Assert.Equal(def.Stat, sys.GetModifierStat(tid));
            Assert.Equal(def.Magnitude, sys.GetModifierMagnitude(tid));
            Assert.Equal(def.Rarity, sys.GetModifierRarity(tid));
            Assert.True(sys.HasModifier(tid));
        }

        [Fact]
        public void RollAtPlacement_Distribution_HitsAllEntriesOverManyRolls()
        {
            // 1000 trials; each index should appear at least once given the weight
            // distribution. We don't assert exact percentages (RNG-dependent) — only
            // that the roll function is reaching all entries.
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config, seed: 42);
            int[] counts = new int[config.TowerModifiers.Length];
            for (int i = 0; i < 1000; i++)
            {
                int tid = MakeTower(store);
                int idx = sys.RollAtPlacement(tid);
                Assert.InRange(idx, 0, config.TowerModifiers.Length - 1);
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
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config, seed: 7);
            int tid = MakeTower(store);

            int first = sys.RollAtPlacement(tid);
            Assert.InRange(first, 0, config.TowerModifiers.Length - 1);
            Assert.True(sys.HasModifier(tid));

            int second = sys.RerollModifier(tid);
            Assert.InRange(second, 0, config.TowerModifiers.Length - 1);
            // Reroll sets the field to the new value (which may equal first by chance).
            Assert.Equal(second, store.TowerModifierId[tid]);
        }

        [Fact]
        public void RerollModifier_InvalidTower_ReturnsMinusOne()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            Assert.Equal(-1, sys.RerollModifier(-1));
            Assert.Equal(-1, sys.RerollModifier(ComponentStore.MAX_ENTITIES));
        }

        // ── ClearModifier ──────────────────────────────────────────────

        [Fact]
        public void ClearModifier_ResetsAssignment()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            int tid = MakeTower(store);
            sys.RollAtPlacement(tid);
            Assert.True(sys.HasModifier(tid));

            sys.ClearModifier(tid);
            Assert.False(sys.HasModifier(tid));
            Assert.Equal(-1, store.TowerModifierId[tid]);
            Assert.Equal(0f, store.TowerModifierMagnitude[tid]);
            Assert.Equal(0, store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void ClearModifier_NoOpOnUnrolledTower()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            int tid = MakeTower(store);
            // No roll yet — clear should be a safe no-op.
            sys.ClearModifier(tid);
            Assert.Equal(-1, store.TowerModifierId[tid]);
        }

        // ── SetMinRarity gate ──────────────────────────────────────────

        [Fact]
        public void SetMinRarity_FiltersOutLowerRarityEntries()
        {
            var (store, config) = MakeStoreAndConfig();
            // Pool has 4 entries with rarities 0, 1, 1, 4.
            // Set minRarity=2 → only the rarity-4 annihilator is eligible.
            var sys = new TowerModifierSystem(store, config);
            sys.SetMinRarity(2);
            for (int i = 0; i < 50; i++)
            {
                int tid = MakeTower(store);
                int idx = sys.RollAtPlacement(tid);
                Assert.Equal(3, idx); // only the rarity-4 entry
            }
        }

        [Fact]
        public void SetMinRarity_ClampedToRange()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            sys.SetMinRarity(-5);
            Assert.Equal(0, sys.GetMinRarity());
            sys.SetMinRarity(99);
            Assert.Equal(4, sys.GetMinRarity());
        }

        // ── ComponentStore accessors ───────────────────────────────────

        [Fact]
        public void ComponentStore_HasTowerModifier_DefaultFalse_TrueAfterRoll()
        {
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            int tid = MakeTower(store);
            Assert.False(store.HasTowerModifier(tid));
            sys.RollAtPlacement(tid);
            Assert.True(store.HasTowerModifier(tid));
        }

        [Fact]
        public void ComponentStore_SetTowerModifier_Sentinel_Clears()
        {
            var store = new ComponentStore();
            int tid = MakeTower(store);
            store.SetTowerModifier(tid, 2, 1.5f, 1);
            Assert.Equal(2, store.TowerModifierId[tid]);
            Assert.Equal(1.5f, store.TowerModifierMagnitude[tid]);
            Assert.Equal(1, store.TowerModifierRarity[tid]);

            store.SetTowerModifier(tid, -1, 0f, 0);
            Assert.Equal(-1, store.TowerModifierId[tid]);
            Assert.Equal(0f, store.TowerModifierMagnitude[tid]);
            Assert.Equal(0, store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void ComponentStore_SetTowerModifier_ClampsRarity()
        {
            var store = new ComponentStore();
            int tid = MakeTower(store);
            store.SetTowerModifier(tid, 0, 1f, 99);
            Assert.Equal(4, store.TowerModifierRarity[tid]); // clamped to 4
            store.SetTowerModifier(tid, 0, 1f, -3);
            Assert.Equal(0, store.TowerModifierRarity[tid]); // clamped to 0
        }

        [Fact]
        public void ComponentStore_Accessors_InvalidEntity_SafeDefaults()
        {
            var store = new ComponentStore();
            Assert.False(store.HasTowerModifier(-1));
            Assert.Equal(-1, store.GetTowerModifierId(-1));
            Assert.Equal(0f, store.GetTowerModifierMagnitude(-1));
            Assert.Equal(0, store.GetTowerModifierRarity(-1));
        }

        // ── Reset paths ────────────────────────────────────────────────

        [Fact]
        public void ResetPath_AddTower_DefaultTowerModifierFields()
        {
            var store = new ComponentStore();
            int tid = store.CreateEntity();
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            // The default state must be -1 / 0f / 0.
            Assert.Equal(-1, store.TowerModifierId[tid]);
            Assert.Equal(0f, store.TowerModifierMagnitude[tid]);
            Assert.Equal(0, store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void ResetPath_AddTowerLongSig_DefaultTowerModifierFields()
        {
            var store = new ComponentStore();
            int tid = store.CreateEntity();
            // AddTower with explicit debuff params + damage-type / turn-rate overload
            store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f, "standard",
                0.1f /*StunChance*/, 0.2f /*SlowAmount*/, 0.3f /*SlowDuration*/);
            Assert.Equal(-1, store.TowerModifierId[tid]);
            Assert.Equal(0f, store.TowerModifierMagnitude[tid]);
            Assert.Equal(0, store.TowerModifierRarity[tid]);
        }

        [Fact]
        public void ResetPath_DestroyEntity_RollsNewModifier_DoesNotLeakStale()
        {
            // The critical anti-leak test: place a tower, roll a modifier, destroy it,
            // then place another tower. The recycled slot must NOT carry the previous
            // tower's rolled modifier.
            var (store, config) = MakeStoreAndConfig();
            var sys = new TowerModifierSystem(store, config);
            int tid1 = MakeTower(store);
            int rolled1 = sys.RollAtPlacement(tid1);
            Assert.InRange(rolled1, 0, config.TowerModifiers.Length - 1);
            Assert.NotEqual(-1, store.TowerModifierId[tid1]);

            store.DestroyEntity(tid1);
            // After destroy, the slot is reset.
            Assert.Equal(-1, store.TowerModifierId[tid1]);
            Assert.Equal(0f, store.TowerModifierMagnitude[tid1]);
            Assert.Equal(0, store.TowerModifierRarity[tid1]);

            // Place a new tower at the recycled slot — its modifier must be -1
            // until RollAtPlacement is invoked. (CreateEntity pops the recycled id
            // from the free list, so we may get a new id, but the test still
            // validates that DestroyEntity's reset ran.)
            int tid2 = MakeTower(store);
            Assert.Equal(-1, store.TowerModifierId[tid2]);
        }

        // ── GameConfig lookups ─────────────────────────────────────────

        [Fact]
        public void GameConfig_GetTowerModifierDef_ByIndex_OutOfRange_ReturnsNull()
        {
            var cfg = new GameConfig();
            cfg.TowerModifiers = new[] { new GameConfig.TowerModifierDef { ModifierId = "x" } };
            Assert.Null(cfg.GetTowerModifierDef(-1));
            Assert.Null(cfg.GetTowerModifierDef(1));
            Assert.Null(cfg.GetTowerModifierDef(999));
        }

        [Fact]
        public void GameConfig_GetTowerModifierDef_ById_Unknown_ReturnsNull()
        {
            var cfg = new GameConfig();
            cfg.TowerModifiers = new[] { new GameConfig.TowerModifierDef { ModifierId = "x" } };
            Assert.Null(cfg.GetTowerModifierDef(""));
            Assert.Null(cfg.GetTowerModifierDef("missing"));
        }

        [Fact]
        public void GameConfig_GetTowerModifierIndex_Unknown_ReturnsMinusOne()
        {
            var cfg = new GameConfig();
            cfg.TowerModifiers = new[] { new GameConfig.TowerModifierDef { ModifierId = "x" } };
            Assert.Equal(-1, cfg.GetTowerModifierIndex(""));
            Assert.Equal(-1, cfg.GetTowerModifierIndex("nope"));
        }

        // ── SetGameConfig late binding ─────────────────────────────────

        [Fact]
        public void SetGameConfig_LateBinding_EnablesPool()
        {
            var store = new ComponentStore();
            var sys = new TowerModifierSystem(store, gameConfig: null);
            int tid = MakeTower(store);
            // First roll: no config → -1.
            Assert.Equal(-1, sys.RollAtPlacement(tid));

            // Late-bind the config — the next roll must succeed.
            var cfg = new GameConfig();
            cfg.TowerModifiers = new[]
            {
                new GameConfig.TowerModifierDef { ModifierId = "x", Name = "X", Stat = "Damage", Magnitude = 0.5f, Rarity = 0, Weight = 1 }
            };
            sys.SetGameConfig(cfg);

            int tid2 = MakeTower(store);
            int idx = sys.RollAtPlacement(tid2);
            Assert.Equal(0, idx);
            Assert.Equal("X", sys.GetModifierName(tid2));
        }
    }
}

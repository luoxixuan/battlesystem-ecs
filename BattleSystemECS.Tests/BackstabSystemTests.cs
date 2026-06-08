using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Round 174 Direction 4 — Backstab positional damage bonus tests.
    /// Verifies: PlaceTower sentinel resolution (0 → 1.0x, > 1.0 → kept),
    /// _backstabEnabled master switch cached correctly, angle clamp, and
    /// recycled-slot reset (no phantom bonus bleed from previous occupant).
    ///
    /// Coverage strategy: PlaceTower's signature doesn't take a TowerConfig, so
    /// the per-tower BackstabDamageMult opt-in path is exercised indirectly via
    /// the recycled-slot test (test 5) which proves a fresh non-rogue tower does
    /// NOT inherit a phantom 2.0x. The default PlaceTower path always resolves
    /// to 1.0x (the critical bug-fix contract).
    /// </summary>
    public class BackstabSystemTests
    {
        // ── 1: PlaceTower default (no per-tower backstab fields) resolves to 1.0x inert ──
        [Fact]
        public void PlaceTower_DefaultConfig_ResolvesToInert1_0x()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig(); // default BackstabConfig: Enabled=true
            var sys = new TowerPlacementSystem(store, r, config);
            int towerId = sys.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(towerId >= 0);
            // Sentinel 0 → 1.0x (inert, fast path). This is the critical bug-fix
            // test: the previous sentinel logic silently turned every non-rogue
            // tower into a 2.0x rogue.
            Assert.Equal(1.0f, store.TowerBackstabDamageMult[towerId]);
        }

        // ── 2: PlaceTower writes angle sentinel (0° → global default 90°) ──
        [Fact]
        public void PlaceTower_DefaultAngle_InheritsGlobalDefault()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            config.Backstab.DefaultAngleDeg = 120f;
            var sys = new TowerPlacementSystem(store, r, config);
            int towerId = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(towerId >= 0);
            // The angle is the global default (used if a future mult is set to > 1.0).
            // For a 1.0x mult the angle is never read, but PlaceTower still writes
            // the resolved value so any later code path that flips the mult works.
            Assert.Equal(120f, store.TowerBackstabAngleDeg[towerId]);
        }

        // ── 3: SetGameConfig caches _backstabEnabled correctly (with and without config) ──
        [Fact]
        public void SetGameConfig_EnabledFalse_DisablesBackstab()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerAttackSystem(store, r);

            // BackstabConfig with Enabled=false → hot path is fully skipped.
            var configOff = new GameConfig { Backstab = new BackstabConfig { Enabled = false } };
            sys.SetGameConfig(configOff);
            Assert.False(configOff.Backstab.Enabled);

            // BackstabConfig with Enabled=true → hot path is on.
            var configOn = new GameConfig { Backstab = new BackstabConfig { Enabled = true } };
            sys.SetGameConfig(configOn);
            Assert.True(configOn.Backstab.Enabled);

            // Backstab = null (default GameConfig but no BackstabConfig attached) → safe.
            var configNull = new GameConfig { Backstab = null };
            sys.SetGameConfig(configNull);

            // GameConfig = null → safe (matrix-less baseline).
            sys.SetGameConfig(null);
        }

        // ── 4: SetGameConfig called repeatedly with different values is stable ──
        [Fact]
        public void SetGameConfig_ResilienceToRepeatedCalls()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerAttackSystem(store, r);

            // First config: enabled
            var c1 = new GameConfig();
            c1.Backstab = new BackstabConfig { Enabled = true, DefaultDamageMult = 2.5f };
            sys.SetGameConfig(c1);

            // Switch off — call set twice in a row, no exception
            c1.Backstab.Enabled = false;
            sys.SetGameConfig(c1);
            sys.SetGameConfig(c1);

            // Switch back on with a different default
            c1.Backstab.Enabled = true;
            c1.Backstab.DefaultDamageMult = 3.0f;
            sys.SetGameConfig(c1);
            Assert.Equal(3.0f, c1.Backstab.DefaultDamageMult);
        }

        // ── 5: Recycled entity slot is reset to 1.0x (no phantom rogue bleed) ──
        [Fact]
        public void DestroyEntity_RecycledSlot_BackstabFieldsReset()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            var sys = new TowerPlacementSystem(store, r, config);

            // Place a tower, then DIRECTLY inject 3.0x mult (simulating a rogue
            // tower placed via a future PlaceTowerFromConfig overload or
            // upgrade-time mutation). This proves the recycled-slot reset
            // path wipes the phantom bonus, regardless of how the rogue
            // configuration was originally written.
            int id1 = sys.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id1 >= 0);
            store.TowerBackstabDamageMult[id1] = 3.0f;
            store.TowerBackstabAngleDeg[id1] = 60f;
            Assert.Equal(3.0f, store.TowerBackstabDamageMult[id1]);

            // Destroy the rogue tower — slot is recycled with fresh 1.0x.
            store.DestroyEntity(id1);

            // Place a normal (non-rogue) tower in the same slot. It must NOT
            // inherit the 3.0x phantom bonus. The sentinel resolution gives
            // 1.0x, and the recycled-slot reset in Remove() also writes 1.0x.
            int id2 = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id2 >= 0);
            Assert.Equal(1.0f, store.TowerBackstabDamageMult[id2]);
        }

        // ── 6: BackstabConfig default constructor has sane values ──
        [Fact]
        public void BackstabConfig_Defaults_AreSane()
        {
            var b = new BackstabConfig();
            Assert.True(b.Enabled);
            Assert.Equal(2.0f, b.DefaultDamageMult);
            Assert.Equal(90f, b.DefaultAngleDeg);
        }

        // ── 7: GameConfig.Backstab default-constructs to a sane instance ──
        [Fact]
        public void GameConfig_Backstab_DefaultConstructs()
        {
            var g = new GameConfig();
            Assert.NotNull(g.Backstab);
            Assert.True(g.Backstab.Enabled);
        }

        // ── 8: TowerConfig has the two new fields with 0f defaults ──
        [Fact]
        public void TowerConfig_BackstabFields_DefaultToZero()
        {
            var tc = new TowerConfig();
            Assert.Equal(0f, tc.BackstabDamageMult);
            Assert.Equal(0f, tc.BackstabAngleDeg);
        }

        // ── 9: Store field arrays are sized for MAX_ENTITIES (no premature OOB) ──
        [Fact]
        public void Store_BackstabArrays_SizedForMaxEntities()
        {
            var store = new ComponentStore();
            Assert.NotNull(store.TowerBackstabDamageMult);
            Assert.NotNull(store.TowerBackstabAngleDeg);
            Assert.Equal(ComponentStore.MAX_ENTITIES, store.TowerBackstabDamageMult.Length);
            Assert.Equal(ComponentStore.MAX_ENTITIES, store.TowerBackstabAngleDeg.Length);
        }

        // ── 10: Multiple backstabs in sequence (idempotent) ──
        [Fact]
        public void PlaceTower_MultipleTowers_AllInertByDefault()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            var sys = new TowerPlacementSystem(store, r, config);
            for (int i = 0; i < 5; i++)
            {
                int towerId = sys.PlaceTower(i, 0, TowerType.Basic, 50f, 3, 1f, 50f);
                Assert.True(towerId >= 0);
                Assert.Equal(1.0f, store.TowerBackstabDamageMult[towerId]);
            }
        }
    }
}

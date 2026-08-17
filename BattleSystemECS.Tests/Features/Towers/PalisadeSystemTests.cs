using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 100 Direction 6: Palisade Tower (control-type, no attack, stuns nearby enemies).
    /// Verifies that:
    ///   - TowerType.Palisade enum value is defined
    ///   - PalisadeConfig defaults are sensible
    ///   - PlaceTower with type=Palisade sets TowerIsPalisade + initializes HP/stun/radius fields
    ///   - PlaceTower with non-Palisade type leaves TowerIsPalisade=false (zero-overhead)
    ///   - PalisadeHP defaults to DefaultPalisadeHP after placement
    ///   - ComponentStore SOA fields are zero-initialized (no ID-reuse leakage)
    ///   - DestroyEntity resets Palisade fields
    /// </summary>
    public class PalisadeSystemTests : BattleTestBase
    {
        private int PlacePalisade(int x, int y)
        {
            _ = Placement; // 构造 Placement（LoadPerTypeCaps 会写 JSON cap），随后显式清空 cap
            DisableTowerCaps();
            return Placement.PlaceTower(x, y, TowerType.Palisade, 0f, 0, 0f, 25f);
        }

        // ─── Config defaults ───────────────────────────────────────────────

        [Fact]
        public void PalisadeConfig_Defaults_AreSensible()
        {
            Assert.True(PalisadeConfig.DefaultPalisadeStunFrames > 0);
            Assert.True(PalisadeConfig.DefaultPalisadeBlockRadius >= 0);
            Assert.True(PalisadeConfig.DefaultPalisadeHP >= 0f);
            Assert.True(PalisadeConfig.EnemyContactDamageToPalisade >= 0f);
        }

        // ─── PlaceTower sets Palisade fields ───────────────────────────────

        [Fact]
        public void PlacePalisade_SetsIsPalisadeTrue()
        {
            int id = PlacePalisade(3, 4);
            Assert.True(id >= 0);
            Assert.True(Store.TowerIsPalisade[id]);
            Assert.Equal(TowerType.Palisade, Store.TowerType[id]);
        }

        [Fact]
        public void PlacePalisade_InitializesStunFramesAndRadius()
        {
            int id = PlacePalisade(5, 5);
            Assert.Equal(PalisadeConfig.DefaultPalisadeStunFrames, Store.PalisadeStunFrames[id]);
            Assert.Equal(PalisadeConfig.DefaultPalisadeBlockRadius, Store.PalisadeBlockRadius[id]);
            Assert.Equal(PalisadeConfig.DefaultPalisadeHP, Store.PalisadeHP[id], 3);
            Assert.Equal(PalisadeConfig.DefaultPalisadeHP, Store.PalisadeMaxHP[id], 3);
        }

        [Fact]
        public void PlaceNonPalisade_LeavesIsPalisadeFalse()
        {
            _ = Placement; // 构造 Placement（LoadPerTypeCaps 会写 JSON cap），随后显式清空 cap
            DisableTowerCaps();
            int id = Placement.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 25f);
            Assert.True(id >= 0);
            Assert.False(Store.TowerIsPalisade[id]);
            Assert.Equal(0, Store.PalisadeStunFrames[id]);
            Assert.Equal(0f, Store.PalisadeHP[id], 3);
        }

        // ─── Zero-overhead default: no Palisade field non-zero unless set ─

        [Fact]
        public void NewComponentStore_AllPalisadeFieldsZero()
        {
            // Backward compat: spawning a regular tower (or no tower at all) must not
            // cause any palisade field to be non-zero. Ensures the new SOA arrays are
            // truly zero-overhead for non-palisade gameplay.
            // Spot-check a few indices
            for (int i = 0; i < 10; i++)
            {
                Assert.False(Store.TowerIsPalisade[i]);
                Assert.Equal(0, Store.PalisadeStunFrames[i]);
                Assert.Equal(0, Store.PalisadeBlockRadius[i]);
                Assert.Equal(0f, Store.PalisadeHP[i], 3);
                Assert.Equal(0f, Store.PalisadeMaxHP[i], 3);
            }
        }

        // ─── DestroyEntity resets Palisade fields (no ID-reuse leakage) ────

        [Fact]
        public void DestroyEntity_ResetsPalisadeFields()
        {
            int id = PlacePalisade(2, 2);
            Assert.True(Store.TowerIsPalisade[id]);

            // Manually bump HP to non-default to confirm reset
            Store.PalisadeHP[id] = 50f;
            Store.PalisadeMaxHP[id] = 200f;
            Store.PalisadeStunFrames[id] = 30;

            Store.DestroyEntity(id);
            Assert.False(Store.TowerIsPalisade[id]);
            Assert.Equal(0, Store.PalisadeStunFrames[id]);
            Assert.Equal(0, Store.PalisadeBlockRadius[id]);
            Assert.Equal(0f, Store.PalisadeHP[id], 3);
            Assert.Equal(0f, Store.PalisadeMaxHP[id], 3);
        }

        // ─── Tile-occupancy interaction: palisade occupies its tile ────────

        [Fact]
        public void PlacePalisade_OccupiesTile()
        {
            int id = PlacePalisade(4, 7);
            Assert.True(id >= 0);
            // Round 95 — tile cache should mark (4, 7) as occupied
            Assert.True(Store.IsTileOccupied(4, 7));
        }
    }
}

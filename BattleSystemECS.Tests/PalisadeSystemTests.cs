using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
    public class PalisadeSystemTests
    {
        private static int PlacePalisade(ComponentStore store, MockRenderer r, int x, int y)
        {
            var sys = new TowerPlacementSystem(store, r);
            return sys.PlaceTower(x, y, TowerType.Palisade, 0f, 0, 0f, 25f);
        }

        // ─── Enum and config defaults ──────────────────────────────────────

        [Fact]
        public void Palisade_EnumValue_IsDefined()
        {
            // Round 100 — verify the new enum slot exists
            Assert.Equal(9, (int)TowerType.Palisade);
        }

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
            var store = new ComponentStore();
            var r = new MockRenderer();
            int id = PlacePalisade(store, r, 3, 4);
            Assert.True(id >= 0);
            Assert.True(store.TowerIsPalisade[id]);
            Assert.Equal(TowerType.Palisade, store.TowerType[id]);
        }

        [Fact]
        public void PlacePalisade_InitializesStunFramesAndRadius()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            int id = PlacePalisade(store, r, 5, 5);
            Assert.Equal(PalisadeConfig.DefaultPalisadeStunFrames, store.PalisadeStunFrames[id]);
            Assert.Equal(PalisadeConfig.DefaultPalisadeBlockRadius, store.PalisadeBlockRadius[id]);
            Assert.Equal(PalisadeConfig.DefaultPalisadeHP, store.PalisadeHP[id]);
            Assert.Equal(PalisadeConfig.DefaultPalisadeHP, store.PalisadeMaxHP[id]);
        }

        [Fact]
        public void PlaceNonPalisade_LeavesIsPalisadeFalse()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int id = sys.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 25f);
            Assert.True(id >= 0);
            Assert.False(store.TowerIsPalisade[id]);
            Assert.Equal(0, store.PalisadeStunFrames[id]);
            Assert.Equal(0f, store.PalisadeHP[id]);
        }

        // ─── Zero-overhead default: no Palisade field non-zero unless set ─

        [Fact]
        public void NewComponentStore_AllPalisadeFieldsZero()
        {
            // Backward compat: spawning a regular tower (or no tower at all) must not
            // cause any palisade field to be non-zero. Ensures the new SOA arrays are
            // truly zero-overhead for non-palisade gameplay.
            var store = new ComponentStore();
            // Spot-check a few indices
            for (int i = 0; i < 10; i++)
            {
                Assert.False(store.TowerIsPalisade[i]);
                Assert.Equal(0, store.PalisadeStunFrames[i]);
                Assert.Equal(0, store.PalisadeBlockRadius[i]);
                Assert.Equal(0f, store.PalisadeHP[i]);
                Assert.Equal(0f, store.PalisadeMaxHP[i]);
            }
        }

        // ─── DestroyEntity resets Palisade fields (no ID-reuse leakage) ────

        [Fact]
        public void DestroyEntity_ResetsPalisadeFields()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            int id = PlacePalisade(store, r, 2, 2);
            Assert.True(store.TowerIsPalisade[id]);

            // Manually bump HP to non-default to confirm reset
            store.PalisadeHP[id] = 50f;
            store.PalisadeMaxHP[id] = 200f;
            store.PalisadeStunFrames[id] = 30;

            store.DestroyEntity(id);
            Assert.False(store.TowerIsPalisade[id]);
            Assert.Equal(0, store.PalisadeStunFrames[id]);
            Assert.Equal(0, store.PalisadeBlockRadius[id]);
            Assert.Equal(0f, store.PalisadeHP[id]);
            Assert.Equal(0f, store.PalisadeMaxHP[id]);
        }

        // ─── Tile-occupancy interaction: palisade occupies its tile ────────

        [Fact]
        public void PlacePalisade_OccupiesTile()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            int id = PlacePalisade(store, r, 4, 7);
            Assert.True(id >= 0);
            // Round 95 — tile cache should mark (4, 7) as occupied
            Assert.True(store.IsTileOccupied(4, 7));
        }
    }
}

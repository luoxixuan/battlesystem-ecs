using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    public class TowerPlacementSystemTests
    {
        // ─── Bug#31: PlaceTower 在 CreateEntity()==-1 时失败 ──────────────────

        [Fact] public void PlaceTower_FailsWhenEntityPoolExhausted()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            while (store.CreateEntity() != -1) { /* exhaust */ }
            var sys = new TowerPlacementSystem(store, r);
            int result = sys.PlaceTower(5, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(-1, result);
        }

        // ─── 幽灵预览 (Ghost Placement) ────────────────────────────────────

        [Fact] public void PreviewPlacement_ValidPositionReturnsTrue()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            bool valid = sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(valid);
            Assert.True(sys.HasActivePreview);
            Assert.True(sys.LastPreviewValid);
        }

        [Fact] public void PreviewPlacement_OutOfBoundsReturnsFalse()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            bool valid = sys.PreviewPlacement(-1, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.False(valid);
            Assert.True(sys.HasActivePreview);
            Assert.False(sys.LastPreviewValid);
        }

        [Fact] public void PreviewPlacement_OnOccupiedCellReturnsFalse()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int placed = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(placed >= 0);
            bool valid = sys.PreviewPlacement(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.False(valid);
        }

        [Fact] public void PreviewPlacement_DoesNotConsumeEntity()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int aliveBefore = store.ActiveTowerIds.Count;
            sys.PreviewPlacement(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.PreviewPlacement(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(aliveBefore, store.ActiveTowerIds.Count);
        }

        [Fact] public void CancelPreview_ClearsState()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(sys.HasActivePreview);
            sys.CancelPreview();
            Assert.False(sys.HasActivePreview);
        }

        [Fact] public void ConfirmPlacement_NoActivePreviewReturnsMinusOne()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int id = sys.ConfirmPlacement();
            Assert.Equal(-1, id);
        }

        [Fact] public void ConfirmPlacement_AfterValidPreviewCreatesTower()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            int id = sys.ConfirmPlacement();
            Assert.True(id >= 0);
            Assert.False(sys.HasActivePreview);
            Assert.Equal(1, store.ActiveTowerIds.Count);
            Assert.Equal(3, (int)store.PositionX[id]);
            Assert.Equal(4, (int)store.PositionY[id]);
        }

        [Fact] public void ConfirmPlacement_AfterInvalidPreviewFails()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.PreviewPlacement(-1, -1, TowerType.Basic, 50f, 3, 1f, 50f);
            int id = sys.ConfirmPlacement();
            Assert.Equal(-1, id);
        }
    }
}

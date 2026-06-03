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

        // ─── Build Queue (BuildPhase 预排多塔位) ────────────────────────────────

        [Fact] public void EnqueueBuild_AppendsInOrder()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            // Enqueue 3 build orders
            bool ok1 = sys.EnqueueBuild(0, 0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            bool ok2 = sys.EnqueueBuild(0, 1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            bool ok3 = sys.EnqueueBuild(0, 2, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(ok1);
            Assert.True(ok2);
            Assert.True(ok3);
            Assert.Equal(3, sys.GetBuildQueueCount(0));
        }

        [Fact] public void EnqueueBuild_RespectsMaxQueueSize()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            // Fill the queue to MAX_BUILD_QUEUE (16)
            for (int i = 0; i < ComponentStore.MAX_BUILD_QUEUE; i++)
            {
                int x = i % 10;
                int y = i / 10;
                bool ok = sys.EnqueueBuild(0, x, y, TowerType.Basic, 50f, 3, 1f, 50f);
                Assert.True(ok, $"Enqueue #{i} should succeed");
            }
            Assert.Equal(ComponentStore.MAX_BUILD_QUEUE, sys.GetBuildQueueCount(0));
            // 17th enqueue must fail
            bool overflow = sys.EnqueueBuild(0, 5, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.False(overflow);
            Assert.Equal(ComponentStore.MAX_BUILD_QUEUE, sys.GetBuildQueueCount(0));
        }

        [Fact] public void EnqueueBuild_RejectsOutOfBoundsPosition()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            Assert.False(sys.EnqueueBuild(0, -1, 5, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.False(sys.EnqueueBuild(0, 5, -1, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.False(sys.EnqueueBuild(0, 10, 5, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.False(sys.EnqueueBuild(0, 5, 20, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.Equal(0, sys.GetBuildQueueCount(0));
        }

        [Fact] public void ClearBuildQueue_EmptiesSlots()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.EnqueueBuild(0, 0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.EnqueueBuild(0, 1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(2, sys.GetBuildQueueCount(0));
            sys.ClearBuildQueue(0);
            Assert.Equal(0, sys.GetBuildQueueCount(0));
            // After clear, refilling should work
            Assert.True(sys.EnqueueBuild(0, 0, 0, TowerType.Basic, 50f, 3, 1f, 50f));
        }

        [Fact] public void ProcessBuildQueue_DrainsInFifoOrder()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            // Grant gold for 3 placements
            store.SetPlayerGold(0, 1000f);
            // Enqueue 3 distinct positions
            sys.EnqueueBuild(0, 0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.EnqueueBuild(0, 1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.EnqueueBuild(0, 2, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(3, sys.GetBuildQueueCount(0));
            // Drain with default interval (0.2s) — 3 ticks of 0.2s each
            int drained1 = sys.ProcessBuildQueue(0, 0.2f);
            int drained2 = sys.ProcessBuildQueue(0, 0.2f);
            int drained3 = sys.ProcessBuildQueue(0, 0.2f);
            Assert.Equal(1, drained1);
            Assert.Equal(1, drained2);
            Assert.Equal(1, drained3);
            Assert.Equal(0, sys.GetBuildQueueCount(0));
            // 3 towers actually placed
            Assert.Equal(3, store.ActiveTowerIds.Count);
            // Gold deducted for 3 placements (3 × 50 = 150)
            Assert.Equal(1000f - 150f, store.GetPlayerGold(0));
        }

        [Fact] public void ProcessBuildQueue_SkipsInsufficientGold()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            // Not enough gold for even 1 placement (cost 50)
            store.SetPlayerGold(0, 30f);
            sys.EnqueueBuild(0, 0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            // Drain — should skip the slot (no gold)
            int drained = sys.ProcessBuildQueue(0, 0.2f);
            Assert.Equal(0, drained);
            // The slot was skipped (count = 0 after CompactQueue)
            Assert.Equal(0, sys.GetBuildQueueCount(0));
            // No tower was placed
            Assert.Equal(0, store.ActiveTowerIds.Count);
            // Gold was NOT deducted (we pre-deducted only on success)
            Assert.Equal(30f, store.GetPlayerGold(0));
        }

        [Fact] public void ProcessBuildQueue_RespectsPacingInterval()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            store.SetPlayerGold(0, 1000f);
            sys.EnqueueBuild(0, 0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            // First tick below interval — should NOT drain
            int below = sys.ProcessBuildQueue(0, 0.1f);
            Assert.Equal(0, below);
            Assert.Equal(1, sys.GetBuildQueueCount(0));
            // Second tick completes the interval
            int above = sys.ProcessBuildQueue(0, 0.1f);
            Assert.Equal(1, above);
            Assert.Equal(0, sys.GetBuildQueueCount(0));
        }

        // ─── Tile Occupancy Cache (Round 95 Direction 4) ────────────────────────
        // O(1) per-tile tower occupancy check. The cache should mirror the
        // active tower set: marked on PlaceTower, cleared on Sell/Destroy,
        // updated on RelocateTower.

        [Fact] public void TileCache_DefaultsToEmpty()
        {
            var store = new ComponentStore();
            for (int x = 0; x < ComponentStore.TILE_GRID_DEFAULT_WIDTH; x++)
            for (int y = 0; y < ComponentStore.TILE_GRID_DEFAULT_HEIGHT; y++)
                Assert.False(store.IsTileOccupied(x, y));
        }

        [Fact] public void TileCache_OutOfBoundsReturnsFalse()
        {
            var store = new ComponentStore();
            Assert.False(store.IsTileOccupied(-1, 5));
            Assert.False(store.IsTileOccupied(5, -1));
            Assert.False(store.IsTileOccupied(ComponentStore.TILE_GRID_DEFAULT_WIDTH, 5));
            Assert.False(store.IsTileOccupied(5, ComponentStore.TILE_GRID_DEFAULT_HEIGHT));
        }

        [Fact] public void PlaceTower_MarksTileOccupied()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            Assert.False(store.IsTileOccupied(3, 4));
            int id = sys.PlaceTower(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            Assert.True(store.IsTileOccupied(3, 4));
        }

        [Fact] public void PlaceTower_RejectsAlreadyOccupiedTile()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int first = sys.PlaceTower(2, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(first >= 0);
            // Second attempt at the same tile should fail via the cache
            int second = sys.PlaceTower(2, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(-1, second);
            Assert.Equal(1, store.ActiveTowerIds.Count);
        }

        [Fact] public void SellTower_ReleasesTile()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int id = sys.PlaceTower(5, 6, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            Assert.True(store.IsTileOccupied(5, 6));
            // Give player 0 enough gold for the sell refund (no gold needed for sell)
            sys.SellTower(id, 0);
            Assert.False(store.IsTileOccupied(5, 6));
            // The tile can now accept a new tower
            int replacement = sys.PlaceTower(5, 6, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(replacement >= 0);
            Assert.True(store.IsTileOccupied(5, 6));
        }

        [Fact] public void RelocateTower_UpdatesTileCache()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            store.SetPlayerGold(0, 1000f);
            int id = sys.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            Assert.True(store.IsTileOccupied(1, 1));
            Assert.False(store.IsTileOccupied(7, 8));
            float cost = sys.RelocateTower(id, 7, 8, 0);
            Assert.True(cost > 0f);
            // Old tile freed, new tile claimed
            Assert.False(store.IsTileOccupied(1, 1));
            Assert.True(store.IsTileOccupied(7, 8));
        }

        [Fact] public void RelocateTower_RejectsOccupiedDestination()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            store.SetPlayerGold(0, 1000f);
            int a = sys.PlaceTower(0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            int b = sys.PlaceTower(3, 3, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(a >= 0 && b >= 0);
            // Try to move tower b onto tower a's tile — should fail
            float cost = sys.RelocateTower(b, 0, 0, 0);
            Assert.Equal(0f, cost);
            // Both tiles still occupied at their original spots
            Assert.True(store.IsTileOccupied(0, 0));
            Assert.True(store.IsTileOccupied(3, 3));
        }

        [Fact] public void ResizeTileOccupancy_ResizesAndClears()
        {
            var store = new ComponentStore();
            // Mark a tile in the default 10x20 grid
            store.SetTileOccupied(2, 3, true);
            Assert.True(store.IsTileOccupied(2, 3));
            // Resize to 5x5 — old tile (2,3) is still inside, but the cache was cleared
            store.ResizeTileOccupancy(5, 5);
            Assert.Equal(5, store.TileOccupiedWidth);
            Assert.Equal(5, store.TileOccupiedHeight);
            Assert.False(store.IsTileOccupied(2, 3));
            // Out-of-bounds now returns false (cache shrank)
            Assert.False(store.IsTileOccupied(7, 7));
        }
    }
}

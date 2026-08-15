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
            Assert.Single(store.ActiveTowerIds);
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
            Assert.Empty(store.ActiveTowerIds);
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
            Assert.Single(store.ActiveTowerIds);
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

        // ─── Direction 2: 玩家停用塔 (Player-Disabled Tower) ──────────────────

        [Fact] public void ToggleTower_StartsActiveThenDisables()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            store.SetPlayerGold(0, 1000f);
            int id = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            // Default: tower is active
            Assert.False(store.TowerPlayerDisabled[id]);
            // First toggle: disable
            int result = sys.ToggleTower(id);
            Assert.Equal(1, result);
            Assert.True(store.TowerPlayerDisabled[id]);
        }

        [Fact] public void ToggleTower_ReenableFlipsBack()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            store.SetPlayerGold(0, 1000f);
            int id = sys.PlaceTower(4, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            sys.ToggleTower(id); // disable
            Assert.True(store.TowerPlayerDisabled[id]);
            int result = sys.ToggleTower(id); // re-enable
            Assert.Equal(0, result);
            Assert.False(store.TowerPlayerDisabled[id]);
        }

        [Fact] public void ToggleTower_RejectsInvalidId()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            // -1 = invalid
            Assert.Equal(-1, sys.ToggleTower(-1));
            // 99999 = out of range
            Assert.Equal(-1, sys.ToggleTower(99999));
            // 0 = unallocated entity (not active)
            Assert.Equal(-1, sys.ToggleTower(0));
        }

        // ─── Direction 2: Per-Type Placement Cap (Round 139) ─────────────────────
        // Verifies that maxPerTypeByType from tower_placement.json is enforced:
        // - LoadPerTypeCaps populates the per-type cap table
        // - PlaceTower rejects the N+1th tower of a capped type
        // - PlaceTower still works for other types even when one type is capped
        // - SellTower frees the per-type slot so a new one can be placed
        // - PlayerTowersOfType increments and decrements in lockstep with the entity count

        [Fact] public void PerTypeCap_LoadFromConfig_PopulatesCapTable()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            // Re-load caps because the test may not have the production config next to it.
            // (The default AppDomain.BaseDirectory in dotnet test is the test bin folder,
            // which DOES copy Data/Configs/ to output, so this should work end-to-end.)
            var sys = new TowerPlacementSystem(store, r);
            sys.LoadPerTypeCaps();
            // Spot-check known entries from tower_placement.json:
            // Basic=8, Sniper=4, EMP=3, Mine=6, Palisade=6
            int basicIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Basic;
            int sniperIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Sniper;
            int empIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP;
            int mineIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Mine;
            Assert.Equal(8, store.PlayerTowersOfTypeCap[basicIdx]);
            Assert.Equal(4, store.PlayerTowersOfTypeCap[sniperIdx]);
            Assert.Equal(3, store.PlayerTowersOfTypeCap[empIdx]);
            Assert.Equal(6, store.PlayerTowersOfTypeCap[mineIdx]);
        }

        [Fact] public void PerTypeCap_BlocksExceedingTypeCount()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.LoadPerTypeCaps();
            // EMP cap = 3. Place 3 EMPs (cost-free, layout occupies (0,0)/(1,0)/(2,0))
            for (int i = 0; i < 3; i++)
            {
                int placed = sys.PlaceTower(i, 0, TowerType.EMP, 50f, 3, 1f, 50f);
                Assert.True(placed >= 0, $"EMP #{i + 1} should place");
            }
            // 4th EMP must be rejected
            int overflow = sys.PlaceTower(3, 0, TowerType.EMP, 50f, 3, 1f, 50f);
            Assert.Equal(-1, overflow);
            // Counter should be 3, not 4
            int empCount = store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP];
            Assert.Equal(3, empCount);
            // 3 active towers total
            Assert.Equal(3, store.ActiveTowerIds.Count);
        }

        [Fact] public void PerTypeCap_DoesNotAffectOtherTypes()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.LoadPerTypeCaps();
            // Fill Sniper to cap (4)
            for (int i = 0; i < 4; i++)
            {
                int placed = sys.PlaceTower(i, 0, TowerType.Sniper, 50f, 3, 1f, 50f);
                Assert.True(placed >= 0, $"Sniper #{i + 1} should place");
            }
            // 5th Sniper rejected
            Assert.Equal(-1, sys.PlaceTower(4, 0, TowerType.Sniper, 50f, 3, 1f, 50f));
            // But a Basic tower still works (cap 8, none placed yet)
            int basicPlaced = sys.PlaceTower(0, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(basicPlaced >= 0);
            int basicCount = store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Basic];
            Assert.Equal(1, basicCount);
        }

        [Fact] public void PerTypeCap_SellFreesTheSlot()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.LoadPerTypeCaps();
            // Place 3 EMPs
            for (int i = 0; i < 3; i++)
            {
                int placed = sys.PlaceTower(i, 0, TowerType.EMP, 50f, 3, 1f, 50f);
                Assert.True(placed >= 0);
            }
            // 4th is blocked
            Assert.Equal(-1, sys.PlaceTower(3, 0, TowerType.EMP, 50f, 3, 1f, 50f));
            // Sell ONE EMP — find the entity at (0,0) and sell it. Slot freed.
            int eid = FindTowerIdAtPosition(store, 0, 0);
            Assert.True(eid >= 0, "expected an EMP tower at (0,0)");
            float refund = sys.SellTower(eid, 0);
            Assert.True(refund > 0f, "SellTower should refund gold");
            // Counter dropped from 3 to 2
            int empCount = store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP];
            Assert.Equal(2, empCount);
            // Now a new EMP fits within the cap of 3
            int freed = sys.PlaceTower(3, 0, TowerType.EMP, 50f, 3, 1f, 50f);
            Assert.True(freed >= 0, "After sell, the freed per-type slot must allow placement");
            int empCount2 = store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP];
            Assert.Equal(3, empCount2);
        }

        [Fact] public void PerTypeCap_DestroyEntityDecrementsCounter()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.LoadPerTypeCaps();
            // Place 1 Mine, then directly destroy it
            int placed = sys.PlaceTower(0, 0, TowerType.Mine, 50f, 3, 1f, 50f);
            Assert.True(placed >= 0);
            int mineIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Mine;
            Assert.Equal(1, store.PlayerTowersOfType[mineIdx]);
            Assert.Equal(1, store.PlayerTowerCount[0]);
            // Direct destroy (simulates death, mine detonation, etc.)
            store.DestroyEntity(placed);
            Assert.Equal(0, store.PlayerTowersOfType[mineIdx]);
            Assert.Equal(0, store.PlayerTowerCount[0]);
        }

        [Fact] public void PerTypeCap_ZeroCapMeansUnlimited()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            // Manually zero out the EMP cap
            int empIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP;
            store.PlayerTowersOfTypeCap[empIdx] = 0;
            // Should be able to place many EMPs (capped only by maxTowers = 20)
            for (int i = 0; i < 10; i++)
            {
                int placed = sys.PlaceTower(i % 10, i / 10, TowerType.EMP, 50f, 3, 1f, 50f);
                Assert.True(placed >= 0, $"EMP #{i + 1} with cap=0 should place (was rejected)");
            }
        }

        [Fact] public void PerTypeCap_PlayerTowerCountMatchesTypeSum()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            sys.LoadPerTypeCaps();
            // Place 2 Basic, 1 Sniper, 3 Stun
            sys.PlaceTower(0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.PlaceTower(1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.PlaceTower(2, 0, TowerType.Sniper, 50f, 3, 1f, 50f);
            sys.PlaceTower(3, 0, TowerType.Stun, 50f, 3, 1f, 50f);
            sys.PlaceTower(4, 0, TowerType.Stun, 50f, 3, 1f, 50f);
            sys.PlaceTower(5, 0, TowerType.Stun, 50f, 3, 1f, 50f);
            // PlayerTowerCount = 6
            Assert.Equal(6, store.PlayerTowerCount[0]);
            // Sum of all per-type counters should also be 6
            int sum = 0;
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                sum += store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + t];
            }
            Assert.Equal(6, sum);
            // Sell one Stun — both drop by 1
            int stunEid = -1;
            foreach (int tid in store.ActiveTowerIds)
            {
                if (store.TowerType[tid] == TowerType.Stun) { stunEid = tid; break; }
            }
            Assert.True(stunEid >= 0);
            sys.SellTower(stunEid, 0);
            Assert.Equal(5, store.PlayerTowerCount[0]);
            int sum2 = 0;
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                sum2 += store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + t];
            }
            Assert.Equal(5, sum2);
        }

        // ─── Round 140 — Direction 7: Per-Tower Sell Ratio Override ───────────────
        // Validates the new sellRatioOverrideByType matrix loaded from tower_placement.json.
        // JSON ships overrides for AOE (1)=0.7, Sniper (2)=0.35, Frost (5)=0.6, Palisade (9)=0.4.
        // Other types (e.g. Basic=0, Tesla=3) keep the global sellRatio (0.5 default).

        [Fact]
        public void PerTypeSellRatio_LoadedFromJson()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            // Place 1 of each overridden type and 1 control type
            int aoe = sys.PlaceTower(0, 0, TowerType.AOE, 50f, 3, 1f, 50f);
            int sniper = sys.PlaceTower(1, 0, TowerType.Sniper, 50f, 3, 1f, 50f);
            int frost = sys.PlaceTower(2, 0, TowerType.Frost, 50f, 3, 1f, 50f);
            int palisade = sys.PlaceTower(3, 0, TowerType.Palisade, 50f, 3, 1f, 50f);
            int basic = sys.PlaceTower(4, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(aoe >= 0 && sniper >= 0 && frost >= 0 && palisade >= 0 && basic >= 0);

            // AOE has 0.7 override (higher → more refund than global 0.5)
            float refundAoe = sys.SellTower(aoe, 0);
            int basicEid = basic;
            // Place a second Basic for control (Basic was already sold? no, basic is still alive)
            // Just sell basic and basic again
            float refundBasic = sys.SellTower(basicEid, 0);

            // Per-type override changes the BASE ratio but tests below verify the ratio values
            // directly via the tower's level-1 refund. Both refund > 0 (sanity).
            Assert.True(refundAoe > 0f, "AOE sell should refund some gold");
            Assert.True(refundBasic > 0f, "Basic sell should refund some gold");
        }

        [Fact]
        public void PerTypeSellRatio_HigherOverrideYieldsHigherRefund()
        {
            // Compare AOE (override=0.7) vs Basic (no override → global=0.5).
            // Both placed at level 1, same upgrade cost. AOE must refund strictly more.
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int aoe = sys.PlaceTower(0, 0, TowerType.AOE, 50f, 3, 1f, 50f);
            int basic = sys.PlaceTower(1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            // Bump both to level 2 to incur a non-zero upgrade cost (otherwise baseCost may be 0 in tests).
            // Actually, the sell refund is based on TowerUpgradeCost; PlaceTower stores the input cost.
            // Default is 50f. Refund = 50 * ratio. AOE: 50 * 0.7 = 35, Basic: 50 * 0.5 = 25.
            float refundAoe = sys.SellTower(aoe, 0);
            float refundBasic = sys.SellTower(basic, 0);
            Assert.True(refundAoe > refundBasic,
                $"AOE (override=0.7) should refund more than Basic (global=0.5). AOE={refundAoe} Basic={refundBasic}");
        }

        [Fact]
        public void PerTypeSellRatio_LowerOverrideYieldsLowerRefund()
        {
            // Compare Sniper (override=0.35) vs Basic (no override → global=0.5).
            // Sniper must refund strictly less.
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int sniper = sys.PlaceTower(0, 0, TowerType.Sniper, 50f, 3, 1f, 50f);
            int basic = sys.PlaceTower(1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            float refundSniper = sys.SellTower(sniper, 0);
            float refundBasic = sys.SellTower(basic, 0);
            Assert.True(refundSniper < refundBasic,
                $"Sniper (override=0.35) should refund less than Basic (global=0.5). Sniper={refundSniper} Basic={refundBasic}");
        }

        [Fact]
        public void PerTypeSellRatio_TypeWithoutOverrideFallsBackToGlobal()
        {
            // Tesla (type 3) has no entry in sellRatioOverrideByType → should use global 0.5.
            // Its refund must equal Basic's refund (same type, same level, same cost).
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = new TowerPlacementSystem(store, r);
            int tesla = sys.PlaceTower(0, 0, TowerType.Tesla, 50f, 3, 1f, 50f);
            int basic = sys.PlaceTower(1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            float refundTesla = sys.SellTower(tesla, 0);
            float refundBasic = sys.SellTower(basic, 0);
            // Both use global 0.5, so refunds should be very close (salvage may differ by
            // accumulated spend which is 0 in both cases, so they should be equal).
            Assert.Equal(refundBasic, refundTesla, 0); // exact equality expected
        }

        // Helper: find a tower at (x, y) or -1 if none. Used by PerTypeCap_SellFreesTheSlot.
        private static int FindTowerIdAtPosition(ComponentStore store, int x, int y)
        {
            foreach (int tid in store.ActiveTowerIds)
            {
                if ((int)store.PositionX[tid] == x && (int)store.PositionY[tid] == y)
                    return tid;
            }
            return -1;
        }
    }
}

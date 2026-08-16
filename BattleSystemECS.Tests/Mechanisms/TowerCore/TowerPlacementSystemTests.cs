using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.TowerCore
{
    public class TowerPlacementSystemTests
    {
        /// <summary>
        /// 构造 TowerPlacementSystem 后把所有 per-type cap 置 0（0 = unlimited）。
        /// 单元测试不依赖 tower_placement.json 里任何具体 cap 值；需要测试 cap
        /// 机制的用例会在该方法返回后显式写入自己的 cap。
        /// </summary>
        private static TowerPlacementSystem MakeSystem(ComponentStore store, MockRenderer r)
        {
            var sys = new TowerPlacementSystem(store, r);
            TestWorld.DisablePerTypeTowerCaps(store);
            return sys;
        }

        // ─── Bug#31: PlaceTower 在 CreateEntity()==-1 时失败 ──────────────────

        [Fact] public void PlaceTower_FailsWhenEntityPoolExhausted()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            while (store.CreateEntity() != -1) { /* exhaust */ }
            var sys = MakeSystem(store, r);
            int result = sys.PlaceTower(5, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(-1, result);
        }

        // ─── 幽灵预览 (Ghost Placement) ────────────────────────────────────

        [Fact] public void PreviewPlacement_ValidPositionReturnsTrue()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            bool valid = sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(valid);
            Assert.True(sys.HasActivePreview);
            Assert.True(sys.LastPreviewValid);
        }

        [Fact] public void PreviewPlacement_OutOfBoundsReturnsFalse()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            bool valid = sys.PreviewPlacement(-1, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.False(valid);
            Assert.True(sys.HasActivePreview);
            Assert.False(sys.LastPreviewValid);
        }

        [Fact] public void PreviewPlacement_OnOccupiedCellReturnsFalse()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            int placed = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(placed >= 0);
            bool valid = sys.PreviewPlacement(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.False(valid);
        }

        [Fact] public void PreviewPlacement_DoesNotConsumeEntity()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            int aliveBefore = store.ActiveTowerIds.Count;
            sys.PreviewPlacement(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.PreviewPlacement(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(aliveBefore, store.ActiveTowerIds.Count);
        }

        [Fact] public void CancelPreview_ClearsState()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(sys.HasActivePreview);
            sys.CancelPreview();
            Assert.False(sys.HasActivePreview);
        }

        [Fact] public void ConfirmPlacement_NoActivePreviewReturnsMinusOne()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            int id = sys.ConfirmPlacement();
            Assert.Equal(-1, id);
        }

        [Fact] public void ConfirmPlacement_AfterValidPreviewCreatesTower()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
            sys.PreviewPlacement(-1, -1, TowerType.Basic, 50f, 3, 1f, 50f);
            int id = sys.ConfirmPlacement();
            Assert.Equal(-1, id);
        }

        // ─── Build Queue (BuildPhase 预排多塔位) ────────────────────────────────

        [Fact] public void EnqueueBuild_AppendsInOrder()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
            Assert.False(store.IsTileOccupied(3, 4));
            int id = sys.PlaceTower(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            Assert.True(store.IsTileOccupied(3, 4));
        }

        [Fact] public void PlaceTower_RejectsAlreadyOccupiedTile()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
            // -1 = invalid
            Assert.Equal(-1, sys.ToggleTower(-1));
            // 99999 = out of range
            Assert.Equal(-1, sys.ToggleTower(99999));
            // 0 = unallocated entity (not active)
            Assert.Equal(-1, sys.ToggleTower(0));
        }

        // ─── Direction 2: Per-Type Placement Cap (Round 139) ─────────────────────
        // 纯代码机制测试：cap 值由测试代码写入 PlayerTowersOfTypeCap，不读取 tower_placement.json。
        // - PlaceTower rejects the N+1th tower of a capped type
        // - PlaceTower still works for other types even when one type is capped
        // - SellTower frees the per-type slot so a new one can be placed
        // - PlayerTowersOfType increments and decrements in lockstep with the entity count

        [Fact] public void PerTypeCap_BlocksExceedingTypeCount()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            // 代码注入 EMP cap = 3
            store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP] = 3;
            // Place 3 EMPs (cost-free, layout occupies (0,0)/(1,0)/(2,0))
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
            var sys = MakeSystem(store, r);
            // 代码注入 Sniper cap = 4；Basic cap = 0（不限）
            store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Sniper] = 4;
            store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Basic] = 0;
            // Fill Sniper to cap (4)
            for (int i = 0; i < 4; i++)
            {
                int placed = sys.PlaceTower(i, 0, TowerType.Sniper, 50f, 3, 1f, 50f);
                Assert.True(placed >= 0, $"Sniper #{i + 1} should place");
            }
            // 5th Sniper rejected
            Assert.Equal(-1, sys.PlaceTower(4, 0, TowerType.Sniper, 50f, 3, 1f, 50f));
            // But a Basic tower still works (cap 0 = unlimited)
            int basicPlaced = sys.PlaceTower(0, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(basicPlaced >= 0);
            int basicCount = store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Basic];
            Assert.Equal(1, basicCount);
        }

        [Fact] public void PerTypeCap_SellFreesTheSlot()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var sys = MakeSystem(store, r);
            // 代码注入 EMP cap = 3
            store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP] = 3;
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
            sys.SellTower(eid, 0);
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
            var sys = MakeSystem(store, r);
            // 代码注入 Mine cap = 0（不限），与 JSON 数据解耦
            store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Mine] = 0;
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
            var sys = MakeSystem(store, r);
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
            var sys = MakeSystem(store, r);
            // 纯代码机制：清空全部 per-type cap（0 = 不限），不依赖任何 JSON 数据
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + t] = 0;
            }
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
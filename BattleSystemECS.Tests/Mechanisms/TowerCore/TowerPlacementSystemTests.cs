using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.TowerCore
{
    public class TowerPlacementSystemTests : BattleTestBase
    {
        /// <summary>
        /// 取基类懒加载的 Placement（复用同一个 Store/Renderer），然后把所有 per-type cap
        /// 置 0（0 = unlimited）。先触发 Placement 构造（会读取真实 JSON 配置），再清空 cap，
        /// 保持原“构造系统 → 清空 JSON 载入 cap”的顺序语义。
        /// 单元测试不依赖 tower_placement.json 里任何具体 cap 值；需要测试 cap
        /// 机制的用例会在该方法返回后显式写入自己的 cap。
        /// </summary>
        private TowerPlacementSystem MakeSystem()
        {
            var sys = Placement; // 触发懒加载，复用基类 Store/Renderer
            DisableTowerCaps();
            return sys;
        }

        // ─── Bug#31: PlaceTower 在 CreateEntity()==-1 时失败 ──────────────────

        [Fact] public void PlaceTower_FailsWhenEntityPoolExhausted()
        {
            var store = new ComponentStore(); // 保留独立 store：填满实体槽场景，不能用基类 Store，否则会污染后续断言
            while (store.CreateEntity() != -1) { /* exhaust */ }
            var sys = new TowerPlacementSystem(store, Renderer); // 保留独立系统：必须绑定被填满的独立 store，不能复用基类 Placement
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + t] = 0; // 独立 store 上等价于 DisableTowerCaps()
            }
            int result = sys.PlaceTower(5, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(-1, result);
        }

        // ─── 幽灵预览 (Ghost Placement) ────────────────────────────────────

        [Fact] public void PreviewPlacement_ValidPositionReturnsTrue()
        {
            var sys = MakeSystem();
            bool valid = sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(valid);
            Assert.True(sys.HasActivePreview);
            Assert.True(sys.LastPreviewValid);
        }

        [Fact] public void PreviewPlacement_OutOfBoundsReturnsFalse()
        {
            var sys = MakeSystem();
            bool valid = sys.PreviewPlacement(-1, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.False(valid);
            Assert.True(sys.HasActivePreview);
            Assert.False(sys.LastPreviewValid);
        }

        [Fact] public void PreviewPlacement_OnOccupiedCellReturnsFalse()
        {
            var sys = MakeSystem();
            int placed = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(placed >= 0);
            bool valid = sys.PreviewPlacement(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.False(valid);
        }

        [Fact] public void PreviewPlacement_DoesNotConsumeEntity()
        {
            var sys = MakeSystem();
            int aliveBefore = Store.ActiveTowerIds.Count;
            sys.PreviewPlacement(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.PreviewPlacement(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(aliveBefore, Store.ActiveTowerIds.Count);
        }

        [Fact] public void CancelPreview_ClearsState()
        {
            var sys = MakeSystem();
            sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(sys.HasActivePreview);
            sys.CancelPreview();
            Assert.False(sys.HasActivePreview);
        }

        [Fact] public void ConfirmPlacement_NoActivePreviewReturnsMinusOne()
        {
            var sys = MakeSystem();
            int id = sys.ConfirmPlacement();
            Assert.Equal(-1, id);
        }

        [Fact] public void ConfirmPlacement_AfterValidPreviewCreatesTower()
        {
            var sys = MakeSystem();
            sys.PreviewPlacement(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            int id = sys.ConfirmPlacement();
            Assert.True(id >= 0);
            Assert.False(sys.HasActivePreview);
            Assert.Single(Store.ActiveTowerIds);
            Assert.Equal(3, (int)Store.PositionX[id]);
            Assert.Equal(4, (int)Store.PositionY[id]);
        }

        [Fact] public void ConfirmPlacement_AfterInvalidPreviewFails()
        {
            var sys = MakeSystem();
            sys.PreviewPlacement(-1, -1, TowerType.Basic, 50f, 3, 1f, 50f);
            int id = sys.ConfirmPlacement();
            Assert.Equal(-1, id);
        }

        // ─── Build Queue (BuildPhase 预排多塔位) ────────────────────────────────

        [Fact] public void EnqueueBuild_AppendsInOrder()
        {
            var sys = MakeSystem();
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
            var sys = MakeSystem();
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
            var sys = MakeSystem();
            Assert.False(sys.EnqueueBuild(0, -1, 5, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.False(sys.EnqueueBuild(0, 5, -1, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.False(sys.EnqueueBuild(0, 10, 5, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.False(sys.EnqueueBuild(0, 5, 20, TowerType.Basic, 50f, 3, 1f, 50f));
            Assert.Equal(0, sys.GetBuildQueueCount(0));
        }

        [Fact] public void ClearBuildQueue_EmptiesSlots()
        {
            var sys = MakeSystem();
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
            var sys = MakeSystem();
            // Grant gold for 3 placements
            Store.SetPlayerGold(0, 1000f);
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
            Assert.Equal(3, Store.ActiveTowerIds.Count);
            // Gold deducted for 3 placements (3 × 50 = 150)
            Assert.Equal(1000f - 150f, Store.GetPlayerGold(0));
        }

        [Fact] public void ProcessBuildQueue_SkipsInsufficientGold()
        {
            var sys = MakeSystem();
            // Not enough gold for even 1 placement (cost 50)
            Store.SetPlayerGold(0, 30f);
            sys.EnqueueBuild(0, 0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            // Drain — should skip the slot (no gold)
            int drained = sys.ProcessBuildQueue(0, 0.2f);
            Assert.Equal(0, drained);
            // The slot was skipped (count = 0 after CompactQueue)
            Assert.Equal(0, sys.GetBuildQueueCount(0));
            // No tower was placed
            Assert.Empty(Store.ActiveTowerIds);
            // Gold was NOT deducted (we pre-deducted only on success)
            Assert.Equal(30f, Store.GetPlayerGold(0));
        }

        [Fact] public void ProcessBuildQueue_RespectsPacingInterval()
        {
            var sys = MakeSystem();
            Store.SetPlayerGold(0, 1000f);
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
            for (int x = 0; x < ComponentStore.TILE_GRID_DEFAULT_WIDTH; x++)
            for (int y = 0; y < ComponentStore.TILE_GRID_DEFAULT_HEIGHT; y++)
                Assert.False(Store.IsTileOccupied(x, y));
        }

        [Fact] public void TileCache_OutOfBoundsReturnsFalse()
        {
            Assert.False(Store.IsTileOccupied(-1, 5));
            Assert.False(Store.IsTileOccupied(5, -1));
            Assert.False(Store.IsTileOccupied(ComponentStore.TILE_GRID_DEFAULT_WIDTH, 5));
            Assert.False(Store.IsTileOccupied(5, ComponentStore.TILE_GRID_DEFAULT_HEIGHT));
        }

        [Fact] public void PlaceTower_MarksTileOccupied()
        {
            var sys = MakeSystem();
            Assert.False(Store.IsTileOccupied(3, 4));
            int id = sys.PlaceTower(3, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            Assert.True(Store.IsTileOccupied(3, 4));
        }

        [Fact] public void PlaceTower_RejectsAlreadyOccupiedTile()
        {
            var sys = MakeSystem();
            int first = sys.PlaceTower(2, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(first >= 0);
            // Second attempt at the same tile should fail via the cache
            int second = sys.PlaceTower(2, 5, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.Equal(-1, second);
            Assert.Single(Store.ActiveTowerIds);
        }

        [Fact] public void SellTower_ReleasesTile()
        {
            var sys = MakeSystem();
            int id = sys.PlaceTower(5, 6, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            Assert.True(Store.IsTileOccupied(5, 6));
            // Give player 0 enough gold for the sell refund (no gold needed for sell)
            sys.SellTower(id, 0);
            Assert.False(Store.IsTileOccupied(5, 6));
            // The tile can now accept a new tower
            int replacement = sys.PlaceTower(5, 6, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(replacement >= 0);
            Assert.True(Store.IsTileOccupied(5, 6));
        }

        [Fact] public void RelocateTower_UpdatesTileCache()
        {
            var sys = MakeSystem();
            Store.SetPlayerGold(0, 1000f);
            int id = sys.PlaceTower(1, 1, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            Assert.True(Store.IsTileOccupied(1, 1));
            Assert.False(Store.IsTileOccupied(7, 8));
            float cost = sys.RelocateTower(id, 7, 8, 0);
            Assert.True(cost > 0f);
            // Old tile freed, new tile claimed
            Assert.False(Store.IsTileOccupied(1, 1));
            Assert.True(Store.IsTileOccupied(7, 8));
        }

        [Fact] public void RelocateTower_RejectsOccupiedDestination()
        {
            var sys = MakeSystem();
            Store.SetPlayerGold(0, 1000f);
            int a = sys.PlaceTower(0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            int b = sys.PlaceTower(3, 3, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(a >= 0 && b >= 0);
            // Try to move tower b onto tower a's tile — should fail
            float cost = sys.RelocateTower(b, 0, 0, 0);
            Assert.Equal(0f, cost);
            // Both tiles still occupied at their original spots
            Assert.True(Store.IsTileOccupied(0, 0));
            Assert.True(Store.IsTileOccupied(3, 3));
        }

        [Fact] public void ResizeTileOccupancy_ResizesAndClears()
        {
            // Mark a tile in the default 10x20 grid
            Store.SetTileOccupied(2, 3, true);
            Assert.True(Store.IsTileOccupied(2, 3));
            // Resize to 5x5 — old tile (2,3) is still inside, but the cache was cleared
            Store.ResizeTileOccupancy(5, 5);
            Assert.Equal(5, Store.TileOccupiedWidth);
            Assert.Equal(5, Store.TileOccupiedHeight);
            Assert.False(Store.IsTileOccupied(2, 3));
            // Out-of-bounds now returns false (cache shrank)
            Assert.False(Store.IsTileOccupied(7, 7));
        }

        // ─── Direction 2: 玩家停用塔 (Player-Disabled Tower) ──────────────────

        [Fact] public void ToggleTower_StartsActiveThenDisables()
        {
            var sys = MakeSystem();
            Store.SetPlayerGold(0, 1000f);
            int id = sys.PlaceTower(2, 2, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            // Default: tower is active
            Assert.False(Store.TowerPlayerDisabled[id]);
            // First toggle: disable
            int result = sys.ToggleTower(id);
            Assert.Equal(1, result);
            Assert.True(Store.TowerPlayerDisabled[id]);
        }

        [Fact] public void ToggleTower_ReenableFlipsBack()
        {
            var sys = MakeSystem();
            Store.SetPlayerGold(0, 1000f);
            int id = sys.PlaceTower(4, 4, TowerType.Basic, 50f, 3, 1f, 50f);
            Assert.True(id >= 0);
            sys.ToggleTower(id); // disable
            Assert.True(Store.TowerPlayerDisabled[id]);
            int result = sys.ToggleTower(id); // re-enable
            Assert.Equal(0, result);
            Assert.False(Store.TowerPlayerDisabled[id]);
        }

        [Fact] public void ToggleTower_RejectsInvalidId()
        {
            var sys = MakeSystem();
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
            var sys = MakeSystem();
            // 代码注入 EMP cap = 3
            Store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP] = 3;
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
            int empCount = Store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP];
            Assert.Equal(3, empCount);
            // 3 active towers total
            Assert.Equal(3, Store.ActiveTowerIds.Count);
        }

        [Fact] public void PerTypeCap_DoesNotAffectOtherTypes()
        {
            var sys = MakeSystem();
            // 代码注入 Sniper cap = 4；Basic cap = 0（不限）
            Store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Sniper] = 4;
            Store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Basic] = 0;
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
            int basicCount = Store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Basic];
            Assert.Equal(1, basicCount);
        }

        [Fact] public void PerTypeCap_SellFreesTheSlot()
        {
            var sys = MakeSystem();
            // 代码注入 EMP cap = 3
            Store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP] = 3;
            // Place 3 EMPs
            for (int i = 0; i < 3; i++)
            {
                int placed = sys.PlaceTower(i, 0, TowerType.EMP, 50f, 3, 1f, 50f);
                Assert.True(placed >= 0);
            }
            // 4th is blocked
            Assert.Equal(-1, sys.PlaceTower(3, 0, TowerType.EMP, 50f, 3, 1f, 50f));
            // Sell ONE EMP — find the entity at (0,0) and sell it. Slot freed.
            int eid = FindTowerAt(0, 0);
            Assert.True(eid >= 0, "expected an EMP tower at (0,0)");
            sys.SellTower(eid, 0);
            // Counter dropped from 3 to 2
            int empCount = Store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP];
            Assert.Equal(2, empCount);
            // Now a new EMP fits within the cap of 3
            int freed = sys.PlaceTower(3, 0, TowerType.EMP, 50f, 3, 1f, 50f);
            Assert.True(freed >= 0, "After sell, the freed per-type slot must allow placement");
            int empCount2 = Store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP];
            Assert.Equal(3, empCount2);
        }

        [Fact] public void PerTypeCap_DestroyEntityDecrementsCounter()
        {
            var sys = MakeSystem();
            // 代码注入 Mine cap = 0（不限），与 JSON 数据解耦
            Store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Mine] = 0;
            // Place 1 Mine, then directly destroy it
            int placed = sys.PlaceTower(0, 0, TowerType.Mine, 50f, 3, 1f, 50f);
            Assert.True(placed >= 0);
            int mineIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.Mine;
            Assert.Equal(1, Store.PlayerTowersOfType[mineIdx]);
            Assert.Equal(1, Store.PlayerTowerCount[0]);
            // Direct destroy (simulates death, mine detonation, etc.)
            Store.DestroyEntity(placed);
            Assert.Equal(0, Store.PlayerTowersOfType[mineIdx]);
            Assert.Equal(0, Store.PlayerTowerCount[0]);
        }

        [Fact] public void PerTypeCap_ZeroCapMeansUnlimited()
        {
            var sys = MakeSystem();
            // Manually zero out the EMP cap
            int empIdx = 0 * ComponentStore.MAX_TOWER_TYPES + (int)TowerType.EMP;
            Store.PlayerTowersOfTypeCap[empIdx] = 0;
            // Should be able to place many EMPs (capped only by maxTowers = 20)
            for (int i = 0; i < 10; i++)
            {
                int placed = sys.PlaceTower(i % 10, i / 10, TowerType.EMP, 50f, 3, 1f, 50f);
                Assert.True(placed >= 0, $"EMP #{i + 1} with cap=0 should place (was rejected)");
            }
        }

        [Fact] public void PerTypeCap_PlayerTowerCountMatchesTypeSum()
        {
            var sys = MakeSystem();
            // 纯代码机制：清空全部 per-type cap（0 = 不限），不依赖任何 JSON 数据
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                Store.PlayerTowersOfTypeCap[0 * ComponentStore.MAX_TOWER_TYPES + t] = 0;
            }
            // Place 2 Basic, 1 Sniper, 3 Stun
            sys.PlaceTower(0, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.PlaceTower(1, 0, TowerType.Basic, 50f, 3, 1f, 50f);
            sys.PlaceTower(2, 0, TowerType.Sniper, 50f, 3, 1f, 50f);
            sys.PlaceTower(3, 0, TowerType.Stun, 50f, 3, 1f, 50f);
            sys.PlaceTower(4, 0, TowerType.Stun, 50f, 3, 1f, 50f);
            sys.PlaceTower(5, 0, TowerType.Stun, 50f, 3, 1f, 50f);
            // PlayerTowerCount = 6
            Assert.Equal(6, Store.PlayerTowerCount[0]);
            // Sum of all per-type counters should also be 6
            int sum = 0;
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                sum += Store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + t];
            }
            Assert.Equal(6, sum);
            // Sell one Stun — both drop by 1
            int stunEid = -1;
            foreach (int tid in Store.ActiveTowerIds)
            {
                if (Store.TowerType[tid] == TowerType.Stun) { stunEid = tid; break; }
            }
            Assert.True(stunEid >= 0);
            sys.SellTower(stunEid, 0);
            Assert.Equal(5, Store.PlayerTowerCount[0]);
            int sum2 = 0;
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                sum2 += Store.PlayerTowersOfType[0 * ComponentStore.MAX_TOWER_TYPES + t];
            }
            Assert.Equal(5, sum2);
        }

    }
}
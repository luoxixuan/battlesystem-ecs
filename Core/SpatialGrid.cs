using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 空间分区网格 — 将敌人按整数坐标 (gx, gy) 哈希到单元格，
    /// 支持 O(1) 范围查询替代 O(N) 全量遍历。
    /// 
    /// 使用固定尺寸 int[] 数组替代 Dictionary+List，消除每帧 GC 分配。
    /// 仅清空脏格子（上一帧实际有敌人的格子），减少 Array.Clear 开销。
    /// </summary>
    public class SpatialGrid
    {
        /// <summary>cell 大小 = 1 格 = 1 坐标系单位，与 MapSystem 网格对齐</summary>
        private const float CellSize = 1f;

        /// <summary>每个格子最多容纳的敌人数量（超出则跳过）</summary>
        private const int CellCapacity = 16;

        /// <summary>地图宽度（gx 范围）</summary>
        private int _mapWidth = 10;

        /// <summary>地图高度（gy 范围）</summary>
        private int _mapHeight = 20;

        /// <summary>
        /// 格子敌人数组。使用 flat index: index = cellIndex * CellCapacity + offset。
        /// </summary>
        private int[] _gridData;

        /// <summary>每个格子的实际敌人数量</summary>
        private int[] _cellCounts;

        /// <summary>
        /// 上一帧有敌人的格子索引列表（用于仅清脏格子）。
        /// </summary>
        private int[] _prevActiveCells;

        /// <summary>上一帧活跃格子数量</summary>
        private int _prevActiveCellCount;

        /// <summary>
        /// 本帧有敌人的格子索引（用于下一帧的脏追踪）。
        /// </summary>
        private int[] _currActiveCells;

        /// <summary>本帧活跃格子数量</summary>
        private int _currActiveCellCount;

        /// <summary>累计 overflow 次数（每帧超过 CellCapacity 的插入次数）。</summary>
        public int OverflowCount { get; private set; }

        public SpatialGrid()
        {
            Allocate(10, 20);
        }

        /// <summary>
        /// Resize the spatial grid. Must be called before any enemies are added —
        /// re-allocate discards all cell data. Call via ComponentStore.SetMapSize()
        /// during game initialization, synchronized with MapSystem.
        /// </summary>
        public void SetMapSize(int width, int height)
        {
            Allocate(width, height);
        }

        /// <summary>
        /// Incremental update — clears dirty cells and re-inserts only the given enemy IDs.
        /// Used by RebuildSpatialGrid() to achieve O(enemies) dirty-cell clearing.
        /// </summary>
        public void UpdateEnemies(ComponentStore store, IReadOnlyList<int> enemyIds)
        {
            OverflowCount = 0;

            // Phase 1: clear all cells that had enemies last frame (dirty cells)
            for (int i = 0; i < _prevActiveCellCount; i++)
            {
                int idx = _prevActiveCells[i];
                int baseOff = idx * CellCapacity;
                for (int j = 0; j < _cellCounts[idx]; j++)
                    _gridData[baseOff + j] = 0;
                _cellCounts[idx] = 0;
            }
            _currActiveCellCount = 0;

            // Phase 2: re-insert the given enemies into their current cells
            for (int i = 0; i < enemyIds.Count; i++)
            {
                int eid = enemyIds[i];
                if (!store.EnemyActive[eid]) continue;

                float x = store.PositionX[eid];
                float y = store.PositionY[eid];
                int gx = (int)x;
                int gy = (int)y;

                if (gx >= 0 && gx < _mapWidth && gy >= 0 && gy < _mapHeight)
                {
                    int cellIndex = gy * _mapWidth + gx;
                    int count = _cellCounts[cellIndex];
                    if (count < CellCapacity)
                    {
                        _gridData[cellIndex * CellCapacity + count] = eid;
                        _cellCounts[cellIndex] = count + 1;
                        if (count == 0)
                        {
                            _currActiveCells[_currActiveCellCount++] = cellIndex;
                        }
                    }
                }
            }

            // Swap: current becomes previous for next frame
            int[] tmp = _prevActiveCells;
            _prevActiveCells = _currActiveCells;
            _currActiveCells = tmp;
            _prevActiveCellCount = _currActiveCellCount;
        }

        private void Allocate(int width, int height)
        {
            _mapWidth = width;
            _mapHeight = height;
            int total = width * height;
            _gridData = new int[total * CellCapacity];
            _cellCounts = new int[total];
            _prevActiveCells = new int[total];
            _currActiveCells = new int[total];
        }

        /// <summary>
        /// 整体重建网格 — O(enemies)。
        /// 必须在帧开始时调用一次。
        /// </summary>
        public void Rebuild(ComponentStore store, IReadOnlyList<int> enemyIds)
        {
            int total = _mapWidth * _mapHeight;

            // 仅清空上一帧有敌人的格子（而非全量 Array.Clear）
            for (int i = 0; i < _prevActiveCellCount; i++)
            {
                int idx = _prevActiveCells[i];
                int baseOff = idx * CellCapacity;
                for (int j = 0; j < _cellCounts[idx]; j++)
                    _gridData[baseOff + j] = 0;
                _cellCounts[idx] = 0;
            }

            // 重建本帧网格
            _currActiveCellCount = 0;

            for (int i = 0; i < enemyIds.Count; i++)
            {
                int eid = enemyIds[i];
                if (!store.EnemyActive[eid]) continue;

                int gx = (int)store.PositionX[eid];
                int gy = (int)store.PositionY[eid];

                if (gx < 0 || gx >= _mapWidth || gy < 0 || gy >= _mapHeight) continue;

                int cellIndex = gy * _mapWidth + gx;
                int count = _cellCounts[cellIndex];
                if (count < CellCapacity)
                {
                    _gridData[cellIndex * CellCapacity + count] = eid;
                    _cellCounts[cellIndex] = count + 1;

                    // 首次向该格子添加敌人：记录到 _currActiveCells
                    if (count == 0)
                    {
                        _currActiveCells[_currActiveCellCount++] = cellIndex;
                    }
                }
                else
                {
                    // Overflow: 同一格堆叠超过 CellCapacity（静默丢敌 = 战斗语义改变）
                    // 记录之；调试/压测时可断言此值为 0
                    OverflowCount++;
                }
            }

            // 交换：当前帧的活跃格子成为下一帧的"上一帧活跃格子"
            int[] tmp = _prevActiveCells;
            _prevActiveCells = _currActiveCells;
            _currActiveCells = tmp;
            _prevActiveCellCount = _currActiveCellCount;
        }

        /// <summary>
        /// 查询指定格子内的所有敌人 ID — O(1) 索引。
        /// </summary>
        public void GetEnemiesAtPoint(float x, float y, List<int> output)
        {
            int gx = (int)Math.Floor(x);
            int gy = (int)Math.Floor(y);
            if (gx < 0 || gx >= _mapWidth || gy < 0 || gy >= _mapHeight) return;

            int cellIndex = gy * _mapWidth + gx;
            int count = _cellCounts[cellIndex];
            if (count == 0) return;

            int baseOffset = cellIndex * CellCapacity;
            for (int i = 0; i < count; i++)
                output.Add(_gridData[baseOffset + i]);
        }

        /// <summary>
        /// Query enemies in range — writes to a pre-allocated array buffer (zero-allocation overload).
        /// count is incremented for each enemy found. Buffer must be large enough.
        /// </summary>
        public void GetEnemiesInRange(ComponentStore store, float towerX, float towerY,
            int range, int[] buffer, ref int count)
        {
            int centerGx = (int)Math.Floor(towerX);
            int centerGy = (int)Math.Floor(towerY);
            float rangeSq = range * range;

            for (int dx = -range; dx <= range; dx++)
            {
                int gx = centerGx + dx;
                if (gx < 0 || gx >= _mapWidth) continue;

                for (int dy = -range; dy <= range; dy++)
                {
                    int gy = centerGy + dy;
                    if (gy < 0 || gy >= _mapHeight) continue;

                    int cellIndex = gy * _mapWidth + gx;
                    int cellCount = _cellCounts[cellIndex];
                    if (cellCount == 0) continue;

                    int baseOffset = cellIndex * CellCapacity;
                    for (int i = 0; i < cellCount; i++)
                    {
                        int eid = _gridData[baseOffset + i];
                        float ex = store.PositionX[eid];
                        float ey = store.PositionY[eid];
                        float ddx = ex - towerX;
                        float ddy = ey - towerY;
                        float distSq = ddx * ddx + ddy * ddy;
                        if (distSq <= rangeSq)
                        {
                            buffer[count++] = eid;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Query enemies in range — writes to a List (backward-compatible overload).
        /// </summary>
        public void GetEnemiesInRange(ComponentStore store, float towerX, float towerY,
            int range, List<int> output)
        {
            int centerGx = (int)Math.Floor(towerX);
            int centerGy = (int)Math.Floor(towerY);
            float rangeSq = range * range;

            for (int dx = -range; dx <= range; dx++)
            {
                int gx = centerGx + dx;
                if (gx < 0 || gx >= _mapWidth) continue;

                for (int dy = -range; dy <= range; dy++)
                {
                    int gy = centerGy + dy;
                    if (gy < 0 || gy >= _mapHeight) continue;

                    int cellIndex = gy * _mapWidth + gx;
                    int count = _cellCounts[cellIndex];
                    if (count == 0) continue;

                    int baseOffset = cellIndex * CellCapacity;
                    for (int i = 0; i < count; i++)
                    {
                        int eid = _gridData[baseOffset + i];
                        float ex = store.PositionX[eid];
                        float ey = store.PositionY[eid];
                        float ddx = ex - towerX;
                        float ddy = ey - towerY;
                        float distSq = ddx * ddx + ddy * ddy;
                        if (distSq <= rangeSq)
                        {
                            output.Add(eid);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Line of sight raycast from a tower to an enemy. Returns false (LoS blocked) if any
        /// active LoS-blocking tower (store.TowerBlocksLOS[towerId] == true) lies in a grid
        /// cell intersected by the integer ray from (fromX, fromY) to (toX, toY). Endpoints
        /// are exempt: the source tower's own cell and the target's cell never block.
        ///
        /// Performance: O(activeTowerCount) per call (simple per-tower cell-on-ray test).
        /// LoS is only invoked for towers that opted-in via TowerRequiresLOS — all default
        /// towers skip this check entirely (backward compatible).
        /// </summary>
        public bool HasLineOfSight(ComponentStore store, int fromTowerId,
            float fromX, float fromY, float toX, float toY)
        {
            int gx0 = (int)Math.Floor(fromX);
            int gy0 = (int)Math.Floor(fromY);
            int gx1 = (int)Math.Floor(toX);
            int gy1 = (int)Math.Floor(toY);

            // Same cell: trivial LoS
            if (gx0 == gx1 && gy0 == gy1) return true;

            // Scan active towers for blockers on the ray. _gridData is enemy-only, so we
            // must iterate ActiveTowerIds directly. The check uses IsCellOnRay which is
            // O(1) per tower; the total cost is proportional to the number of active towers
            // and is only incurred when TowerRequiresLOS is true.
            var towerIds = store.ActiveTowerIds;
            for (int i = 0; i < towerIds.Count; i++)
            {
                int tid = towerIds[i];
                if (tid == fromTowerId) continue;
                if (!store.TowerBlocksLOS[tid]) continue;
                float tx = store.PositionX[tid];
                float ty = store.PositionY[tid];
                int tgx = (int)Math.Floor(tx);
                int tgy = (int)Math.Floor(ty);
                if (IsCellOnRay(gx0, gy0, gx1, gy1, tgx, tgy)) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns true if cell (cx, cy) lies on the integer ray from (x0, y0) to (x1, y1)
        /// (endpoints inclusive). Used by HasLineOfSight to test whether a LoS-blocking
        /// tower's cell is intersected by the line.
        /// </summary>
        private static bool IsCellOnRay(int x0, int y0, int x1, int y1, int cx, int cy)
        {
            int minAxis1, maxAxis1, minAxis2, maxAxis2;
            if (x0 == x1)
            {
                // Vertical line: same column
                if (cx != x0) return false;
                minAxis1 = Math.Min(y0, y1);
                maxAxis1 = Math.Max(y0, y1);
                return cy >= minAxis1 && cy <= maxAxis1;
            }
            if (y0 == y1)
            {
                // Horizontal line: same row
                if (cy != y0) return false;
                minAxis1 = Math.Min(x0, x1);
                maxAxis1 = Math.Max(x0, x1);
                return cx >= minAxis1 && cx <= maxAxis1;
            }
            // Diagonal: use parametric t = (cx - x0) / (x1 - x0), check y matches
            int dx = x1 - x0;
            int dy = y1 - y0;
            // Bresenham-style "cell on line" check: |dy*(cx - x0) - dx*(cy - y0)| <= max(|dx|,|dy|)
            long lhs = (long)dy * (cx - x0);
            long rhs = (long)dx * (cy - y0);
            long diff = lhs - rhs;
            long tol = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (Math.Abs(diff) > tol) return false;
            minAxis1 = Math.Min(x0, x1);
            maxAxis1 = Math.Max(x0, x1);
            minAxis2 = Math.Min(y0, y1);
            maxAxis2 = Math.Max(y0, y1);
            return cx >= minAxis1 && cx <= maxAxis1 && cy >= minAxis2 && cy <= maxAxis2;
        }
    }
}

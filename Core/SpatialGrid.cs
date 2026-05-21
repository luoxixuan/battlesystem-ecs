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
        /// 查询范围内所有敌人 ID — O(cells queried)，通常远小于 O(enemies)。
        /// range 是以塔为中心的正方形半径（单位：cell）。
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
    }
}

using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 空间分区网格 — 将敌人按整数坐标 (gx, gy) 哈希到单元格，
    /// 支持 O(1) 范围查询替代 O(N) 全量遍历。
    /// 
    /// 地图尺寸 10×20，cell 大小 1×1 与网格坐标系对齐。
    /// 网格在每帧开始时整体重建（O(enemies)），之后所有塔查询均为 O(1) 哈希。
    /// </summary>
    public class SpatialGrid
    {
        /// <summary>cell 大小 = 1 格 = 1 坐标系单位，与 MapSystem 网格对齐</summary>
        private const float CellSize = 1f;

        /// <summary>
        /// (gx, gy) → 该格子内所有敌人 ID 列表。
        /// gx = floor(x / CellSize), gy = floor(y / CellSize)。
        /// </summary>
        private Dictionary<(int gx, int gy), List<int>> _grid
            = new Dictionary<(int, int), List<int>>(1024);

        /// <summary>稀疏格子集合（避免空格子查询）</summary>
        private HashSet<(int gx, int gy)> _activeCells
            = new HashSet<(int, int)>();

        /// <summary>
        /// 整体重建网格 — O(enemies)。
        /// 必须在帧开始时调用一次。
        /// </summary>
        public void Rebuild(ComponentStore store, IReadOnlyList<int> enemyIds)
        {
            _grid.Clear();
            _activeCells.Clear();

            for (int i = 0; i < enemyIds.Count; i++)
            {
                int eid = enemyIds[i];
                if (!store.EnemyActive[eid]) continue;

                int gx = (int)Math.Floor(store.PositionX[eid]);
                int gy = (int)Math.Floor(store.PositionY[eid]);

                if (!_grid.TryGetValue((gx, gy), out var list))
                {
                    list = new List<int>(4);
                    _grid[(gx, gy)] = list;
                    _activeCells.Add((gx, gy));
                }
                list.Add(eid);
            }
        }

        /// <summary>
        /// 查询指定格子内的所有敌人 ID — O(1) 哈希。
        /// </summary>
        public void GetEnemiesAtPoint(float x, float y, List<int> output)
        {
            int gx = (int)Math.Floor(x);
            int gy = (int)Math.Floor(y);
            if (!_grid.TryGetValue((gx, gy), out var list)) return;
            for (int i = 0; i < list.Count; i++)
                output.Add(list[i]);
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

            for (int dx = -range; dx <= range; dx++)
            {
                int gx = centerGx + dx;
                for (int dy = -range; dy <= range; dy++)
                {
                    int gy = centerGy + dy;
                    if (!_grid.TryGetValue((gx, gy), out var list)) continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        int eid = list[i];
                        // Bounding-box filter (already close cell-level)
                        float ex = store.PositionX[eid];
                        float ey = store.PositionY[eid];
                        float ddx = ex - towerX;
                        float ddy = ey - towerY;
                        float distSq = ddx * ddx + ddy * ddy;
                        float rangeSq = range * range;
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

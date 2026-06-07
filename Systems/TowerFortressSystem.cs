using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Fortress System — 塔集群协同减伤/加速系统
    ///
    /// 与 TowerSynergySystem 的差异：
    ///   - SynergySystem 是"跨类型组合"（如 火+冰 触发 DoT 协同）
    ///   - FortressSystem 是"同类聚集"（3+ 同 TowerType 塔在 N 格内 → 全员 +damage/+speed）
    ///
    /// 启动时无 JSON 加载（Fortress 阈值在 FortressConfig 静态常量里，方便 designers 改 config 即可）。
    /// SetTurn 时按 ActiveTowerIds 做 O(N²) Chebyshev 距离扫描，按 neighbor count 写缓存字段。
    /// Update 阶段不做事（缓存是"读"用的，不需要每帧写）。
    ///
    /// Hot-path 设计：
    ///   - 缓存字段写在 tower 实体上（read by TowerAttackSystem on attack）
    ///   - SetTurn 单次 pass 算所有塔的 neighbor count（O(N²) gated by active count）
    ///   - 全部 default 0 字段 = 孤立塔的零开销
    /// </summary>
    public class TowerFortressSystem
    {
        private ComponentStore store;
        private IRenderer logger;

        public TowerFortressSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
        }

        /// <summary>
        /// SetTurn 时按 ActiveTowerIds 做 O(N²) Chebyshev 距离扫描。
        /// 写入 TowerFortressNeighborCount / TowerFortressCachedDmgBonus / TowerFortressCachedAtkSpdBonus。
        ///
        /// Chebyshev 距离 (max(|dx|, |dy|)) 而非 Euclidean：
        ///   - 跟 TowerPlacementSystem / AggroSystem 等的"格子距离"语义一致
        ///   - 不用 sqrt，性能更高
        /// </summary>
        public void SetTurn()
        {
            var activeTowerIds = store.ActiveTowerIds;
            int count = activeTowerIds.Count;
            if (count == 0) return;

            float radius = FortressConfig.FortressRadius;
            // 使用 float 比较以避免截断误差（Chebyshev 距离 = max(|dx|, |dy|)）
            // 历史上此处在 (int) 转换上做过取舍：grid 坐标本来就是整数，所以截断
            // 不会出错；但保险起见改为 float 比较，未来如果引入非整数坐标也能正确工作。

            // 第一遍：先清零所有 active 塔的缓存（避免跨帧累加）
            for (int i = 0; i < count; i++)
            {
                int tid = activeTowerIds[i];
                store.SetTowerFortressNeighborCount(tid, 0);
                store.SetTowerFortressDmgBonus(tid, 0f);
                store.SetTowerFortressAtkSpdBonus(tid, 0f);
            }

            // 第二遍：O(N²) 扫描同类型邻居
            // 优化：内层循环从 i+1 开始避免重复计数对，结束后再加回 i 自己的 count
            for (int i = 0; i < count; i++)
            {
                int ti = activeTowerIds[i];
                if (!store.TowerActive[ti]) continue;
                // Round 180 Direction 5 — bug-scan fix: a dispelled tower should not
                //   compute or apply its own fortress bonuses (a dispel removes the
                //   tower's contribution to combat; a dispelled tower is, in spirit,
                //   "out of the fight" for cluster purposes too). Skipping the outer
                //   also prevents the asymmetry the bug scanner flagged: inner loop
                //   skips dispelled towers, so they don't contribute as neighbors;
                //   outer loop should mirror that and not compute bonuses for them.
                if (store.TowerIsDispelled[ti]) continue;
                TowerType typeI = store.TowerType[ti];

                float ix = store.PositionX[ti];
                float iy = store.PositionY[ti];
                int neighbors = 0;

                for (int j = 0; j < count; j++)
                {
                    if (j == i) continue;
                    int tj = activeTowerIds[j];
                    if (!store.TowerActive[tj]) continue;
                    if (store.TowerIsDispelled[tj]) continue;

                    if (store.TowerType[tj] != typeI) continue;

                    float jx = store.PositionX[tj];
                    float jy = store.PositionY[tj];
                    float dx = Math.Abs(jx - ix);
                    float dy = Math.Abs(jy - iy);
                    float cheby = dx > dy ? dx : dy;
                    if (cheby > radius) continue;
                    if (cheby == 0f) continue; // 防御：同坐标 0 距离 → 跳过（不应该发生但便宜）
                    neighbors++;
                }

                if (neighbors <= 0) continue;

                // 邻居数 → tier 选择
                int t1 = FortressConfig.FortressT1NeighborCount;
                int t2 = FortressConfig.FortressT2NeighborCount;

                float dmgBonus = 0f;
                float atkSpdBonus = 0f;
                if (neighbors >= t2)
                {
                    dmgBonus = FortressConfig.FortressT2DmgBonus;
                    atkSpdBonus = FortressConfig.FortressT2AtkSpdBonus;
                }
                else if (neighbors >= t1)
                {
                    dmgBonus = FortressConfig.FortressT1DmgBonus;
                    atkSpdBonus = FortressConfig.FortressT1AtkSpdBonus;
                }

                store.SetTowerFortressNeighborCount(ti, neighbors);
                store.SetTowerFortressDmgBonus(ti, dmgBonus);
                store.SetTowerFortressAtkSpdBonus(ti, atkSpdBonus);
            }
        }

        /// <summary>
        /// Update 阶段：当前实现里是 no-op（缓存是 SetTurn 一次性写入的）。
        /// 保留此方法以便未来做"per-frame 衰减"或"事件触发"时扩展。
        /// </summary>
        public void Update(float deltaTime)
        {
            // No-op: SetTurn handles the full pass. Per-frame work intentionally avoided
            // because fortress bonuses are "static placement rewards", not time-based.
        }
    }
}

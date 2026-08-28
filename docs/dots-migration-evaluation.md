# Unity DOTS 引入可行性评估

> 评估日期：2026-08-27 ｜ 环境：Ryzen 9 3900X（12C）/ 64GB / Unity 2022.3.62f2c1 LTS
> 结论先行：**不建议全量迁移 DOTS**。推荐路线 C（维持 SOA 架构 + 持续定点优化），可选路线 B（Unity 侧局部 Burst 加速）作为低风险实验。性能不是迁移的瓶颈理由——模拟负载只占 60 FPS 帧预算的 ~1.2%。

---

## 1. 现状量化（为什么性能不构成迁移理由）

本轮优化后实测（10K 敌 × 500 帧，2026-08-27）：

| 压测模式 | 2026-06-09 文档基线 | 2026-08-27 优化后 | 说明 |
|----------|-------------------|------------------|------|
| mode 2 合并热路径 | 7,385 FPS | **8,333 FPS** | 手写合并循环，主要衡量纯 SOA 数组吞吐 |
| mode 4 真实系统链路 | 3,125 FPS | **5,212 FPS** | 4 项并行优化落地后（PlayerAttack 27.0→12.1ms） |
| mode 5 完整一局 | 2,848 FPS | **4,874 FPS** | 5 关全通（410 帧，0.205ms/帧），真实波次 |

关键帧预算分析（这是评估的核心论据）：

- **60 FPS 帧预算 = 16.67ms**；mode 5 完整一局平均帧成本 ≈ **0.2ms**（模拟侧全系统链）。
- 即使 DOTS 把模拟侧提速 **10 倍**，总帧时间在 60 FPS 预算下也只能省 ~0.18ms（≈1%）——**玩家完全无感**。
- 当前 10K 敌规模下模拟已跑到 0.19ms/帧；线性外推 100K 敌（`MAX_ENTITIES` 上限）≈ 2ms/帧，仍远低于预算。**项目不存在"模拟性能撑不住"的问题。**
- Unity 端真正的帧成本大头在渲染侧（`UnityEventBus` 每帧同步 GameObject），那是渲染架构问题，迁移 ECS 模拟层不会自动解决，除非连渲染端一起 ECS 化（Entities Graphics）。

本轮优化同时证明了现有架构的优化空间还很大（详见 §4 模式对照）：仅消除 4 类并行反模式就拿到 mode 4 +19%，且不改变任何战斗语义。

## 2. DOTS 能带来什么（客观盘点）

| 能力 | 对本项目的实际价值 |
|------|------------------|
| **Burst 编译器**（HPC# → LLVM，SIMD 自动向量化） | 对纯数值内循环（伤害公式、距离筛选）有 2-10× 收益（对比 .NET JIT；对比 Mono 可达 10-100×）。但本项目热点已大量是内存带宽受限的 SOA 数组扫描，而非计算受限——收益上限低于宣传值 |
| **Job System + 调度器**（work-stealing、批处理、依赖图） | 对比现有 `Parallel.For` 两阶段模式：调度开销更低、可表达系统间依赖；但本轮证明 `Parallel.For` 的开销问题主要在调用侧反模式（锁/分配），修掉后差距缩小 |
| **Chunk 内存布局**（按组件集合分段存储，16KB chunk） | **本项目收益有限**：SOA `ComponentStore` 本质上就是"每类实体一个巨型 chunk"的布局，缓存友好度同量级；且所有敌人共享同一组件集（无 archetype 分裂收益） |
| **确定性容器/确定性并行** | ✅ 真实价值：`Unity.Collections` 确定性模式支持回放/lockstep 联机（知识库参考：`UnityLockstep`、`kisence-mian/UnityLockStepDemo`） |
| **Entities Graphics / 渲染 ECS 化** | ✅ 若渲染端 GameObject 同步成为瓶颈，这是唯一治本方案——但代价是渲染层重写 |
| **编辑器工具链**（Entities Hierarchy、Profiler、Debugger） | 中等价值：调试可视化优于手写日志 |

## 3. 兼容性事实核查（2026-08 已核实）

1. **版本兼容 ✓**：Entities 1.3.14/1.3.15 官方支持 Unity 2022.3 LTS（最低编辑器版本 2022.3.13f1）。本项目 Unity 2022.3.62f2c1 **满足要求**。Unity 6 下同样可用 1.3.14+。
2. **致命约束 ✗：Entities/Burst 无官方 NuGet 包，无法脱离 Unity 工具链编译运行**。这意味着：
   - `dotnet build` / `dotnet test`（1281 项测试）/ `dotnet run` 三模式压测的 **CLI 工作流全部失效**——CI 与 AGENTS.md §8 提交门禁体系需要推倒重建（改为 Unity Test Framework + Player 构建）；
   - `BattleSystemECS.Core`（netstandard2.1 + LangVersion 9 + 双端复用）的架构设计前提（控制台与 Unity 共用一份 DLL）被打破；
   - 变通方案（从 Unity 工程抠 DLL 引用 / 自编译 needle-mirror 镜像源码）在涉及 Burst、source generator、baking 时不可靠，官方不支持。

## 4. 迁移成本盘点（路线 A：全量迁移）

| 项 | 规模 | 迁移后命运 |
|----|------|-----------|
| 战斗逻辑 | Core+Systems ~52k 行 / 144 系统 | 逐个改写为 `ISystem`（Burst）或 `SystemBase`，两阶段结算模式 → Job 依赖图 |
| `ComponentStore` ×5 partial | ~200 个 SOA 字段 | 全部转为 `IComponentData`/`IBufferElementData`，热路径直接数组索引 → `IJobEntity`/指针访问 |
| 双事件总线 | `IBattleEventBus` + `EventBus<T>` | 逻辑→渲染可改 ECS Graphics/`ICleanupComponent`；系统间事件 → Job 依赖或 `NativeQueue` |
| GAS 子模块 | Attributes/Effect/Ability | 概念可映射（GameplayEffect → 状态组件 + 事件），但需整体重写 |
| 配置加载 | `GameConfigLoader` + JSON | 运行时只读 → **BlobAssets**（烘焙期构建），加载链路重做 |
| 测试 | 1281 项 xUnit（四分层） | 全部重写为 Unity Test Framework（EditMode/PlayMode），基建（TestWorld/Specs）重建 |
| 基准/CI | mode 2/3/4/5 + 静态规则门禁 | 全部迁入 Unity 工程或 Player 构建，吞吐测量方法重定 |

估算：**3-6 人月**（熟悉 DOTS 的工程师），且迁移期间业务扩展暂停（与 AGENTS.md "业务扩展暂停期" 的定位冲突）。风险最高的三块：确定性 RNG 语义对齐（现用 `Rng.Shared` 与哈希 RNG 混合）、行为树求值器的 Burst 化（含字符串/委托的 BT 结构需数据化重写）、1281 项测试的断言语义迁移。

## 5. 三条路线对比

| | A. 全量迁移 DOTS | B. Unity 侧局部 Burst | C. 维持 SOA + 定点优化（推荐） |
|---|---|---|---|
| 性能收益 | 模拟侧 2-10×（但只占帧预算 ~1.2%，总帧收益 <2%） | 热点内循环 2-5×，同样受帧预算封顶 | 本轮已 +19%（mode 4），后续仍有 SIMD/分批/阈值空间 |
| 成本 | 3-6 人月 + 工作流重建 | 数周（挑选 5-10 个纯计算循环） | 持续小步，零架构风险 |
| 风险 | 高（测试/CI/语义全重写） | 中低（Unity 侧 DLL 内做，需防双实现漂移） | 低 |
| CLI 基准/测试门禁 | ✗ 失效 | ✓ 保留（逻辑仍在 Core 库） | ✓ 保留 |
| 额外所得 | 确定性回放/联机潜力、Entities Graphics、官方工具链 | 无 | 无 |
| 适用触发条件 | 需要联机 lockstep 或渲染端 ECS 化，且接受工作流重建 | Unity 帧率实测被模拟热点拖累时 | 默认 |

**推荐 C 的核心理由**：本项目是性能基准项目，性能数据已经证明模拟侧远未到瓶颈；DOTS 的边际收益买不回 1281 项测试 + CLI 门禁工作流的重建成本。若未来出现"需要确定性回放/联机"或"Unity 渲染端必须 ECS 化"的产品需求，再评估路线 A；若 Unity 实机帧率剖析显示模拟热点拖帧，先走路线 B 验证收益。

## 6. 本轮已落地的优化（路线 C 的第一轮，2026-08-27）

| # | 优化 | 模式 | 效果 |
|---|------|------|------|
| 1 | `PlayerTowerAttackSystem`：并行段全局锁 → 按批分区无锁收集（256/批）+ <500 敌串行快速路径 | mode 4 | PlayerAttack 27.0→12.1ms（**−55%**） |
| 2 | `SkillSystem`：8 处 `Parallel.ForEach`+每命中加锁 → 统一 `CollectHits`（阈值串行/无锁批分区），删除 7 个命中列表 + 8 把锁 | mode 4/5 | Skill 桶内收集段归零（总时长受 buff/combo 混排影响） |
| 3 | `TowerAttackSystem`：<8 塔走串行驱动（跳过 TPL 启停），≥8 塔并行行为不变 | mode 2/4/5 | TowerAttack 19.4→15.6ms（−20%） |
| 4 | 全仓 24 处每帧 `new ParallelOptions` → `ParallelOptionsCache.HotPath` 静态缓存 | 全部 | 消除每帧 ~24 次小对象分配（Gen0 压力） |

汇总：mode 2 6,724→**8,333**（+24%）、mode 4 4,419→**5,212**（+18%）、mode 5 4,612→**4,874**（+6%）（对比 2026-08-27 当日干净基线；对比上一轮 CHANGELOG 记录为 +7.6%/+13.1%/+13.6%）。最终门禁数据见 `AGENTS.md` §7 基准表（已同步更新）。

## 7. 若未来仍决定迁移：分步策略

1. **先做渲染端剖析**（Unity Profiler 确认模拟 vs 渲染占比），避免为不存在的瓶颈买单；
2. 以 **mode 4 的 12 系统链**为试点在 Unity 工程内建平行 DOTS World，逐系统对拍 500 帧输出（利用现有确定性 RNG）；
3. 迁移顺序按"数据纯、无事件、无字符串"优先：Movement → Spatial（SpatialGrid → NativeHashMap/网格 job）→ Combat damage → AI/BT（最后，需先把 BT 数据化）；
4. 配置转 BlobAssets 的烘焙器与 `GameConfigLoader` 并行维护，直到切换完成；
5. CLI 基准保留给旧实现做回归对照，新实现基准迁入 Unity（Editor 闭式循环或 Player headless）。

## 8. 参考资料

- [Entities 1.3 官方文档（支持 2022.3 LTS）](https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/index.html)
- [Entities 1.3.15 Changelog（最低编辑器 2022.3.13f1）](https://docs.unity3d.com/Packages/com.unity.entities@1.3/changelog/CHANGELOG.html)
- [Unity 2022.3 Manual: Entities 包版本列表](https://docs.unity3d.com/2022.3/Documentation/Manual/com.unity.entities.html)
- [Unity DOTS 状态公告（2024-09, ECS for All）](https://discussions.unity.com/t/dots-development-status-and-milestones-ecs-for-all-september-2024/1519286)
- 项目调研知识库参考仓库：`Unity-Technologies/ECS-Network-Racing-Sample`（官方 DOTS 完整样例）、`Daivuk/tddod`（DOTS 塔防）、`reeseschultz/ReeseUnityDemos`、`proepkes/UnityLockstep`（DOTS 确定性联机）

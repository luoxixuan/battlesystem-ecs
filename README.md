# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-06-04, Round 102 — 玩家伤害转化 Damage Conversion）

| 指标 | 数值 |
|------|------|
| **mode 5**（完整一局） | **3362 FPS**，400 帧 |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **9655 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **3923 FPS** |
| mode 3 | 微基准测试（单系统操作级性能剖析） |

> 本轮实施 Round 102 方向7（玩家伤害转化 Damage Conversion）：`GameConfig` 新增 `PlayerDamageConversionRatio`（默认 0f，0..1 区间）+ `PlayerConvertedDamageType`（默认 Physical）两字段；`DamageConversionConfig` 静态类 2 常量（`ConversionDefaultCap=0.5f` 全局 cap / `MinMeaningfulRatio=0.01f` 零开销阈值）；`PlayerTowerAttackSystem` 引入 `DamageType` 元组的 ping-pong damage queue（从 3-tuple 扩到 4-tuple，零额外分配），并行段提取 `ApplyResistancesAndEnqueue` 私有方法承担 resistance/immunity/exposure/death-mark 流水线；`convRatio < MinMeaningfulRatio` 走 fast path（单事件），否则按 `clamp(convRatio, 0, DefaultCap)` 拆成 original + converted 两段独立 apply（双事件，各自走对应的 armor/magicResist/immunity 路径）。`Data/Configs/damage_conversion.json` 配置元数据。配套 20 个测试覆盖 Config 常量、GameConfig defaults、Default 零分配、Clamp-at-Cap、Above-threshold 双事件、低于阈值 fast-path、Physical 走 armor、Physical-immune 仍吃 Magic portion、Magic 走 magicResist、crit 沿用、DestroyEntity reset 兼容性等。**228/228 tests PASS**。bench2 9655（bench2 仍 < 10000 阈值，与上轮 9259 相比 +4.3% 改善，但未达阈值）、bench4 3923（-3.8% 跌破 4300 阈值）、bench5 3362（-0.5% 跌破 3800 阈值，三项均未达阈值 ⚠️）。
>
> mode 5 是最接近真实游戏的压测：5 关全通、真实波次生成、2 塔防守，400 帧通关。mode 4 是 10K 固定实体规模下的主要参考指标。mode 2 是手写合并热路径，参考价值次之。

## 优化演进（关键节点）

| commit | FPS (mode 4) | 关键改动 |
|--------|--------------|---------|
| `c67b567` | 212 | 初始（无并行） |
| `3885275` | 3368 | TowerAttack 并行化 + ActiveTowerIds |
| `ccc42e3` | ~4900 | EnemyAI 两阶段 + BeginFrame 每回合 |
| `d707920` | ~4900 | PlayerAttack/TowerAttack damage 累加修正 |
| `223c84d` | ~4000 | 10x 扩展（150 塔 × 200 怪物）后 EnemyAI ~22ms |

> `223c84d` 是 10x 规模（150 塔）后基准，与 5 塔基准 `d707920`（~4900 FPS）不可直接对比。

---

## 架构特点

- **SOA 存储**: 所有组件用平行 `float[]/int[]/bool[]` 数组，CPU 缓存友好
- **全系统并行**: 每个系统内部用 `Parallel.For` 批处理，4 核加速
- **行为树 AI**: 敌人用 flat-array BTCachedTree 驱动，O(1) 节点访问。BT 评估缓存用 health-driven version counter
- **GAS 技能系统**: `Core/GAS/` 模块化 Attributes + GameplayEffect + GameplayAbility
- **科技树系统**: 3 分支（⚔️进攻/🛡️防御/💰经济）× 5 节点，支持前置依赖解锁
- **预计算优化**: BT 构建时预计算 action enum，跳过运行时字符串转换

---

## 项目结构

```
BattleSystem-ECS/
├── Core/
│   ├── ComponentStore.cs            # SOA 核心：常量、Position、实体生命周期、死亡队列
│   ├── ComponentStore_Enemy.cs      # SOA 敌人字段 + 访问方法
│   ├── ComponentStore_Tower.cs      # SOA 塔字段 + 访问方法
│   ├── ComponentStore_Player.cs     # SOA 玩家字段 + 访问方法
│   ├── ComponentStore_World.cs      # SOA 世界/环境/资源字段 + 访问方法
│   ├── GameManager.cs        # 游戏主循环与系统调度
│   ├── EntityManager.cs
│   ├── EventBus.cs
│   ├── IRenderer.cs / ConsoleLogger.cs / FileLogger.cs
│   └── GAS/
│       ├── Attributes.cs
│       ├── GameplayEffect.cs
│       └── GameplayAbility.cs
├── Components/
│   ├── BuffData.cs
│   ├── EnemyActionType.cs
│   ├── EnemyComponent.cs
│   └── SkillComponent.cs
├── Systems/
│   ├── EnemyAISystem.cs       # 行为树评估 + 攻击执行
│   ├── EnemyMovementSystem.cs
│   ├── PlayerTowerAttackSystem.cs
│   ├── TowerAttackSystem.cs
│   ├── TowerPlacementSystem.cs
│   ├── TowerUpgradeSystem.cs
│   ├── WaveSpawningSystem.cs   # 波次生成（含 OnWaveComplete 事件）
│   ├── UpgradeSystem.cs        # 玩家升级
│   ├── SkillSystem.cs          # GAS 技能系统
│   ├── TechTreeSystem.cs       # 科技树（3分支 × 5节点）
│   ├── GoldSystem.cs
│   ├── MapSystem.cs
│   ├── BenchmarkSystem.cs      # 全链路压测
│   ├── BehaviorTreeEvaluator.cs
│   └── BehaviorTreeNodes.cs
├── Configs/
│   ├── game_config.json        # 怪物类型 / 等级 / 波次配置
│   ├── behavior_trees.json     # 行为树定义
│   ├── skills.json
│   ├── tower_placement.json
│   ├── tech_tree.json          # 科技树节点定义
│   └── TechTreeDef.cs          # 科技树配置结构
├── Research/
│   ├── tower_defense_knowledge.md  # 自动更新的塔防知识库（21 repos）
│   ├── bug-fix.md              # Bug 追踪（48 项，全部已修复）
│   └── findings/               # 爬取原始数据
├── BattleSystemECS.Tests/
│   └── ...                     # 120 单元测试
└── Program.cs                  # 入口（游戏/压测/微基准）
```

---

## 快速开始

```bash
# 构建
dotnet build

# 运行
dotnet run
# 1: 塔防游戏（交互式）
# 2: 全链路性能压测（10K 敌 × 500 帧，手写合并热路径）
# 3: 微基准测试（单系统操作级性能剖析）
# 4: 真实系统链路压测（10K 敌 × 500 帧，实际系统调用链）
# 5: 完整一局压测（5 关全通，真实波次生成）

# 测试
cd BattleSystemECS.Tests
dotnet test
```

---

## 科技树（TechTree）

每波结算产出 **1 研究点数**，用于解锁科技节点（前置依赖检查已实现）：

| 分支 | 节点 |
|------|------|
| ⚔️ 进攻 | 锋利 I→II、连射、致命打击、穿刺射击 |
| 🛡️ 防御 | 铁壁 I→II、喘息、堡垒、不朽 |
| 💰 经济 | 盗墓、高效击杀、理财、商队、淘金热 |

详见 `Configs/tech_tree.json`。

---

## Bug 追踪

详见 [`docs/design-and-bugs.md`](docs/design-and-bugs.md)。所有 48 项 Bug 已全部修复。

---

## 更新记录

完整更新历史见 [CHANGELOG.md](CHANGELOG.md)。

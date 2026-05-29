# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-05-29, commit `bff4ff8`）

| 指标 | 数值 |
|------|------|
| **mode 5**（完整一局） | **~5007 FPS**，400 帧，~0.20 ms |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **~10496 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **~5188 FPS** |
| mode 3 | 微基准测试（单系统操作级性能剖析） |

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
│   ├── ComponentStore.cs     # SOA 数据存储（核心性能点）
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
│   ├── bug-fix.md              # Bug 追踪（46 项，45 已修复，1 未修复）
│   └── findings/               # 爬取原始数据
├── BattleSystemECS.Tests/
│   └── ...                     # 48 单元测试
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
- **2026-05-29**: 方向二（续）：为全部 150 个技能 JSON 添加 ManaCost 字段（20-115，基于 Cooldown × DamageMultiplier 计算），法力系统完全打通（bench2: 10496, bench4: 5188, bench5: 5007）
- **2026-05-29**: 方向五：敌人驱散/净化塔增益 Enemy Dispel/Purge（TowerIsDispelled/TowerDispelTimer/TowerDispelImmunityTimer），DispelEnemyDef配置类，ExecuteDispelTower()，TowerDispelSystem，FrameScheduler Phase 6.2注入，AuraTowerSystem/TowerSynergySystem跳过dispelled塔（bench2: 9260, bench4: 5276, bench5: 5029）
- **2026-05-29**: 方向二（部分）：技能法力消耗 (SkillConfig.ManaCost → GameplayAbilityDef.Cost)，ManaSystem.HasEnoughMana/ConsumeMana 已就绪，GameConfigLoader 解析 ManaCost JSON 字段（bench2: 8494, bench4: 4661, bench5: 1066）
- **2026-05-29**: 方向四：敌方治疗/辅助单位 Enemy Healer System（EnemyHealerSystem.cs，HealerDef配置类，EnemyHealerHealAmount/HealInterval/HealTargetPriority等SOA组件，GameConfig.HealerDefs，FrameScheduler Phase 3.75注入）（bench2: 10145, bench4: 5230, bench5: 4888）
- **2026-05-29**: 方向七：敌人产卵巢穴 Nest System（NestSystem.cs，NestDef配置类，NestHealth/SpawnTimer/ActiveCount等SOA组件，GameConfig.NestDefs，FrameScheduler Phase 1.5注入）（bench2: 10653, bench4: 5302, bench5: 5016）
- **2026-05-29**: 自动基准更新（bench2: 10624, bench4: 5501, bench5: 5129）
- **2026-05-29**: 方向九：塔被动资源生产 Tower Passive Resource Generation（TowerIsIncomeTower/TowerGoldPerSecond），TowerIncomeSystem，GameConfig.IsIncomeTower/GoldPerSecond，PlaceTower 配置读取，经济塔跳过攻击逻辑，BuildPhase+WavePhase 双阶段产金（bench2: 10033, bench4: 5294, bench5: 4831）
- **2026-05-29**: 方向三：金币窃取敌人 Gold-Stealing Enemies（EnemyCanStealGold/StealAmount/StolenGold/HasStolenGold），ThiefDef 配置类，EnemyStealGoldSystem，ComponentStore.LoseGold()，小偷偷金币不扣血，击杀逃跑小偷奖励 GoldOnReturn（bench2: 10600, bench4: 5411, bench5: 4704）
- **2026-05-29**: 方向六：塔牺牲/自毁效果 Tower Demolish（AoE 拆除，Fire/Ice/Lightning/Poison/Arcane 五种效果类型），TowerDemolishSystem，DemolishTower() 入口（bench2: 10642, bench4: 5509, bench5: 5121）✅
- **2026-05-28**: 方向八：肉盾/前锋敌人 Vanguard（EnemyIsVanguard/EnemyVanguardCoverRange/EnemyVanguardDmgTransfer），TowerAttackSystem 伤害结算时检测并转移伤害（bench2: 9265, bench4: 5058, bench5: 4809）
- **2026-05-28**: 自动基准更新（bench2: 10735, bench4: 5046, bench5: 4096）⚠️ mode 4/5 显著下降，疑似新增方向系统引入开销
- **2026-05-28**: mode 5（完整一局压测）上线 — 5 关全通 400 帧 6520 FPS
- **2026-05-13**: 科技树系统上线（3分支 × 5节点，研究点数每波产出）
- **2026-05-12**: BT Cache fix + Merged pipeline，FPS 达到 8334
- **2026-05-12**: GAS 技能系统重构（Bug#9 修复）
- **2026-05-12**: TowerAttack 并行化（ActiveTowerIds）
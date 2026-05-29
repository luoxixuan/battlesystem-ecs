# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-05-30, commit `26a3054`）

|     | 指标 | 数值 |
|-----|------|------|
| **mode 5**（完整一局） | **~4920 FPS**，400 帧，~0.20 ms |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **~9920 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **~5344 FPS** |
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

> 仅记录功能上线和重大修复。日常基准波动见顶部性能基准表。

- **2026-05-30**: 方向四：塔建造延迟（ConstructionTime/ConstructionHP/IsVulnerableDuringConstruction + TowerConstructionSystem + TowerPlacementSystem 建造初始化 + TowerAttackSystem 跳过建造中塔）；bench2: 9920, bench4: 5344, bench5: 4920
- **2026-05-30**: 方向一：塔重定位/重新部署（TowerRelocateSystem + TowerPlacementSystem.RelocateTower + tower_placement.json 配置）；bench2: 9698, bench4: 5286, bench5: 4832
- **2026-05-30**: 方向三：时间操纵塔/Chrono Tower（ChronoTowerSystem + TowerIsChronoTower/TowerTimeFieldRadius/TowerTimeScale/EnemyTimeScale 组件 + FrameScheduler Phase 5.1 注入 + EnemyMovement 集成）；bench2: 10116, bench4: 5158, bench5: 4767
- **2026-05-30**: 方向九：敌人受伤减速/瘸腿（EnemyWoundSystem + EnemyWoundThreshold/EnemyWoundSlowRatio/EnemyIsWounded 组件 + FrameScheduler Phase 3 注入 + ClearEnemyWound）；bench2: 9800, bench4: 5116, bench5: 4594
- **2026-05-29**: 方向五：复活/亡灵法师敌人（NecromancerSystem + EnemyCanResurrect/EnemyIsReanimated + CorpseQueue + WaveSpawning 初始化 + FrameScheduler Phase 2.6 注入）；bench2: 9871, bench4: 5122, bench5: 4882
- **2026-05-29**: 方向二：昼夜循环系统（DayNightSystem + DayNightConfig + GlobalDayNightPhase/Timer/CycleCount 组件 + TowerAttack/EnemyMovement 集成）；bench2: 10483, bench4: 5135, bench5: 5021
- **2026-05-29**: 方向一：钻地/潜行敌人（EnemyBurrowSystem + EnemyIsBurrowed/BurrowTimer/BurrowCooldown + Emerge AoE + TowerAttack 索敌跳过）；bench2: 10115, bench4: 5487, bench5: 4878
- **2026-05-29**: 方向四：流血/撕裂 DoT（BleedSystem + TowerIsBleedTower/EnemyBleedStacks + 流血塔配置）；bench2: 10081, bench4: 5464, bench5: 5087
- **2026-05-29**: 方向七：路径修改塔（PathModifierSystem + PathModifierDef + ComponentStore.PathModifier 组件）；bench2: 10112, bench4: 5545, bench5: 4826
- **2026-05-29**: 方向三：牵引/磁力/漩涡塔（PullTowerSystem + TowerIsPullTower/PullStrength/PullRadius + EnemyIsBeingPulled）；bench2: 10128→10214, bench4: 5345→5344, bench5: 5007→4947
- **2026-05-29**: 8 项业务系统扩展 — 法力消耗系统（150技能ManaCost）、金币窃取敌人（ThiefDef）、敌方治疗者（EnemyHealerSystem）、敌人驱散净化（TowerDispelSystem）、塔牺牲/自毁（TowerDemolishSystem）、敌人产卵巢穴（NestSystem）、塔被动产金（TowerIncomeSystem）、召唤战斗单位修复；方向八：飞行/浮空敌人（EnemyIsFlying + TowerCanHitAir/Ground + 障碍物/地形跳过）；bench2: 10406→10375, bench4: 5638→5216, bench5: 5050→4979
- **2026-05-30**: 方向九：随机事件 Bug 修复（移除 RandomEventSystem 死字段 `_eventCooldown`；InterestSystem 添加 ResetMerchantDiscount + EndEvent 时重置商人折扣）；bench2: 9947, bench4: 5251, bench5: 5050
- **2026-05-29**: 方向六：诅咒/削弱光环（CurseAuraSystem + TowerCurse/EnemyCurse 字段 + CurseTowerConfig）；bench2: 10375→10128, bench4: 5216→5345, bench5: 4979→5007
- **2026-05-28**: mode 5 完整一局压测上线（5关全通，400帧，6520 FPS）
- **2026-05-28**: 方向八：肉盾/前锋敌人（Vanguard，伤害转移）；bench2: 9265, bench4: 5058, bench5: 4809
- **2026-05-13**: 科技树系统上线（3分支 × 5节点，研究点数每波产出）
- **2026-05-12**: BT Cache fix + Merged pipeline（FPS 8334）；GAS 技能系统重构；TowerAttack 并行化（ActiveTowerIds）
# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-05-31, commit `8cfc3d3`）

|     | 指标 | 数值 |
|-----|------|------|
| **mode 5**（完整一局） | **~4733 FPS**，400 帧，~0.21 ms |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **~12319 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **~5648 FPS** |
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
### 2026-05-31
- 光束/激光连续塔基础设施（BeamTowerSystem + ComponentStore_Tower.cs）；bench2: 11369, bench4: 5587, bench5: 5094
- N 击护盾系统（HitShieldSystem + ComponentStore_Enemy.cs + TowerAttackSystem/PlayerTowerAttackSystem）；bench2: 11546, bench4: 5590, bench5: 4824
- 法力燃烧/资源剥夺系统（ManaBurnSystem + ComponentStore_Enemy.cs + ComponentStore_Player.cs）；bench2: 11802, bench4: 5663, bench5: 4889
- 幽灵/相位敌人系统（PhaseSystem + ComponentStore_Enemy.cs + TowerAttackSystem）；bench2: 11643, bench4: 5501, bench5: 5017
- 敌人塔破坏/EMP 瘫痪系统（TowerSabotageSystem + ComponentStore_Tower.cs + ComponentStore_Enemy.cs）；bench2: 12187, bench4: 5471, bench5: 4847
- 自爆/殉爆敌人系统（SuicideBombSystem + ComponentStore_Enemy.cs + CombatGroup）；bench2: 11529, bench4: 5199, bench5: 4567
- 塔造价递增/成本梯度（PlacementCountByType + TowerPlacementSystem + tower_placement.json）；bench2: 11970, bench4: 5389, bench5: 4855
- 伤害免疫/属性克制系统（DamageImmunityMask + DamageType扩展 + TowerAttackSystem/PlayerTowerAttackSystem）；bench2: 9588, bench4: 5712, bench5: 5026
- 敌人吸血系统（EnemyLifestealSystem + EnemyLifestealRatio/Cap/Active + EnemyAISystem）；bench2: 12070, bench4: 5685, bench5: 4951
- 拉扯/真空吸引系统（PullSystem + ComponentStore_World.cs + MovementGroup + SystemRegistry）；bench2: 11784, bench4: 5544, bench5: 4879
- 恐惧/混乱敌人系统（FearSystem + ComponentStore_Enemy.cs + ComponentStore_Tower.cs + AIGroup + SystemRegistry）；bench2: 11955, bench4: 5514, bench5: 4934
- 地图热区/地形加成系统（HotZoneSystem + ComponentStore_Tower.cs + GameConfig.cs + CombatSetupGroup + SystemRegistry）；bench2: 11895, bench4: 5703, bench5: 4592
- 敌人巢穴/生成建筑系统（NestSystem + SystemRegistry + SpawningGroup）；bench2: 11686, bench4: 5669, bench5: 4798
- 保护者/守卫敌人系统（ProtectorSystem + ComponentStore_Enemy.cs）；bench2: 12066, bench4: 5726, bench5: 4709
- 英雄/雇佣兵系统（HeroSystem + ComponentStore_Player.cs + CombatGroup + SystemRegistry）；bench2: 12319, bench4: 5648, bench5: 4733

### 2026-05-30
- **工程改进**：ComponentStore 按领域拆分为 5 个 partial 文件（Enemy/Tower/Player/World + 核心生命周期）；伤害公式测试补齐至 120 项
- 迫击炮/弧线弹道系统（ProjectileSystem + ComponentStore_Tower.cs）；bench2: 12116, bench4: 5349, bench5: 5011
- 风力/气流推动系统（WindSystem + ComponentStore_World.cs）；bench2: 12001, bench4: 5727, bench5: 5018
- 敌人克隆/复制（EnemyCloneSystem）；bench2: 12155, bench4: 5701, bench5: 4714
- 移动/巡逻塔（PatrolTowerSystem）；bench2: 9192, bench4: 4538, bench5: 4227
- 战争迷雾/视野系统（FogOfWarSystem）；bench2: 9806, bench4: 5161, bench5: 4911
- 塔建造延迟（TowerConstructionSystem）；bench2: 9920, bench4: 5344, bench5: 4920
- 玩家全局技能/终极技能（GlobalSkillSystem）；bench2: 10049, bench4: 5100, bench5: 4702
- 塔重定位/重新部署（TowerRelocateSystem）；bench2: 9698, bench4: 5286, bench5: 4832
- 时间操纵塔/Chrono Tower（ChronoTowerSystem）；bench2: 10116, bench4: 5158, bench5: 4767
- 敌人受伤减速/瘸腿（EnemyWoundSystem）；bench2: 9800, bench4: 5116, bench5: 4594
- 随机事件 Bug 修复；bench2: 9947, bench4: 5251, bench5: 5050
- 塔过热/热量系统（HeatSystem + ComponentStore_Tower.cs）；bench2: 11643, bench4: 5655, bench5: 5049
- 塔能量/法力资源系统（TowerEnergySystem + ComponentStore_Tower.cs）；bench2: 10605, bench4: 5672, bench5: 4735
- 光束/激光连续塔（BeamTowerSystem + ComponentStore_Tower.cs）；bench2: 11369, bench4: 5587, bench5: 5094
- 塔能量/法力资源系统（TowerEnergySystem + ComponentStore_Tower.cs + TowerAttackSystem）；bench2: 10605, bench4: 5672, bench5: 4735

### 2026-05-29
- 复活/亡灵法师敌人（NecromancerSystem + CorpseQueue）；bench2: 9871, bench4: 5122, bench5: 4882
- 昼夜循环系统（DayNightSystem）；bench2: 10483, bench4: 5135, bench5: 5021
- 钻地/潜行敌人（EnemyBurrowSystem + Emerge AoE）；bench2: 10115, bench4: 5487, bench5: 4878
- 流血/撕裂 DoT（BleedSystem）；bench2: 10081, bench4: 5464, bench5: 5087
- 路径修改塔（PathModifierSystem）；bench2: 10112, bench4: 5545, bench5: 4826
- 牵引/磁力/漩涡塔（PullTowerSystem）；bench2: 10214, bench4: 5344, bench5: 4947
- 诅咒/削弱光环（CurseAuraSystem）；bench2: 10128, bench4: 5345, bench5: 5007
- 8 项业务系统扩展（法力消耗/金币窃取/敌方治疗/驱散/塔自毁/产卵/被动产金/召唤修复）+ 飞行/浮空敌人；bench2: 10375, bench4: 5216, bench5: 4979
- mode 5 完整一局压测上线（5关全通，400帧，6520 FPS）
- 肉盾/前锋敌人（Vanguard，伤害转移）；bench2: 9265, bench4: 5058, bench5: 4809

### 2026-05-12～13
- 科技树系统上线（3分支 × 5节点，研究点数每波产出）
- BT Cache fix + Merged pipeline（FPS 8334）；GAS 技能系统重构；TowerAttack 并行化（ActiveTowerIds）

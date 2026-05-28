# BattleSystem-ECS — AI Agent 项目指南

> 本文件面向 AI Coding Agent。阅读者被假设为**完全不了解本项目**的外部代理。所有信息均基于当前代码库的实际内容，不做推测。

---

## 1. 项目概述

**BattleSystem-ECS** 是一个基于 **SOA (Struct of Arrays) ECS 架构**的塔防战斗系统性能基准项目。

- **语言**: C# / .NET 6（主程序），测试项目使用 .NET 9
- **架构**: SOA ECS（逻辑与渲染完全分离）
- **运行时**: 控制台应用（含交互式游戏 + 非交互式压测）
- **核心特征**: 全系统并行化 (`Parallel.For`)、零分配热路径、配置驱动、帧末统一结算
- **代码规模**: 约 20+ 个系统，Core + Systems 约 1.5k+ 行核心逻辑（不含配置加载与数据定义）

---

## 2. 技术栈与构建系统

### 2.1 关键配置文件

| 文件 | 作用 |
|------|------|
| `BattleSystemECS.csproj` | 主项目 SDK-style 项目文件，TargetFramework=`net6.0`，OutputType=`Exe`，显式 `Compile Include` |
| `BattleSystemECS.Tests/BattleSystemECS.Tests.csproj` | 测试项目，TargetFramework=`net9.0`，引用 xUnit + coverlet.collector |
| `game_config.json` | 运行时游戏主配置（怪物类型、关卡、波次、玩家属性、连击参数），会被 `CopyToOutputDirectory=PreserveNewest` |
| `Data/Configs/*.json` | 子配置：行为树、技能、科技树、塔位规则、波次生成、阶段行为、自动技能 |
| `Data/Monsters/*.json` | 200 种怪物静态定义 |
| `Data/Skills/*.json` | 150 种技能静态定义 |
| `Data/Towers/*.json` | 150 种塔静态定义 |
| `Data/Levels/*.json` | 5 个关卡配置 |

**注意**：没有 `.sln` 解决方案文件，直接使用 `dotnet build` / `dotnet test` 对单个 `.csproj` 操作。

### 2.2 构建与运行命令

```bash
# 构建主项目（必须 0 warnings 0 errors）
dotnet build

# 运行交互式游戏（菜单选择）
dotnet run

# 非交互式压测（推荐在 CI / 脚本中使用）
dotnet run 2   # mode 2：合并热路径压测（手写合并循环，10K 敌，500 帧）
dotnet run 4   # mode 4：真实系统链路压测（真实 Update 链，10K 敌，500 帧）
dotnet run 3   # mode 3：微基准测试（单系统操作级性能剖析）
dotnet run 5   # mode 5：完整一局压测（5 关全通，真实波次生成）

# 运行单元测试（必须全部通过）
dotnet test BattleSystemECS.Tests
```

---

## 3. 项目结构与模块划分

```
BattleSystem-ECS/
├── Core/                          # ECS 核心与基础设施
│   ├── ComponentStore.cs          # SOA 数据存储（核心性能点，MAX_ENTITIES=100000）
│   ├── Entity.cs                  # 极简实体：仅含 int Id
│   ├── EntityManager.cs           # 实体创建/销毁/命名
│   ├── GameManager.cs             # 游戏主循环、系统初始化、关卡循环
│   ├── FrameScheduler.cs          # 统一帧调度器（所有帧路径唯一入口）
│   ├── StateMachine.cs            # 游戏状态机（BuildPhase / WavePhase / Intermission...）
│   ├── EventBus.cs                # 事件总线（并行安全：lock + snapshot）
│   ├── IRenderer.cs               # 渲染接口（逻辑与渲染分离）
│   ├── ConsoleLogger.cs           # 控制台渲染器实现
│   ├── FileLogger.cs              # 文件日志实现
│   ├── GameConfig.cs              # 运行时配置类定义
│   ├── GameConfigLoader.cs        # JSON 配置加载器
│   ├── GameState.cs               # 游戏状态枚举
│   ├── GameEvents.cs              # 事件定义
│   ├── SpatialGrid.cs             # 空间网格（O(1) cell 访问，range 查询）
│   ├── TechTreeDef.cs             # 科技树配置结构
│   ├── Time.cs                    # 时间工具
│   ├── BuffType.cs                # Buff 类型枚举 + 位标志
│   ├── EnemyActionType.cs         # 敌人动作枚举（BT 结果预计算）
│   └── GAS/                       # Gameplay Ability System 模块
│       ├── Attributes.cs
│       ├── GameplayEffect.cs
│       └── GameplayAbility.cs
│
├── Systems/                       # 游戏系统（职责单一）
│   ├── WaveSpawningSystem.cs      # 波次生成 + 难度曲线
│   ├── EnemyAISystem.cs           # 行为树评估（两阶段：并行评估 + 串行执行）
│   ├── EnemyMovementSystem.cs     # 敌人移动（EnemyActionEnum 驱动）
│   ├── EnemyAbilitySystem.cs      # 敌人技能（冷却 / Buff / 自疗 / AoE）
│   ├── PlayerTowerAttackSystem.cs # 玩家攻击（两阶段：并行收集 damage，串行 apply）
│   ├── TowerAttackSystem.cs       # 塔攻击（两阶段 + ActiveTowerIds + 多索敌模式）
│   ├── TowerPlacementSystem.cs    # 塔放置 / 出售
│   ├── TowerUpgradeSystem.cs      # 塔升级 / 路径切换
│   ├── TowerSynergySystem.cs      # 塔协同增益（配置驱动）
│   ├── SkillSystem.cs             # GAS 技能施放（AreaShape 驱动，只 queue 死亡）
│   ├── BuffSystem.cs              # DoT 追踪（ping-pong 双缓冲 damage 队列）
│   ├── ComboSystem.cs             # 连击计数与衰减
│   ├── AutoSkillSystem.cs         # BuildPhase 自动施放技能（不影响战斗帧预算）
│   ├── TechTreeSystem.cs          # 科技树解锁与效果缓存
│   ├── GoldSystem.cs              # 击杀产金与波次结算
│   ├── UpgradeSystem.cs           # 玩家升级（阈值触发）
│   ├── MapSystem.cs               # 地图渲染（Debug 用）
│   ├── BenchmarkSystem.cs         # 性能压测（mode 2 / mode 3 / mode 4）
│   ├── BehaviorTreeEvaluator.cs   # BT 评估器（flat-array BTCachedTree）
│   └── BehaviorTreeNodes.cs       # BT 节点定义桩
│
├── Data/                          # 静态数据与运行时配置
│   ├── Configs/                   # JSON 运行时配置
│   ├── Levels/                    # 5 个关卡
│   ├── Monsters/                  # 200 种怪物
│   ├── Skills/                    # 150 种技能
│   └── Towers/                    # 150 种塔
│
├── BattleSystemECS.Tests/         # 单元测试（xUnit）
│   ├── CombatResolutionTests.cs   # 战斗结算不变量测试
│   ├── ComponentStoreTests.cs     # 组件存储生命周期测试
│   ├── FrameSchedulerTests.cs     # 帧调度测试
│   ├── GameConfigIntegrationTests.cs
│   ├── GameConfigLoaderTests.cs
│   ├── GameplayAbilityTests.cs
│   ├── GameSimulationTests.cs
│   ├── SkillSystemTests.cs
│   ├── TowerPlacementSystemTests.cs
│   ├── UpgradeSystemTests.cs
│   ├── WaveSpawningSystemTests.cs
│   └── MockRenderer.cs            # 测试用渲染器桩
│
├── docs/
│   ├── architecture.md            # 架构文档（系统结构、设计决策、更新记录）
│   ├── design-and-bugs.md         # 设计治理与 Bug 追踪（48 项 Bug 全部已修复）
│   └── philosophy.md              # 开发理念与工程原则
│
├── Research/                      # 研究、日志与知识库
│   ├── tower_defense_knowledge.md
│   ├── logs/
│   └── tower_defense_explorer.py
│
├── Program.cs                     # 入口（游戏 / 压测 / 微基准）
└── game_config.json               # 运行时主配置（根目录，构建时复制到输出目录）
```

---

## 4. 运行时架构与数据流

### 4.1 ECS 存储模型（SOA）

所有组件数据平铺为连续数组，存储在 `Core/ComponentStore.cs` 中：

- **位置组件**: `float[] PositionX`, `float[] PositionY`, `bool[] PositionActive`
- **玩家组件**: `float[] PlayerAttackDamage`, `float[] PlayerCurrentHealth`, `float[] PlayerGold`, `int[] PlayerResearchPoints`, ...（MAX_PLAYERS=10）
- **敌人组件**: `float[] EnemyHealth`, `float[] EnemyMoveSpeed`, `bool[] EnemyActive`, `EnemyActionType[] EnemyActionEnum`, `BTCachedTree[] EnemyBehaviorTree`, ...（MAX_ENTITIES=100,000）
- **塔组件**: `List<int> ActiveTowerIds`, `float[] TowerAttackDamage`, `int[] TowerRange`, ...

**关键规则**：
- 热路径直接数组索引访问，禁止字典查询或 struct 复制。
- `ActiveTowerIds` / `ActiveEnemyIds` 只缓存活跃实体，遍历时不扫描全量数组。

### 4.2 统一帧调度（FrameScheduler）

`FrameScheduler.Tick(deltaTime, turn)` 是所有帧路径（GameManager / Benchmark / Tests）的**唯一入口**。

帧顺序（WavePhase）：

```
1. BeginFrame()          # 重置 damage/death 队列
2. WaveSpawning.Update() # 生成敌人
3. EnemyAI.SetTurn + Update()      # BT 评估
4. EnemyAbility.SetTurn + Update() # 敌人技能
5. EnemyMovement.SetTurn + Update()# 移动
6. RebuildSpatialGrid()  # 重建空间网格
7. PlayerTowerAttack.Update()      # 玩家攻击
8. TowerAttack.Update()            # 塔攻击
9. TowerSynergy.Update()           # 协同增益
10. Buff.Update() + Skill.ResolveSkillDamage() + Buff.ResolveDotDamage()
11. Skill.Update(deltaTime)        # 技能冷却
12. ResolveEnemiesKilledThisFrame()# 帧末统一死亡结算
```

BuildPhase 时只运行：Gold / Upgrade / Skill(cd) / AutoSkill，**不运行**任何战斗系统。

### 4.3 两阶段并行安全模式

所有涉及并行写共享状态的系统必须遵循：

```
并行段（Parallel.For）
  → 只读组件数据，收集 damage/death 事件到线程局部结构
  → 禁止写 EnemyHealth / PlayerHealth / ActiveEnemyIds / EventBus

串行段（帧末统一结算）
  → 从收集结构取出事件，串行 apply：enemyHealth -= damage
  → QueueEnemyDeath → ResolveEnemiesKilledThisFrame() 统一销毁 + 奖励结算
```

**不可违反的原则**：
- damage queue 必须存 **raw damage**，不能存 newHealth（否则 last-write-wins 丢伤害）。
- 帧末唯一死亡结算点：系统只 queue，不直接调用 `ResolveEnemiesKilledThisFrame()`。
- 实体销毁由 `ComponentStore.DestroyEntity()` 完成，必须同时清理所有 archetype 字段并从 `ActiveTowerIds` / `ActiveEnemyIds` 移除。

### 4.4 状态机（Phase 循环）

```
Init → BuildPhase → WavePhase → Intermission → WavePhase → ... → LevelComplete → BuildPhase
                                          ↓
                                    GameOver / Victory
```

- `BuildPhase`: 建造、升级、科技树操作、自动技能。
- `WavePhase`: 完整战斗管道。
- `Intermission`: 同 WavePhase（仍运行战斗引擎，用于信息显示）。

状态转换由 `StateMachine` 管理，`FrameScheduler.Phase` 跟随同步，系统按 Phase 门控执行。

### 4.5 配置驱动架构

- 技能、科技树、怪物类型、行为树、波次、阶段行为均从 JSON 加载。
- `GameConfigLoader` 在启动时一次性解析所有配置，运行时通过 `GameConfig` 实例只读访问。
- 科技树效果缓存在 `TechTreeSystem` 内部字段（如 `GetFinalAttackDamage()` 合并 base × mult），避免每帧重复计算。

---

## 5. 开发规范

### 5.1 代码风格

- 使用中文注释（与现有代码保持一致）。
- 系统类命名：`XxxSystem.cs`，职责单一。
- 组件命名：`XxxComponent` 为概念名，实际实现为 `ComponentStore` 中的 SOA 数组字段。
- 日志前缀约定：
  - `[BOOTSTRAP]` — 初始化流程
  - `[INFO]` — 一般信息
  - `[HEALTH]` — 血量变化
  - `[TECH]` — 科技树效果
  - `[PHASE]` — 阶段切换
  - `[SHIELD]` — 护盾相关
  - `[TEST]` — 测试/调试行为

### 5.2 并行与性能规范

- 热路径使用 `Parallel.For`，支持 `MaxDegreeOfParallelism`。
- 禁止在系统热路径中 `new Random()`；使用类级 `static readonly Random` 或线程局部随机。
- `GetAllActiveEnemyIds()` 不再在循环内调用；由 `SetTurn()` 时缓存一次。
- 禁止每帧分配 List/字典；复用数组或缓存。
- 空间查询使用 `SpatialGrid`，不使用 `GridSpatialHash`（已废弃删除）。

### 5.3 GAS 规范

- `Core/GAS/` 为技能与效果的核心定义。
- `AreaShapeType`: Single=0, Cross=1, Box=2, Circle=3, Chain=4, Heal=5, Shield=6, Line=7, Freeze=8。
- `EffectType`: Instant / Duration / Periodic。
- 护盾链路：`CastShield → ApplyPlayerShield → DecreasePlayerHealth(先扣护盾) → 每回合衰减 duration`。

---

## 6. 测试策略

### 6.1 测试框架

- **xUnit**（`Xunit`），测试项目 `BattleSystemECS.Tests`（TargetFramework=`net9.0`）。
- 测试运行器：`xunit.runner.visualstudio`，覆盖率收集：`coverlet.collector`。
- 当前测试数量：**73 项**（全部通过为门禁要求）。

### 6.2 测试组织

| 测试类 | 关注领域 |
|--------|----------|
| `CombatResolutionTests` | 实体销毁、死亡结算幂等性、多塔同目标击杀只计一次、活跃列表不变量 |
| `ComponentStoreTests` | SOA 存储生命周期、BeginFrame / Resolve 语义 |
| `FrameSchedulerTests` | 帧调度顺序、Phase 门控 |
| `GameConfigIntegrationTests` | 配置加载与整合 |
| `GameConfigLoaderTests` | Loader 单元 |
| `GameplayAbilityTests` | GAS 属性与效果基础 |
| `GameSimulationTests` | 端到端模拟 |
| `SkillSystemTests` | 技能施放、DoT、护盾、范围形状 |
| `TowerPlacementSystemTests` | 塔放置与移除 |
| `UpgradeSystemTests` | 玩家升级阈值触发 |
| `WaveSpawningSystemTests` | 波次生成与事件 |

### 6.3 测试辅助

- `MockRenderer`：实现 `IRenderer` 的空桩，用于无控制台输出的系统测试。
- 测试中直接构造 `ComponentStore` 和系统实例，不依赖 `GameManager` 的完整初始化链（单元测试隔离原则）。

---

## 7. 性能基准与门禁

> **门禁是硬要求，任何代码改动后必须验证，否则禁止提交。**

| 压测模式 | 说明 | 门禁 FPS | 允许误差 |
|----------|------|----------|----------|
| mode 2 | 合并热路径（手写合并循环，含完整 skill+buff） | ≥ 12,000 | ±5% |
| mode 4 | 真实系统链路（调用真实系统 Update 链，含完整 skill+buff） | ≥ 7,000 | ±5% |

**mode 2 与 mode 4 语义不同，禁止用一个数字代表全部性能。**

运行方式：
```bash
echo 2 | dotnet run   # 或 dotnet run 2
echo 4 | dotnet run   # 或 dotnet run 4
```

---

## 8. 提交前检查清单（强制）

> 严格按顺序执行，**全部通过后才能 `git commit`**。

1. **`dotnet build`** — 确认 0 warnings, 0 errors。
2. **`dotnet test BattleSystemECS.Tests`** — 确认全部通过（当前 73/73）。
3. **`echo 2 | dotnet run`** — mode 2 压测，确认 FPS 无下降（±5% 误差内）。
4. **`echo 4 | dotnet run`** — mode 4 压测，确认主指标无下降（±5% 误差内）。
5. **同步文档** — 若修改了架构、Bug 状态或性能数字，更新：
   - `AGENTS.md`
   - `README.md`
   - `docs/architecture.md`
   - `docs/design-and-bugs.md`
6. **`git add -A && git commit -m "描述"`** — 原子性最小改动，一个 commit 只做一件事。
7. **`git push`** — commit 完成后立即推送。

### Git 提交风格

- ✅ `fix: DestroyEntity remove from ActiveEnemyIds`
- ❌ `fix and perf various issues`（禁止大而全的 commit）

### 禁止事项

- ❌ 禁止在 build / test / 压测未全部通过的情况下提交。
- ❌ 禁止跳过压测直接提交。
- ❌ 禁止 `git reset` / `rebase` 前不 commit 当前改动。
- ❌ 禁止运行 `git commit`、`git push`、`git reset`、`git rebase` 等操作，除非用户**明确请求**。

---

## 9. 安全与稳定性注意事项

- **数组越界**：`ComponentStore.MAX_ENTITIES = 100,000`。`CreateEntity()` 在回收池耗尽或越界时返回 `-1`，调用方需检查返回值。
- **并发写**：任何新增并行循环必须经过两阶段模式审查。禁止在 `Parallel.For` 内修改共享数组元素或调用 `EventBus.Publish`。
- **配置有效性**：`game_config.json` 和 `Data/Configs/*.json` 在启动时加载，运行时缺失或格式错误会导致 `GameConfigLoader` 抛异常。修改 JSON 结构时必须同步更新 Loader 解析代码。
- **Nullable 与 ImplicitUsings**：
  - 主项目：`#nullable disable`，`ImplicitUsings=disable`。
  - 测试项目：`#nullable enable`，`ImplicitUsings=enable`。
  主项目代码不需要可空注解，但测试项目可以。保持现有设置，不要随意统一。

---

## 10. 关键文件速查

| 需求 | 文件 |
|------|------|
| 添加新组件字段 | `Core/ComponentStore.cs`（SOA 数组声明 + 初始化 + DestroyEntity 清理） |
| 添加新系统 | `Systems/XxxSystem.cs` + `Core/GameManager.cs` 初始化 + `Core/FrameScheduler.cs` 注入 |
| 修改帧顺序 | `Core/FrameScheduler.cs` 的 `Tick()` 方法 |
| 修改并行策略 | 对应系统的 `Update()`，注意两阶段模式审查 |
| 修改配置格式 | `Core/GameConfig.cs`（类定义）+ `Core/GameConfigLoader.cs`（解析）+ `Data/Configs/*.json` |
| 修改技能/效果 | `Core/GAS/*.cs` + `Systems/SkillSystem.cs` + `Data/Configs/skills.json` |
| 修改科技树 | `Core/TechTreeDef.cs` + `Systems/TechTreeSystem.cs` + `Data/Configs/tech_tree.json` |
| 修改行为树 | `Data/Configs/behavior_trees.json` + `Systems/BehaviorTreeEvaluator.cs` |
| 修改测试 | `BattleSystemECS.Tests/XxxTests.cs` |
| 查看 Bug 历史 | `docs/design-and-bugs.md` |
| 查看架构决策 | `docs/architecture.md` |
| 查看开发理念 | `docs/philosophy.md` |

---

> **最后更新**：2026-05-24（基于当前 HEAD 全面重写）

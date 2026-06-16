# BattleSystem-ECS — AI Agent 项目指南

> 本文件面向 AI Coding Agent。阅读者被假设为**完全不了解本项目**的外部代理。所有信息均基于当前代码库的实际内容，不做推测。

---

## 1. 项目概述

**BattleSystem-ECS** 是一个基于 **SOA (Struct of Arrays) ECS 架构**的塔防战斗系统性能基准项目。
- **语言**: C#，**双项目结构**：
  - `BattleSystemECS.Core` — 战斗逻辑核心库（netstandard2.1，LangVersion=9.0，Unity 兼容）
  - `BattleSystemECS` — 控制台 EXE（net6.0，引用 Core 库）
- **架构**: SOA ECS（逻辑与渲染完全分离），事件总线（`IBattleEventBus`）驱动渲染
- **运行时**: 控制台应用（含交互式游戏 + 非交互式压测）+ Unity 2D 渲染端
- **核心特征**: 全系统并行化 (`Parallel.For`)、零分配热路径、配置驱动、帧末统一结算
- **代码规模**: Core 库 ~52k 行（Core + Systems）、Tests ~17k 行；1332 项 xUnit 测试
- **Unity 工程**: `F:\AI\BattleSystem-ECS-Unity`（2022.3.62f2c1 LTS），通过 `BattleDriver` 消费 DLL

---

## 2. 技术栈与构建系统

### 2.1 关键配置文件

| 文件 | 作用 |
|------|------|
| `BattleSystemECS.Core/BattleSystemECS.Core.csproj` | **核心库** — netstandard2.1，LangVersion=9.0，编译 Core/ + Systems/ 全部代码。含 polyfill（IsExternalInit、Rng、PolyfillExtensions）和 IBattleEventBus/ConsoleEventBus |
| `BattleSystemECS.csproj` | 主 EXE — net6.0，引用 Core 库，仅含 Program.cs |
| `BattleSystemECS.Tests/BattleSystemECS.Tests.csproj` | 测试项目 — net9.0，引用 Core 库（不含 EXE） |
| `game_config.json` | 运行时游戏主配置（怪物类型、关卡、波次、玩家属性、连击参数），`CopyToOutputDirectory=PreserveNewest` |
| `Data/Configs/*.json` | 子配置：行为树、技能、科技树、塔位规则、波次生成、阶段行为、自动技能 |
| `Data/Monsters/*.json` | 200 种怪物静态定义 |
| `Data/Skills/*.json` | 150 种技能静态定义 |
| `Data/Towers/*.json` | 150 种塔静态定义 |
| `Data/Levels/*.json` | 5 个关卡配置 |

**注意**：没有 `.sln` 解决方案文件，直接使用 `dotnet build` / `dotnet test` 对单个 `.csproj` 操作。Core 库使用 Linked Files 方式编译，不会重复存储源码。

### 2.2 构建与运行命令

```bash
# 构建核心库（netstandard2.1，必须 0 warnings 0 errors）
dotnet build BattleSystemECS.Core

# 构建主程序（net6.0，引用 Core）
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

### 3.1 双项目架构（Core Library + EXE）

```
BattleSystem-ECS/
├── BattleSystemECS.Core/              # 核心库（netstandard2.1）
│   ├── BattleSystemECS.Core.csproj    # 通过 Linked Files 编译 Core/ + Systems/
│   ├── Core/IsExternalInit.cs        # Polyfill: init; 语法支持
│   ├── Core/Rng.cs                   # Polyfill: Random.Shared 替代
│   ├── Core/PolyfillExtensions.cs    # Polyfill: CollectionsMarshal.AsSpan
│   ├── Core/IBattleEventBus.cs       # 事件总线接口（逻辑→渲染）
│   └── Core/ConsoleEventBus.cs       # 控制台事件总线实现
├── BattleSystemECS.csproj             # 控制台 EXE（net6.0，仅含 Program.cs）
├── BattleSystemECS.Tests/             # 单元测试（net9.0，引用 Core）
├── Core/                              # ECS 核心与基础设施（编译到 Core 库）
│   ├── ComponentStore.cs              # SOA 核心：常量、Position、实体生命周期、死亡队列、查询
│   ├── ComponentStore_Enemy.cs        # SOA 敌人字段 + 访问方法
│   ├── ComponentStore_Tower.cs        # SOA 塔字段 + 访问方法
│   ├── ComponentStore_Player.cs       # SOA 玩家字段 + 访问方法
│   ├── ComponentStore_World.cs        # SOA 世界/环境字段 + 访问方法
│   ├── Entity.cs                      # 极简实体：仅含 int Id
│   ├── EntityManager.cs               # 实体创建/销毁/命名
│   ├── GameManager.cs                 # 游戏主循环、系统初始化、关卡循环
│   ├── FrameScheduler.cs              # 统一帧调度器（所有帧路径唯一入口，含事件发射）
│   ├── SystemRegistry.cs              # 所有 system 的集中创建/依赖注入/group 分配
│   ├── StateMachine.cs                # 游戏状态机
│   ├── IRenderer.cs                   # 渲染接口（调试日志用，保留不动）
│   ├── ConsoleLogger.cs               # 控制台渲染器实现
│   ├── FileLogger.cs                  # 文件日志实现
│   ├── GameConfig.cs                  # 运行时配置类定义
│   ├── GameConfigLoader.cs            # JSON 配置加载器
│   ├── SpatialGrid.cs                 # 空间网格（O(1) cell 访问，range 查询）
│   ├── TechTreeDef.cs                 # 科技树配置结构
│   ├── CurveTable.cs                  # 曲线表（伤害/经验成长）
│   ├── *Group.cs（12 个）             # SystemGroup 定义
│   ├── GAS/                           # Gameplay Ability System 模块
│   │   ├── Attributes.cs
│   │   ├── GameplayEffect.cs
│   │   └── GameplayAbility.cs
│   └── [枚举]: BuffType / EnemyActionType / TowerTargetingMode / TowerType / DamageType / ElementType / GameState
├── Systems/                           # 游戏系统（编译到 Core 库，20+ 个）
│   ├── WaveSpawningSystem.cs          # 波次生成 + 难度曲线（含事件发射）
│   ├── EnemyAISystem.cs               # 行为树评估（两阶段：并行评估 + 串行执行）
│   ├── EnemyMovementSystem.cs         # 敌人移动
│   ├── EnemyAbilitySystem.cs          # 敌人技能
│   ├── PlayerTowerAttackSystem.cs     # 玩家攻击（含事件发射）
│   ├── TowerAttackSystem.cs           # 塔攻击（含事件发射）
│   ├── TowerPlacementSystem.cs        # 塔放置/出售（含事件发射）
│   ├── TowerSynergySystem.cs          # 塔协同增益
│   ├── SkillSystem.cs                 # GAS 技能施放
│   ├── BuffSystem.cs                  # DoT 追踪
│   ├── ProjectileSystem.cs            # 投射物更新（含事件发射）
│   ├── ComboSystem.cs                 # 连击计数与衰减
│   ├── TechTreeSystem.cs              # 科技树解锁与效果缓存
│   ├── GoldSystem.cs                  # 击杀产金与波次结算
│   ├── BenchmarkSystem.cs             # 性能压测
│   └── ...（共 20+ 个）
├── Data/                              # 静态数据与运行时配置
│   ├── Configs/
│   ├── Levels/                        # 5 个关卡
│   ├── Monsters/                      # 200 种怪物
│   ├── Skills/                        # 150 种技能
│   └── Towers/                        # 150 种塔
├── docs/ / Research/                  # 架构文档、研究日志
├── Program.cs                         # 入口（游戏 / 压测 / 微基准）
└── game_config.json                   # 运行时主配置
```

### 3.2 SystemGroup 模式与 SystemRegistry

`FrameScheduler` 不直接持有 system，而是通过 **SystemGroup 模式**解耦调度与 system 实现：

- 12 个 `*Group.cs` 位于 `Core/`：`BuildGroup` / `PreGameGroup` / `SpawningGroup` / `AIGroup` / `MovementGroup` / `TerrainGroup` / `CombatSetupGroup` / `SpatialGroup` / `CombatGroup` / `SkillBuffGroup` / `PostDeathGroup`
- 每个 group 是一组相关 system + 固定执行顺序。
- `Core/SystemRegistry.cs` 集中负责所有 system 的 `CreateAll` / `WireDependencies` / `AssignToGroups`。
- 添加新 system 的标准流程：
  1. `SystemRegistry` 加 `public XxxSystem? Xxx { get; private set; }`
  2. `CreateAll()` 中 `new`（传入 `battleEventBus` 参数）
  3. `WireDependencies()` 中调 `SetXxx(...)` 注入依赖
  4. `AssignToGroups()` 中分配到正确的 `scheduler.Group.Xxx = Xxx`

新增 system 必须通过 `SystemRegistry` 注入，不要直接改 `FrameScheduler`。

### 3.3 事件总线（IBattleEventBus）

战斗逻辑通过 `IBattleEventBus` 接口与渲染层解耦：

- **接口**：`Core/IBattleEventBus.cs` — 定义事件类型（EntityCreated/EntityDestroyed/EntityKilled/PositionChanged/DamageDealt/ProjectileFired/WaveStarted/GameEnded）
- **控制台实现**：`Core/ConsoleEventBus.cs` — 调试用空操作
- **Unity 实现**：`BattleSystem-ECS-Unity/Assets/Scripts/UnityEventBus.cs` — 消费事件创建/更新 GameObject
- **注入路径**：`GameManager` → `SystemRegistry.CreateAll(battleEventBus)` → 各个 System（WaveSpawning、TowerPlacement、TowerAttack、PlayerTowerAttack、Projectile）+ `FrameScheduler`（Movement/Death 事件）
- **ComponentStore 不持有 eventBus 引用**（纯数据层原则）

### 3.4 Unity 渲染端

`F:\AI\BattleSystem-ECS-Unity`（Unity 2022.3.62f2c1 LTS）：

```
Assets/
├── Plugins/
│   └── BattleSystemECS.Core.dll      # 核心库 DLL（构建产物）
├── Scripts/
│   ├── BattleDriver.cs               # MonoBehaviour，每帧调用FrameScheduler.Tick()
│   ├── UnityEventBus.cs              # 实现IBattleEventBus，管理GameObject生命周期
│   └── UnityLogger.cs               # 替代ConsoleLogger的Unity日志
├── Scenes/
│   └── Main.unity                    # 2D 主场景
└── StreamingAssets/                  # JSON 配置文件
```

### 3.5 GAS 子模块

`Core/GAS/` 是独立的 Gameplay Ability System 模块：`Attributes.cs` / `GameplayEffect.cs` / `GameplayAbility.cs`（含 `AreaShapeType` 枚举等）。技能、护盾、Buff 的核心定义都在此。

---

## 4. 运行时架构与数据流

### 4.1 ECS 存储模型（SOA）

`ComponentStore` 使用 `partial class` 按领域拆分为 5 个文件：

- **ComponentStore.cs**: 核心生命周期 — MAX_ENTITIES=100000、实体创建/销毁、活跃 ID 管理（swap-and-pop O(1)）、死亡队列（ping-pong 双缓冲）
- **ComponentStore_Enemy.cs**: 敌人字段（Health, Armor, CC, Bleed, AI 等）
- **ComponentStore_Tower.cs**: 塔字段（Damage, Range, Targeting, Projectile 等）
- **ComponentStore_Player.cs**: 玩家字段（Health, Shield, Mana, Buff, Combo 等）
- **ComponentStore_World.cs**: 世界/环境字段（Weather, DayNight, Corpse, Skill, GAS 等）

**关键规则**：
- 热路径直接数组索引访问，禁止字典查询或 struct 复制。
- `ActiveTowerIds` / `ActiveEnemyIds` 只缓存活跃实体。

### 4.2 统一帧调度（FrameScheduler）

`FrameScheduler.Tick(deltaTime, turn)` 是所有帧路径的**唯一入口**。

13 阶段（WavePhase），每个对应一个 `Core/*Group.cs`：

```
1. BeginFrame()          # 重置 damage/death 队列
2. WaveSpawning.Update() # 生成敌人 + OnEntityCreated/OnWaveStarted 事件
3. EnemyAI.SetTurn + Update()
4. EnemyAbility.SetTurn + Update()
5. EnemyMovement.SetTurn + Update()  # 移动后发射 OnPositionChanged
6. RebuildSpatialGrid()
7. PlayerTowerAttack.Update()
8. TowerAttack.Update()
9. TowerSynergy.Update()
10. Buff.Update() + Skill.ResolveSkillDamage()
11. Skill.Update(deltaTime)
12. ResolveEnemiesKilledThisFrame()  # 帧末统一死亡结算 + OnEntityKilled/OnEntityDestroyed
```

BuildPhase 时只运行 `BuildGroup`，不运行任何战斗系统。

### 4.3 两阶段并行安全模式

```
并行段（Parallel.For）
  → 只读组件数据，收集 damage/death 事件到线程局部结构
  → 禁止写共享状态或 EventBus

串行段（帧末统一结算）
  → 从收集结构取出事件，串行 apply
  → QueueEnemyDeath → ResolveEnemiesKilledThisFrame() 统一销毁
```

### 4.4 状态机

```
Init → BuildPhase → WavePhase → Intermission → WavePhase → ... → LevelComplete → BuildPhase
                                          ↓
                                    GameOver / Victory
```

### 4.5 配置驱动架构

- 技能、科技树、怪物类型、行为树、波次均从 JSON 加载。
- `GameConfigLoader` 在启动时一次性解析，运行时通过 `GameConfig` 只读访问。

---

## 5. 开发规范

### 5.1 代码风格

- 使用中文注释（与现有代码保持一致）。
- 系统类命名：`XxxSystem.cs`，职责单一。
- 日志前缀约定：`[BOOTSTRAP]` / `[INFO]` / `[HEALTH]` / `[TECH]` / `[PHASE]` / `[SHIELD]` / `[TEST]`

### 5.2 并行与性能规范

- 热路径使用 `Parallel.For`。
- 禁止在热路径中 `new Random()`；使用 `Rng.Shared`（Core 库 polyfill）。
- `GetAllActiveEnemyIds()` 不在循环内调用；由 `SetTurn()` 缓存。
- 禁止每帧分配 List/字典；复用数组或缓存。
- 空间查询使用 `SpatialGrid`。

### 5.3 项目引用规则

- **Main EXE 和 Tests 都只引用 Core 库**（不直接引用 Core/ 或 Systems/ 源码）。
- Core 库通过 Linked Files（`<Compile Include="..\Core\*.cs" Link="..." />`）编译源码，不复制。
- 修改 Core/ 或 Systems/ 下的文件后，两个项目（Core 库 + 引用方）都需重新编译验证。
- Polyfill 文件（`IsExternalInit.cs`、`Rng.cs`、`PolyfillExtensions.cs`）仅存在于 Core 库项目目录，不放在 Core/。

---

## 6. 测试策略

### 6.1 测试框架

- **xUnit**（`Xunit`），测试项目 `BattleSystemECS.Tests`（TargetFramework=`net9.0`，引用 Core 库）。
- 测试运行器：`xunit.runner.visualstudio`，覆盖率收集：`coverlet.collector`。
- 当前测试数量：**1332 项**（全部通过为门禁要求）。

### 6.2 测试辅助

- `MockRenderer`：实现 `IRenderer` 的空桩。
- 测试中直接构造 `ComponentStore` 和系统实例，不依赖 `GameManager` 完整初始化链。

---

## 7. 性能基准与门禁

> **门禁是硬要求，任何代码改动后必须验证，否则禁止提交。**

### 7.1 基准（业务扩展暂停前，2026-06-09）

| 压测模式 | 说明 | 当前 FPS | 硬门禁 |
|----------|------|----------|---------|
| mode 2 | 合并热路径（10K 敌 ×500 帧） | 7,385 | ≥ 7,000 |
| mode 4 | 真实系统链路（10K 敌 ×500 帧） | 3,125 | ≥ 3,000 |
| mode 5 | 完整一局（5 关全通） | 2,848 | ≥ 2,500 |

### 7.2 相对门禁

- 与上一轮相比，mode 2 / mode 4 / mode 5 任何一项 FPS **不得下降超过 ±5%**。
- 三个模式全部测一遍；不能只跑一个模式就提交。

### 7.3 运行方式

```bash
echo 2 | dotnet run
echo 4 | dotnet run
echo 5 | dotnet run
```

---

## 8. 提交前检查清单（强制）

> 严格按顺序执行，**全部通过后才能 `git commit`**。

1. **`dotnet build BattleSystemECS.Core`** — Core 库 0 warnings, 0 errors
2. **`dotnet build`** — EXE 0 warnings, 0 errors
3. **`dotnet test BattleSystemECS.Tests`** — 全部通过（当前 1332/1332）
4. **`echo 2 | dotnet run`** — mode 2 压测
5. **`echo 4 | dotnet run`** — mode 4 压测
6. **`echo 5 | dotnet run`** — mode 5 压测
7. **同步文档** — 更新 `AGENTS.md` / `README.md` / `docs/` / `CHANGELOG.md`
8. **`git add -A && git commit -m "描述"`** — 原子性最小改动
9. **`git push github master`** — commit 完成后立即推送

### Git 提交风格

- ✅ `fix: DestroyEntity remove from ActiveEnemyIds`
- ❌ `fix and perf various issues`（禁止大而全的 commit）

### 禁止事项

- ❌ 禁止在 build / test / 压测未全部通过的情况下提交
- ❌ 禁止跳过压测直接提交
- ❌ 禁止 `git reset` / `rebase` 前不 commit 当前改动
- ❌ 禁止运行 `git commit`、`git push` 等操作，除非用户**明确请求**

---

## 9. 安全与稳定性注意事项

- **数组越界**：`MAX_ENTITIES = 100,000`。`CreateEntity()` 越界返回 `-1`，调用方需检查。
- **并发写**：新增并行循环必须经过两阶段模式审查。禁止在 `Parallel.For` 内修改共享数组元素。
- **配置有效性**：JSON 结构修改时必须同步更新 `GameConfigLoader`。
- **Nullable 与 ImplicitUsings**：
  - Core 库 / 主项目：`#nullable disable`，`ImplicitUsings=disable`
  - 测试项目：`#nullable enable`，`ImplicitUsings=enable`
  - 保持现有设置，不要统一。
- **netstandard2.1 兼容**：新增代码可能依赖 .NET 6+ API（如 `Random.Shared`、`init;` 语法），需要：
  - 用 `Rng.Shared` 替代 `Random.Shared`
  - 确保 `IsExternalInit` polyfill 已覆盖
  - 用 `PolyfillExtensions` 替代 `CollectionsMarshal.AsSpan`

---

## 10. 关键文件速查

| 需求 | 文件 |
|------|------|
| 添加新组件字段 | 对应领域的 `Core/ComponentStore_Xxx.cs` |
| 添加新系统 | `Systems/XxxSystem.cs` + 在 `Core/SystemRegistry.cs` 注册（4 步：属性 / `CreateAll` / `WireDependencies` / `AssignToGroups`） |
| 修改帧顺序 | `Core/FrameScheduler.cs` 的 `RunWavePhase()` 方法 |
| 添加事件发射 | `Core/IBattleEventBus.cs`（接口）+ 对应 System / FrameScheduler（发射点） |
| 修改并行策略 | 对应系统的 `Update()`，注意两阶段模式审查 |
| 修改配置格式 | `Core/GameConfig.cs` + `Core/GameConfigLoader.cs` + `Data/Configs/*.json` |
| 修改技能/效果 | `Core/GAS/*.cs` + `Systems/SkillSystem.cs` + `Data/Configs/skills.json` |
| 修改科技树 | `Core/TechTreeDef.cs` + `Systems/TechTreeSystem.cs` + `Data/Configs/tech_tree.json` |
| 修改行为树 | `Data/Configs/behavior_trees.json` + `Systems/BehaviorTreeEvaluator.cs` |
| 修改 Polyfill | `BattleSystemECS.Core/Core/{IsExternalInit,Rng,PolyfillExtensions}.cs` |
| 修改测试 | `BattleSystemECS.Tests/XxxTests.cs` |
| 查看 Bug 历史 | `docs/design-and-bugs.md` |
| 查看架构决策 | `docs/architecture.md` |
| 查看 CodeReview 改进 | `Research/CodeReview_Improvements.md` |

---

> **最后更新**：2026-06-09（业务扩展暂停，文档同步当前状态：双项目架构、事件总线、Unity 渲染端、polyfill、1332 tests）

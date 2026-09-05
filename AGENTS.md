# BattleSystem-ECS — AI Agent 项目指南

> 本文件面向 AI Coding Agent。阅读者被假设为**完全不了解本项目**的外部代理。所有信息均基于当前代码库的实际内容，不做推测。

---

## 1. 项目概述

**BattleSystem-ECS** 是一个基于 **SOA (Struct of Arrays) ECS 架构**的塔防战斗系统性能基准项目。
- **语言**: C#，**四项目结构**：
  - `BattleSystemECS.Engine` — 帧执行与内容值合同（netstandard2.1，不引用 Core/Systems）
  - `BattleSystemECS.Core` — 战斗逻辑核心库（netstandard2.1，LangVersion=9.0，引用 Engine，Unity 兼容）
  - `BattleSystemECS` — 控制台 EXE（net6.0，引用 Core）
  - `BattleSystemECS.Tests` — xUnit 测试（net9.0，仅项目引用 Core；Engine 合同通过 Core 暴露，不添加直接引用）
- **架构**: SOA ECS（逻辑与渲染完全分离），事件总线（`IBattleEventBus`）驱动渲染
- **运行时**: 控制台应用（含交互式游戏 + 非交互式压测）+ Unity 2D 渲染端
- **核心特征**: 全系统并行化 (`Parallel.For`)、零分配热路径、配置驱动、帧末统一结算
- **代码规模**: Core 库与 Tests 按当前工作树构建；xUnit 测试数量以最新门禁日志为准（框架层 / 机制层 / 业务层 / 集成层四分层）
- **Unity 工程**: `F:\AI\BattleSystem-ECS-Unity`（2022.3.62f2c1 LTS），通过 `BattleDriver` 消费 DLL

---

## 2. 技术栈与构建系统

### 2.1 关键配置文件

| 文件 | 作用 |
|------|------|
| `BattleSystemECS.Engine/BattleSystemECS.Engine.csproj` | **引擎合同库** — netstandard2.1，仅编译 `Contracts.cs`；定义 `IFrameContext` / `IFrameNode` / `IFrameExecutionPlan` 等帧执行和值合同，不引用 Core/Systems |
| `BattleSystemECS.Core/BattleSystemECS.Core.csproj` | **核心库** — netstandard2.1，LangVersion=9.0，引用 Engine，编译 Core/ + Systems/ 全部代码。含 polyfill（IsExternalInit、Rng、PolyfillExtensions）、事件总线（IBattleEventBus/ConsoleEventBus + EventBus/GameEvents） |
| `BattleSystemECS.csproj` | 主 EXE — net6.0，引用 Core 库，仅含 Program.cs |
| `BattleSystemECS.Tests/BattleSystemECS.Tests.csproj` | 测试项目 — net9.0，仅项目引用 Core（不含 EXE；Core 内部消费 Engine） |
| `game_config.json` | 运行时游戏主配置（怪物类型、关卡、波次、玩家属性、连击/TowerOvercharge/PositionalDamage 参数），`CopyToOutputDirectory=PreserveNewest` |
| `Data/Configs/*.json` | 子配置：行为树、技能（skills.json = 精选技能表，加载进 `GameConfig.SkillDefs`）、科技树、塔位规则、波次生成、阶段行为、自动技能 |
| `Data/Monsters/*.json` | 200 种怪物静态定义 |
| `Data/Skills/*.json` | 150 种技能静态定义（按名去重合并进 `GameConfig.SkillDefs`，精选条目优先） |
| `Data/Towers/*.json` | 150 种塔静态定义 |
| `Data/Levels/*.json` | 5 个关卡配置 |

**注意**：没有 `.sln` 解决方案文件，直接使用 `dotnet build` / `dotnet test` 对单个 `.csproj` 操作。Core 库使用 Linked Files 方式编译，不会重复存储源码。

### 2.2 构建与运行命令

```bash
# 构建 Engine 合同库（netstandard2.1，必须 0 warnings 0 errors）
dotnet build BattleSystemECS.Engine

# 构建核心库（会引用 Engine，必须 0 warnings 0 errors）
dotnet build BattleSystemECS.Core

# 构建主程序（net6.0，引用 Core；本机若存在被 ignore 的 .sln，裸 `dotnet build` 会 MSB1011）
dotnet build BattleSystemECS.csproj

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

### 3.1 目录与职责

```
BattleSystem-ECS/
├── BattleSystemECS.Engine/          # 独立帧执行/内容值合同；Core → Engine，Engine 不反向引用 Core/Systems
├── BattleSystemECS.Core/            # 核心库项目（仅 csproj；源码经 Linked Files 链接编译，不复制）
├── BattleSystemECS.csproj           # 控制台 EXE（net6.0，仅 Program.cs）
├── BattleSystemECS.Tests/           # 单元测试（xUnit，net9.0，仅引用 Core；四分层见 §6）
│   ├── Infrastructure/              # 测试基建（TestWorld / Specs / MockRenderer，无 [Fact]）
│   ├── Framework/                   # 框架层测试
│   ├── Mechanisms/                  # 机制层测试（Combat / Control / Perception / Movement / Spawning / World / TowerCore）
│   ├── Features/                    # 业务层测试（Towers / Enemies / Bosses / Skills / Economy / Buffs / World）
│   ├── Integration/                 # 集成层测试（读取真实配置，只断言结构自洽与相对关系）
│   └── README.md                    # 测试分层与编写规范（恒真断言/纯 smoke 禁止规则）
├── Core/                            # ECS 核心与基础设施（编译到 Core 库）
│   ├── ComponentStore*.cs           # SOA 存储，5 个 partial：核心生命周期 / 敌人 / 塔 / 玩家 / 世界
│   ├── FrameScheduler.cs            # 统一帧调度器（所有帧路径唯一入口）
│   ├── SystemRegistry.cs            # 所有 system 的集中创建 / 依赖注入 / group 分配
│   ├── GameManager.cs               # 游戏主循环、系统初始化、关卡循环
│   ├── IBattleEventBus.cs           # 事件总线接口（逻辑→渲染）
│   ├── ConsoleEventBus.cs           # 事件总线空实现（含 NullEventBus）
│   ├── EventBus.cs                  # 系统间类型化事件总线 EventChannel<T>（零分配）
│   ├── GameEvents.cs                # 系统间事件 DTO 定义
│   ├── GAS/                         # Gameplay Ability System（Attributes / GameplayEffect / GameplayAbility）
│   ├── *Group.cs（11 个）           # SystemGroup 定义（对应帧调度各阶段）
│   ├── ISystemGroup.cs              # SystemGroup 接口
│   ├── 枚举                          # BuffType / DamageType / ElementType / EnemyActionType / GameState / TowerTargetingMode / TowerType
│   ├── Polyfill                     # IsExternalInit / Rng / PolyfillExtensions（netstandard2.1 兼容）
│   └── 配置 / 日志 / 工具            # GameConfig(+Loader) / SpatialGrid / TechTreeDef / CurveTable / *Logger / StateMachine / Entity(+Manager) 等
├── Systems/                         # 游戏系统（144 个，编译到 Core 库）
│   └── 按职责拆分（生成 / AI / 移动 / 攻击 / 塔 / 技能 / Buff / 经济 / 天气 / 地形 …），逐一举列无意义，统一经 SystemRegistry 装配
├── Data/                            # 静态数据与运行时配置
│   ├── Configs/                     # 行为树、技能、科技树、塔位、波次、阶段、自动技能
│   ├── Levels/                      # 5 个关卡
│   ├── Monsters/                    # 200 种怪物
│   ├── Skills/                      # 150 种技能
│   └── Towers/                      # 150 种塔
├── docs/ / Research/                # 架构文档、研究日志；迁移计划在 docs/plan/
├── Program.cs                       # 入口（游戏 / 压测 / 微基准）
└── game_config.json                 # 运行时主配置
```

### 3.2 SystemGroup 模式与 SystemRegistry

`FrameScheduler` 不直接持有 system，而是通过 **SystemGroup 模式**解耦调度与 system 实现：

- 11 个 `*Group.cs` 位于 `Core/`（`BuildGroup` / `PreGameGroup` / `SpawningGroup` / `AIGroup` / `MovementGroup` / `TerrainGroup` / `CombatSetupGroup` / `SpatialGroup` / `CombatGroup` / `SkillBuffGroup` / `PostDeathGroup`），另加 `ISystemGroup.cs` 接口。
- 每个 group 是一组相关 system + 固定执行顺序（对应 §4.2 的帧调度各阶段）。
- `ProductionSystemInstaller` 是生产组装唯一入口；`SystemRegistry` 的 `CreateAll` / `WireDependencies` / `AssignToGroups` 仅保留为受 session guard 约束的兼容 facade。
- 添加新 system 的标准流程：
  1. `SystemRegistry` 加 `public XxxSystem? Xxx { get; private set; }`，在 `Core/SystemRegistrationRecipes.cs` 实现 typed Factory/Wire/Bind 方法
  2. 在 schema v3 `tools/system-registration-spec.json` 仅用受控方法标识选择 recipe，并显式声明 owner token、依赖、feature policy 与 frame bindings；禁止嵌入 C# 语句
  3. 运行 `tools/generate-system-registry-ledger.ps1`，同步生成 manifest 与 nullable ledger
  4. 由 `ProductionSystemInstaller` 执行稳定依赖顺序并在 Seal 时验证 graph↔manifest 双向合同

新增 system 必须通过 `SystemRegistry` 注入，不要直接改 `FrameScheduler`。

### 3.3 事件总线（两套，职责不同）

战斗逻辑用**两套**事件机制解耦，边界不要混淆：

1. **`IBattleEventBus`（逻辑 → 渲染）** — 战斗逻辑向视图层（Unity）推送展示事件。
   - **接口**：`Core/IBattleEventBus.cs` — 定义 EntityCreated/TowerCreated/EntityDestroyed/PositionChanged(s)/DamageDealt/EntityKilled/ProjectileFired/WaveStarted/GameOver
   - **实现**：`Core/ConsoleEventBus.cs` 的 `NullEventBus`（空操作，压测/无渲染用）+ Unity 侧 `UnityEventBus.cs`（消费事件创建/更新 GameObject）
- **注入路径**：`GameManager` → `ProductionSystemInstaller`（唯一生产组装入口）→ `SystemRegistry` 与 `FrameScheduler`；旧 facade 仅供受 guard 约束的兼容/测试路径。

2. **`EventBus` / `EventChannel<T>`（系统 → 系统）** — 系统间类型化事件（`PlayerDamaged` / `EnemyHit` / `EnemyCrit` / `EnemyCharging` / `EnemyChargeReleased` / `BossPhaseChanged` / `SideQuestCompleted`），定义在 `Core/EventBus.cs` + `Core/GameEvents.cs`。零分配（多播委托单次调用），在 `SystemRegistry` 中构造并注入。

> 注意：`ComponentStore` 持有 `OnEnemyKilled` / `OnTowerKill` 两个 C# 事件作为死亡通知中枢（死亡唯一事实源在 store）；订阅数组在注册期构建，dispatch 逐项容错且无每次死亡分配。store **不持有**任何 eventBus 引用。

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
- GAS `EffectPool` 保持固定逻辑容量与 generation handle，但 handle 元数据和 runtime payload
  按 256 槽分页；观测走内部 `GameplayObservation.Capture`，生产 Tick 不自动采样。

### 4.2 统一帧调度（FrameScheduler）

`FrameScheduler.Tick(deltaTime, turn)` 是所有帧路径的**唯一入口**，内部按 **SystemGroup** 顺序执行（`Build` → `PreGame` → `Spawning` → `AI` → `Movement` → `Terrain` → `CombatSetup` → `Spatial` → `Combat` → `SkillBuff` → `PostDeath`）。每个 group 通过 `Execute(store, dt, turn)` 驱动其内部的全部 system。

```
BeginFrame()              # 重置 damage/death 队列 + I-frames/Phaser/Blinker 倒计时
Build.Execute()           # 仅 BuildPhase：经济/建造类系统；WavePhase 跳过
PreGame.Execute()         # 天气/昼夜/难度/随机事件（enemyDt）
Spawning.Execute()        # 波次/巢穴生成（enemyDt）
AI.Execute()              # 行为树/技能/挖掘/死灵/生命链接/词缀（enemyDt）
Movement.Execute()        # 移动/伤口/寻路/地形修饰（enemyDt）
EmitPositionEvents()      # 移动后发射 OnPositionsChanged（逻辑→渲染）
Terrain.Execute()         # 地形/变异/变形（enemyDt）
CombatSetup.Execute()     # 战斗系统 SetTurn 缓存（enemyDt）
Spatial.Execute()         # 空间网格重建 + 巡逻/时光/迷雾/预警（enemyDt）
# PreCombat 图节点（无独立 Group）：skill-buff.skill.update / global-skill / tower-active-skill / hero-skill / ability.commit
Combat.Execute()          # 攻击/协同/光环/投射物（combatDt，全速）；含 combat.rally.consume（tower-attack 前）
SkillBuff.Execute()       # effect.commit + effect.tick + DoT/流血/rally（combatDt）
ResolveEnemiesKilledThisFrame()  # 帧末统一死亡结算（dt-free）
PostDeath.Execute()       # 分裂/生命链接/目标/资源/尸体/连击（combatDt）；corpse 后 post-death.effect.commit
Threat Score EMA 更新      # 玩家 DPS 威胁分指数滑动平均
```

> 说明：bullet-time（子弹时间）开启时，`enemyDt`（敌人侧 7 个 group）按比例减速，`combatDt`（玩家/塔攻击侧）保持全速——战术暂停效果。BuildPhase 只运行 `BuildGroup`，不运行任何战斗系统。`skill-buff.skill.update` 已挪到 Combat 前只入队；`ability.commit` 在 PreCombat drain 执行项并入队 granted `EffectRequests`，`effect.commit` 在 SkillBuff 开头、`effect.tick` 之前统一 `TryApply`。Build 相位 `build.ability.commit` 在 shop-reroll 之后。`AbilityRequests` / `EffectRequests` 是真 command buffer（Seal 后 Activate/ApplyDot/CommitPlan-GE 只入队，对应 commit 节点 `CommitPlan`/`TryApply`）；`frame.begin` 对未消费队列记 `Unconsumed*` 并清空。`build.skill/auto-skill/global-skill.update` 写 `AbilityRequests`；`post-death.corpse.update` 写 `EffectRequests`。Rally 激活走 `combat.rally.consume` + `skill-buff.rally.update` 消费 `DamageApplied`，writes 为 `PlayerAttributes` + `TowerState`，不再订 `PlayerDamaged`。

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
- 模拟热路径禁止 `new Random()` / `Rng.Shared`；只从 Frame `DeterminismContext` 领号，且仅 `CommitSerial`（本仓 SerialUpdate / SerialCommit / SerialPrepare / InternalParallelCollectSerialCommit 的串行段）取数。并行工作线程不得领号。`Rng.Shared` 仅保留给压测等非模拟路径。比赛种子由 `GameManager.Initialize` 开局 `Reset` 一次；系统构造器不得改种。
- `GetAllActiveEnemyIds()` 不在循环内调用；由 `SetTurn()` 缓存。
- 禁止每帧分配 List/字典；复用数组或缓存。
- 空间查询使用 `SpatialGrid`。

### 5.3 项目引用规则

- 引用方向固定为 **Engine ← Core ← EXE**；Tests 仅引用 Core，由 Core 间接消费 Engine。Engine 禁止引用 Core/Systems，Core/Systems 禁止反向把具体 content 类型暴露进 Engine。
- `BattleSystemECS.Content.Contracts` namespace 当前由 Core 的 `Core/ContentContracts.cs` 编译并拥有业务 port/interface；Engine 只拥有 `BattleSystemECS.Engine` 的帧执行和值合同，禁止引入具体 `BattleSystemECS.Systems.*` 类型。
- Main EXE 不直接引用 Core/ 或 Systems/ 源码；Tests 通过项目引用消费 Core/Engine。
- Core 库通过 Linked Files（`<Compile Include="..\Core\*.cs" Link="..." />`）编译源码，不复制。
- 修改 Core/ 或 Systems/ 下的文件后，两个项目（Core 库 + 引用方）都需重新编译验证。
- Polyfill 文件（`IsExternalInit.cs`、`Rng.cs`、`PolyfillExtensions.cs`）位于顶层 `Core/` 目录，通过 csproj 的 `<Compile Include="..\Core\*.cs">` Linked Files 编译进 Core 库（不在 `BattleSystemECS.Core/` 项目目录内）。

---

## 6. 测试策略

### 6.1 测试框架

- **xUnit**（`Xunit`），测试项目 `BattleSystemECS.Tests`（TargetFramework=`net9.0`，引用 Core 库）。
- 测试运行器：`xunit.runner.visualstudio`，覆盖率收集：`coverlet.collector`。
- 当前测试数量以最新完整门禁日志为准；全部通过是门禁要求，文档不维护易腐的手工计数。
- **测试范围分层**（目录即分层，详见 `BattleSystemECS.Tests/README.md`）：
  - **Infrastructure**：`TestWorld` / `Specs` / `MockRenderer` / `BattleTestBase` 共享基建，不含测试。
  - **Framework 框架层**：ECS 存储生命周期、帧调度与死亡结算、状态机、配置加载、GAS 冷却、曲线表、技能核心、时光快照。
  - **Mechanisms 机制层**：伤害公式 / 抗性 / 处决 / 标记 / 光环 / 多目标 / 控制免疫 / 仇恨威胁 / 移动寻路 / 生成难度 / 地形区域 / 塔位公共机制。
  - **Features 业务层**：具名塔 / 敌怪 / Boss / 技能 / 经济 / 具名 Buff / 天气，只保留有真实断言的测试。
  - **Integration 集成层**：读取 `game_config.json` 作为输入，只断言结构自洽与相对关系（引用存在、唯一性、字段有效范围、数量与配置推导值一致），不钉住任何具体配置值。
- **测试质量规则**：禁止 `Assert.True(true)` 恒真断言与调试残留；每个 `[Fact]` 至少一条有意义断言；随机数固定种子；**允许读取配置数据，但禁止把配置中的某个具体值当固定常量断言**（期望值从读取结果推导或由测试代码注入，如 `TestWorld.DisablePerTypeTowerCaps`）；`AddTower` 已自动注册活跃塔列表，不要再重复 `AddActiveTowerId`（需要绕过注册的存储层直测用原始数组操作并注释原因），驱动塔攻击前需 `RebuildSpatialGrid()`（见 README）。

### 6.2 测试辅助

- `MockRenderer`：实现 `IRenderer` 的空桩（位于 `Infrastructure/`）。
- 测试中直接构造 `ComponentStore` 和系统实例，不依赖 `GameManager` 完整初始化链；共享工厂经 `BattleTestBase` / `TestWorld` 使用。

---

## 7. 性能基准与门禁

> **门禁是硬要求，任何代码改动后必须验证，否则禁止提交。**

### 7.1 性能门禁

性能压测结果、相对门禁和延期状态以最新 fresh evidence 为准；当前迁移轮次暂不运行
mode2/mode4/mode5，未运行项不得标记为通过。

M8 fresh evidence 必须在 evidence 首写前捕获 HEAD/branch/index/patch/status/untracked hashes，
并在全部命令后复核仓库状态未漂移。mode 2/4/5 的 `DEFERRED` 和 Unity 的
`UNAVAILABLE/BLOCKED` 必须与 PASS command manifest 分开记录。

### 7.2 运行方式

```bash
echo 2 | dotnet run   # mode 2：stdin 菜单路径（Program.cs 读取 "2"）
echo 4 | dotnet run   # mode 4：stdin 菜单路径（Program.cs 读取 "4"）
dotnet run -- 5       # mode 5：必须走命令行参数路径；stdin 输入 "5" 不会进入压测分支
```

---

## 8. 提交前检查清单（强制）

> 严格按顺序执行，**全部通过后才能 `git commit`**。

1. **`dotnet build BattleSystemECS.Engine`** — Engine 0 warnings, 0 errors
2. **`dotnet build BattleSystemECS.Core`** — Core 库 0 warnings, 0 errors
3. **`dotnet build BattleSystemECS.csproj`** — EXE 0 warnings, 0 errors（不要对含 sln 的工作目录裸 `dotnet build`）
4. **`dotnet test BattleSystemECS.Tests`** — 当前发现的全部测试通过
5. **`pwsh -File tools\check-test-rules.ps1`** — 测试静态规则 0 违规（零断言测试 + 恒真/恒假断言）
6. **`git diff --check`** — 无空白/行尾错误（CRLF、trailing whitespace）
7. **`echo 2 | dotnet run`** — mode 2 压测
8. **`echo 4 | dotnet run`** — mode 4 压测
9. **`dotnet run -- 5`** — mode 5 压测（注意：参数模式，不能用 `echo 5 | dotnet run`）
10. **同步文档** — 更新 `AGENTS.md` / `README.md` / `docs/` / `CHANGELOG.md`
11. **`git add -A && git commit -m "描述"`** — 原子性最小改动
12. **`git push github master`** — commit 完成后立即推送

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
  - 模拟热路径用 Frame `DeterminismContext` 领号，禁止 `Random.Shared` / `Rng.Shared` / 无种子 `new Random()`
  - 非模拟（压测等）若仍需要线程局部 Random，用 `Rng.Shared` 替代 `Random.Shared`
  - 确保 `IsExternalInit` polyfill 已覆盖
  - 用 `PolyfillExtensions` 替代 `CollectionsMarshal.AsSpan`

---

## 10. 关键文件速查

| 需求 | 文件 |
|------|------|
| 添加新组件字段 | 对应领域的 `Core/ComponentStore_Xxx.cs` |
| 添加新系统 | `Systems/XxxSystem.cs` + `Core/SystemRegistry.cs` 属性 + `Core/SystemRegistrationRecipes.cs` typed recipe + schema v3 `tools/system-registration-spec.json`；运行生成器同步 manifest/ledger，并由 installer/Seal 校验 |
| 修改帧顺序 | `Core/FrameSystemGraph.cs`（节点 reads/writes 与更名）+ `Core/FrameBindingFacts.cs`；生产 Tick 前必须 `SealGraphComposition` |
| 添加事件发射 | 逻辑→渲染走 `Core/IBattleEventBus.cs`；系统间走 `Core/EventBus.cs` + `Core/GameEvents.cs`（DTO） |
| 修改并行策略 | 对应系统的 `Update()`，注意两阶段模式审查 |
| 修改配置格式 | `Core/GameConfig.cs` + `Core/GameConfigLoader.cs` + `Data/Configs/*.json` |
| 修改技能/效果 | `Core/GAS/*.cs` + `Systems/SkillSystem.cs` + `game_config.json` Skills 数组（玩家技能栏）；共享技能表（SkillDefs）= `Data/Configs/skills.json`（精选）+ `Data/Skills/*.json`（静态）。技能 id/name 互解析统一走 `GameConfig.GetSkillIdByName / TryGetSkillById / GetSkillDisplayName`（归一化索引空间：[0, SkillDefs.Count) 索引共享表，其后偏移索引 Skills），消费方禁止各自手写遍历 |
| 模拟随机领号 | `Core/DeterminismContext.cs`（挂在 `ComponentStore.Determinism`）；开局由 `GameManager.Initialize` `Reset` 一次；生产 Tick 由 `FrameGraph.Execute` 按节点语义在 CommitSerial 取数 |
| 玩家伤害写入 | 生产路径只走 `ComponentStore.ApplyPlayerDamageAuthority`（`ResourceResolver.TryApply(PlayerDamageRequest)`）；`DecreasePlayerHealth` 仅允许 `ResourceResolver` 调用 |
| 修改科技树 | `Core/TechTreeDef.cs` + `Systems/TechTreeSystem.cs` + `Data/Configs/tech_tree.json` |
| 修改行为树 | `Data/Configs/behavior_trees.json` + `Systems/BehaviorTreeEvaluator.cs` |
| 修改 Polyfill | `Core/{IsExternalInit,Rng,PolyfillExtensions}.cs`（经 Linked Files 编译进 Core 库） |
| 修改测试 | `BattleSystemECS.Tests/<层级>/XxxTests.cs`（分层规则见 `BattleSystemECS.Tests/README.md`） |
| 观察 GAS 稳定性/容量 | `Core/GAS/GameplayObservation.cs` + `tools/capture-m8-fresh-evidence.ps1`；只读显式采样，不接入生产 Tick |
| CI 测试静态规则 | `tools/check-test-rules.ps1`（0 违规门禁：零断言测试 + 恒真/恒假断言） |
| 查看 Bug 历史 | `docs/design-and-bugs.md` |
| 查看架构决策 | `docs/architecture.md` |
| 查看 ECS+GAS 迁移计划 | `docs/plan/ecs-gas-migration-plan.md` |
| 查看 Lumio 对照收口计划 | `docs/plan/ecs-gas-lumio-contract-alignment.md` |
| 查看 CodeReview 改进 | `Research/CodeReview_Improvements.md` |

---

> **最后更新**：2026-09-05。当前构建、测试与审计结果以本轮仓外 final evidence 目录及其原始日志为准；文档不维护易腐的手工测试计数。

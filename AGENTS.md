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
- **代码规模**: Core 库 ~52k 行（Core + Systems）、Tests ~22k 行；1317 项 xUnit 测试（框架层 / 机制层 / 业务层 / 集成层四分层）
- **Unity 工程**: `F:\AI\BattleSystem-ECS-Unity`（2022.3.62f2c1 LTS），通过 `BattleDriver` 消费 DLL

---

## 2. 技术栈与构建系统

### 2.1 关键配置文件

| 文件 | 作用 |
|------|------|
| `BattleSystemECS.Core/BattleSystemECS.Core.csproj` | **核心库** — netstandard2.1，LangVersion=9.0，编译 Core/ + Systems/ 全部代码。含 polyfill（IsExternalInit、Rng、PolyfillExtensions）、事件总线（IBattleEventBus/ConsoleEventBus + EventBus/GameEvents） |
| `BattleSystemECS.csproj` | 主 EXE — net6.0，引用 Core 库，仅含 Program.cs |
| `BattleSystemECS.Tests/BattleSystemECS.Tests.csproj` | 测试项目 — net9.0，引用 Core 库（不含 EXE） |
| `game_config.json` | 运行时游戏主配置（怪物类型、关卡、波次、玩家属性、连击/TowerOvercharge/PositionalDamage 参数），`CopyToOutputDirectory=PreserveNewest` |
| `Data/Configs/*.json` | 子配置：行为树、技能（skills.json = 精选技能表，加载进 `GameConfig.SkillDefs`）、科技树、塔位规则、波次生成、阶段行为、自动技能 |
| `Data/Monsters/*.json` | 200 种怪物静态定义 |
| `Data/Skills/*.json` | 150 种技能静态定义（按名去重合并进 `GameConfig.SkillDefs`，精选条目优先） |
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

### 3.1 目录与职责

```
BattleSystem-ECS/
├── BattleSystemECS.Core/            # 核心库项目（仅 csproj；源码经 Linked Files 链接编译，不复制）
├── BattleSystemECS.csproj           # 控制台 EXE（net6.0，仅 Program.cs）
├── BattleSystemECS.Tests/           # 单元测试（xUnit，net9.0，引用 Core 库；四分层见 §6）
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
├── docs/ / Research/                # 架构文档、研究日志
├── Program.cs                       # 入口（游戏 / 压测 / 微基准）
└── game_config.json                 # 运行时主配置
```

### 3.2 SystemGroup 模式与 SystemRegistry

`FrameScheduler` 不直接持有 system，而是通过 **SystemGroup 模式**解耦调度与 system 实现：

- 11 个 `*Group.cs` 位于 `Core/`（`BuildGroup` / `PreGameGroup` / `SpawningGroup` / `AIGroup` / `MovementGroup` / `TerrainGroup` / `CombatSetupGroup` / `SpatialGroup` / `CombatGroup` / `SkillBuffGroup` / `PostDeathGroup`），另加 `ISystemGroup.cs` 接口。
- 每个 group 是一组相关 system + 固定执行顺序（对应 §4.2 的帧调度各阶段）。
- `Core/SystemRegistry.cs` 集中负责所有 system 的 `CreateAll` / `WireDependencies` / `AssignToGroups`。
- 添加新 system 的标准流程：
  1. `SystemRegistry` 加 `public XxxSystem? Xxx { get; private set; }`
  2. `CreateAll()` 中 `new`（传入 `battleEventBus` 参数）
  3. `WireDependencies()` 中调 `SetXxx(...)` 注入依赖
  4. `AssignToGroups()` 中分配到正确的 `scheduler.Group.Xxx = Xxx`

新增 system 必须通过 `SystemRegistry` 注入，不要直接改 `FrameScheduler`。

### 3.3 事件总线（两套，职责不同）

战斗逻辑用**两套**事件机制解耦，边界不要混淆：

1. **`IBattleEventBus`（逻辑 → 渲染）** — 战斗逻辑向视图层（Unity）推送展示事件。
   - **接口**：`Core/IBattleEventBus.cs` — 定义 EntityCreated/TowerCreated/EntityDestroyed/PositionChanged(s)/DamageDealt/EntityKilled/ProjectileFired/WaveStarted/GameOver
   - **实现**：`Core/ConsoleEventBus.cs` 的 `NullEventBus`（空操作，压测/无渲染用）+ Unity 侧 `UnityEventBus.cs`（消费事件创建/更新 GameObject）
   - **注入路径**：`GameManager` → `SystemRegistry.CreateAll(battleEventBus)` → 各 system + `FrameScheduler`（Movement/Death 事件）

2. **`EventBus` / `EventChannel<T>`（系统 → 系统）** — 系统间类型化事件（`PlayerDamaged` / `EnemyHit` / `EnemyCrit` / `EnemyCharging` / `EnemyChargeReleased` / `BossPhaseChanged` / `SideQuestCompleted`），定义在 `Core/EventBus.cs` + `Core/GameEvents.cs`。零分配（多播委托单次调用），在 `SystemRegistry` 中构造并注入。

> 注意：`ComponentStore` 持有 `OnEnemyKilled` / `OnTowerKill` 两个 C# 事件作为死亡通知中枢（死亡唯一事实源在 store），但**不持有**任何 eventBus 引用。

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
Combat.Execute()          # 攻击/协同/光环/投射物（combatDt，全速）
SkillBuff.Execute()       # 技能结算 + DoT + 流血（combatDt）
ResolveEnemiesKilledThisFrame()  # 帧末统一死亡结算（dt-free）
PostDeath.Execute()       # 分裂/生命链接/目标/资源/尸体/连击（combatDt）
Threat Score EMA 更新      # 玩家 DPS 威胁分指数滑动平均
```

> 说明：bullet-time（子弹时间）开启时，`enemyDt`（敌人侧 7 个 group）按比例减速，`combatDt`（玩家/塔攻击侧）保持全速——战术暂停效果。BuildPhase 只运行 `BuildGroup`，不运行任何战斗系统。

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
- Polyfill 文件（`IsExternalInit.cs`、`Rng.cs`、`PolyfillExtensions.cs`）位于顶层 `Core/` 目录，通过 csproj 的 `<Compile Include="..\Core\*.cs">` Linked Files 编译进 Core 库（不在 `BattleSystemECS.Core/` 项目目录内）。

---

## 6. 测试策略

### 6.1 测试框架

- **xUnit**（`Xunit`），测试项目 `BattleSystemECS.Tests`（TargetFramework=`net9.0`，引用 Core 库）。
- 测试运行器：`xunit.runner.visualstudio`，覆盖率收集：`coverlet.collector`。
- 当前测试数量：**1317 项**（全部通过为门禁要求）。
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

### 7.1 基准（2026-08-27，并行热路径优化后）

| 压测模式 | 说明 | 当前 FPS | 硬门禁 |
|----------|------|----------|---------|
| mode 2 | 合并热路径（10K 敌 ×500 帧） | 8,333 | ≥ 7,000 |
| mode 4 | 真实系统链路（10K 敌 ×500 帧） | 5,212 | ≥ 3,000 |
| mode 5 | 完整一局（5 关全通） | 4,874 | ≥ 2,500 |

### 7.2 相对门禁

- 与上一轮相比，mode 2 / mode 4 / mode 5 任何一项 FPS **不得下降超过 ±5%**。
- 三个模式全部测一遍；不能只跑一个模式就提交。

### 7.3 运行方式

```bash
echo 2 | dotnet run   # mode 2：stdin 菜单路径（Program.cs 读取 "2"）
echo 4 | dotnet run   # mode 4：stdin 菜单路径（Program.cs 读取 "4"）
dotnet run -- 5       # mode 5：必须走命令行参数路径；stdin 输入 "5" 不会进入压测分支
```

---

## 8. 提交前检查清单（强制）

> 严格按顺序执行，**全部通过后才能 `git commit`**。

1. **`dotnet build BattleSystemECS.Core`** — Core 库 0 warnings, 0 errors
2. **`dotnet build`** — EXE 0 warnings, 0 errors
3. **`dotnet test BattleSystemECS.Tests`** — 全部通过（当前 1317/1317）
4. **`pwsh -File tools\check-test-rules.ps1`** — 测试静态规则 0 违规（零断言测试 + 恒真/恒假断言）
5. **`git diff --check`** — 无空白/行尾错误（CRLF、trailing whitespace）
6. **`echo 2 | dotnet run`** — mode 2 压测
7. **`echo 4 | dotnet run`** — mode 4 压测
8. **`dotnet run -- 5`** — mode 5 压测（注意：参数模式，不能用 `echo 5 | dotnet run`）
9. **同步文档** — 更新 `AGENTS.md` / `README.md` / `docs/` / `CHANGELOG.md`
10. **`git add -A && git commit -m "描述"`** — 原子性最小改动
11. **`git push github master`** — commit 完成后立即推送

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
| 添加事件发射 | 逻辑→渲染走 `Core/IBattleEventBus.cs`；系统间走 `Core/EventBus.cs` + `Core/GameEvents.cs`（DTO） |
| 修改并行策略 | 对应系统的 `Update()`，注意两阶段模式审查 |
| 修改配置格式 | `Core/GameConfig.cs` + `Core/GameConfigLoader.cs` + `Data/Configs/*.json` |
| 修改技能/效果 | `Core/GAS/*.cs` + `Systems/SkillSystem.cs` + `game_config.json` Skills 数组（玩家技能栏）；共享技能表（SkillDefs）= `Data/Configs/skills.json`（精选）+ `Data/Skills/*.json`（静态）。技能 id/name 互解析统一走 `GameConfig.GetSkillIdByName / TryGetSkillById / GetSkillDisplayName`（归一化索引空间：[0, SkillDefs.Count) 索引共享表，其后偏移索引 Skills），消费方禁止各自手写遍历 |
| 修改科技树 | `Core/TechTreeDef.cs` + `Systems/TechTreeSystem.cs` + `Data/Configs/tech_tree.json` |
| 修改行为树 | `Data/Configs/behavior_trees.json` + `Systems/BehaviorTreeEvaluator.cs` |
| 修改 Polyfill | `Core/{IsExternalInit,Rng,PolyfillExtensions}.cs`（经 Linked Files 编译进 Core 库） |
| 修改测试 | `BattleSystemECS.Tests/<层级>/XxxTests.cs`（分层规则见 `BattleSystemECS.Tests/README.md`） |
| CI 测试静态规则 | `tools/check-test-rules.ps1`（0 违规门禁：零断言测试 + 恒真/恒假断言） |
| 查看 Bug 历史 | `docs/design-and-bugs.md` |
| 查看架构决策 | `docs/architecture.md` |
| 查看 CodeReview 改进 | `Research/CodeReview_Improvements.md` |

---

> **最后更新**：2026-08-29（第三批：技能/战斗系统可维护性重构 —— 技能 id 归一化约定集中 `GameConfig.GetSkillIdByName/TryGetSkillById`、SkillSystem 9 处圆形 AoE 谓词收敛 `CollectCircleHits`、ExecuteAbility switch 命名常量化、TowerAttackSystem 朝向 dot 提取 `TryComputeRearDot` 共用；净 -177 行，1298 测试全过。同日第二批：死配置接线 SkillDefs/TowerOvercharge/PositionalDamage + hero 技能槽修复 + game_config.json 修复为合法 JSON，测试 1281→1298）

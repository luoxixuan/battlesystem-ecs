
# Unity Integration MVP — Implementation Plan (v2)

> **Status: ✅ COMPLETED** — Phase 1-5 已实施，Phase 6-7 待实施。
> **Hermes:** 用户确认后方可执行。每 Phase 结束跑 `dotnet test` 验证通过再继续。
> **事件策略：方案 B** — ComponentStore 保持纯净，事件由 FrameScheduler + 各系统按需发射。

**Goal:** 将 BattleSystem-ECS 战斗逻辑抽成独立 Class Library（netstandard2.1），通过 IBattleEventBus 与 Unity 2D 渲染层解耦，同时保持所有测试可独立运行。

---

## 架构图

```
Unity 2D (不实现)
  └─ BattleDriver : MonoBehaviour
       └─ FrameScheduler.Tick(dt, turn)
            └─ IBattleEventBus (UnityEventBus)

BattleSystemECS.Core (netstandard2.1, Class Library)
  ├─ ComponentStore (纯数据，无事件依赖)    ← 不改
  ├─ FrameScheduler (注入 eventBus)        ← 发射位置变化 + 击杀事件
  ├─ 各 System (构造注入 eventBus)          ← 发射伤害/投射物/波次事件
  ├─ IBattleEventBus / NullEventBus        ← 新增
  └─ IRenderer (保留)                       ← 不改

BattleSystemECS (Console Exe)
  ├─ Program.cs (精简)
  ├─ ConsoleEventBus                       ← 新增
  └─ GameManager (注入 eventBus)

BattleSystemECS.Tests (xunit)
  └─ 所有测试 → 不传 eventBus (默认 null) → NullEventBus 行为
```

---

## Phase 1: 项目拆分【纯机械操作，零逻辑改动】

### Step 1.1: 创建目录

```bash
mkdir -p /mnt/f/AI/BattleSystem-ECS/BattleSystemECS.Core
```

### Step 1.2: 创建 Core Library csproj

**文件:** `BattleSystemECS.Core/BattleSystemECS.Core.csproj`（Create）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <RootNamespace>BattleSystemECS</RootNamespace>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <AssemblyName>BattleSystemECS.Core</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="..\Core\*.cs" Link="Core\%(Filename)%(Extension)" />
    <Compile Include="..\Core\GAS\*.cs" Link="Core\GAS\%(Filename)%(Extension)" />
    <Compile Include="..\Systems\*.cs" Link="Systems\%(Filename)%(Extension)" />
  </ItemGroup>

  <ItemGroup>
    <None Include="..\game_config.json" Link="game_config.json" CopyToOutputDirectory="PreserveNewest" />
    <None Include="..\Data\Configs\*.json" Link="Data\Configs\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <None Include="..\Data\Towers\*.json" Link="Data\Towers\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <None Include="..\Data\Monsters\*.json" Link="Data\Monsters\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <None Include="..\Data\Levels\*.json" Link="Data\Levels\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <None Include="..\Data\Saves\*.json" Link="Data\Saves\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

### Step 1.3: 修改 Exe csproj

**文件:** `BattleSystemECS.csproj`（Modify）

删除所有 `<Compile Include="Core\*.cs" />`、`<Compile Include="Core\GAS\*.cs" />`、`<Compile Include="Systems\*.cs" />`、`<Compile Include="Program.cs" />` 行。替换为：

```xml
  <ItemGroup>
    <ProjectReference Include="BattleSystemECS.Core\BattleSystemECS.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="Program.cs" />
  </ItemGroup>

  <ItemGroup>
    <None Update="game_config.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="Data\Configs\wave_gold_decay.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="Data\Configs\path_terrain.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="Data\Configs\daily_modifiers.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

### Step 1.4: 修改 Test csproj

**文件:** `BattleSystemECS.Tests/BattleSystemECS.Tests.csproj`（Modify）

将：
```xml
<ProjectReference Include="..\BattleSystemECS.csproj" />
```
改为：
```xml
<ProjectReference Include="..\BattleSystemECS.Core\BattleSystemECS.Core.csproj" />
```

### Step 1.5: 验证 Phase 1

```bash
cd /mnt/f/AI/BattleSystem-ECS
dotnet build BattleSystemECS.Core/BattleSystemECS.Core.csproj
dotnet build BattleSystemECS.csproj
dotnet test BattleSystemECS.Tests/BattleSystemECS.Tests.csproj
```

**预期:** 全部通过。编译错误 → 可能 netstandard2.1 缺 API，根据具体错误微调。

---

## Phase 2: 定义 IBattleEventBus

### Step 2.1: 创建接口文件

**文件:** `Core/IBattleEventBus.cs`（Create）

```csharp
namespace BattleSystemECS.Core
{
    /// <summary>
    /// 战斗事件总线 — 向渲染层发结构化事件。
    /// 与 IRenderer（人类可读日志）职责分离。
    /// Unity 侧实现 UnityEventBus，测试用 NullEventBus。
    /// </summary>
    public interface IBattleEventBus
    {
        // ── 实体生命周期 ──
        void OnEntityCreated(int entityId, float x, float y, string entityType);
        void OnEntityDestroyed(int entityId);

        // ── 位置变化（移动阶段结束后批量发射）──
        void OnPositionChanged(int entityId, float x, float y);

        // ── 战斗 ──
        void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical);
        void OnEntityKilled(int entityId, int killerId);

        // ── 投射物 ──
        void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed);

        // ── 波次 / 流程 ──
        void OnWaveStarted(int waveNumber);
        void OnGameOver(bool victory);
    }

    /// <summary>
    /// NullEventBus — 所有方法空实现。测试 + headless 模式使用。
    /// 单例避免重复分配。
    /// </summary>
    public sealed class NullEventBus : IBattleEventBus
    {
        public static readonly NullEventBus Instance = new NullEventBus();

        public void OnEntityCreated(int entityId, float x, float y, string entityType) { }
        public void OnEntityDestroyed(int entityId) { }
        public void OnPositionChanged(int entityId, float x, float y) { }
        public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { }
        public void OnEntityKilled(int entityId, int killerId) { }
        public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
        public void OnGameOver(bool victory) { }
        public void OnWaveStarted(int waveNumber) { }
    }
}
```

### Step 2.2: 创建 ConsoleEventBus

**文件:** `Core/ConsoleEventBus.cs`（Create）

```csharp
namespace BattleSystemECS.Core
{
    /// <summary>
    /// ConsoleEventBus — 把事件转成 Console.WriteLine。
    /// 用于维持现有命令行模式。大部分事件静默（日志已在 IRenderer），
    /// 只在波次/游戏结束时输出。
    /// </summary>
    public sealed class ConsoleEventBus : IBattleEventBus
    {
        public void OnEntityCreated(int entityId, float x, float y, string entityType) { }
        public void OnEntityDestroyed(int entityId) { }
        public void OnPositionChanged(int entityId, float x, float y) { }
        public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { }
        public void OnEntityKilled(int entityId, int killerId) { }
        public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }

        public void OnWaveStarted(int waveNumber)
        {
            System.Console.WriteLine($"[EVENT] Wave {waveNumber} started");
        }

        public void OnGameOver(bool victory)
        {
            System.Console.WriteLine(victory ? "[EVENT] Victory!" : "[EVENT] Game Over!");
        }
    }
}
```

### Step 2.3: 验证 Phase 2

```bash
dotnet build BattleSystemECS.Core/BattleSystemECS.Core.csproj
```

---

## Phase 3: FrameScheduler 持有 eventBus【方案 B — 集中发射】

### Step 3.1: FrameScheduler 构造函数

**文件:** `Core/FrameScheduler.cs`（Modify）

```csharp
// 新增字段
private readonly IBattleEventBus _eventBus;

// 修改构造函数，增加 eventBus 参数（可选，默认 NullEventBus）
public FrameScheduler(ComponentStore store, GameConfig gameConfig, IBattleEventBus eventBus = null)
{
    this.store = store ?? throw new ArgumentNullException(nameof(store));
    _ = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
    _eventBus = eventBus ?? NullEventBus.Instance;
}
```

### Step 3.2: FrameScheduler 发射移动事件

在 `RunWavePhase` 中，Movement 阶段执行完后，遍历 ActiveEnemyIds 发射位置事件：

```csharp
// 在 Movement.Execute(store, enemyDt, turn); 之后增加：
EmitPositionEvents();
```

新增私有方法：

```csharp
private void EmitPositionEvents()
{
    var activeEnemies = store.ActiveEnemyIds;
    for (int i = 0; i < activeEnemies.Count; i++)
    {
        int eid = activeEnemies[i];
        float x = store.PositionX[eid];
        float y = store.PositionY[eid];
        _eventBus.OnPositionChanged(eid, x, y);
    }
}
```

### Step 3.3: FrameScheduler 发射击杀事件

在 `ResolveEnemiesKilledThisFrame` 之后发射事件。需要在 `RunWavePhase` 中捕获被击杀的敌人。

**方案：** 在 `RunWavePhase` 的 Phase 10（死亡处理）之后遍历 `DeathQueue`（或增加一个 killed-this-frame 列表）。

更简单的方案：在 `ComponentStore.ResolveEnemiesKilledThisFrame()` 内部已经遍历了死亡队列，我们在 FrameScheduler 中再遍历一次 `Store.DeathQueue`（死亡队列是公开的）。

但实际上 `DeathQueue` 是 List<int[]>（每个元素是 {enemyId, killerId}）。让我确认...

我先在框架上预留接口，实际实现时根据 ComponentStore 的死亡队列结构来调用。

伪代码（实际实现时需根据 ComponentStore 字段调整）：

```csharp
// Phase 10 之后：
foreach (var entry in store.GetKilledThisFrame())
{
    _eventBus.OnEntityKilled(entry.EnemyId, entry.KillerId);
}
foreach (var entry in store.GetKilledThisFrame())
{
    _eventBus.OnEntityDestroyed(entry.EnemyId);
}
```

### Step 3.4: 检查 ComponentStore 死亡队列

需要确认 `ComponentStore` 是否有暴露死亡列表的方法。如果没有，增加一个 `GetKilledThisFrame()` 方法或直接在 FrameScheduler 中读取公开字段。

**验证:** 搜索 ComponentStore 中 `DeathQueue` 或 `KilledThisFrame` 相关代码。

---

## Phase 4: 各系统注入 eventBus【按需】

### Step 4.1: WaveSpawningSystem — 发射实体创建 + 波次开始

**文件:** `Systems/WaveSpawningSystem.cs`（Modify）

```csharp
private readonly IBattleEventBus _eventBus;

public WaveSpawningSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig,
                          IBattleEventBus eventBus = null)
{
    // ... 现有初始化 ...
    _eventBus = eventBus ?? NullEventBus.Instance;
}
```

在 `SpawnEnemy()` 方法中，创建敌人后发射：

```csharp
// 在 store.AddEnemy(...) 返回 enemyId 之后：
_eventBus.OnEntityCreated(enemyId, x, y, "Enemy");
```

在波次开始时发射：

```csharp
// 在 SpawnWave 方法入口：
_eventBus.OnWaveStarted(waveNumber);
```

### Step 4.2: TowerPlacementSystem — 发射塔创建

**文件:** `Systems/TowerPlacementSystem.cs`（Modify）

```csharp
private readonly IBattleEventBus _eventBus;

public TowerPlacementSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig, 
                            int playerId, IBattleEventBus eventBus = null)
{
    _eventBus = eventBus ?? NullEventBus.Instance;
}
```

在 `PlaceTower()` 方法中，`AddTower` 后发射：

```csharp
_eventBus.OnEntityCreated(towerId, x, y, "Tower");
```

### Step 4.3: TowerAttackSystem — 发射伤害 + 投射物

**文件:** `Systems/TowerAttackSystem.cs`（Modify）

```csharp
private readonly IBattleEventBus _eventBus;

public TowerAttackSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig = null,
                         IBattleEventBus eventBus = null)
{
    _eventBus = eventBus ?? NullEventBus.Instance;
}
```

在每次成功造成伤害后（`finalDmg > 0` 分支）：

```csharp
_eventBus.OnDamageDealt(targetId, finalDmg, damageTypeString, isCritical);
```

在发射投射物时：

```csharp
_eventBus.OnProjectileFired(fromX, fromY, toX, toY, projectileSpeed);
```

### Step 4.4: PlayerTowerAttackSystem — 同 TowerAttackSystem

**文件:** `Systems/PlayerTowerAttackSystem.cs`（Modify）

与 TowerAttackSystem 模式完全一致，增加 `IBattleEventBus` 参数和事件发射。

### Step 4.5: ProjectileSystem — 发射投射物事件

**文件:** `Systems/ProjectileSystem.cs`（Modify）

```csharp
private readonly IBattleEventBus _eventBus;

public ProjectileSystem(ComponentStore store, IRenderer renderer, IBattleEventBus eventBus = null)
{
    _eventBus = eventBus ?? NullEventBus.Instance;
}
```

在投射物创建/移动时发射。

---

## Phase 5: SystemRegistry 传递 eventBus

### Step 5.1: CreateAll 增加 eventBus 参数

**文件:** `Core/SystemRegistry.cs`（Modify）

```csharp
public void CreateAll(ComponentStore store, GameConfig gameConfig, IRenderer logger, 
                      int playerId, StateMachine stateMachine, IBattleEventBus eventBus = null)
```

在创建需要 eventBus 的系统的 new 语句中传入 eventBus：

```csharp
WaveSpawning = new WaveSpawningSystem(store, logger, gameConfig, eventBus);
TowerPlacement = new TowerPlacementSystem(store, logger, gameConfig, playerId, eventBus);
TowerAttack = new TowerAttackSystem(store, logger, gameConfig, eventBus);
// PlayerTowerAttack 在 CombatGroup 中构造，需要检查
```

### Step 5.2: 检查 CombatGroup 中的 PlayerTowerAttack

**文件:** `Core/CombatGroup.cs`（Modify）

CombatGroup 负责创建 PlayerTowerAttackSystem，需要增加 eventBus 参数传递链路。

### Step 5.3: GameManager 传入 ConsoleEventBus

**文件:** `Core/GameManager.cs`（Modify）

```csharp
private readonly IBattleEventBus _eventBus;

public GameManager()
{
    store = new ComponentStore();
    _eventBus = new ConsoleEventBus();
    // ...
}
```

在初始化和创建系统时传入：

```csharp
scheduler = new FrameScheduler(store, gameConfig, _eventBus);
registry.CreateAll(store, gameConfig, logger, playerId, stateMachine, _eventBus);
```

### Step 5.4: BenchmarkSystem 适配

**文件:** `Systems/BenchmarkSystem.cs`（Modify）

BenchmarkSystem 自己 new 系统，需要传入 `NullEventBus.Instance` 或 `null`（默认就是 NullEventBus）：

```csharp
var towerAttack = new TowerAttackSystem(store, logger, null, null);  // gameConfig=null, eventBus=null
// null eventBus → NullEventBus.Instance, 零开销
```

---

## Phase 6: GameManager 发射实体创建事件

GameManager 直接创建了一些实体（Player、Destructible），需要在这些点发射事件：

### Step 6.1: InitializePlayer 发射玩家创建

**文件:** `Core/GameManager.cs`（Modify）

```csharp
// 在 InitializePlayer 末尾：
_eventBus.OnEntityCreated(playerEntity.Id, 5f, 0f, "Player");
```

### Step 6.2: SpawnDestructiblesForLevel 发射可破坏物创建

**文件:** `Core/GameManager.cs`（Modify）

```csharp
// 在 store.AddObstacle(...) 后：
_eventBus.OnEntityCreated(oid, entry.X, entry.Y, "Destructible");
```

### Step 6.3: 塔自动部署发射创建

**文件:** `Core/GameManager.cs`（Modify）

```csharp
// PlaceTower 已经在 TowerPlacementSystem 内部发射事件，无需重复
// 但确认 TowerPlacementSystem.PlaceTower 是否被 GameManager 直接调用
```

---

## Phase 7: 全局验证

### Step 7.1: 编译

```bash
cd /mnt/f/AI/BattleSystem-ECS
dotnet build BattleSystemECS.Core/BattleSystemECS.Core.csproj
dotnet build BattleSystemECS.csproj
```

### Step 7.2: 全量测试

```bash
dotnet test BattleSystemECS.Tests/BattleSystemECS.Tests.csproj
```

### Step 7.3: Benchmark 对比

```bash
dotnet run --project BattleSystemECS.csproj 4
```

与改动前结果对比，波动应在 ±3% 以内（eventBus 空实现 + 判空 branch 极快）。

### Step 7.4: 交互模式烟雾测试

```bash
echo "1" | dotnet run --project BattleSystemECS.csproj
```

确认命令行模式正常启动、建塔、运行不出错。

---

## Phase 8: Unity 侧规格（仅文档，不实现）

### BattleDriver.cs 骨架

```csharp
using UnityEngine;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

public class BattleDriver : MonoBehaviour
{
    [SerializeField] private float _tickInterval = 1f / 60f;
    
    private ComponentStore _store;
    private FrameScheduler _scheduler;
    private GameConfig _config;
    private SystemRegistry _registry;
    private UnityEventBus _eventBus;
    
    private float _timer;
    private int _turn;

    void Awake()
    {
        _eventBus = new UnityEventBus(this);
        _store = new ComponentStore();
        // ... 初始化同 GameManager.Initialize() ...
        _scheduler = new FrameScheduler(_store, _config, _eventBus);
    }

    void Update()
    {
        _timer += Time.deltaTime;
        if (_timer >= _tickInterval)
        {
            _timer -= _tickInterval;
            _scheduler.Tick(_tickInterval, _turn++);
        }
    }
}
```

### UnityEventBus 骨架

```csharp
public class UnityEventBus : IBattleEventBus
{
    private readonly BattleDriver _driver;
    // 内部维护 entityId → GameObject 映射
    
    public void OnEntityCreated(int id, float x, float y, string type)
    {
        // Instantiate prefab by type, register mapping
    }
    
    public void OnPositionChanged(int id, float x, float y)
    {
        // Lerp transform.position
    }
    
    public void OnDamageDealt(int targetId, float amount, string type, bool crit)
    {
        // Spawn floating text + hit VFX
    }
    
    // ...
}
```

### 2D 约定

- 坐标系：x=列, y=行（格子地图）
- 格子大小：通过 `_pixelsPerUnit` 可配置
- 摄像机：orthographic，从上方俯视
- 渲染顺序：地面 → 塔 → 敌人 → 投射物 → 特效

---

## 改动文件清单

| 文件 | 操作 | Phase |
|---|---|---|
| `BattleSystemECS.Core/BattleSystemECS.Core.csproj` | Create | 1 |
| `BattleSystemECS.csproj` | Modify | 1 |
| `BattleSystemECS.Tests/BattleSystemECS.Tests.csproj` | Modify | 1 |
| `Core/IBattleEventBus.cs` | Create | 2 |
| `Core/ConsoleEventBus.cs` | Create | 2 |
| `Core/FrameScheduler.cs` | Modify | 3 |
| `Systems/WaveSpawningSystem.cs` | Modify | 4 |
| `Systems/TowerPlacementSystem.cs` | Modify | 4 |
| `Systems/TowerAttackSystem.cs` | Modify | 4 |
| `Systems/PlayerTowerAttackSystem.cs` | Modify | 4 |
| `Systems/ProjectileSystem.cs` | Modify | 4 |
| `Core/SystemRegistry.cs` | Modify | 5 |
| `Core/CombatGroup.cs` | Modify | 5 |
| `Core/GameManager.cs` | Modify | 5, 6 |
| `Systems/BenchmarkSystem.cs` | Modify | 5 |

**不改的文件：** ComponentStore 及所有 partial、其他 ~40+ System、所有 JSON、Program.cs（只精简 using）

---

## 风险与缓解

| 风险 | 缓解 | Phase |
|---|---|---|
| `netstandard2.1` 缺少 `MathF` | 替换为 `(float)Math.xxx` 或切 net6.0 | 1 |
| `netstandard2.1` 缺少 `Parallel.For` | 回退到串行 `for` / 切 net6.0 | 1 |
| 死亡队列在 ComponentStore 中无公开接口 | 增加 `GetKilledThisFrame()` 内部方法 | 3 |
| Benchmark 回归 >3% | Profile 排查事件总线分支开销。可能需内联判空 | 7 |
| SystemRegistry.CreateAll 参数爆炸 | 已有 ~6 个参数，拆成 options 类（后续重构） | 5 |

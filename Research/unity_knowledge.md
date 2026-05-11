# Unity 架构知识库 · 2026-05-11

来源：BattleSystem-ECS GitHub 研究爬虫 + Unity ECS / AbilitySystem 专项分析

---

## 一、塔防项目研究（61 仓库 · 39,946 ⭐）

### 语言分布（C# 最多）

| 语言 | 仓库数 |
|------|--------|
| C# | 20 |
| JavaScript | 10 |
| C++ | 6 |
| Python | 5 |
| GDScript | 4 |
| Java | 3 |
| TypeScript | 2 |
| Rust | 2 |

### 架构模式出现频率

| 模式 | 出现次数 |
|------|----------|
| modular | 2 (3%) |
| data driven | 2 (3%) |
| plugin system | 1 (2%) |
| config driven | 1 (2%) |
| JSON config | 1 (2%) |
| component based | 1 (2%) |
| state machine | 1 (2%) |
| FSM | 1 (2%) |
| event system | 1 (2%) |
| object pool | 1 (2%) |

### 核心结论

> **无专用 ECS 塔防项目。** 当前数据集里的 C# 项目主要用 GameFramework、组件化、FSM。建议研究通用 ECS 框架（LeoECS、Entitas、Unity DOTS）寻找模式。

---

## 二、Unity ECS 模式 · 来自优秀开源项目

### 2.1 SOA（Struct of Arrays）数据布局

**适用于**：大规模实体（10K+ 敌怪 / 塔）

```csharp
// ✅ 正确：平行数组，CPU 缓存友好
public float[] PositionX = new float[MAX_ENTITIES];
public float[] PositionY = new float[MAX_ENTITIES];
public bool[]  EnemyActive = new bool[MAX_ENTITIES];
public float[] EnemyHealth = new float[MAX_ENTITIES];

// ❌ 错误：字典包装实体
public Dictionary<int, EnemyData> Enemies; // GC pressure + 缓存不友好
```

**性能对比**（BattleSystem-ECS 实测）：

| 规模 | 字典方案 | SOA 数组方案 |
|------|----------|--------------|
| 10K 敌 × 20 塔 | ~60 FPS | **851 FPS** |

### 2.2 空间哈希 GridSpatialHash

**格子大小 = 3**（匹配塔标准射程），用于 O(1) 邻域查询。

```csharp
// O(candidates) ≈ O(10)，替代 O(MAX_ENTITIES) 全量扫描
var candidates = store.GetEnemiesNear(tx, ty, range);
```

**维护流程**：

```
AddEnemy       → AddToSpatialHash(entityId)
EnemyMovement  → UpdateSpatialHash(entityId)  [每帧移动后]
DestroyEntity  → RemoveFromSpatialHash(entityId)
```

### 2.3 事件总线 EventBus

**13 种事件类型**，系统间通信唯一出口。

```csharp
// 发布
bus.Publish(GameEvents.EnemyKilled, new EnemyKilledEvent { EnemyId = id, GoldReward = gold });

// 订阅
bus.Subscribe(GameEvents.EnemyKilled, evt => {
    store.TotalKills++;
});
```

**约束**：

- `Publish` 前复制处理器列表（防止递归修改）
- 每个 handler 独立 try/catch
- 禁止在 `OnEnter`/`OnExit` 回调中发布同一状态转换事件

---

## 三、AbilitySystem（来自 GenshinGamePlay · ⭐443）

### 3.1 两层分离架构

```
Ability（技能层）    → 配置型，描述"做什么"
  └─ Modifier（修正层）→ 运行时应用，描述"怎么做"
```

**配置示例（JSON）**：

```json
{
  "abilityId": "fireball",
  "name": "火球术",
  "type": "projectile",
  "cooldown": 5.0,
  "modifiers": [
    { "type": "damage", "value": 100, "element": "fire" },
    { "type": "area", "radius": 3.0 },
    { "type": "buff", "effect": "burn", "duration": 3.0 }
  ]
}
```

### 3.2 AbilitySystem 核心结构

```
GamePlayAbility          — 技能实例，持有 Context
├── AbilitySpec          — 技能装备数据（等级、冷却剩余）
├── GamePlayContext      — 运行时数据（施法者、目标、触发条件）
├── ModifierSpec         — 修正实例
└── ModifierMagnitude    — 数值计算（BaseValue + Scalings）
```

### 3.3 Modifier 计算链

```
CalculateMagnitude(ModifierSpec, GamePlayContext)
  → BaseValue                          （配置值）
  → + Scalings                         （角色属性缩放，如 ATK * 1.5）
  → + ModifierStackValue               （叠加层数）
  → = FinalMagnitude
```

### 3.4 技能触发类型

| 触发类型 | 说明 |
|----------|------|
| `CastOnPressed` | 按下即施放 |
| `CastOnReleased` | 松开时施放 |
| `CastOnButtonPressedAndReleased` | 按下+松开组合 |
| `Passive` | 被动（无按钮） |
| `Charged` | 蓄力型 |
| `Channeled` | 引导型（持续施法） |

### 3.5 行为树节点（FSM/Behavior Tree 融合）

```
Composite（组合节点）
├── Sequence     — 顺序执行，全部成功才成功
├── Selector     — 选择首个成功者
└── Parallel     — 并行执行

Condition（条件节点）
├── DistanceCondition      — 距离判断
├── StateCondition        — 状态判断
├── CooldownCondition     — 冷却判断
└── BuffCondition        — Buff 判断

Action（行为节点）
├── MoveToAction          — 移动到目标
├── AttackAction          — 攻击
├── UseAbilityAction      — 使用技能
└── WaitAction            — 等待
```

---

## 四、数据驱动设计模式

### 4.1 配置与代码分离

| 内容 | 存储方式 |
|------|----------|
| 技能/Buff 配置 | JSON（`Configs/skills.json`） |
| 怪物属性 | JSON（`Monsters/monster_*.json`） |
| 关卡波次 | JSON（`Levels/level_*.json`） |
| 玩家初始状态 | JSON（`Configs/player.json`） |

### 4.2 ScriptableObject 典型用法（Unity）

```csharp
[CreateAssetMenu(fileName = "TowerData", menuName = "TowerDefense/TowerData")]
public class TowerData : ScriptableObject
{
    public string towerName;
    public int damage;
    public float range;
    public float attackSpeed;
    public Sprite icon;
}
```

### 4.3 状态机数据驱动化

```csharp
// 状态转换规则迁移到 JSON
{
  "transitions": [
    { "from": "BuildPhase", "to": "WavePhase", "condition": "player_ready" },
    { "from": "WavePhase",  "to": "Intermission", "condition": "all_waves_cleared" }
  ]
}
```

---

## 五、对象池模式

**目的**：避免 GC 峰值，提升帧率稳定性。

```csharp
// ComponentStore 维护自由 ID 栈
private Stack<int> freeEntityIds = new Stack<int>();

public int CreateEntity() {
    return freeEntityIds.Count > 0
        ? freeEntityIds.Pop()
        : nextEntityId++;
}

public void DestroyEntity(int id) {
    ClearComponents(id);           // 清理组件数据
    RemoveFromSpatialHash(id);     // 清理空间哈希
    freeEntityIds.Push(id);        // 归还 ID
}
```

---

## 六、性能优化要点

### 6.1 避免的写法

| 错误写法 | 正确做法 |
|----------|----------|
| `Dictionary<int, T>` 存实体 | 平行数组 |
| `GetAllActiveEnemyIds()` 寻敌 | `GetEnemiesNear(x, y, range)` 空间哈希 |
| 每帧 `Instantiate`/`Destroy` | 对象池复用 |
| 状态用 `bool flag1, flag2...` | 枚举 + 状态机 |

### 6.2 GC 优化

- 使用 `struct` 而非 `class` 定义组件
- 事件对象用 `struct` + 栈分配
- 避免在热路径（每帧）new 对象

### 6.3 帧率数据（BattleSystem-ECS 实测）

| 场景 | FPS |
|------|-----|
| 10K 敌 × 20 塔（全管线，行为树 AI） | **5,513 FPS** |
| 1K 敌 × 20 塔（全管线，行为树 AI） | **19,717 FPS** |
| 1K 敌（空间哈希 O(10)） vs 全量 O(10000) | 13x 提升 |

---

## 七、项目结构（BattleSystem-ECS 参考）

```
BattleSystem-ECS/
├── AGENTS.md              # 开发规则（必读）
├── Components/            # ECS 组件（数据 only）
│   ├── BuffData.cs
│   └── ...
├── Core/                 # 核心系统
│   ├── ComponentStore.cs  # SOA 数据存储 + 对象池
│   ├── EventBus.cs        # 事件总线
│   ├── GameEvents.cs      # 13 种事件定义
│   ├── GridSpatialHash.cs # 空间哈希
│   └── StateMachine.cs    # 7 状态机
├── Systems/               # 系统（逻辑）
│   ├── EnemyAISystem.cs   # 行为树
│   ├── EnemyMovementSystem.cs
│   ├── TowerAttackSystem.cs
│   └── ...
├── Configs/               # JSON 配置
├── Levels/                # 关卡配置
├── Monsters/              # 怪物配置
└── Research/
    ├── unity_knowledge.md  # 本文档
    ├── csharp_unity_resources.md
    └── findings/
```

---

## 八、参考仓库索引

| 仓库 | ⭐ | 适用场景 |
|------|----|----------|
| [526077247/GenshinGamePlay](https://github.com/526077247/GenshinGamePlay) | 443 | AbilitySystem / 行为树 |
| [No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity) | 803 | GAS 独立实现 |
| [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter) | 487 | Unity ECS 启动器 |
| [MaiKuraki/UnityGameplayAbilitySystemSample](https://github.com/MaiKuraki/UnityGameplayAbilitySystemSample) | 23 | GAS 示例 |

---

## 九、后续可引入的方向

| # | 方向 | 收益 |
|---|------|------|
| 1 | **AoE 技能配置化**（Ability + Modifier 两层分离） | 新技能只需配 JSON，改动极小 |
| 2 | **眩晕链/硬直系统**（Modifier 叠加） | 战斗深度提升 |
| 3 | **Unity Editor 可视化节点图**（行为树编辑） | 策划可直接编辑 AI，无需改代码 |
| 4 | **DOTS / Burst Compiler**（百万实体） | 性能再上台阶 |

---

## 十、ECS 实战教训（来自 BattleSystem-ECS 代码审计）

### 10.1 SOA 列表别名风险 ⚠️ HIGH

**问题**：`GetAllActiveEnemyIds()` 直接返回内部 `List<int>` 引用，调用方修改列表会污染 `ComponentStore` 内部状态。

```csharp
// ❌ 错误：返回可变引用
public List<int> GetAllActiveEnemyIds() => ActiveEnemyIds;

// ✅ 正确：返回只读副本
public List<int> GetAllActiveEnemyIds() => new List<int>(ActiveEnemyIds);
```

**后果**：任何调用方 `Sort()` / `Remove()` / `Add()` 都会直接破坏 `ComponentStore` 的内部状态，导致实体列表越界、重复遍历、状态不一致。

### 10.2 对象池回收必须同步清理所有索引

**问题**：`DestroyEntity()` 只清理了空间哈希，但没有从 `ActiveEnemyIds` 列表中移除。列表只增不减，最终 O(n) 扫描变成 O(n²)。

```csharp
// ❌ 错误：只 push 到 freeEntityIds，没有从 ActiveEnemyIds 移除
public void DestroyEntity(int id) {
    ClearComponents(id);
    RemoveFromSpatialHash(id);
    freeEntityIds.Push(id);  // ActiveEnemyIds 中该 ID 仍然存在！
}

// ✅ 正确：必须从所有索引中移除
public void DestroyEntity(int id) {
    ClearComponents(id);
    RemoveFromSpatialHash(id);
    ActiveEnemyIds.Remove(id);  // 必须显式移除
    freeEntityIds.Push(id);
}
```

### 10.3 每帧 new Random() 是性能陷阱

**问题**：热路径（每帧枚举所有实体）内创建 `Random` 实例，触发 GC。

```csharp
// ❌ 错误：热路径内 new Random
foreach (var id in store.GetAllActiveEnemyIds())
    var rand = new Random();  // 每实体一次 new = GC 噩梦

// ✅ 正确：使用类级别静态 Random 或 Random.Shared（C# 9+）
private static readonly Random rng = new();
```

### 10.4 硬编码实体 ID 是定时炸弹

**问题**：代码中假设塔 ID 为 2 和 3，但 `CreateEntity()` 动态分配，不一定连续。

```csharp
// ❌ 错误：假设 ID
towerUpgradeSystem.UpgradeTower(2);
towerUpgradeSystem.UpgradeTower(3);

// ✅ 正确：保存返回值
int towerId1 = towerPlacementSystem.PlaceTower(...);
int towerId2 = towerPlacementSystem.PlaceTower(...);
towerUpgradeSystem.UpgradeTower(towerId1);
towerUpgradeSystem.UpgradeTower(towerId2);
```

### 10.5 SkillSystem 技能初始化覆盖 bug

**问题**：`InitializePlayerSkills()` 循环中每次迭代都覆盖同一个索引，导致 3 个技能全部变成最后一个。

```csharp
// ❌ 错误：每次迭代覆盖索引 0
for (int i = 0; i < skills.Length; i++)
    store.SetPlayerSkill(0, skill);  // i 被忽略，3 次都写到 [0]

// ✅ 正确：传入正确索引
store.SetPlayerSkill(i, skill);
```

### 10.6 System/ 和 Systems/ 目录重复实现

**问题**：项目同时存在 `System/` 和 `Systems/` 两套几乎相同的文件（`TowerAttackSystem.cs`、`WaveGenerationSystem.cs` 等），csproj 只编译 `Systems/`，但源码中可能存在旧的重复实现导致混淆。

**修复**：删除 `System/` 目录下的重复文件，统一使用 `Systems/`。

---

## 十一、帧率数据更新（BattleSystem-ECS Benchmark）

| 场景 | FPS | 备注 |
|------|-----|------|
| 10K 敌 × 20 塔（全管线） | **851 FPS** | 早期版本 |
| 10K 敌 × 20 塔（行为树 AI） | **5,513 FPS** | 加入 AI 后优化显著 |
| 1K 敌 × 20 塔（行为树 AI） | **19,717 FPS** | 规模缩小 10x |
| 空间哈希 O(10) vs 全量 O(10K) | **13x 提升** | 寻敌优化 |
| 1K 敌 + 行为树（无渲染） | **20,432 FPS** | 极限值 |

> 数据来源：`dotnet run` → 输入 `2` 压测模式，`[BENCHMARK]` 输出帧时间（ms）取平均。

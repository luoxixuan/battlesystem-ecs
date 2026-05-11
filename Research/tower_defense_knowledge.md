# 塔防游戏 ECS + GAS 知识库

> 自动生成 + 手动整理 · 2026-05-12

来源：GitHub 探索 + BattleSystem-ECS 代码审计

---

## 一、塔防专项模式（来自 GitHub 探索）

### 波次生成系统
塔防核心：波次配置化、动态难度、敌人生成调度
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 敌人生成池
对象池化生成，支持波次间复用
来源：[genaray/Arch](https://github.com/genaray/Arch)

### 波次配置化
波次属性（敌人类型、数量、间隔、Buff）用 JSON/SO 管理
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 塔升级系统
塔等级/星级/进阶，属性成长曲线配置化
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 塔攻击系统
射程检测、目标选择策略（最近/血量最低/随机）
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 弹道对象池
子弹/技能弹道复用，减少 GC
来源：[genaray/Arch](https://github.com/genaray/Arch)

### 伤害计算
攻击/防御/暴击/属性缩放公式
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 技能系统（GAS 风格）
Ability + Modifier 分离，配置型技能描述 + 运行时修正
来源：[imnazake/Unify](https://github.com/imnazake/Unify), [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### Buff/Debuff 系统
叠加层数、持续时间、效果叠加规则
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 属性修正系统
加减乘除多段修正，优先级/覆盖规则
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 实体管理器（ECS 风格）
实体创建/销毁/查询，Archetype 模式
来源：[genaray/Arch](https://github.com/genaray/Arch), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 组件存储（SOA 布局）
平行数组存储，CPU 缓存友好，避免 Dictionary GC
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 空间哈希分区
GridSpatialHash O(1) 邻域查询，避免全量遍历
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 状态机 AI
敌怪状态机：移动/攻击/死亡
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 行为树
行为树节点：Sequence/Selector/Condition/Action
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### Unity DOTS Archetype
chunk data layout + entity query，Burst 编译
来源：[genaray/Arch](https://github.com/genaray/Arch), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

---

## 二、通用工程模式

### 对象池模式
预先创建对象并复用，避免频繁的创建销毁开销
来源：[Unity-Technologies/com.unity.multiplayer.samples.coop](https://github.com/Unity-Technologies/com.unity.multiplayer.samples.coop)

### 事件总线
解耦系统通信，Publish/Subscribe
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 状态机模式
状态转换清晰，支持可视化编辑
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 工厂模式
封装复杂对象创建逻辑
来源：[marinasundstrom/raven](https://github.com/marinasundstrom/raven)

### 单例模式
全局唯一实例，常用于管理器类（慎用，易造成耦合）

### 策略模式
定义一系列算法，可相互替换

### 观察者模式
一对多依赖，当对象状态变化时自动通知

### MVC / MVVM
数据、界面、控制逻辑解耦

### 依赖注入
通过外部注入依赖，而非内部创建，提高可测试性

---

## 三、Unity 实用技巧

### SerializeField
私有字段在 Inspector 显示，保持封装

### ScriptableObject 数据容器
可作为数据资产或事件载体，减少 MonoBehaviour 耦合

### Addressables 资源系统
动态资源加载，支持热更新和依赖管理

### Unity Job System / Burst
多线程计算，充分利用多核 CPU

### async/await / UniTask
异步编程，避免阻塞主线程

### Awake / OnEnable 初始化
组件绑定后立即调用，适合做依赖获取

### FixedUpdate 固定更新
按固定时间步执行物理计算，与帧率解耦

### LateUpdate 延迟更新
所有 Update 后执行，适合相机跟随

### 避免 GC 分配
频繁分配触发 GC，影响帧率，用对象池和结构体优化

### 结构体优先
小型固定数据用 struct，避免装箱开销

### 线程安全
多线程访问共享数据时需要同步机制

---

## 四、BattleSystem-ECS 代码审计教训

### SOA 列表别名风险（HIGH）
`GetAllActiveEnemyIds()` 直接返回内部 `List<int>` 引用，调用方修改列表会污染 `ComponentStore` 内部状态。
```csharp
// ❌ 错误
public List<int> GetAllActiveEnemyIds() => ActiveEnemyIds;
// ✅ 正确
public List<int> GetAllActiveEnemyIds() => new List<int>(ActiveEnemyIds);
```

### 对象池回收必须同步清理所有索引（HIGH）
`DestroyEntity()` 只 push 到 freeEntityIds，没有从 `ActiveEnemyIds` 移除，列表只增不减。
```csharp
// ❌ 错误
public void DestroyEntity(int id) {
    ClearComponents(id);
    RemoveFromSpatialHash(id);
    freeEntityIds.Push(id); // ActiveEnemyIds 中该 ID 仍存在！
}
// ✅ 正确
public void DestroyEntity(int id) {
    ClearComponents(id);
    RemoveFromSpatialHash(id);
    ActiveEnemyIds.Remove(id);
    freeEntityIds.Push(id);
}
```

### 每帧 new Random() 是性能陷阱（HIGH）
热路径内创建 Random 实例触发 GC。
```csharp
// ❌ 错误：每实体一次 new
foreach (var id in store.GetAllActiveEnemyIds())
    var rand = new Random();
// ✅ 正确：类级别静态 Random
private static readonly Random rng = new();
```

### 硬编码实体 ID 是定时炸弹（MEDIUM）
假设塔 ID 为 2 和 3，但 CreateEntity() 动态分配。
```csharp
// ❌ 错误
towerUpgradeSystem.UpgradeTower(2);
towerUpgradeSystem.UpgradeTower(3);
// ✅ 正确
int towerId1 = towerPlacementSystem.PlaceTower(...);
int towerId2 = towerPlacementSystem.PlaceTower(...);
```

### SkillSystem 技能初始化覆盖 bug（MEDIUM）
循环内 `SetPlayerSkill(0, ...)` 忽略索引，3 个技能全变成最后一个。
```csharp
// ❌ 错误
for (int i = 0; i < skills.Length; i++)
    store.SetPlayerSkill(0, skill); // i 被忽略
// ✅ 正确
for (int i = 0; i < skills.Length; i++)
    store.SetPlayerSkill(i, skill);
```

---

## 五、帧率数据（BattleSystem-ECS Benchmark）

| 场景 | FPS | 备注 |
|------|-----|------|
| 10K 敌 × 20 塔（全管线） | **851 FPS** | 早期版本 |
| 10K 敌 × 20 塔（行为树 AI） | **5,513 FPS** | 加入 AI 后优化显著 |
| 1K 敌 × 20 塔（行为树 AI） | **19,717 FPS** | 规模缩小 10x |
| 空间哈希 O(10) vs 全量 O(10K) | **13x 提升** | 寻敌优化 |
| 1K 敌 + 行为树（无渲染） | **20,432 FPS** | 极限值 |

---

## 六、后续可引入方向

| # | 方向 | 收益 |
|---|------|------|
| 1 | **AoE 技能配置化**（Ability + Modifier 两层分离） | 新技能只需配 JSON |
| 2 | **眩晕链/硬直系统**（Modifier 叠加） | 战斗深度提升 |
| 3 | **波次配置化**（JSON + 动态难度） | 策划可直接编辑波次 |
| 4 | **DOTS / Burst Compiler**（百万实体） | 性能再上台阶 |

---

## 七、参考仓库

| 仓库 | ⭐ | 适用场景 |
|------|----|----------|
| [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) | - | GAS 完整实现 |
| [imnazake/Unify](https://github.com/imnazake/Unify) | - | AbilitySystem |
| [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS) | - | ECS 架构参考 |
| [genaray/Arch](https://github.com/genaray/Arch) | - | ECS 实体管理 |
| [526077247/GenshinGamePlay](https://github.com/526077247/GenshinGamePlay) | 443 | AbilitySystem / 行为树 |
| [No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity) | 803 | GAS 独立实现 |
| [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter) | 487 | Unity ECS 启动器 |

---

## 八、实践洞察

- "That's a best practice (Based on working experience), that's not only for Attributes Sets, but for any C++ class that might get reference by another object." — Narxim/Narxim-GAS-Example
- "The reason is simple: a game should avoid rubber-banding death." — Narxim/Narxim-GAS-Example
- "Import only what you need, remove what you don't." — MaiKuraki/UnityStarter
- "> It is important to understand that this disables any checks for null, so in the release build any calls to a null object will lead to a hard crash." — scellecs/morpeh
- "Consider this list a work in progress as well as the project." — nilpunch/massive-ecs

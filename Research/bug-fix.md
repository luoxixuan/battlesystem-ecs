# Bug Fix Report — BattleSystem-ECS

> 生成日期: 2026-05-10 | 分析范围: 全部 .cs 源文件 (ECS 肉鸽塔防)

---

## HIGH (影响游戏正确性 / 运行时崩溃风险)

### 1. EventBus 注入 BuffSystem 时 bus 尚未创建 → BuffSystem 事件总线永久为 null

**文件**: `Core/GameManager.cs:150-167`

**问题**: `Initialize()` 第 150-152 行调用 `buffSystem.SetEventBus(bus)`，但 `bus = new EventBus()` 在第 158 行才执行。此时 `bus` 为 null，BuffSystem 永远拿不到 EventBus 引用。BuffSystem 中杀死敌人后的 `bus?.Publish(...)` 全部静默失效。

```csharp
// 当前顺序（错误）:
buffSystem.SetEventBus(bus);       // line 151 — bus 还是 null!
// ...
bus = new EventBus();              // line 158 — 太晚了
```

**修复**: 将 `bus = new EventBus()` 和 `sm = new StateMachine()` 移到 `buffSystem.SetEventBus()` 之前。

---

### 2. ActiveEnemyIds 列表从不清理 → 内存泄漏 + GetActiveEnemyCount 返回错误值

**文件**: `Core/ComponentStore.cs:71,144-156,321,497-500`

**问题**: `AddEnemy()` 向 `ActiveEnemyIds` 添加实体 ID，但 `DestroyEntity()` 没有从中移除。`GetActiveEnemyCount()` 返回 `ActiveEnemyIds.Count`，随着敌人不断生成和死亡，该计数无限增长，返回虚高的"活跃"敌人数量。

`GetAllActiveEnemyIds()` 不受此影响（它遍历数组检查 `EnemyActive[i]`），但 `GetActiveEnemyCount()` 和 `GetActiveEnemyIds()` 都基于这个未清理的列表。

```csharp
// DestroyEntity — 缺少:
// ActiveEnemyIds.Remove(entityId);
```

**修复**: 在 `DestroyEntity()` 中添加 `ActiveEnemyIds.Remove(entityId)`。

---

### 3. SkillSystem 完全不接入 EventBus → 技能击杀不发布事件

**文件**: `Systems/SkillSystem.cs` (整个文件)

**问题**: `PlayerTowerAttackSystem` 和 `TowerAttackSystem` 在击杀敌人时都会:
- `store.TotalKills++`
- `store.SetPlayerGold(...)`  
- `bus?.Publish(GameEvents.EnemyKilled, ...)`
- `bus?.Publish(GameEvents.GoldChanged, ...)`
- `buffSystem?.TryApplyDebuff(...)`

但 `SkillSystem` 的 3 个技能（Cross Slash / Mega Explosion / Sniper Shot）只做了前两项，完全没有:
- 接入 EventBus（没有 `SetEventBus` 方法，没有 `bus` 字段）
- 接入 BuffSystem
- 发布 `EnemyKilled` / `GoldChanged` 事件

此外，`CrossSlash` 的 `for` 循环里使用 `break` 跳出 `xOffset/yOffset` 内部循环（line 253），但此时如果同一次技能释放中还有其他敌人也落在这个十字范围内，它们不会再受到伤害——因为已经 `break` 退出了偏移量遍历。实际上 cross slash 模式在这里是逐敌人检查是否被十字覆盖，break 只是跳出偏移查找。但外部是 foreach 遍历所有活跃敌人，内层 for(int i = 0; i < xOffset.Length; i++) 检查每个偏移位置可以击中多少敌人。break 退出的只是偏移循环——这意味着一旦找到"该敌人被某个偏移命中"，就不再检查其他偏移。但因为一个敌人不可能同时处于两个偏移位置，所以 break 在这里是正确且性能友好的。

**修复**: 给 SkillSystem 添加 `SetEventBus()` / `SetBuffSystem()` 方法，在各技能击杀路径中发布事件、尝试施加 debuff。

---

### 4. SkillSystem InitializePlayerSkills 写入 SOA 数组时互相覆盖

**文件**: `Systems/SkillSystem.cs:91-137`

**问题**: SOA 存储中技能是单槽数组（`SkillName[playerId]` / `SkillDamageMultiplier[playerId]` 等），但 `InitializePlayerSkills()` 连续三次写入同一个 playerId：

```csharp
store.SetSkillName(playerId, skillCrossSlash.Name);      // 写入
store.SetSkillName(playerId, skillMegaExplosion.Name);    // 覆盖！
store.SetSkillName(playerId, skillSniperShot.Name);       // 再次覆盖！
```

最终 SOA 中只有 Sniper Shot 的数据。幸好 SkillSystem 的实际逻辑完全使用本地字段 (`skillCrossSlash` / `cooldownCrossSlash` 等) 而非 SOA 数组，所以这不是运行时 bug——但 SOA 中的这些技能字段完全成了死写（write-only dead code）。

**修复方案（二选一）**:
- A) 删除 `InitializePlayerSkills()` 中对 SOA 的全部写入，以及 SOA 中无用字段
- B) 将 SOA 技能字段改为 `List<SkillConfig>[]` 支持多技能存储，并让系统从这里读取

---

### 5. TowerPlacementSystem GoldChanged 事件 NewTotal 硬编码为 0

**文件**: `Systems/TowerPlacementSystem.cs:57-58`

```csharp
bus?.Publish(GameEvents.GoldChanged, new GoldChangedEvent {
    Amount = -cost, NewTotal = 0, Source = "tower_build"  // NewTotal 应为实际余额
});
```

**修复**: 在发布前获取实际剩余金币并填入 `NewTotal`。

---

## MEDIUM (性能退化 / 设计缺陷)

### 6. 热路径中频繁 `new Random()` — 4 处

**文件**:
- `Systems/PlayerTowerAttackSystem.cs:62` — 每帧每个被攻击敌人
- `Systems/BuffSystem.cs:131` — 每次尝试施加 debuff
- `Systems/UpgradeSystem.cs:78` — 每次升级
- `Systems/WaveSpawningSystem.cs:119` — 每帧生成敌人时

**问题**: .NET 中 `new Random()` 使用系统时钟作为种子，在紧密循环中创建多个实例会导致:
1. 性能显著下降
2. 同毫秒内创建的实例产生相同序列（非真正随机）

**修复**: 使用 `private static readonly Random _rng = new Random();` 或 `[ThreadStatic]` 共享实例。

---

### 7. GetAllActiveEnemyIds() 全量扫描 100,000 个数组位置

**文件**: `Core/ComponentStore.cs:484-495`

**问题**: 该方法遍历 `MAX_ENTITIES`（100,000）个位置来收集活跃敌人，每帧被调用多次：

| 调用方 | 调用频率 |
|---|---|
| `EnemyMovementSystem.Update()` | 1×/帧 |
| `EnemyAttackSystem.Update()` | 1×/帧 |
| `PlayerTowerAttackSystem.Update()` | 1×/帧 |
| `MapSystem.GetCellCharacter()` | **每个格子 1 次** (每渲染帧 210×) |
| `TowerAttackSystem.FindNearestEnemy()` | 每个塔 1×/帧 |
| `SkillSystem.FindNearestEnemyInRange()` | 每个技能 cast 1× |
| `GameManager.CheckEnemiesAtBottom()` | 1×/帧 |

**MapSystem 是最严重的**：每 3 回合渲染一次，每次 `GetCellCharacter` 扫描 10 列 × ~21 行 = 210 个格子，每个格子调用 `GetAllActiveEnemyIds()` 遍历 100k 数组 = **2100 万次数组访问** 仅为一帧渲染。

**修复**: 
- 维护 `List<int> ActiveEnemyIds`（修复 #2 后即可用），`GetAllActiveEnemyIds()` 直接返回它的副本
- `GetCellCharacter()` 应只调用一次 `GetAllActiveEnemyIds()` 然后传入复用

---

### 8. GoldRewardSystem 与 UpgradeSystem 功能重复

**文件**: `Systems/GoldRewardSystem.cs`, `Systems/UpgradeSystem.cs:40`

**问题**: 两系统都在每帧打印几乎相同的 gold/threshold 信息。`GoldRewardSystem.Update()` 没有任何独特逻辑——它只做日志输出，而 `UpgradeSystem.Update()` 也在第 40 行做同样的事：

```csharp
// GoldRewardSystem.Update():
renderer.Log($"[UPGRADE] Current gold: {gold:F1} / {threshold:F1} (next upgrade)");

// UpgradeSystem.Update() line 40:
renderer.Log($"[UPGRADE] Current gold: {gold:F1} / {threshold:F1} (next upgrade)");
```

**修复**: 删除 `GoldRewardSystem` 或在 GameManager 中停止调用它。

---

### 9. MapSystem.GetEnemyTypeChar 对受伤敌人返回错误类型

**文件**: `Systems/MapSystem.cs:148-171`

**问题**: 用当前 HP 与配置的 MaxHealth 做精确浮点比较 (`Math.Abs(health - monster.Health) < 1f`) 来推断敌人类型。一旦敌人受伤，当前 HP 就不再等于任何配置值，回退到硬编码的生命值范围猜测逻辑：

```csharp
// 回退逻辑——受伤的 30 HP Strong 怪在 health=25 时被误判为 'S'? 恰好碰对。
// 但受伤到 10 HP 的 Strong 怪会被标成 'R' (Ranged).
if (health >= 25f) return 'S';
if (health >= 18f) return 'N';
if (health >= 12f) return 'F';
return 'R'; // <12 HP 全是 Ranged
```

**修复**: 在 `AddEnemy` 时存储敌人类型字符串，供渲染直接使用，而非靠 HP 反推。或者使用 `entityNames`（如 `"Normal_L1W1_3"`）解析类型前缀。

---

## LOW (代码质量 / 边缘情况)

### 10. WaveSpawningSystem 硬编码生成高度 Y=19

**文件**: `Systems/WaveSpawningSystem.cs:128`

**问题**: 地图高度是 50，但敌人生成位置硬编码为 `startY = 19f`。MapSystem 的可见窗口以玩家 Y=0 为基准向上 20 格 (`viewTop = (int)playerY + 20 = 20`)，所以 Y=19 刚好在视野边缘。如果玩家移动到地图上方，敌人会生成在玩家下方。

**修复**: 根据地图高度动态计算：`startY = mapHeight - 1`。

---

### 11. ConsoleLogger.Log 自动包裹 [INFO] 前缀导致双重标签

**文件**: `Core/ConsoleLogger.cs:14`

```csharp
public void Log(string message) => Console.WriteLine($"[INFO] {message}");
```

但调用方已经自带标签，输出变成：
```
[INFO] [ATTACK] Player attacks enemy 5...
[INFO] [DAMAGE] Enemy 5 attacked Player! ...
[INFO] [GOLD] Killed Normal_L1W1_1, gained 10 gold
```

**修复**: 移除 `ConsoleLogger.Log()` 中的 `[INFO]` 前缀，或改为无标签直接输出。

---

### 12. EntityManager.GetComponent<UpgradeComponent> Skills 始终为空列表

**文件**: `Core/EntityManager.cs:162`

```csharp
Skills = new List<string>()  // 硬编码空列表，丢弃实际技能数据
```

**修复**: 从正确的来源填充 Skills 列表，或移除该字段。

---

### 13. AddEnemy 返回 -1 时 WaveSpawningSystem 不处理

**文件**: `Systems/WaveSpawningSystem.cs:130-141`

**问题**: 当 `store.AddEnemy()` 返回 -1（实体池耗尽），代码仍继续调用 `store.SetEntityName(-1, ...)`（Dict 插入 key=-1），且 spawn 计数器照常递增。

**修复**: 检查返回值，若为 -1 则记录错误并跳过后处理。

---

### 14. EnemyAttackSystem 在玩家死亡后继续遍历

**文件**: `Systems/EnemyAttackSystem.cs:75-78`

**问题**: 第 75-78 行在玩家死亡时执行 `return`，但这只是提前退出。如果第一个攻击的敌人就杀了玩家，其他敌人不会再有攻击机会——这符合"玩家已死无需继续"的语义。但 `enemiesAttacked++` 的统计可能不完整。

实际上这是设计选择而非 bug：一旦玩家死亡立即停止遍历是合理的。

---

### 15. SkillSystem CastSkill 在无目标时也进入冷却

**文件**: `Systems/SkillSystem.cs:269-272,258`

**问题**: Mega Explosion 的 `FindNearestEnemyInRange()` 返回 null 时直接 `return` 不触发冷却。但 Cross Slash（line 212）在 `target == null` 时也直接 return，却不会设置冷却——这是正确的。然而 Sniper Shot（line 363）在 `closestEnemyId == -1`（无目标）时仍然设置 `cooldownSniperShot`：

```csharp
// Line 341-368: 无目标时 skipped 了伤害和日志，但仍然设置冷却
cooldownSniperShot = skillSniperShot.Cooldown; // Line 363 — 无条件执行
```

**修复**: 空放 Sniper Shot 时不应进入冷却，将冷却设置移入 `if (closestEnemyId != -1)` 分支内。

---

### 16. PlayerTowerAttackSystem.gameConfig 参数从未使用

**文件**: `Systems/PlayerTowerAttackSystem.cs:21`

```csharp
public PlayerTowerAttackSystem(..., GameConfig gameConfig) // gameConfig 未被存储
```

构造函数接受 `gameConfig` 参数但未存储到字段，完全是死参数。

**修复**: 如果确实不需要则移除该参数。

---

### 17. MapSystem.GameConfig 参数用途有限

**文件**: `Systems/MapSystem.cs:17,24`

`gameConfig` 仅在 `GetEnemyTypeChar` 中用于 HP→类型推断（参考 #9），且该机制本身就有缺陷。

---

## 总结统计

| 严重度 | 数量 | 关键影响 |
|---|---|---|
| HIGH | 5 | EventBus 注入失败、内存泄漏、事件丢失、数据覆盖 |
| MEDIUM | 4 | 随机数性能、渲染性能 (2100万次扫描/帧)、重复代码 |
| LOW | 8 | 硬编码值、日志格式、边界处理、死参数 |

**建议修复优先级**: #1 (EventBus 注入) → #2 (ActiveEnemyIds 泄漏) → #3 (SkillSystem 事件) → #7 (渲染性能) → #6 (Random 实例化) → 其余

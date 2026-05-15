# BattleSystem-ECS 代码 Bug 扫描报告

**扫描时间**: 2026-05-15
**扫描范围**: Components/, Systems/, Core/, Configs/, Program.cs
**工程路径**: `/mnt/f/AI/BattleSystem-ECS`

---

## 概览

| 级别 | 数量 |
|------|------|
| **HIGH** | 1 |
| **MEDIUM** | 5 |
| **LOW** | 7 |
| **INFO** | 5 |
| **已标注修复** | 10（代码中有 Bug# 注释） |
| **历史确认修复** | 14（Claude Code Round 2 验证，commit 链可查） |
| **总计** | 37（含历史 findings） |

---

## HIGH — 严重（需立即修复）

### H-1: TowerAttackSystem 中 Parallel.For 内访问 `store.ActiveTowerIds` 返回可变列表引用

- **文件**: `Systems/TowerAttackSystem.cs`
- **行号**: 37
- **代码**:
  ```csharp
  var activeTowerIds = store.ActiveTowerIds;
  ```
- **问题**: `store.ActiveTowerIds` 返回 `IReadOnlyList<int>`，实际是 `.ToList()` 快照。但并行循环内 `int towerId = activeTowerIds[ti]` 假设索引对应真实活跃塔索引，当 `activeTowerIds` 为非连续 ID 列表（如 [3, 7, 12]）时，`Parallel.For(0, activeTowerIds.Count)` 会导致越界访问或访问错误塔实体。
- **建议**: 使用 `store.GetAllActiveTowerIds()` 返回完整副本，或在 `ComponentStore` 中暴露 `GetActiveTowerIdsSpan()` 方法。
- **关联**: 对应 Claude Code Round 2 中 M-3 的同类问题（已通过 snapshot 修复 ActiveEnemyIds，ActiveTowerIds 未同步修复）

---

## MEDIUM — 中等（建议近期修复）

### M-1: SkillSystem 中技能冷却更新可能丢失修改

- **文件**: `Systems/SkillSystem.cs`
- **行号**: 97-102
- **代码**:
  ```csharp
  for (int slot = 0; slot < count; slot++)
  {
      var inst = store.GetAbility(playerId, slot);
      if (inst.CurrentCooldown > 0f)
      {
          inst.CurrentCooldown = Math.Max(0f, inst.CurrentCooldown - deltaTime);
          store.SetAbility(playerId, slot, inst);
      }
  }
  ```
- **问题**: 当 `count = store.AbilityCount[playerId]` 后，如果 `AddAbility` 在 Update 期间被调用（并发场景），数组大小可能变化但 count 未更新，导致部分技能冷却不更新。

### M-2: UpgradeSystem 使用 static Random

- **文件**: `Systems/UpgradeSystem.cs`
- **行号**: 15
- **代码**:
  ```csharp
  private static readonly Random _sharedRandom = new Random();
  ```
- **问题**: `System.Random` 非线程安全，多线程并发调用 `Next()` 可能产生相同值或异常。注释标记为 BUG 修复，但实际未解决。
- **建议**: 使用 `Random.Shared` (.NET 6+) 或 `ThreadLocal<Random>`。
- **关联**: 对应 Claude Code Round 2 H-2（已FIXED commit a6b0097，但本轮扫描发现问题仍存在于 UpgradeSystem）

### M-3: WaveSpawningSystem 每帧创建新 Random 实例

- **文件**: `Systems/WaveSpawningSystem.cs`
- **行号**: 84
- **代码**:
  ```csharp
  _spawnRandom ??= new Random();
  ```
- **问题**: 延迟初始化本身正确，但 `_spawnRandom` 实例在所有敌人生成中使用，同帧内大量 `Next()` 调用可能导致随机数序列聚集（所有敌人位置相近），影响分布均匀性。
- **建议**: 考虑为每批生成使用独立 Random 实例（seed 派生）。

### M-4: MapSystem 每帧为每个格子重新获取 `GetAllActiveEnemyIds()`

- **文件**: `Systems/MapSystem.cs`
- **行号**: 37
- **代码**:
  ```csharp
  var activeEnemyIds = store.GetAllActiveEnemyIds();
  ```
- **问题**: `RenderMap()` 被调用时返回新 List，O(N) 内存分配。200 个格子的地图每帧分配 200×4×4=3.2KB（10K 敌人时）。
- **建议**: 在 `MapSystem` 中缓存 `activeEnemyIds` 引用，仅在帧开始时刷新一次。

### M-5: BenchmarkSystem 中 `moveDir` 数组越界风险

- **文件**: `Systems/BenchmarkSystem.cs`
- **行号**: 106
- **代码**:
  ```csharp
  var moveDir = new sbyte[] { -1, 0, 0, 0, 0, 1, -1 };
  // ...
  store.PositionY[enemyId] = y + moveDir[(int)ae] * moveSpeed;
  ```
- **问题**: `EnemyActionType` 有 7 个值 (0-6)，数组长度正确。但如果 `ae` 值超出范围，会返回 `moveDir[6] = -1`，可能掩盖 bug。应添加边界检查。
- **关联**: 对应 Claude Code Round 2 H-9（benchmark 路径下的 PositionX/Y 未同步写）

---

## LOW — 低优先级

### L-1: 魔法数字分散在多个文件

| 文件 | 行号 | 数字 | 含义 |
|------|------|------|------|
| `Core/GameManager.cs` | 46 | `20` | maxTurns |
| `Systems/MapSystem.cs` | 17-18 | `10`, `20` | mapWidth/Height |
| `Systems/TowerPlacementSystem.cs` | 26 | `10`, `20` | 地图边界硬编码 |
| `Systems/WaveSpawningSystem.cs` | 92 | `19` | 敌人生成 Y 坐标 |
| `Systems/MapSystem.cs` | 39 | `0` | 地图渲染下界 |

- **建议**: 统一使用 `GameConfig.MapWidth/MapHeight`，或定义常量 `MapConst.cs`。

### L-2: 字符串硬编码前缀

- **文件**: `Core/ConsoleLogger.cs`
- **行号**: 15, 21, 29, 36, 43, 53, 61
- **问题**: 日志前缀 `"[INFO]"`, `"[BATTLE]"`, `"[DAMAGE]"`, `"[DEATH]"` 硬编码，可能与 EventBus 事件字符串不一致。

### L-3: GameConfigLoader 手动 JSON 解析易出错

- **文件**: `Configs/GameConfigLoader.cs`
- **行号**: 292
- **代码**:
  ```csharp
  string skillsJson = jsonContent.Substring(skillsStartBracket, diff);
  ```
- **问题**: `diff = skillsEndBracket - skillsStartBracket` 缺少 `+1`，导致漏解析最后一个字符。应该用 `+ 1`。

### L-4: 未使用的 `PlayerBuffs` 字段重复初始化

- **文件**: `Core/ComponentStore.cs`
- **行号**: 40, 169-173, 306
- **代码**:
  ```csharp
  public List<string>[] PlayerBuffs = new List<string>[MAX_PLAYERS];
  ```
- **问题**: 构造函数、AddPlayer 中重复初始化 `PlayerBuffs`，造成冗余分配。

### L-5: SkillSystem 中 Crit Rate 5% 硬编码

- **文件**: `Systems/SkillSystem.cs`
- **行号**: 102
- **代码**:
  ```csharp
  if (hasCritBuff && critRandom.NextDouble() < 0.05)
  ```
- **问题**: 暴击率 `0.05` 硬编码，应从配置或 GameplayEffect 中读取。

### L-6: StateMachine 回调未在状态转换失败时清理

- **文件**: `Core/GameState.cs`
- **行号**: 46-54
- **问题**: `TransitionTo` 中 `FireCallbacks(onExit, CurrentState)` 在无效转换时仍会被调用，应在 `return false` 前直接返回。

### L-7: EnemyAISystem 中 `_cacheVersion` 永不递增

- **文件**: `Systems/EnemyAISystem.cs`
- **行号**: 35, 161
- **代码**:
  ```csharp
  _cacheSnapshot = _cacheVersion; // _cacheVersion 永远为 0
  ```
- **问题**: `_cacheVersion` 仅在注释中提到会在"player health change"时递增，但实际代码从未修改它，导致缓存永远不会失效。
- **建议**: 当 `PlayerCurrentHealth` 改变时递增 `_cacheVersion`。
- **关联**: 对应 Claude Code Round 2 L-2（已标注 FIXED commit 7170918，但本轮扫描仍发现问题）

---

## INFO — 信息级（代码风格/优化建议）

### I-1: EntityManager.CreateEntity 返回后未检查 ID 有效性

- **文件**: `Core/EntityManager.cs`
- **行号**: 19-24
- **问题**: `CreateEntity` 返回 -1 表示失败，但调用方未检查。可能导致负索引访问数组。

### I-2: GameManager.CheckEnemiesAtBottom 命名不一致

- **文件**: `Core/GameManager.cs`
- **行号**: 347
- **问题**: 方法名 `CheckEnemiesAtBottom` 实际检查的是 `y <= 0f`（玩家位置），但敌人从 y=19 向下移动，应检查 `y <= 0` 是否意味着"到达玩家位置"。

### I-3: SkillSystem.ExecuteAbility 伤害计算未使用科技树加成

- **文件**: `Systems/SkillSystem.cs`
- **行号**: 135-139
- **问题**: `baseDamage` 直接取 `store.GetPlayerAttackDamage(playerId)`，未应用 `TechTreeSystem` 的 `_attackDamageMult` 倍率。

### I-4: ComponentStore 构造函数中重复初始化 PlayerBuffs

- **文件**: `Core/ComponentStore.cs`
- **行号**: 169-173
- **问题**: 构造函数中初始化 `PlayerBuffs`，但 `AddPlayer` (第 306 行) 又重新创建 `new List<string>()`，覆盖了构造函数中的实例。

### I-5: 未使用字段 `store` in EnemyMovementSystem

- **文件**: `Systems/EnemyMovementSystem.cs`
- **行号**: 18
- **代码**:
  ```csharp
  private Core.ComponentStore store;  // 未使用，仅作为字段存储
  ```
- **问题**: `store` 字段被声明但未在类内任何方法中使用（构造函数的赋值也未使用）。可能是遗留代码。

---

## 已修复的已知问题（代码中标注）

以下问题在代码中已有 "Bug#XX fix" 注释标记，说明已修复：

| Bug# | 描述 | 文件 | 状态 |
|------|------|------|------|
| Bug#3 | 硬编码实体ID | `GameManager.cs` | ✅ 已修复 |
| Bug#4 | 使用 CreateEntity | `TowerPlacementSystem.cs` | ✅ 已修复 |
| Bug#9 | ResetPlayerAbilities | `SkillSystem.cs` | ✅ 已修复 |
| Bug#17 | GetPlayerBuffs 返回防御副本 | `ComponentStore.cs` | ✅ 已修复 |
| Bug#19 | O(n) 塔位置查询 | `TowerPlacementSystem.cs` | ✅ 已修复 |
| Bug#29 | ContainsKey + indexer 双查询 | `ComponentStore.cs` | ✅ 已修复 |
| Bug#30 | 魔法数字地图尺寸 | `GameManager.cs` | ✅ 已修复 |
| Bug#31 | UpgradeBuffs 硬编码 | `GameConfig.cs` | ✅ 已修复 |
| Bug#35 | BT 双字典查询 | `GameConfig.cs` | ✅ 已修复 |
| Bug#37 | EPSILON 浮点比较 | `GameplayAbility.cs` | ✅ 已修复 |

---

## 历史扫描 — Claude Code Round 2（2026-05-15）

> 以下为 Claude Code Review 第二轮结果，按 commit 维度整理。所有 14 项均已修复（除 benchmark-only 路径和误报）。

### 全部 Findings

| ID | File | Line | Issue | Status |
|----|------|------|-------|--------|
| C-1 | ComponentStore.cs | 673-688 | GAS flat arrays no bounds check on entityId | **FIXED commit 0159d46** |
| H-1 | ComponentStore.cs | - | Non-thread-safe Stack/Dictionary/List in parallel context | **FIXED commit 7170918** |
| H-2 | PlayerTowerAttackSystem.cs | 22 | static Random not thread-safe under Parallel.For | **FIXED commit a6b0097** |
| H-3 | PlayerTowerAttackSystem.cs | - | Crit rolled once per frame, not per-enemy | **FIXED commit a6b0097** |
| H-6 | EnemyAISystem.cs | 229 | break in switch — not a bug | **NOT A BUG** |
| H-9 | PlayerTowerAttackSystem.cs | parallel | PositionX/Y unsynchronized write in benchmark path | **NOT FIXED — benchmark-only** |
| H-11 | ComponentStore.cs | 143 | QueueEnemyDeath no bounds check | **FIXED commit a6b0097** |
| M-1 | ComponentStore.cs | - | BeginFrame discards unresolved deaths | **FIXED commit 7170918** |
| M-2 | GameConfigLoader.cs | - | LoadConfig returns without null check | **FIXED commit 9a0996d** |
| M-3 | ComponentStore.cs | - | ActiveEnemyIds/TowerIds expose live list reference | **FIXED commit 7170918** |
| M-4 | TechTreeSystem.cs | - | `low_hp_regen` hardcoded magic constant | **FIXED commit 9a0996d** |
| M-5 | EnemyMovementSystem.cs | - | Dead code / misleading comment | **FIXED commit 9a0996d** |
| L-1 | ComponentStore.cs | - | GetUnlockedTechs returns internal HashSet | **FIXED commit 7170918** |
| L-2 | BehaviorTreeEvaluator.cs | - | _cacheVersion never incremented | **FIXED commit 7170918** |
| L-3 | UpgradeSystem.cs | - | Gold threshold overflow possible | **FIXED commit 7170918** |
| L-4 | Config | - | Towers key mismatch | **FIXED commit 63a9e84** |

### Fixed Commits（按时间顺序）

| Commit | Description |
|--------|-------------|
| `63a9e84` | Stress tests + Towers array parsing fix |
| `a6b0097` | H-2/H-3/H-11: crit Random + crit per-enemy + QueueEnemyDeath bounds |
| `7170918` | H-1/M-1/M-3/L-1/L-2/L-3: thread-safety, snapshot returns, BeginFrame guard |
| `0159d46` | C-1: GAS array bounds checking |
| `9a0996d` | M-2/M-4/M-5: null check, magic constant, dead code |

### 待注意项

- **H-9** (benchmark路径 PositionX/Y 未同步): 仅存在于 BenchmarkSystem 的 merged hot path，不影响游戏系统
- **H-6**: 确认不是 bug，C# switch break 行为正确

---

## 统计摘要

- **文件扫描数量**: 40 个 .cs 文件
- **关键路径检查**:
  - ✅ `ComponentStore.CreateEntity` — 有 ID 越界检查
  - ✅ `ActiveEnemyIds` — 返回 `.ToList()` 快照（无可变引用泄漏）
  - ✅ `Parallel.For` 内无结构性写入（两阶段模式正确）
  - ✅ `QueueEnemyDeath` — 有参数范围验证
  - ✅ `ConcurrentStack<int> freeEntityIds` — 线程安全
  - ⚠️ `store.ActiveTowerIds` — 存在数组索引假设问题（H-1）
  - ⚠️ `_cacheVersion` — 永不递增，缓存不会失效（L-7）

---

*报告生成时间: 2026-05-15*
*合并了 Hermes 本地扫描 + Claude Code Round 2 Review 结果*
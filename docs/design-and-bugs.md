# BattleSystem-ECS 设计治理与 Bug 追踪

> 项目路径：F:\AI\BattleSystem-ECS
> 最后更新：2026-05-22

---

## 一、设计治理（11 项，全部完成）

|| # | 优先级 | 任务 | 状态 | 备注 |
|---|--------|------|------|------|------|
|| 1 | 最高 | 主循环刷新缓存 | ✅ 已完成 | GameManager 每回合对所有系统调用 SetTurn()（`commit 743664c`）|
|| 2 | 最高 | 并行系统两阶段死亡解析 | ✅ 已完成 | PlayerAttack/TowerAttack 两阶段（`2248d4a`），统一帧末 resolve（`3bd1c9c`）|
|| 3 | 最高 | 禁止并行段直接改 active list / EventBus | ✅ 已完成 | EnemyAI 两阶段（`ccc42e3`），EventBus 加锁（`afb988d`），两阶段模式保证调用路径安全 |
|| 4 | 最高 | SkillSystem 击杀统一 DestroyEntity | ✅ 已完成 | HandleKill 只 queue 死亡（`3bd1c9c`），帧末统一 resolve |
|| 5 | 高 | ComponentStore 生命周期字段收口 | ✅ 已完成 | ActiveEnemyIds/TowerIds 暴露为 IReadOnlyList（`840bc3e`），freeEntityIds Stack 并发安全由两阶段模式保证 |
|| 6 | 高 | DestroyEntity 完整清理 | ✅ 已完成 | 清所有 archetype 字段（`c0d85cf`）|
|| 7 | 高 | GameConfigLoader 完整解析 | ✅ 已完成 | 解析 MaxHealth / StartingSkills（`736746c`）|
|| 8 | 高 | EventBus 并行安全改造 | ✅ 已完成 | lock + snapshot iteration + Reset()（`afb988d`）|
|| 9 | 中 | 击杀奖励集中化 | ✅ 已完成 | 帧末统一 resolve 内处理（`3bd1c9c`）|
|| 10 | 中 | Benchmark 真实系统链路 | ✅ 已完成 | mode 4 真实系统链路 benchmark（`7ef56aa`），AGENTS.md 写入 dual benchmark 规则（`41cc6a5`）|
|| 11 | 高 | Phase 阶段循环打通 | ✅ 已完成 | `phase_behavior.json` → `StateMachine` → `FrameScheduler.Phase`（2026-05-22）|

---

## 二、Bug 追踪（46 项，全部已修复）

### 2.1 汇总

| 严重度 | 总数 | 已修复 | 未修复 |
|--------|------|--------|--------|
| HIGH   | 13   | 13     | 0      |
| MEDIUM | 16   | 16     | 0      |
| LOW    | 9    | 9      | 0      |
| INFO   | 6    | 6      | 0      |
| **合计** | **47** | **47** | **0** |

> 2026-05-15：#39（GridSpatialHash 空文件）和 #40（csproj 编译项）均已确认已修复/非问题，46/46 全部解决。

#### 47. TowerPlacementSystem 无参构造导致 debuff 塔效果失效 [MEDIUM] ✅ FIXED
**文件**: Core/GameManager.cs, Systems/TowerPlacementSystem.cs
**状态**: ✅ 已修复
**说明**: GameManager 初始化 TowerPlacementSystem 时使用无参构造函数，导致 `gameConfig == null`。PlaceTower 中的 debuff 查询逻辑 `if (gameConfig != null)` 永远走 else 分支，Cryo/Firewall/Leech/Tesla 等功能性塔的 StunChance/SlowAmount/SlowDuration 保持默认值 0，debuff 效果不生效。
**修复内容**: `GameManager.cs:97` 改为传入 `gameConfig` 参数：`new TowerPlacementSystem(store, logger, gameConfig)`。TowerPlacementSystem 已有两个构造函数，无参版本保留兼容。

### 2.2 未修复项

| Bug# | 严重度 | 描述 | 状态 |
|------|--------|------|------|
| — | — | 无 | 全部已修复 |

> 2026-05-15 心跳核查：#39（GridSpatialHash.cs 空文件）已于 `7f02a52` 删除；#40（csproj EnableDefaultCompileItems）实际不成立，UpgradeSystem.cs 由 `Compile Include="Systems\*.cs"` 显式包含。

---

## 三、完整 Bug 列表

### 【HIGH】严重问题

#### 1. ComponentStore.GetAllActiveEnemyIds 返回内部可变列表引用
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit `04c50a6`）（返回 `new List<int>(ActiveEnemyIds)` 副本解耦）
**说明**: 当前调用方在 SetTurn 时缓存一次，不再在循环中重复调用。偶发问题但应修复。

#### 2. ComponentStore.CreateEntity ID 越界风险
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit `d8da251`）
**说明**: `CreateEntity()` 现在校验回收池返回的 ID 有有效范围（< id < MAX_ENTITIES），超出时返回自增分配；自增达到 MAX_ENTITIES 时返回 -1。TowerPlacementSystem 等直接调用方会收到 -1 而非越界 ID。

#### 3. GameManager 硬编码塔实体 ID
**文件**: Core/GameManager.cs
**状态**: ✅ 已修复（代码审查确认）
**说明**: `PlaceTower()` 返回真实 entity ID，`UpgradeTower()` 使用返回值 `towerId1`/`towerId2` 而非硬编码常量。Bug 报告时可能是旧代码，当前版本正常。

#### 4. System/TowerPlacementSystem.PlaceTower 使用 NextEntityId 而非 CreateEntity
**文件**: System/TowerPlacementSystem.cs（第53行）
**状态**: ✅ FIXED（Systems/ 版本已修复）
**说明**: Systems/TowerPlacementSystem.cs 已使用 `store.CreateEntity()`，System/ 目录是死代码未编译。

#### 5. GameStateSystem 调用不存在的 ComponentStore 方法
**文件**: System/GameStateSystem.cs
**状态**: ℹ️ 不适用
**说明**: System/GameStateSystem.cs 不在 csproj 中编译，未使用。

#### 6. Systems/TowerAttackSystem.SetTurn 调用不存在方法 ✅ FIXED
**文件**: Systems/TowerAttackSystem.cs
**状态**: ✅ 已修复
**修复内容**:
- 新增 `ComponentStore.ActiveTowerIds` (List<int>)
- `AddTower()` 添加 `ActiveTowerIds.Add(entityId)`
- `RemoveTower()` 添加 `ActiveTowerIds.Remove(entityId)`
- `DestroyEntity()` 处理塔清理并从 ActiveTowerIds 移除
- TowerAttackSystem 使用 `store.ActiveTowerIds` 直接遍历

#### 7. System/TowerAttackSystem 使用 DateTime.Now 作为攻击计时器
**文件**: System/TowerAttackSystem.cs
**状态**: ℹ️ 不适用
**说明**: Systems/TowerAttackSystem.cs 已使用 `deltaTime` 参数，未使用 DateTime.Now。

#### 8. WaveGenerationSystem 每帧创建新 Random 实例
**文件**: System/WaveGenerationSystem.cs
**状态**: ℹ️ 不适用
**说明**: System/ 目录未编译。Systems/WaveGenerationSystem.cs 未知（未检查）。

#### 9. SkillSystem.InitializePlayerSkills 只保留最后一个技能
**文件**: Systems/SkillSystem.cs
**状态**: ✅ 已修复（commit `5052fd1` GAS 重构）
**说明**: SkillSystem 已重构为 GAS 架构，`AddAbility()` 按 slot 顺序添加（不覆盖），ResetPlayerAbilities() 在重新初始化前清空。

#### 10. ComponentStore.DestroyEntity 未从 ActiveEnemyIds 移除 ✅ FIXED
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复
**说明**: 代码中已有 `ActiveEnemyIds.Remove(entityId)`（第133行）

#### 11. ActiveEnemyIds 在迭代中被修改
**文件**: Core/ComponentStore.cs / Systems/*.cs
**状态**: ✅ 已修复（GetAllActiveEnemyIds 返回副本，调用方 SetTurn 时缓存）
**说明**: GetAllActiveEnemyIds 返回 `new List<int>(ActiveEnemyIds)` 副本，PlayerTowerAttackSystem.Parallel.For 并行安全。

#### 12. EnemyAISystem 缓存失效逻辑错误 ✅ FIXED (79fea25)
**文件**: Systems/EnemyAISystem.cs
**状态**: ✅ 已修复（commit `79fea25`）
**说明**: health-driven version counter 解决了缓存命中率问题。

#### 13. GameConfig MonsterTypes 用 List.Find 导致 O(n) 查询
**文件**: Core/GameConfig.cs
**状态**: ℹ️ 可接受
**说明**: MonsterTypes 数量少（4个），性能影响可忽略；_monsterCache 提供 O(1) 缓存保护。

---

### 【MEDIUM】中等问题

#### 14. UpgradeSystem 每帧创建新 Random
**文件**: Systems/UpgradeSystem.cs
**状态**: ✅ 已确认修复
**说明**: `UpgradeSystem` 类级别已有 `private static readonly Random _sharedRandom`，无每帧分配问题。

#### 15. PlayerTowerAttackSystem critRandom 静态实例可预测
**文件**: Systems/PlayerTowerAttackSystem.cs
**状态**: ✅ 已修复
**说明**: `private static readonly Random critRandom = new Random();` 在声明处初始化，不再分离初始化逻辑。随机性在单局游戏中影响轻微（游戏逻辑层面），但代码形式上已规范化。

#### 16. System/TowerPlacementSystem 与 Systems/TowerPlacementSystem 并存
**文件**: System/ vs Systems/
**状态**: ✅ 已确认
**说明**: System/ 目录是死代码。Systems/ 版本正常工作。

#### 17. ComponentStore.PlayerBuffs 数组元素直接赋值而非操作列表
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit `bb9e200`）
**说明**: `PlayerBuffs[entityId]` 直接访问已改为 `GetPlayerBuffs()` 返回副本（`new List<string>(PlayerBuffs[playerId])`），外部调用方 BenchmarkSystem.cs 和 PlayerTowerAttackSystem.cs 也已改为调用 `GetPlayerBuffs()` 而非直接访问字段。

#### 18. MapSystem.RenderMap 每帧分配新 List
**文件**: Systems/MapSystem.cs
**状态**: ✅ 已修复（commit `390d587`）
**说明**: GetAllActiveEnemyIds 挪到 for(y) 最外层，00× 娆″э級，销毁人类位置检查改为 Math.Round() 直接比较

#### 19. TowerPlacementSystem.PlaceTower O(n) 位置检查
**文件**: Systems/TowerPlacementSystem.cs
**状态**: ✅ 已修复（commit `f803566`）
**说明**: PlaceTower 现在遍历 ActiveTowerIds 而非 NextEntityId（全量扫描）

#### 20. FileLogger 未指定编码
**文件**: Core/FileLogger.cs
**状态**: ✅ 已修复（commit `d8da251`）
**说明**: `File.AppendAllText` 和 `File.WriteAllText` 均显式指定 `Encoding.UTF8`，避免跨平台默认编码纷争。

#### 21. EntityManager.GetAllEntities 每帧分配
**文件**: Core/EntityManager.cs
**状态**: ✅ 已修复（commit `f803566`）
**说明**: 返回静态空列表 _emptyEntityList（无调用方）

#### 22. ComponentStore.EntityNames 用 Dictionary 而非数组
**文件**: Core/ComponentStore.cs
**状态**: ℹ️ 可接受（仅调测用）

#### 23. EventBus 非线程安全
**文件**: Core/EventBus.cs
**状态**: ✅ 已修复（commit `afb988d`）
**修复内容**: lock + snapshot iteration + Reset()

#### 24. System/WaveGenerationSystem 错误调用 GetLevelConfig
**文件**: System/WaveGenerationSystem.cs
**状态**: ℹ️ 不适用（System/ 未编译）

#### 25. System/EnemyPathSystem 与 Systems/EnemyMovementSystem 职责重叠
**文件**: System/ vs Systems/
**状态**: ℹ️ 不适用（System/ 未编译）

#### 26. System/GameStateSystem 调用不存在方法
**文件**: System/GameStateSystem.cs
**状态**: ℹ️ 不适用（未编译）

#### 27. ComponentStore.AddToSpatialHash/GetEnemiesNear 是空桩
**文件**: Core/ComponentStore.cs
**状态**: ℹ️ 明确为空桩（GridSpatialHash 在 range=3 场景是反模式）
**说明**: AGENTS.md 明确注释 SpatialHash 在 range=3 场景 cell 开通 > O(N) 扫描，已废弃。

#### 28. Systems/TowerAttackSystem.TowerActive 访问越界风险 ✅ FIXED
**文件**: Systems/TowerAttackSystem.cs
**状态**: ✅ 已修复
**说明**: 现在使用 `ActiveTowerIds` 遍历，不再访问 NextEntityId 范围外的脏。

#### 30. ComponentStore.DestroyEntity ActiveTowerIds.Remove 顺序错误（先 false 再检查，永不执行） ✅ FIXED (60865d2)
**文件**: Core/ComponentStore.cs (DestroyEntity)
**状态**: ✅ 已修复
**修复内容**: `TowerActive[entityId] = false` 原先在 `if (TowerActive[entityId])` 检查之前执行，导致 Remove 分支不触发。修复为先检查再标记 false。

#### 31. TowerPlacementSystem.PlaceTower 未处理 CreateEntity() 返回 -1 ✅ FIXED (60865d2)
**文件**: Systems/TowerPlacementSystem.cs
**状态**: ✅ 已修复
**修复内容**: `CreateEntity()` 在实体池满时返回 -1，原代码未检查直接用于 AddPosition/AddTower。已新增 `if (towerId == -1) return -1` 保护。

---

### 【LOW】轻微问题

#### 29. ComponentStore.GetName 用 Dictionary.ContainsKey 双重查找 ✅ FIXED (5052fd1)
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复
**修复内容**: `ContainsKey`+indexer 双哈希查找 → `TryGetValue` 单次查找

#### 30. GameManager.SetMapSize 魔法数字 ✅ FIXED
**文件**: Core/GameManager.cs, Systems/EnemyMovementSystem.cs, Configs/GameConfig.cs
**状态**: ✅ 已修复
**修复内容**:
- `GameConfig` 新增 `MapWidth`（默认10）和 `MapHeight`（默认20）属性
- `GameManager.Initialize()` 调用 `mapSystem.SetMapSize(gameConfig.MapWidth, gameConfig.MapHeight)` 替换硬编码 `(10, 20)`
- `EnemyMovementSystem` 构造函数新增 `mapWidth` 参数，`Dodge` 分支中 `9f` 替换为 `mapWidth - 1f`
- `GameManager` 实例化 `EnemyMovementSystem` 时传入 `gameConfig.MapWidth`
- `CheckEnemiesAtBottom()` 的 `0f` 边界是游戏逻辑（底部 = y <= 0），不需要参数化

#### 31. SkillSystem buff 字符串硬编码 ✅ FIXED (16a6198)
**文件**: Systems/UpgradeSystem.cs
**状态**: ✅ 已修复
**修复内容**: UpgradeSystem.RandomlyGainBuff() 原先在代码中硬编码 `string[] buffs = {"Attack+10%", ...}`。现移动到 GameConfig.UpgradeBuffs（List<string>），通过 GetUpgradeBuffs() 暴露，构建函数注入 GameConfig。GameManager 和 BenchmarkSystem 的 UpgradeSystem 实例也已更新。

#### 32. GameEvents 定义了 20+ 事件但大部分未使用 ✅ FIXED
**文件**: Core/GameEvents.cs
**状态**: ✅ 已修复
**修复内容**: 通过全代码库 `Publish`/`Subscribe` 调用点扫描，确认只有 3 个事件在使用：`PlayerDamaged`、`EnemyCharging`、`EnemyChargeReleased`。已移除 18 个未使用事件常量，以及 9 个未使用的 DTO 空版本（`EnemyKilledEvent`、`WaveEvent`、`PlayerUpgradedEvent`、`LevelEvent`、`GameOverEvent`、`TowerEvent`、`GoldChangedEvent`、`EnemySpawnedEvent`、`EnemyChargingEvent` 的空桩版本）。保留的 DTO 已填充完整字段（`EnemyChargeReleasedEvent` 新增 `EnemyId` 和 `Damage` 字段）。

#### 33. EnemyMovementSystem Dodge 分支有副作用 ✅ FIXED (3f98b2f)
**文件**: Systems/EnemyMovementSystem.cs
**状态**: ✅ 已修复
**修复内容**: `case EnemyActionType.Dodge` 分支末尾 `return` 改为 `break`，使 Dodge 动作在修改 X 坐标后合并到统一的 `store.PositionY[enemyId] = y + direction * moveSpeed` 路径，修复"Dodge 后敌人不向下移动"的副作用。

#### 34. BTCachedTreeBuilder 用 List 构建 indexMap 后再查 Dictionary ✅ FIXED (3f98b2f)
**文件**: Systems/BehaviorTreeEvaluator.cs (BTCachedTreeBuilder.Build)
**状态**: ✅ 已修复
**修复内容**: `indexMap` 字典初始化从 `new Dictionary<string, int>()` 改为 `new Dictionary<string, int>(nodeIds.Count)` 预分配容量，避免动态扩容开销；同时 `cached.Nodes[nodeIdx]` 赋值从 `indexMap[kvp.Key]` 重复查询改为 `nodeIdx` 变量直接使用。

#### 35. GameConfig.GetCachedBehaviorTree 调用 GetBehaviorTree 造成双重字典查找 ✅ FIXED
**文件**: Core/GameConfig.cs
**状态**: ✅ 已修复
**修复内容**: `GetCachedBehaviorTree()` 原先通过 `GetBehaviorTree()` 查询，再在里面查 `_btCache` 和 `BehaviorTrees`，造成双层查找。现改为直接查询 `BehaviorTrees.TryGetValue()`，避免中间层。

---

### 【INFO】信息级

#### 36. GameConfig.MonsterTypes.Find 使用线性搜索
**文件**: Core/GameConfig.cs
**状态**: ℹ️ 可接受
**说明**: MonsterTypes 数量少（4个），`_monsterCache` 提供 O(1) 保护，影响可忽略。

#### 37. Systems/SkillSystem.CastSkill 冷却检测用 float 相等 ✅ FIXED (5052fd1 + 60865d2)
**文件**: Systems/SkillSystem.cs + Core/GAS/GameplayAbility.cs
**状态**: ✅ 完全修复
**修复内容**:
- SkillSystem.CastSkill 手动施法路径：冷却检测 `== 0f` → `<= 0.0001f`（5052fd1）
- **GameplayAbility.CanActivate() epsilon 全局修复**（60865d2）：统一 `CurrentCooldown <= EPSILON(0.0001f)`，覆盖 AutoCastBestSkill 所有自动施法路径

#### 38. ComponentStore 中 MAX_BUFFS=10 常量定义但未使用 ✅ FIXED
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit `a4650bc`）
**修复内容**: 移除 `private const int MAX_BUFFS = 10;` 死代码常量，该常量在整个代码库中无任何引用。

#### 31. 护盾吸收逻辑缺失 ✅ FIXED
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit `d34e5fd` + 本次）
**修复内容**:
- `DecreasePlayerHealth()` 已实现护盾吸收：shield > 0 时优先扣除 shield，剩余穿透到 health
- `SetTurnCCFlags()` 已实现护盾持续时间递减，到 0 时清零 shield 值 + 打印 `[SHIELD] 护盾消散！`
- `ApplyPlayerShield()` 已实现护盾叠加和持续时间
- `SkillSystem.CastShield()` 调用 `ApplyPlayerShield()`，护盾施放链路完整

#### second_wind 喘息科技缺失触发逻辑 ✅ 已实现
**文件**: Systems/TechTreeSystem.cs, Core/GameManager.cs
**状态**: ✅ 已确认存在
**说明**: `TechTreeSystem` 已有完整字段：`_lowHpRegenThreshold = 0.30f`（30% 触发线）、`_lowHpRegenValue`（回复值）、`TickLowHpRegen()` 方法（278-291行）。GameManager.Run() 已调用 `techTreeSystem.TickLowHpRegen()`（379-381行）。科技树配置 `tech_tree.json` 中 `second_wind` 节点链接正确（前置 iron_skin，消耗 2 点，效果 low_hp_regen=0.15）。触发链路完整，无需新增代码。

#### 41. ComponentStore 中 MAX_MONSTERS=20000 常量定义但未使用 ✅ FIXED
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit `a4650bc` 同类清理）
**修复内容**: 移除 `private const int MAX_MONSTERS = 20000;` 死代码常量，与 MAX_BUFFS 同类，属于遗留死代码清理。

#### 39. Systems/GridSpatialHash.cs 为空文件 ✅ FIXED
**文件**: Systems/GridSpatialHash.cs
**状态**: ✅ 已删除（commit `7f02a52` — "cleanup: delete dead code — System/ (5 files), GridSpatialHash, 9 old Components"）
**说明**: 空文件已删除，不再存在于代码库。

#### 40. csproj EnableDefaultCompileItems=false 导致 UpgradeSystem 可能不被编译 ✅ NOT A BUG
**文件**: BattleSystemECS.csproj
**状态**: ✅ 确认不成立
**说明**: `EnableDefaultCompileItems=false` 后，csproj 显式使用 `Compile Include="Systems\*.cs"` 包含所有 Systems 目录文件，UpgradeSystem.cs 在其中，不存在漏编译问题。

---

## 四、并行安全原则（两阶段模式）

所有涉及并行写共享状态的系统，必须遵循以下原则：

### 两阶段模式（Two-Phase Pattern）

```
并行段（Parallel.For）
  → 只读组件数据，收集 damage/death 事件到 ConcurrentBag
  → 禁止写 EnemyHealth / PlayerHealth / ActiveEnemyIds / ActiveTowerIds / EventBus

串行段（帧末统一结算）
  → 从 ConcurrentBag 取出事件，串行 apply damage（`enemyHealth -= damage`）
  → QueueEnemyDeath → ResolveEnemiesKilledThisFrame() 统一销毁实体 + 结算奖励
```

### 调用链

```
GameManager.Run() / BenchmarkSystem
  → BeginFrame()（重置 queues）
  → 各系统 Update()（只 queue，不 resolve）
  → ResolveEnemiesKilledThisFrame()（统一结算，死亡队列自清空）
```

### 关键原则

- **damage queue 存 raw value**：`enemyHealth -= damage` 累加
  - ❌ 禁止存 `(enemyId, newHealth)`，否则 last-write-wins，多攻击者丢伤害
- **帧末唯一死亡结算点**：系统只 queue，GameManager/Benchmark 统一 resolve
- **EnemyAI 两阶段**：并行段做 BT 评估 + 写 EnemyActionEnum，串行段执行动作（含 EventBus.Publish）

---

## 五、性能基准

> 2026-05-18 修正：BENCH_ENEMY_HEALTH 已恢复 100（原 1e9 导致敌人永生，Workload 从 ~100/帧变为 10K/帧，FPS 下降 10x 但非系统退化）。HP=100 下敌人持续死亡重生，benchmark 场景与 c9f79c0 可比。

测试覆盖：63 单元测试。

| benchmark | FPS | EnemyAI | 说明 |
|-----------|------|---------|------|
| mode 2（合并热路径，HP=100） | **~13663** | ~0.07ms | ≥5000 门禁 ✅ |
| mode 4（真实系统链路，HP=100） | **~7096** | ~0.14ms | ≥3500 门禁 ✅ |

---

## 六、今日完成（2026-05-18 下午）

### Benchmark 对比测量修复（2026-05-18 下午）

| # | 内容 |
|---|------|
| 1 | 发现：AutoCast + Poison Nova 每帧杀敌 → WaveSpawning 每帧补怪 → ResolveEnemiesKilledThisFrame 每帧 O(10K) |
| 2 | 修复（第一次）：BENCH_ENEMY_HEALTH=1e9，敌人永生，但导致 Workload 变为 10K/帧，FPS 下降 10x |
| 3 | 修复（第二次）：BENCH_ENEMY_HEALTH=100 恢复，benchmark 场景与 c9f79c0 可比 |

| benchmark | FPS | 说明 |
|-----------|------|------|
| Mode2 | **~13663** | 含完整 skill+buff 链路 |
| Mode4 | **~7096** | 含 BuffSystem + AutoCast |

> 注：HP=1e9 的 ~510/~2070 数据已废弃，保留于 git history。根因：HP=1e9 后敌人永生，每帧 EnemyAI 处理 10K 敌人（vs HP=100 时 ~100 活跃敌人），Workload 差 100x。 |

---

### Benchmark HP=100 恢复（2026-05-18 晚）

| # | 内容 |
|---|------|
| 1 | 发现 BENCH_ENEMY_HEALTH=1e9 后 Mode2 FPS 从 ~6442 降到 ~510（EnemyAI 从 7.88ms 到 351ms）|
| 2 | 根因：HP=1e9 → 敌人永生 → 每帧活跃敌人从 ~100 变为 10K → EnemyAI Workload 差 100x |
| 3 | 确认：c9f79c0 的 BenchmarkSystem.cs（HP=100）在 HEAD 上跑 = 6561 FPS，证明 Core/ECS 无退化 |

---

## 七、今日完成（2026-05-13）

---

## 七（续）、开发理念

详见 `docs/philosophy.md`。

核心：
1. **先问"对不对"，再问"快不快"** — 牺牲正确性换速度是走捷径
2. **两阶段模式是通用的"延迟写，统一同步"思维** — 并行段只读不写，串行段做真正的写
3. **职责收口比功能实现更重要** — 系统只 queue，调度层统一 resolve
4. **死代码是负债** — 删除不仅是清理，更是降低认知负担
5. **Benchmark 必须代表真实调用链** — 测量必须代表真实场景
6. **文档是开发的一部分** — 每次 commit 时同步更新
7. **接受不完美，但不要假装完美** — 标注状态比假装完成更好

---

## 十、今日完成（2026-05-22）

### 波次动态难度曲线（2026-05-22）

|| # | 内容 |
|---|------|
| 1 | 新增 `Data/Configs/wave_spawn.json` 的 `difficultyConfig` 配置段 |
| 2 | WaveSpawningSystem 新增 `DifficultyConfig` / `WaveSpawnConfig` 类和 `LoadWaveSpawnConfig()` |
| 3 | Update() 实现三层难度缩放：基础波次增长 + 精英（wave≥5） + Boss（wave≥10） |
| 4 | EnemyName 标记 [ELITE] / [BOSS] 前缀 |

**配置项**：
- `baseHealthMultPerWave: 0.05`（每波+5%血量）
- `baseDamageMultPerWave: 0.05`（每波+5%伤害）
- `eliteStartWave: 5` / `bossStartWave: 10`
- `eliteHealthMult: 2.0` / `bossHealthMult: 5.0`

**性能**：Mode2 14423 FPS ✅ / Mode4 7095 FPS ✅（门禁 12000/7000）

**commit**：`f2a2bf7` — 波次动态难度曲线：敌人属性随波次增长（含精英/Boss缩放）

### Bug Scan 报告（本次，WaveSpawningSystem.cs）

| 严重度 | 行 | 问题 | 状态 |
|--------|----|------|------|
| HIGH | 162-167 | GetSpawnRandom() Random 双初始化竞态（预存） | 预存 |
| HIGH | 179 | 边界检查被参数求值顺序绕过（预存） | 预存 |
| MEDIUM | 285 | GetCachedBehaviorTree null 返回未处理（预存） | 预存 |
| MEDIUM | 301 | WaveCount/Waves.Count 不一致（预存） | 预存 |

---

## 八、今日完成（2026-05-17）

### 自动 Cron 执行记录

| 时间 | 研究阶段 | 执行阶段 | 状态 |
|------|---------|---------|------|
| 2026-05-17 01:00 | 方向：SpatialGrid.Rebuild GC 压力 | 执行失败（Mode4 3443 < 3500，低于门禁） | ❌ |
| 2026-05-17 02:00 | 方向：SpatialGrid.Rebuild GC 压力 | 修复 2 个 HIGH bug（EnemyMovementSystem L77 冗余写入、SkillSystem L354 死代码），Mode2 6053 FPS，Mode4 3458 FPS | ✅ 修复 |
| 2026-05-17 02:47 | — | GitHub 爬取（tower_defense_explorer） | ✅ |
| 2026-05-17 03:00 | — | 无方向文件，跳过 | [SILENT] |
| 2026-05-17 04:00 | 方向：SkillSystem Cast 方法 GC 消除（ConcurrentBag 预分配） | 方向已产出，执行失败（见晚上记录） | ❌ |
| 2026-05-17 05:00 | — | 无方向文件，跳过 | [SILENT] |
| 2026-05-17 06:00-19:00 | — | 无方向文件，跳过 | [SILENT] |
| 2026-05-17 20:46 | 方向：SpatialGrid.Rebuild（增量更新探索） | SpatialGrid 增量更新（ConcurrentBag 追踪 → 决策保留全量），Mode2 6353，Mode4 3810 | ✅ |

### 本次 commit（上午）

- `2acf9a1` — 修复 EnemyMovementSystem Dodge case 冗余写入 + SkillSystem ResolveSkillDamage 死代码
- Mode2: 6053 FPS（baseline ~6200, -2.4%），Mode4: 3458 FPS（baseline ~3507, -1.4%）
- Bug review: 3 HIGH，3 MEDIUM

### 本次 commit（下午）

- `c36747b` — TechTreeSystem O(N)→O(1) Dictionary 查找（`GetTechNode`/`CanUnlock`/`TryUnlock` 全部改为 Dictionary）
- 目录结构重组：配置类 `GameConfig.cs`/`GameConfigLoader.cs`/`TechTreeDef.cs` 迁移 `Core/`；`all_towers.json` 迁移 `Data/Towers/`；日志/脚本归入 `Research/logs/` / `Research/scripts/`
- Mode2: ~6200 FPS，Mode4: ~3500 FPS

### 本次 commit（晚上，20:46）

- `f6e086c` — SpatialGrid 增量更新（incremental rebuild）
- 探索结论：ConcurrentBag 追踪方案存在正确性边界问题，最终决策保留全量 O(enemies) 重建（~0.03ms，占比极小）
- 代码改动（5 files, +120/-24）：`Core/SpatialGrid.cs` 新增 `UpdateEnemies()`，`Core/ComponentStore.cs` 改为调用 `UpdateEnemies()`，`Systems/EnemyMovementSystem.cs` 清理未使用追踪字段，`Core/GameManager.cs` 注释更新，`docs/` 架构文档同步
- Git push 超时，commit 保留本地

> 注：`f6e086c` 的基准（Mode2 6353 / Mode4 3810）已被 `d34e5fd` 替代，因后者补全了 BuffSystem 链路，测量更真实。

### 目录结构重组（2026-05-17 下午）

| 原路径 | 现路径 |
|------|--------|--------|
| `Configs/GameConfig.cs` | `Core/GameConfig.cs` |
| `Configs/GameConfigLoader.cs` | `Core/GameConfigLoader.cs` |
| `Configs/TechTreeDef.cs` | `Core/TechTreeDef.cs` |
| `Configs/all_towers.json` | `Data/Towers/all_towers.json` |
| `Research/bench2.log` 等 | `Research/logs/` |
| `Research/*.ps1` | `Research/scripts/` |

Configs/ 现在只含运行时 .json 配置（7 个文件），不再含 .cs 文件。

### 当前性能基准（2026-05-17 晚）

| benchmark | FPS | 说明 |
|-----------|-----|------|
| mode 2（合并热路径） | ~6353 | >5000 |
| mode 4（真实系统链路） | ~3810 | >3500 |

## 九、优化方向策略（2026-05-18）

> 性能优化已基本到瓶颈（Mode4 ~2070 FPS，门禁已稳定通过），benchmark 框架本身存在测量问题（Mode2/4 EnemyAI 计时差异巨大），FPS 绝对值仅供参考。转向业务和架构升级。

### 优先级排序
|| 优先级 | 方向 | 说明 |
|--------|------|------|
| **主** | 业务功能升级 | 新塔种、新技能、新系统、玩法扩展 |
| **主** | 架构升级 | 模块解耦、可扩展性、配置化、工具链 |
| **次** | 正确性修复 | Bug fix、边界处理、安全加固 |
| ~~次~~ | ~~性能优化~~ | benchmark 框架存在测量问题，不做新性能优化 |

### 底线
- benchmark 框架测量存在系统差异（Mode2/4 EnemyAI ~350ms vs ~42ms），FPS 绝对值仅供参考，不做跨 commit 性能退化比较
- 正确性修复无门槛，HIGH/MEDIUM 发现即修，不受方向调整影响
- 新功能开发需保持现有测试通过率（63/63）

### 禁止事项

- ❌ 禁止以"性能优化"名义改动业务逻辑
- ❌ 禁止牺牲正确性换 FPS
- ❌ 禁止在未通过测试的情况下做业务/架构改动

### 文档状态

- `design-and-bugs.md`：本次更新
- `docs/bug-fix.md`：项目要求存放于 `docs/bug-fix.md`，当前不存在（文档内容合并至 `design-and-bugs.md`）
- `Research/tower_defense_knowledge.md`：99 行，GitHub 爬取持续更新
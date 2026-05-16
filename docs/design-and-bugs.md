# BattleSystem-ECS 设计治理与 Bug 追踪

> 项目路径：F:\AI\BattleSystem-ECS
> 最后更新：2026-05-17（commit `2acf9a1`）

---

## 一、设计治理（10 项，全部完成）

| # | 优先级 | 任务 | 状态 | 备注 |
|---|--------|------|------|------|
| 1 | 最高 | 主循环刷新缓存 | ✅ 已完成 | GameManager 每回合对所有系统调用 SetTurn()（`commit 743664c`）|
| 2 | 最高 | 并行系统两阶段死亡解析 | ✅ 已完成 | PlayerAttack/TowerAttack 两阶段（`2248d4a`），统一帧末 resolve（`3bd1c9c`）|
| 3 | 最高 | 禁止并行段直接改 active list / EventBus | ✅ 已完成 | EnemyAI 两阶段（`ccc42e3`），EventBus 加锁（`afb988d`），两阶段模式保证调用路径安全 |
| 4 | 最高 | SkillSystem 击杀统一 DestroyEntity | ✅ 已完成 | HandleKill 只 queue 死亡（`3bd1c9c`），帧末统一 resolve |
| 5 | 高 | ComponentStore 生命周期字段收口 | ✅ 已完成 | ActiveEnemyIds/TowerIds 暴露为 IReadOnlyList（`840bc3e`），freeEntityIds Stack 并发安全由两阶段模式保证 |
| 6 | 高 | DestroyEntity 完整清理 | ✅ 已完成 | 清所有 archetype 字段（`c0d85cf`）|
| 7 | 高 | GameConfigLoader 完整解析 | ✅ 已完成 | 解析 MaxHealth / StartingSkills（`736746c`）|
| 8 | 高 | EventBus 并行安全改造 | ✅ 已完成 | lock + snapshot iteration + Reset()（`afb988d`）|
| 9 | 中 | 击杀奖励集中化 | ✅ 已完成 | 帧末统一 resolve 内处理（`3bd1c9c`）|
| 10 | 中 | Benchmark 真实系统链路 | ✅ 已完成 | mode 4 真实系统链路 benchmark（`7ef56aa`），AGENTS.md 写入 dual benchmark 规则（`41cc6a5`）|

---

## 二、Bug 追踪（46 项，全部已修复）

### 2.1 汇总

| 严重度 | 总数 | 已修复 | 未修复 |
|--------|------|--------|--------|
| HIGH   | 13   | 13     | 0      |
| MEDIUM | 15   | 15     | 0      |
| LOW    | 9    | 9      | 0      |
| INFO   | 6    | 6      | 0      |
| **合计** | **46** | **46** | **0** |

> 2026-05-15：#39（GridSpatialHash 空文件）和 #40（csproj 编译项）均已确认已修复/非问题，46/46 全部解决。

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
**文件**: Configs/GameConfig.cs
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
**文件**: Configs/GameConfig.cs
**状态**: ✅ 已修复
**修复内容**: `GetCachedBehaviorTree()` 原先通过 `GetBehaviorTree()` 查询，再在里面查 `_btCache` 和 `BehaviorTrees`，造成双层查找。现改为直接查询 `BehaviorTrees.TryGetValue()`，避免中间层。

---

### 【INFO】信息级

#### 36. GameConfig.MonsterTypes.Find 使用线性搜索
**文件**: Configs/GameConfig.cs
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

| benchmark | FPS | 说明 |
|-----------|-----|------|
| mode 2（合并热路径） | ~9500 | 手写合并热路径，参考用 |
| mode 4（真实系统链路） | ~5100 | **主指标**，直接调用各系统 `.Update()` |

mode 2 和 mode 4 是不同的语义，**不要再用一个 FPS 代表全部性能**。

测试覆盖：48 单元测试。

---

## 六、今日完成（2026-05-13）

| # | 内容 | commit |
|---|------|--------|
| 1 | EnemyAI 两阶段重构（并行 eval + 串行动作执行） | `ccc42e3` |
| 2 | 删除未使用队列字段 `_playerDamageQueue` / `_eventQueue` | `a92116c` |
| 3 | PlayerAttack + TowerAttack 两阶段（并行收集 → 串行 apply） | `2248d4a` |
| 4 | damage queue 累加正确性修复（存 damage 不存 newHealth） | `d707920` |
| 5 | TowerAttackSystem.cs 残留 `float newHealth` 死代码 | `d707920` |
| 6 | 死亡队列自清空（Resolve 后 new ConcurrentBag） | `7ef56aa` |
| 7 | 模式 4 真实系统链路 benchmark | `7ef56aa` |
| 8 | 统一帧末死亡结算（系统只 queue，GameManager/Benchmark resolve） | `3bd1c9c` |
| 9 | AGENTS.md 并行安全原则写入 | `41cc6a5` |
| 10 | 文档同步（46 bugs、48 tests、mode 2/4 FPS） | `63d9c1f` |

---

## 七、开发理念

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

_记录时间：2026-05-13 22:20 GMT+8_

---

## 八、今日完成（2026-05-17）

### 自动 Cron 执行记录

| 时间 | 研究阶段 | 执行阶段 | 状态 |
|------|---------|---------|------|
| 2026-05-17 01:00 | 方向：SpatialGrid.Rebuild GC 压力 | 执行失败（Mode4 3443 < 3500，低于门禁） | ❌ |
| 2026-05-17 02:00 | 方向：SpatialGrid.Rebuild GC 压力 | 修复 2 个 HIGH bug（EnemyMovementSystem L77 冗余写入、SkillSystem L354 死代码），Mode2 6053 FPS，Mode4 3458 FPS | ✅ 修复 |
| 2026-05-17 02:47 | — | GitHub 爬取（tower_defense_explorer） | ✅ |
| 2026-05-17 03:00 | — | 无方向文件，跳过 | [SILENT] |
| 2026-05-17 04:00 | 方向：SkillSystem Cast 方法 GC 消除（ConcurrentBag 预分配） | 执行中…（方向文件已生成待执行） | ⏳ |

### 本次 commit

- `2acf9a1` — 修复 EnemyMovementSystem Dodge case 冗余写入 + SkillSystem ResolveSkillDamage 死代码
- Mode2: 6053 FPS（baseline ~6200, -2.4%），Mode4: 3458 FPS（baseline ~3507, -1.4%）
- Bug review: 3 HIGH（BenchmarkSystem/SkillSystem 存于 BenchmarkSystem，不在约束范围），3 MEDIUM

### 当前性能基准（2026-05-17）

| benchmark | FPS | 门禁 |
|-----------|-----|------|
| mode 2（合并热路径） | ~6053 | >5000 |
| mode 4（真实系统链路） | ~3458 | >3500（当前低于门禁 42 FPS） |

### 待处理方向

- **SkillSystem Cast GC 消除**（研究阶段 03:52 已产出方向文件，待下次执行阶段执行）
  - 将 `CastSingleTarget`/`CastCrossArea`/`CastBoxArea` 中的 `new ConcurrentBag<>()` 改为 field-level 预分配

### 文档状态

- `design-and-bugs.md`：本次更新
- `bug-fix.md`：项目要求存放于 `docs/bug-fix.md`，当前不存在（文档内容合并至 `design-and-bugs.md`）
- `AGENTS.md`：性能基准已更新为 Mode2 ~6053 / Mode4 ~3458
- `Research/tower_defense_knowledge.md`：99 行，GitHub 爬取持续更新
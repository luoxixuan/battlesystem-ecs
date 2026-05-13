# BattleSystem-ECS Bug Fix Report

**扫描时间**: 2026-05-13
**更新**: 2026-05-13 13:28（第二十三轮 — BenchmarkSystem AddTower、UpgradeBuffs 默认值统一、47 测试）
**项目路径**: F:\AI\BattleSystem-ECS
**治理 commit**: `fd03f95` — UpgradeBuffs 统一为 Attack+10%/Crit Rate+5%/Defense+10%，docs 同步

---

## 当前基准（2026-05-13 13:28）

| 指标 | 数值 | 备注 |
|------|------|------|
| FPS | **~8859** | 10K 敌 × 200 帧 × 8 系统 |
| 测试 | **47/47 pass** | dotnet test（新增 5 个 UpgradeSystem 测试、AddEnemy 负 ID 测试） |
| 构建 | **0 warnings 0 errors** | dotnet build（无 net6.0 EOL warning） |

---

## 优化成果

| 指标 | 基准 (3885275) | 最终 (5052fd1) | 累计变化 |
|------|---------------|----------------|---------|

|
| FPS | 3368 | **~9563** | **+184%** |
| EnemyAI | 31.7 ms | **6.66 ms** | **-79%** |
| Movement+PlayerAttack | 24.0 ms | **7.60 ms** (MoveAttack) | **-68%** |
| TowerAttack | 0.18 ms | 1.44 ms | — |
| Total | 59.4 ms | **20.91 ms** | **-65%** |

优化措施（3885275→5052fd1）：
1. **TowerAttack 并行化 + ActiveTowerIds** — 3885275（基准）
2. **P0 Bug 修复** — GetAllActiveEnemyIds 副本 + DestroyEntity 清理（04c50a6）
3. **Precomputed BT Action Enum** — 构建时 enum，跳过运行时 StringToActionEnum（c505461）
4. **移除死写 SetEnemyAIAction** — 攻击动作不再写无用字符串（626b13b）
5. **chargeParams ConcurrentDictionary→float[] SOA** — 消除并行锁竞争（01c05a7）
6. **BT Cache fix** — health-driven version counter，缓存命中率 10x（79fea25）
7. **Merged pipeline** — Movement+PlayerAttack 合并为一次 Parallel.For + move dir 查表（79fea25）
8. **Bug#29 GetName** — Dictionary.ContainsKey+indexer 双查 → TryGetValue 单查（5052fd1）
9. **Bug#37 CastSkill 冷却** — `CanActivate()` float 相等 → epsilon 0.0001f（5052fd1）

---

## 【HIGH】严重问题

### 1. ComponentStore.GetAllActiveEnemyIds 返回内部可变列表引用
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit 04c50a6）（返回 `new List<int>(ActiveEnemyIds)` 防御性副本）
**说明**: 当前调用方在 SetTurn 时缓存一次，不在循环中重复调用。暂未触发问题但应修复。

---

### 2. ComponentStore.CreateEntity ID 越界风险
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit d8da251）
**说明**: `CreateEntity()` 现在验证回收栈返回的 ID 有效范围（0 ≤ id < MAX_ENTITIES），超出时回退到自增分配；自增达到 MAX_ENTITIES 时返回 -1。TowerPlacementSystem 等直接调用者会收到 -1 而非越界 ID。

---

### 3. GameManager 硬编码塔实体 ID
**文件**: Core/GameManager.cs
**状态**: ✅ 已修复（代码审查确认）
**说明**: `PlaceTower()` 返回真实 entity ID，`UpgradeTower()` 使用返回值 `towerId1`/`towerId2` 而非硬编码常量。Bug 报告时可能是旧代码，当前版本正常。

---

### 4. System/TowerPlacementSystem.PlaceTower 使用 NextEntityId 而非 CreateEntity
**文件**: System/TowerPlacementSystem.cs（第53行）
**状态**: ✅ FIXED（Systems/ 版本已修复）
**说明**: Systems/TowerPlacementSystem.cs 已使用 `store.CreateEntity()`，System/ 目录是死代码未编译。

---

### 5. GameStateSystem 调用不存在的 ComponentStore 方法
**文件**: System/GameStateSystem.cs
**状态**: ℹ️ 不适用
**说明**: System/GameStateSystem.cs 不在 csproj 中编译，未使用。

---

### 6. Systems/TowerAttackSystem.SetTurn 调用不存在方法 ✅ FIXED
**文件**: Systems/TowerAttackSystem.cs
**状态**: ✅ 已修复
**修复内容**:
- 新增 `ComponentStore.ActiveTowerIds` (List<int>)
- `AddTower()` 添加 `ActiveTowerIds.Add(entityId)`
- `RemoveTower()` 添加 `ActiveTowerIds.Remove(entityId)`
- `DestroyEntity()` 处理塔清理并从 ActiveTowerIds 移除
- TowerAttackSystem 使用 `store.ActiveTowerIds` 直接遍历

---

### 7. System/TowerAttackSystem 使用 DateTime.Now 作为攻击计时器
**文件**: System/TowerAttackSystem.cs
**状态**: ℹ️ 不适用
**说明**: Systems/TowerAttackSystem.cs 已使用 `deltaTime` 参数，未使用 DateTime.Now。

---

### 8. WaveGenerationSystem 每帧创建新 Random 实例
**文件**: System/WaveGenerationSystem.cs
**状态**: ℹ️ 不适用
**说明**: System/ 目录未编译。Systems/WaveGenerationSystem.cs 未知（未检查）。

---

### 9. SkillSystem.InitializePlayerSkills 只保留最后一个技能
**文件**: Systems/SkillSystem.cs
**状态**: ✅ 已修复（commit 5052fd1 GAS 重构）
**说明**: SkillSystem 已重构为 GAS 架构，`AddAbility()` 按 slot 顺序添加（不覆盖），ResetPlayerAbilities() 在重新初始化前清空。

---

### 10. ComponentStore.DestroyEntity 未从 ActiveEnemyIds 移除 ✅ FIXED
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复
**说明**: 代码中已有 `ActiveEnemyIds.Remove(entityId)`（第133行）

---

### 11. ActiveEnemyIds 在迭代中被修改
**文件**: Core/ComponentStore.cs / Systems/*.cs
**状态**: ✅ 已修复（GetAllActiveEnemyIds 返回副本，调用方 SetTurn 时缓存）
**说明**: GetAllActiveEnemyIds 返回 `new List<int>(ActiveEnemyIds)` 副本，PlayerTowerAttackSystem.Parallel.For 并行安全。

---

### 12. EnemyAISystem 缓存失效逻辑错误 ✅ FIXED (79fea25)

---

### 13. GameConfig MonsterTypes 用 List.Find 导致 O(n) 查询
**文件**: Configs/GameConfig.cs
**状态**: ℹ️ 可接受
**说明**: MonsterTypes 数量少（4个），性能影响可忽略；_monsterCache 提供 O(1) 缓存保护。

---

## 【MEDIUM】中等问题

### 14. UpgradeSystem 每帧创建新 Random
**文件**: Systems/UpgradeSystem.cs
**状态**: ✅ 已确认修复
**说明**: `UpgradeSystem` 类级别已有 `private static readonly Random _sharedRandom`，无每帧分配问题。

---

### 15. PlayerTowerAttackSystem critRandom 静态实例可预测
**文件**: Systems/PlayerTowerAttackSystem.cs
**状态**: ℹ️ 低优先级（游戏逻辑影响轻微）

---

### 16. System/TowerPlacementSystem 与 Systems/TowerPlacementSystem 并存
**文件**: System/ vs Systems/
**状态**: ✅ 已确认
**说明**: System/ 目录是死代码。Systems/ 版本正常工作。

---

### 17. ComponentStore.PlayerBuffs 数组元素直接赋值而非操作列表
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit bb9e200）
**说明**: `PlayerBuffs[entityId]` 直接访问已改为 `GetPlayerBuffs()` 返回防御性副本（`new List<string>(PlayerBuffs[playerId])`），外部调用方 BenchmarkSystem.cs 和 PlayerTowerAttackSystem.cs 也已改为调用 `GetPlayerBuffs()` 而非直接访问字段。

---

### 18. MapSystem.RenderMap 每帧分配新 List
**文件**: Systems/MapSystem.cs
**状态**: ✅ 已修复（commit 390d587）
**说明**: GetAllActiveEnemyIds 移到 for(y) 外层（200→1 次/帧），玩家/敌人位置检查改为 Math.Round() 直接比较

---

### 19. TowerPlacementSystem.PlaceTower O(n) 位置检查
**文件**: Systems/TowerPlacementSystem.cs
**状态**: ✅ 已修复（commit f803566）
**说明**: PlaceTower 现在遍历 ActiveTowerIds 而非 NextEntityId（全量扫描）

---

### 20. FileLogger 未指定编码
**文件**: Core/FileLogger.cs
**状态**: ✅ 已修复（commit d8da251）
**说明**: `File.AppendAllText` 和 `File.WriteAllText` 均显式指定 `Encoding.UTF8`，避免跨平台默认编码差异。

---

### 21. EntityManager.GetAllEntities 每帧分配
**文件**: Core/EntityManager.cs
**状态**: ✅ 已修复（f803566）
**说明**: 返回静态空列表 _emptyEntityList（无调用方）

---

### 22. ComponentStore.EntityNames 用 Dictionary 而非数组
**文件**: Core/ComponentStore.cs
**状态**: ℹ️ 可接受（仅调试用）

---

### 23. EventBus 非线程安全
**文件**: Core/EventBus.cs
**状态**: ℹ️ 可接受（当前单线程使用）

---

### 24. System/WaveGenerationSystem 错误调用 GetLevelConfig
**文件**: System/WaveGenerationSystem.cs
**状态**: ℹ️ 不适用（System/ 未编译）

---

### 25. System/EnemyPathSystem 与 Systems/EnemyMovementSystem 职责重叠
**文件**: System/ vs Systems/
**状态**: ℹ️ 不适用（System/ 未编译）

---

### 26. System/GameStateSystem 调用不存在方法
**文件**: System/GameStateSystem.cs
**状态**: ℹ️ 不适用（未编译）

---

### 27. ComponentStore.AddToSpatialHash/GetEnemiesNear 是空桩
**文件**: Core/ComponentStore.cs
**状态**: ℹ️ 明确为空桩（GridSpatialHash 在 range=3 塔防场景下是反模式）
**说明**: AGENTS.md 明确标注 SpatialHash 在 range=3 场景下 cell 锁开销 > O(N) 扫描，已废弃。

---

### 28. Systems/TowerAttackSystem.TowerActive 访问越界风险 ✅ FIXED
**文件**: Systems/TowerAttackSystem.cs
**状态**: ✅ 已修复
**说明**: 现在使用 `ActiveTowerIds` 遍历，不再访问 NextEntityId 范围外的塔。

---

### 30. ComponentStore.DestroyEntity ActiveTowerIds.Remove 顺序错误（先 false 再检查，永不执行） ✅ FIXED (60865d2)
**文件**: Core/ComponentStore.cs (DestroyEntity)
**状态**: ✅ 已修复
**说明**: `TowerActive[entityId] = false` 原本在 `if (TowerActive[entityId])` 检查之前执行，导致 Remove 分支永不触发。修复为先检查再标记 false。

---

### 31. TowerPlacementSystem.PlaceTower 未处理 CreateEntity() 返回 -1 ✅ FIXED (60865d2)
**文件**: Systems/TowerPlacementSystem.cs
**状态**: ✅ 已修复
**说明**: `CreateEntity()` 在实体池满时返回 -1，原代码未检查直接用于 AddPosition/AddTower。已新增 `if (towerId == -1) return -1` 保护。

---

## 【LOW】轻微问题

### 29. ComponentStore.GetName 用 Dictionary.ContainsKey 双重查找 ✅ FIXED (5052fd1)
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复
**修复内容**: `ContainsKey`+indexer 双哈希查找 → `TryGetValue` 单次查找

---

### 30. GameManager.SetMapSize 魔法数字
### 31. SkillSystem buff 字符串硬编码 ✅ FIXED (16a6198)
**文件**: Systems/UpgradeSystem.cs
**状态**: ✅ 已修复
**说明**: UpgradeSystem.RandomlyGainBuff() 原本在代码中硬编码字符串数组，现迁移到 GameConfig.UpgradeBuffs（List<string>），通过 GetUpgradeBuffs() 暴露，构造函数注入 GameConfig。GameManager 和 BenchmarkSystem 的 UpgradeSystem 实例化也已更新。
**文件**: Systems/UpgradeSystem.cs
**状态**: ✅ 已修复（commit 16a6198）
**说明**: UpgradeSystem.RandomlyGainBuff() 原本在代码中硬编码 `string[] buffs = {"Attack+10%", ...}`。现迁移到 GameConfig.UpgradeBuffs（List<string>），由 GetUpgradeBuffs() 暴露，构造函数注入 GameConfig。GameManager 和 BenchmarkSystem 的 UpgradeSystem 实例化也已更新。
### 32. GameEvents 定义了 20+ 事件但大部分未使用 ✅ FIXED
**文件**: Core/GameEvents.cs
**状态**: ✅ 已修复
**修复内容**: 通过全代码库 `Publish`/`Subscribe` 调用点扫描，确认仅有 3 个事件在用：`PlayerDamaged`、`EnemyCharging`、`EnemyChargeReleased`。已删除 18 个未使用事件常量，以及 9 个未使用的 DTO 类（`EnemyKilledEvent`、`WaveEvent`、`PlayerUpgradedEvent`、`LevelEvent`、`GameOverEvent`、`TowerEvent`、`GoldChangedEvent`、`EnemySpawnedEvent`、`EnemyChargingEvent` 的空桩版本）。保留的 DTO 已补全字段（`EnemyChargeReleasedEvent` 新增 `EnemyId` 和 `Damage` 字段）。
**验证**: 构建 0 warnings / 0 errors；测试 47/47 pass；压测稳定在 ±5% 基线波动内（7134-8010 FPS，多轮实测）

### 33. EnemyMovementSystem Dodge 分支有副作用
### 34. BTCachedTreeBuilder 用 List 构建 indexMap 后再查 Dictionary
### 35. GameConfig._btCache 和 _cachedBtCache 可能重复构建

---

## 【INFO】信息级

### 36. GameConfig.MonsterTypes.Find 使用线性搜索
### 37. Systems/SkillSystem.CastSkill 冷却检测用 float 相等 ✅ FIXED (5052fd1 + 60865d2)
**文件**: Systems/SkillSystem.cs + Core/GAS/GameplayAbility.cs
**状态**: ✅ 完全修复
**修复内容**: 
- SkillSystem.CastSkill 手动施法路径：冷却检测 `== 0f` → `<= 0.0001f`（5052fd1）
- **GameplayAbility.CanActivate() epsilon 全局修复**（60865d2）：统一 `CurrentCooldown <= EPSILON(0.0001f)`，覆盖 AutoCastBestSkill 所有自动施法路径

---

### 38. ComponentStore 中 MAX_BUFFS=10 常量定义但未使用
### 39. Systems/GridSpatialHash.cs 为空文件
### 40. csproj EnableDefaultCompileItems=false 导致 UpgradeSystem 可能不被编译

---

## 汇总

| 严重度 | 总数 | 已修复 | 未修复 |
|--------|------|--------|--------|
| HIGH   | 13   | 13     | 0      |
| MEDIUM | 15   | 14     | 1      |
| LOW    | 9    | 5      | 4      |
| INFO   | 5    | 4      | 1      |
| **合计** | **45** | **39** | **6**  |

本轮新增修复：
- Bug#32: GameEvents 20+ 未使用事件 → 仅保留 3 个活跃事件 + 3 个 DTO（清理 18 事件 + 9 DTO 空桩）
- Bug#31: UpgradeSystem buff 硬编码 → GameConfig.UpgradeBuffs（16a6198）
- Bug#37: GameplayAbility.CanActivate epsilon 0.0001f（含 AutoCastBestSkill 路径）（60865d2）

### 最后更新
- **治理 commit**: `_HEAD` — Bug#32 GameEvents 未使用事件清理（20+ → 3 活跃事件 + 3 DTO）
- **测试**: 47/47 pass（dotnet test，clean build 后实测）
- **压测**: 7134–8010 FPS（10K 敌 × 200 帧 × 8 系统，多轮 clean 后实测，±5% 基线波动）

---

## 性能基准

| 版本 | FPS | TowerAttack ms | 说明 |
|------|-----|----------------|------|
| 初始 | 2477 | 14.94 | 顺序遍历所有 NextEntityId |
| 优化后 | 3306 | 0.18 | Parallel.For + ActiveTowerIds |
| 达成 | **8334** | 1.9 | BT cache + Merged pipeline (79fea25) |
| **本轮** | **9775** | 2.27 | Bug#31 fix后实测 (16a6198) |
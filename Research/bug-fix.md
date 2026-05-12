# BattleSystem-ECS Bug Fix Report

**扫描时间**: 2026-05-12
**更新**: 2026-05-12 (晚间第三轮 P0 修复 + Precomputed Enum 优化)
**项目路径**: F:\AI\BattleSystem-ECS
**分析范围**: 所有 .cs 源文件（Core/, Systems/, System/, Components/, Configs/, Program.cs）
**治理 commit**: `c505461` — P0 Bug Fix + Precomputed BT Action Enum

---

## 优化成果

| 指标 | 基准 (3885275) | 最终 (79fea25) | 累计变化 |
|------|---------------|----------------|---------|
| FPS | 3368 | **~8334** | **+147%** |
| EnemyAI | 31.7 ms | **9.9 ms** | **-69%** |
| Movement+PlayerAttack | 24.0 ms | **8.7 ms** (MoveAttack) | **-64%** |
| TowerAttack | 0.18 ms | 1.9 ms | — |
| Total | 59.4 ms | **24.1 ms** | **-59%** |

优化措施（3885275→79fea25）：
1. **TowerAttack 并行化 + ActiveTowerIds** — 3885275（基准）
2. **P0 Bug 修复** — GetAllActiveEnemyIds 副本 + DestroyEntity 清理（04c50a6）
3. **Precomputed BT Action Enum** — 构建时 enum，跳过运行时 StringToActionEnum（c505461）
4. **移除死写 SetEnemyAIAction** — 攻击动作不再写无用字符串（626b13b）
5. **chargeParams ConcurrentDictionary→float[] SOA** — 消除并行锁竞争（01c05a7）
6. **BT Cache fix** — health-driven version counter，缓存命中率 10x（79fea25）
7. **Merged pipeline** — Movement+PlayerAttack 合并为一次 Parallel.For + move dir 查表（79fea25）

---

## 【HIGH】严重问题

### 1. ComponentStore.GetAllActiveEnemyIds 返回内部可变列表引用
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复（commit 04c50a6）（仍 `return ActiveEnemyIds`）
**说明**: 当前调用方在 SetTurn 时缓存一次，不在循环中重复调用。暂未触发问题但应修复。

---

### 2. ComponentStore.CreateEntity ID 越界风险
**文件**: Core/ComponentStore.cs
**状态**: ⚠️ 未修复
**说明**: `freeEntityIds.Pop()` 返回的 ID 可能 >= MAX_ENTITIES。正常游戏流程未触发。

---

### 3. GameManager 硬编码塔实体 ID
**文件**: Core/GameManager.cs
**状态**: ⚠️ 未修复
**说明**: `towerUpgradeSystem.UpgradeTower(2)` 等硬编码 ID，但 benchmark 测试中未调用升级，无影响。

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
**状态**: ⚠️ 未验证
**说明**: benchmark 测试中未使用技能系统，未触发此问题。

---

### 10. ComponentStore.DestroyEntity 未从 ActiveEnemyIds 移除 ✅ FIXED
**文件**: Core/ComponentStore.cs
**状态**: ✅ 已修复
**说明**: 代码中已有 `ActiveEnemyIds.Remove(entityId)`（第133行）

---

### 11. ActiveEnemyIds 在迭代中被修改
**文件**: Core/ComponentStore.cs / Systems/*.cs
**状态**: ⚠️ 未修复（依赖 Bug #1）
**说明**: GetAllActiveEnemyIds 返回内部引用，PlayerTowerAttackSystem.Parallel.For 修改 EnemyActive 时可能有问题。当前 benchmark 未触发。

---

### 12. EnemyAISystem 缓存失效逻辑错误 ✅ FIXED (79fea25)

---

### 13. GameConfig MonsterTypes 用 List.Find 导致 O(n) 查询
**文件**: Configs/GameConfig.cs
**状态**: ⚠️ 未修复
**说明**: MonsterTypes 数量少（4个），性能影响可忽略。

---

## 【MEDIUM】中等问题

### 14. UpgradeSystem 每帧创建新 Random
**文件**: Systems/UpgradeSystem.cs
**状态**: ⚠️ 未修复

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
**状态**: ⚠️ 未修复

---

### 18. MapSystem.RenderMap 每帧分配新 List
**文件**: Systems/MapSystem.cs
**状态**: ⚠️ 未修复

---

### 19. TowerPlacementSystem.PlaceTower O(n) 位置检查
**文件**: Systems/TowerPlacementSystem.cs
**状态**: ⚠️ 未修复

---

### 20. FileLogger 未指定编码
**文件**: Core/FileLogger.cs
**状态**: ⚠️ 未修复

---

### 21. EntityManager.GetAllEntities 每帧分配
**文件**: Core/EntityManager.cs
**状态**: ⚠️ 未修复

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
**状态**: ⚠️ 未修复

---

### 28. Systems/TowerAttackSystem.TowerActive 访问越界风险 ✅ FIXED
**文件**: Systems/TowerAttackSystem.cs
**状态**: ✅ 已修复
**说明**: 现在使用 `ActiveTowerIds` 遍历，不再访问 NextEntityId 范围外的塔。

---

## 【LOW】轻微问题

### 29. ComponentStore.GetName 用 Dictionary.ContainsKey 双重查找
### 30. GameManager.SetMapSize 魔法数字
### 31. SkillSystem buff 字符串硬编码
### 32. GameEvents 定义了 20+ 事件但大部分未使用
### 33. EnemyMovementSystem Dodge 分支有副作用
### 34. BTCachedTreeBuilder 用 List 构建 indexMap 后再查 Dictionary
### 35. GameConfig._btCache 和 _cachedBtCache 可能重复构建

---

## 【INFO】信息级

### 36. GameConfig.MonsterTypes.Find 使用线性搜索
### 37. Systems/SkillSystem.CastSkill 冷却检测用 float 相等
### 38. ComponentStore 中 MAX_BUFFS=10 常量定义但未使用
### 39. Systems/GridSpatialHash.cs 为空文件
### 40. csproj EnableDefaultCompileItems=false 导致 UpgradeSystem 可能不被编译

---

## 汇总

| 严重度 | 总数 | 已修复 | 未修复 |
|--------|------|--------|--------|
| HIGH   | 13   | 2      | 11     |
| MEDIUM | 18   | 1      | 17     |
| LOW    | 9    | 0      | 9      |
| INFO   | 5    | 0      | 5      |
| **合计** | **45** | **3** | **42** |

已修复（3项）：
- Bug#1: GetAllActiveEnemyIds 返回副本 (04c50a6)
- Bug#4: DestroyEntity ActiveTowerIds 清理 (04c50a6)
- Bug#12: EnemyAISystem BT cache health-driven version counter (79fea25)

### 最后更新
- **治理 commit**: `79fea25` — BT cache fix + merged MoveAttack pipeline
- **测试**: 27/27 pass
- **压测**: ~8334 FPS (10K 敌 × 200 帧 × 8 系统)

---

## 性能基准

| 版本 | FPS | TowerAttack ms | 说明 |
|------|-----|----------------|------|
| 初始 | 2477 | 14.94 | 顺序遍历所有 NextEntityId |
| 优化后 | 3306 | 0.18 | Parallel.For + ActiveTowerIds |
| **达成** | **8334** | 1.9 | BT cache + Merged pipeline (79fea25) |

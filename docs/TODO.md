# BattleSystem-ECS 设计治理待办

> 来源：漂泊者审查反馈（2026-05-13）
> 优先级顺序：最高 → 高 → 中

---

## 【最高优先级】

### 1. 主循环没有刷新 Movement / PlayerAttack 的 active enemy 缓存
**问题**：GameManager.cs:279-290 每回合只调用 `enemyAISystem.SetTurn(turn)`，但 EnemyMovementSystem 和 PlayerTowerAttackSystem 都在内部缓存 `_activeEnemyList`。第一帧缓存后，新生成的敌人可能不会进入移动/攻击系统。

**修复**：在 GameManager 主循环中，EnemyMovementSystem 和 PlayerTowerAttackSystem 每回合也调用 `SetTurn(turn)`，确保缓存刷新。

**验证**：添加单元测试或让 benchmark 调用正确的主循环。

---

### 2. 并行系统里直接 DestroyEntity()，线程安全风险
**问题**：`PlayerTowerAttackSystem.cs:82-105` 和 `TowerAttackSystem.cs:32-82` 在 `Parallel.For` 内调用 `store.DestroyEntity(enemyId)`。`DestroyEntity` 修改 `ActiveEnemyIds`（List）/ `ActiveTowerIds`（List）/ `freeEntityIds`（Stack），都不是线程安全结构。多塔可能同时选同一敌人，重复 DestroyEntity 导致同一 ID 被 push 两次到 freeEntityIds，后续分配出重复实体 ID。

**修复**：两阶段模式。
- 并行阶段：只收集 `deadEnemyIds` / `damageEvents` / `goldEvents`
- 串行阶段：统一 `ResolveDeaths()`，只销毁一次，统一发奖励

---

### 3. EnemyAI 并行扣玩家血不是原子操作
**问题**：`ComponentStore.cs:595-598`：`PlayerCurrentHealth[playerId] = Math.Max(0f, PlayerCurrentHealth[playerId] - damage)` 是读-改-写竞争。多个敌人在并行路径里同时攻击玩家，伤害可能丢失。

**修复**：并行阶段用线程本地累积总伤害（`Interlocked.Add`），主线程一次性扣血。

---

### 4. SkillSystem 击杀没有统一走实体销毁
**问题**：`SkillSystem.cs:284-290` 只加 gold 和 TotalKills，没有 `DestroyEntity()`，也没有清 EnemyActive。技能杀死的敌人可能还留在 ActiveEnemyIds，被 AI/移动/攻击系统继续处理。

**修复**：所有伤害路径统一到死亡解析入口，不让 PlayerAttack / TowerAttack / SkillSystem 各自处理死亡。

---

## 【高优先级】

### 5. ComponentStore 暴露太多 public 数组，无法维护不变量
**问题**：`ComponentStore.cs` 大量字段是 `public float[]` / `public List<int>`，外部系统可绕过生命周期 API 直接修改，导致 active list 和 flag 不一致、ID 回收重复等。

**修复**：至少把生命周期相关字段收口：
- ActiveEnemyIds / ActiveTowerIds 不直接 public 可变
- 统一通过 AddEnemy / DestroyEnemy / AddTower / DestroyTower
- 对外提供只读 snapshot 或 Span

---

### 6. DestroyEntity 清理不完整，ID 复用可能带脏数据
**问题**：DestroyEntity 只清了一部分状态，没有系统性清理 EnemyBehaviorTree / EnemyTypeName / EnemyChargeParam / tower stats / entity name / ability/effect 计数等。ID 复用时漏写字段会继承旧实体数据。

**修复**：设计按 archetype 的清理：
```
DestroyEnemy(id)
DestroyTower(id)
DestroyPlayerOwnedEffect(id)
```
或 DestroyEntity() 调用完整 reset。

---

### 7. GameConfigLoader 半解析问题
**问题**：`GameConfigLoader.cs:217-287` 手写 parser 只解析了部分字段（Player.Name/Type/AttackRange/AttackInterval/AttackDamage/CurrentLevel/UpgradeThreshold），没解析 MaxHealth / StartingSkills / TowerTypes / Skills / UpgradeBuffs。导致有无配置文件运行结果不一致。

**修复**：完整 JSON 反序列化，或 loader 必须覆盖 GameConfig 所有可配置字段。

---

### 8. EventBus 设计和并行执行模型不匹配
**问题**：EventBus 是全局 singleton + Dictionary，非线程安全。在并行段里 publish 事件有竞争风险，handler 异常只写 stderr，调用方不知道失败。

**修复**：
- 如果事件只用于日志/提示：保留但禁止在并行段发
- 如果事件影响游戏逻辑：改成本帧 event queue，主线程统一 dispatch

---

## 【中优先级】

### 9. 击杀奖励逻辑分散
**问题**：PlayerAttack / TowerAttack / SkillSystem 各处理死亡和奖励，GoldSystem 基本空壳。没有单一死亡结算所有者。

**修复**：统一 DamageSystem / DeathResolutionSystem：
- 统一判断死亡
- 统一 DestroyEntity
- 统一 GoldReward
- 统一 TotalKills
- 统一事件发布

---

### 10. Benchmark 不能完全代表真实主循环
**问题**：
- 热路径手写 Movement + PlayerAttack 合并，不是跑真实系统
- 直接 `EnemyActive = false`，不走 DestroyEntity
- 不暴露主循环 SetTurn() 漏调问题

**修复**：让 benchmark 至少有一个"真实系统链路 benchmark"，跑真实系统调用路径。

---

## 执行顺序

| # | 优先级 | 任务 | 状态 |
|---|--------|------|------|
| 1 | 最高 | 主循环补 SetTurn(turn) | pending |
| 2 | 最高 | 并行系统两阶段死亡解析 | pending |
| 3 | 最高 | 禁止并行段直接改 active list / EventBus | pending |
| 4 | 最高 | SkillSystem 击杀统一 DestroyEntity | pending |
| 5 | 高 | ComponentStore 生命周期字段收口 | pending |
| 6 | 高 | DestroyEntity 完整清理 | pending |
| 7 | 高 | GameConfigLoader 完整解析 | pending |
| 8 | 高 | EventBus 并行安全改造 | pending |
| 9 | 中 | 击杀奖励集中化 | pending |
| 10 | 中 | Benchmark 真实系统链路 | pending |
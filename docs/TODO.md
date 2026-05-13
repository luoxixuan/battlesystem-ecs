# BattleSystem-ECS 设计治理待办

> 来源：漂泊者审查反馈（2026-05-13）
> 优先级顺序：最高 → 高 → 中

---

## 【最高优先级】

### 1. 主循环刷新 Movement / PlayerAttack 的 active enemy 缓存
**问题**：GameManager.cs 每回合只调用 `enemyAISystem.SetTurn(turn)`，但 EnemyMovementSystem 和 PlayerTowerAttackSystem 在内部缓存 `_activeEnemyList`。新敌人可能不进入移动/攻击系统。
**修复状态**：✅ 已完成（`commit 743664c`）— GameManager 每回合对所有系统调用 SetTurn()
**今日补充**：✅ 已完成 — GameManager.Run() 在所有系统调用后统一 ResolveEnemiesKilledThisFrame()（`commit 3bd1c9c`）

---

### 2. 并行系统里直接 DestroyEntity()，线程安全风险
**问题**：`PlayerTowerAttackSystem` 和 `TowerAttackSystem` 在 `Parallel.For` 内调用 `store.DestroyEntity(enemyId)`。`DestroyEntity` 修改非线程安全结构，多塔选同一敌人会重复 push freeEntityIds。
**修复状态**：✅ 已完成（`commit e474f1c`）— 两阶段模式，并行收集→串行 ResolveDeaths
**今日补充**：✅ 已完成 — 统一帧末死亡结算（`commit 3bd1c9c`）：系统只 queue 死亡，GameManager/Benchmark 在帧末统一 resolve，不再各自 resolve

---

### 3. EnemyAI 并行扣玩家血不是原子操作
**问题**：`PlayerCurrentHealth[playerId] = Math.Max(0f, PlayerCurrentHealth[playerId] - damage)` 是读-改-写竞争，多敌人同时攻击玩家伤害可能丢失。
**修复状态**：✅ 已完成（`commit ccc42e3`）— EnemyAI 两阶段重构：并行阶段只做 BT 评估，串行动作执行改用 foreach，`DecreasePlayerHealth` 不再并发调用
**今日补充**：✅ 已完成 — EventBus 并行安全（`commit afb988d` 已加锁），EnemyAI 的 EventBus.Publish 改走串行动作执行阶段

---

### 4. SkillSystem 击杀没有统一走实体销毁
**问题**：`SkillSystem.cs:284-290` 只加 gold 和 TotalKills，没有 `DestroyEntity()`，技能杀死的敌人可能留在 ActiveEnemyIds。
**修复状态**：✅ 已完成（`commit 10d53c4`）— HandleKill 走 ResolveEnemiesKilledThisFrame()
**今日补充**：✅ 已完成 — HandleKill 改为只 queue 死亡（`commit 3bd1c9c`），帧末统一结算职责更干净

---

## 【高优先级】

### 5. ComponentStore 暴露太多 public 数组，无法维护不变量
**问题**：大量 `public float[]` / `public List<int>`，外部系统可绕过生命周期 API 直接修改。
**修复状态**：✅ 已完成 — ActiveEnemyIds/TowerIds 暴露为 IReadOnlyList（`commit 840bc3e`），DestroyEntity 只在帧末串行阶段调用，不再有并行并发修改 active list 的风险
**今日补充**：✅ 已完成 — 两阶段模式保证了所有 DestroyEntity 调用都发生在串行阶段（GameManager/Benchmark 的 ResolveEnemiesKilledThisFrame()），即使 freeEntityIds Stack 内部仍有并发写可能，但 DestroyEntity 的调用路径已经是安全的了

---

### 6. DestroyEntity 清理不完整，ID 复用可能带脏数据
**问题**：DestroyEntity 只清部分状态，ID 复用时漏写字段会继承旧实体数据。
**修复状态**：✅ 已完成（`commit c0d85cf`）— 清所有 archetype 字段
**今日补充**：✅ 已完成 — 统一帧末 resolve 后 `DestroyEntity` 调用时机更统一

---

### 7. GameConfigLoader 半解析问题
**问题**：手写 parser 只解析部分字段，有无配置文件运行结果不一致。
**修复状态**：✅ 已完成（`commit 736746c`）— 解析 MaxHealth / StartingSkills
**今日补充**：✅ 已完成 — 无相关遗留问题

---

### 8. EventBus 设计和并行执行模型不匹配
**问题**：EventBus 是全局 singleton + Dictionary，非线程安全，并行段里 publish 有竞争风险。
**修复状态**：✅ 已完成（`commit afb988d`）— lock + snapshot iteration + Reset()
**今日补充**：✅ 已完成 — EnemyAI 改串行动作执行后，EventBus.Publish 不再从并行段调用（`ccc42e3`）

---

## 【中优先级】

### 9. 击杀奖励逻辑分散
**问题**：PlayerAttack / TowerAttack / SkillSystem 各处理死亡和奖励，GoldSystem 基本空壳。
**修复状态**：✅ 已完成 — 与 #2/#4 同 commit，已统一到两阶段死亡解析
**今日补充**：✅ 已完成 — 统一帧末结算（`commit 3bd1c9c`）后，死亡奖励在 ResolveEnemiesKilledThisFrame() 内统一处理：`TotalKills++` / `PlayerGold += EnemyGoldReward` / `DestroyEntity`

---

### 10. Benchmark 不能完全代表真实主循环
**问题**：
- 热路径手写 Movement + PlayerAttack 合并，不是跑真实系统
- 直接 `EnemyActive = false`，不走 DestroyEntity
- 不暴露主循环 SetTurn() 漏调问题
**修复状态**：✅ 已完成（`commit 7ef56aa`）— 新增 mode 4 真实系统链路 benchmark
**今日补充**：✅ 已完成（`commit 41cc6a5`）— AGENTS.md 写入 dual benchmark 规则：mode 2（合并热路径~9500FPS）参考用，mode 4（真实系统链路~5100FPS）为主指标

---



## 今日开发理念沉淀（2026-05-13）

### 1. 先问"对不对"，再问"快不快"
性能优化应该在 correctness 有保障的前提下去做。牺牲正确性换速度是走捷径，迟早反噬。
> "If it's wrong, making it faster just means you fail more quickly."

### 2. 两阶段模式是通用的"延迟写，统一同步"思维
并行段只读不写只收集，串行段做真正的写。任何共享状态并行系统都应遵循。

### 3. 职责收口比功能实现更重要
系统内部各自调 Resolve 看起来功能正常，但职责边界模糊。清晰的职责边界让系统更可预测。

### 4. 死代码是负债，不是中性资产
增量编译可能掩盖 CS0219 警告。历史遗留的废代码会让整个代码库变得不可信。

### 5. Benchmark 必须代表真实调用链
模式 2 和模式 4 差异说明：测量必须代表真实场景，否则优化方向可能完全错误。

### 6. 文档是开发的一部分，不是"搞完再做"的
每次 commit 时同步更新相关文档，把文档当作代码的一部分来维护。

### 7. 接受不完美，但不要假装完美
未修的问题标注状态，比"修完了"更能帮助下一个接手的人。
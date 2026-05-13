# BattleSystem-ECS 架构文档

> 每次修改架构或业务逻辑后，必须同步更新此文档。
> 最后更新：2026-05-13（commit `69bb49b`）

---

## 1. 项目概述

- **语言**: C# / .NET 6
- **架构**: SOA (Struct of Arrays) ECS
- **定位**: 塔防战斗系统性能基准，逻辑与渲染完全分离
- **性能目标**: 10K 敌 × 200 帧 ≥ 5,000 FPS（mode 4 真实系统链路）
- **性能基准**: mode 2 ~9500 FPS（合并热路径参考）/ mode 4 ~5100 FPS（真实系统链路主指标）
- **测试覆盖**: 48 单元测试

---

## 2. 核心设计原则

1. **SOA 优先**: 所有组件用平行数组（`float[]`, `int[]`, `bool[]`），连续内存访问，CPU 缓存命中率高
2. **并行化**: 每个系统内部使用 `Parallel.For` 批处理，最大化多核利用率
3. **两阶段安全**: 所有并行写共享状态遵循"并行收集 → 串行 apply"模式（见第 2 节）
4. **零分配热路径**: `GetAllActiveEnemyIds()` 返回副本，`ActiveEnemyIds` 用 List 但读取路径无锁
5. **配置驱动**: 技能、科技树、怪物类型、行为树均从 JSON 加载，代码只负责逻辑
6. **帧末统一结算**: 实体生命周期（DestroyEntity/奖励结算）统一在帧末由调度层处理

---

## 并行安全原则（两阶段模式）

所有涉及并行写共享状态的系统，必须遵循**两阶段模式**：

```
并行段（Parallel.For）
  → 只读组件数据，收集 damage/death 事件到 ConcurrentBag
  → 禁止写 EnemyHealth / PlayerHealth / ActiveEnemyIds / ActiveTowerIds / EventBus

串行段（帧末统一结算）
  → 从 ConcurrentBag 取出事件，串行 apply damage（`enemyHealth -= damage`）
  → QueueEnemyDeath → ResolveEnemiesKilledThisFrame() 统一销毁实体 + 结算奖励
```

调用链：
```
GameManager.Run() / BenchmarkSystem
  → BeginFrame()（重置 queues）
  → 各系统 Update()（只 queue，不 resolve）
  → ResolveEnemiesKilledThisFrame()（统一结算，死亡队列自清空）
```

**关键原则**：
- **damage queue 存 raw value**：`(enemyId, damage)` + `enemyHealth -= damage` 累加
  - ❌ 禁止存 `(enemyId, newHealth)`，否则 last-write-wins，多攻击者丢伤害
- **帧末唯一死亡结算点**：系统只 queue，GameManager/Benchmark 统一 resolve
- **EnemyAI 两阶段**：并行段做 BT 评估 + 写 EnemyActionEnum，串行段执行动作（含 EventBus.Publish）

---

## 3. 系统架构图

```
┌─────────────────────────────────────────────────────────┐
│                      GameManager                         │
│  (Initialize → Run → 波次循环 → 回合处理)                │
└──────────────┬──────────────────────────────────────────┘
               │
    ┌──────────┼──────────┬──────────┬──────────┐
    ▼          ▼          ▼          ▼          ▼
 WaveSpawn  EnemyAI   Movement   PlayerAttack  TowerAttack
 (生成敌人)  (BT评估)  (移动+Y)  (攻击伤害)    (塔索敌)
    │          │          │                     │
    └──────────┴──────────┴──────────┬──────────┘
                                      ▼
                              ComponentStore (SOA)
                              ├── PositionX/Y/Active
                              ├── Player: Gold, Level, Buffs, ResearchPoints
                              ├── Enemy: Health, MoveSpeed, ActionEnum, BT
                              ├── Tower: Damage, Range, ActiveTowerIds
                              └── Skill: Abilities, Effects (GAS)
                                      │
                               ┌──────┴──────┐
                               ▼             ▼
                          EventBus       IRenderer
                         (事件发布)    (Console/File)

辅助系统：
  GoldSystem     — 波次结算金币
  UpgradeSystem  — 玩家升级（等级 → 属性）
  SkillSystem    — GAS 技能施放（AreaShape 驱动）
  TechTreeSystem — 研究点数 + 节点解锁
  MapSystem      — 地图渲染（Debug）
  BenchmarkSystem — 全链路性能压测
```

---

## 4. 组件存储（ComponentStore — SOA）

所有组件以 struct of arrays 形式存储在 `Core/ComponentStore.cs`：

### 玩家组件（MAX_PLAYERS = 10）
```csharp
float[]  PlayerAttackDamage, PlayerAttackSpeed, PlayerAttackRange
float[]  PlayerMaxHealth, PlayerCurrentHealth
int[]    PlayerCurrentLevel
float[]  PlayerGold, PlayerUpgradeThreshold
List<>[] PlayerBuffs
int[]    PlayerResearchPoints        // 科技树研究点数
HashSet<>[] PlayerUnlockedTechs     // 已解锁科技节点
```

### 敌人组件（MAX_ENTITIES = 100,000）
```csharp
float[]  EnemyHealth, EnemyMaxHealth, EnemyMoveSpeed, EnemyDamage
int[]    EnemyGoldReward, EnemyWaveNumber
bool[]   EnemyActive
float[]  EnemyChargeParam            // SOA: 替代 ConcurrentDictionary
string[] EnemyAIAction               // 调试用
int[]    EnemyAIChargeCounter, EnemyAILastAttackTurn
EnemyActionType[] EnemyActionEnum    // 预计算 enum（热路径）
BTCachedTree[] EnemyBehaviorTree     // 预缓存 BT（O(1) 访问）
```

### 塔组件
```csharp
string[] TowerType, float[] TowerAttackDamage
int[]    TowerRange, float[] TowerAttackSpeed, TowerLevel
bool[]   TowerActive, float[] TowerLastAttackTime
List<int> ActiveTowerIds             // 仅活跃塔 ID（并行遍历用）
```

---

## 5. 系统说明

| 系统 | 文件 | 职责 | 关键设计 |
|------|------|------|---------|
| WaveSpawningSystem | Systems/ | 波次生成 | `OnWaveComplete` 事件触发科技树点数产出 |
| EnemyAISystem | Systems/ | 行为树评估 | **两阶段**：并行 BT 评估写 EnemyActionEnum，串行动作执行（含 EventBus.Publish） |
| EnemyMovementSystem | Systems/ | 敌人移动 | `EnemyActionEnum` 驱动方向；Dodge 分支有副作用 |
| PlayerTowerAttackSystem | Systems/ | 玩家攻击 | **两阶段**：并行收集 `(enemyId, damage)`，串行 `enemyHealth -= damage` + queue 死亡 |
| TowerAttackSystem | Systems/ | 塔攻击 | **两阶段**：遍历 `ActiveTowerIds`，并行收集 damage，串行 apply + queue 死亡 |
| UpgradeSystem | Systems/ | 玩家升级 | 等级阈值触发；`_sharedRandom` 类级单例 |
| SkillSystem | Systems/ | 技能施放 | GAS 架构；AreaShape 驱动；**只 queue 死亡，帧末统一 resolve** |
| TechTreeSystem | Systems/ | 科技树 | 前置依赖检查；效果缓存在 `TechTreeSystem` 字段 |
| GoldSystem | Systems/ | 金币结算 | 击杀产金；`Interlocked.Add` 并行安全 |
| BenchmarkSystem | Systems/ | 性能压测 | **dual mode**：mode 2 合并热路径 / mode 4 真实系统链路，各独立计时 |

---

## 6. GAS 模块（Core/GAS/）

```
Core/GAS/
├── Attributes.cs          # 属性集定义（ATTACK_DAMAGE, CRIT_RATE...）
├── GameplayEffect.cs      # 效果定义（类型/操作符/数值）
└── GameplayAbility.cs     # 技能定义（冷却/消耗/范围形状）
```

关键类型：
- `AttributeSetDefinitions` — 静态常量定义
- `GameplayAbilityDef` — 技能元数据（Name, Cooldown, AreaShape, FixedBaseDamage）
- `AbilityInstance` — 技能实例（含 CurrentCooldown）
- `AppliedEffect` — 已应用的效果实例

---

## 7. 科技树系统

配置文件：`Configs/tech_tree.json`

结构：
```
researchPointsPerWave: 1
branches:
  - id: offense    (⚔️进攻)
  - id: defense    (🛡️防御)
  - id: economy    (💰经济)
  每分支 5 节点，有前置依赖 (prerequisites)
```

解锁流程：
```
波次完成 → OnWaveComplete → techTreeSystem.OnWaveComplete()
                                ↓
                     PlayerResearchPoints += 1
                                ↓
                     player calls TryUnlock(nodeId)
                                ↓
                     CanUnlock() → 前置检查 → 消耗点数 → ApplyEffects()
```

---

## 8. 行为树（BehaviorTree）

配置文件：`Configs/behavior_trees.json`

- **预计算**：Build 时将 `BTCachedTree` 缓存到 `store.EnemyBehaviorTree[entityId]`
- **评估缓存**：health-driven version counter，enemy health 或 player health 变化时才失效
- **枚举驱动**：BT 结果直接映射到 `EnemyActionType` enum，无需字符串比较

关键类型：
- `BTCachedTree` — 预构建的扁平化树结构
- `BTCachedTreeEvaluator` — 评估器（`EvaluateWithEnum` 返回 action + enum）
- `EnemyActionType` — 动作枚举（MoveToTarget/AttackMelee/RangedAttack/ChargeAttack/Dodge/Retreat/None）

---

## 9. 配置系统

| 文件 | 内容 |
|------|------|
| `Configs/game_config.json` | 怪物类型、等级、波次 |
| `Configs/behavior_trees.json` | 行为树定义 |
| `Configs/skills.json` | 技能定义（未使用，已迁移到 GAS） |
| `Configs/tech_tree.json` | 科技树节点 |
| `Configs/phase_behavior.json` | 阶段行为 |
| `Configs/tower_placement.json` | 塔位配置 |

Loader：`Configs/GameConfigLoader.cs`

---

## 10. 数据流（回合处理）

```
GameManager.Run() → while(gameRunning) → 每回合:
  1. waveSpawning.Update()        → 生成敌人，触发 OnWaveComplete
  2. enemyAI.SetTurn(turn)        → 缓存玩家位置 + enemy list
  3. enemyAI.Update()              → BT 评估 + 缓存命中逻辑
  4. enemyMovement.Update()        → 读取 EnemyActionEnum 移动
  5. playerTowerAttack.Update()     → 攻击范围内敌人
  6. towerAttack.Update(deltaTime) → ActiveTowerIds 并行遍历
  7. gold.Update()                 → 击杀产金
  8. upgrade.Update()              → 阈值触发升级
  9. skill.Update(deltaTime)       → 冷却减少
 10. map.Update()                  → Debug 渲染
```

---

## 11. 关键设计决策

1. **ActiveTowerIds 而非遍历全量**: TowerAttackSystem 只遍历活跃塔，避免 `NextEntityId` 范围外的空数据
2. **BTCachedTree 预缓存**: WaveSpawning 时已将 BT 存到 `store.EnemyBehaviorTree`，EnemyAISystem 无需 Dictionary 查找
3. **ActionEnum 预计算**: BT 构建时转换 string→enum，热路径无字符串比较
4. **并行合并 MoveAttack**: BenchmarkSystem 内置 merged pipeline，单独计时，不影响系统设计
5. **科技树效果缓存**: `TechTreeSystem` 内部字段存储 computed multiplier，`GetFinalAttackDamage()` 合并 base × mult

---

## 12. 已删除（2026-05-13）

| 路径 | 原状态 | 说明 |
|------|--------|------|
| `System/` (大写) | 未编译 | 全目录已删除，原 5 个死文件 |
| `GridSpatialHash.cs` | 空桩 | 已删除，Spatial Hash 在 range=3 场景是反模式 |
| `Components/Components.cs` | 老架构 | 已删除 |
| `Components/BuffDebuffComponents.cs` | 老架构 | 已删除 |
| `Components/GameStateComponent.cs` 等 9 个 | 老架构 | 已删除（仅保留 BuffData/EnemyActionType/EnemyComponent/SkillComponent）|

---

## 13. 更新记录

| 日期 | commit | 变更 |
|------|--------|------|
| 2026-05-13 | `c4c360b` | 清理死代码（System/、GridSpatialHash、9个旧组件、EntityManager精简） |
| 2026-05-13 | `2ce3352` | README 更新（添加 TechTree） |
| 2026-05-13 | `5e01a26` | 新增科技树系统（3分支 × 5节点） |
| 2026-05-12 | `79fea25` | BT Cache fix + Merged pipeline，FPS 8334 |
| 2026-05-12 | `01c05a7` | chargeParams SOA 化 |
| 2026-05-12 | `04c50a6` | P0 Bug 修复（GetAllActiveEnemyIds 副本） |
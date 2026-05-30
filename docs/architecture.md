# BattleSystem-ECS 架构文档

> 每次修改架构或业务逻辑后，必须同步更新此文档。
> 最后更新：2026-05-18（commit `d34e5fd`）

---

## 1. 项目概述

- **语言**: C# / .NET 6
- **架构**: SOA (Struct of Arrays) ECS
- **定位**: 塔防战斗系统性能基准，逻辑与渲染完全分离
- **性能目标**: 10K 敌 × 200 帧 ≥ 5,000 FPS（mode 4 真实系统链路）
- **性能基准**: Mode2 ~13663 FPS / Mode4 ~7096 FPS（`HEAD`，2026-05-20，HP=100，500帧，门禁 ≥12000/≥7000）
- **测试覆盖**: 63 单元测试

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

`ComponentStore` 使用 `partial class` 按领域拆分为 5 个文件，每个自包含其字段声明与访问方法：

| 文件 | 职责 |
|------|------|
| `Core/ComponentStore.cs` | 核心生命周期：常量（MAX_ENTITIES=100000）、Position、实体 CRUD、活跃 ID 管理、死亡队列、构造/析构、查询、SpatialGrid |
| `Core/ComponentStore_Enemy.cs` | 敌人：Health/Armor/CC/Bleed/Burrow/Necromancer/Summon/Boss/Fission/Morph/LifeLink/Path/Teleport/Resistance/Vanguard/Healer/Thief/Affix/Element/Nest/AI + 方法 |
| `Core/ComponentStore_Tower.cs` | 塔：Damage/Range/Targeting/Projectile/Ammo/Overcharge/Synergy/Chrono/Aura/Curse/Pull/Bleed/Income/Construction/Demolish/Link/Patrol/Fog + 方法 |
| `Core/ComponentStore_Player.cs` | 玩家：Attack/Health/Shield/Mana/GlobalSkill/Gold/Buff/CC/TechTree/Combo/Bank + 方法 |
| `Core/ComponentStore_World.cs` | 世界：Weather/DayNight/Objective/Adaptive/Resource/Time/Events/Fog/Ascension/Pickup/Wave/Obstacle/Hazard/Corpse/Skill/GAS + 方法 |

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
int[]    TowerTargetingMode            // 索敌模式：0=最近,1=最远,2=血量最低,3=血量最高,4=最先生成,5=最后生成
string[] TowerType, float[] TowerAttackDamage
int[]    TowerRange, float[] TowerAttackSpeed, TowerLevel
bool[]   TowerActive, float[] TowerLastAttackTime
List<int> ActiveTowerIds             // 仅活跃塔 ID（并行遍历用）
```

---

## 5. 系统说明

| 系统 | 文件 | 职责 | 关键设计 |
|------|------|------|---------|
| WaveSpawningSystem | Systems/ | 波次生成 + 难度曲线 | `OnWaveComplete` 事件触发科技树点数产出；**波次动态难度曲线**（基础缩放 + 精英 + Boss）|
| EnemyAISystem | Systems/ | 行为树评估 | **两阶段**：并行 BT 评估写 EnemyActionEnum，串行动作执行（含 EventBus.Publish） |
| EnemyMovementSystem | Systems/ | 敌人移动 | `EnemyActionEnum` 驱动方向；Dodge 分支有副作用 |
| PlayerTowerAttackSystem | Systems/ | 玩家攻击 | **两阶段**：并行收集 `(enemyId, damage)`，串行 `enemyHealth -= damage` + queue 死亡 |
| TowerAttackSystem | Systems/ | 塔攻击 | **两阶段**：遍历 `ActiveTowerIds`，并行收集 damage，串行 apply + queue 死亡；**索敌模式**（最近/最远/血量最低/血量最高/最先生成/最后生成）可配置 |
| TowerSynergySystem | Systems/ | 塔协同增益 | **配置驱动**：从 `Data/Towers/tower_synergy.json` 加载协同效果；SetTurn 时按 TowerType 分组缓存 ActiveTowerIds，Update 时检测塔组合触发协同（Buff/伤害加成） |
| TowerPlacementSystem | Systems/ | 塔放置/出售 | `UpgradePath` 从塔 JSON 配置读取，写入 `store.TowerUpgradePathId` |
| TowerUpgradeSystem | Systems/ | 塔升级/路径切换 | `UpgradeTower`：按路径曲线应用属性；`SwitchUpgradePath`：+50% 切换成本，重新应用当前等级曲线 |
| UpgradeSystem | Systems/ | 玩家升级 | 等级阈值触发；`_sharedRandom` 类级单例 |
| SkillSystem | Systems/ | 技能施放 | GAS 架构；AreaShape 驱动；**只 queue 死亡，帧末统一 resolve** |
| TechTreeSystem | Systems/ | 科技树 | 前置依赖检查；效果缓存在 `TechTreeSystem` 字段；**O(1) Dictionary 查找（`c36747b`）** |
| GoldSystem | Systems/ | 金币结算 | 击杀产金；`Interlocked.Add` 并行安全 |
| MapSystem | Systems/ | 地图渲染 | Debug 渲染 |
| SpatialGridSystem | Systems/ | 空间网格 | 范围查询（塔攻击范围、Buff 范围）；O(1) cell 访问 |
| BuffSystem | Systems/ | 持续伤害（DoT）追踪 | Periodic EffectType；ping-pong 双缓冲 DoT 伤害队列；`ApplyDot`/`Update`/`ResolveDotDamage` |
| EnemyAbilitySystem | Systems/ | 敌人技能系统 | `UpdateCooldowns`/`ExecuteAbilities`/`Update`；FrameScheduler 已集成；冷却/Buff/自疗/AoE |
| AutoSkillSystem | Systems/ | BuildPhase 自动施放技能 | 冷却保护（`MinCooldownToConsider`）+ 选优策略（CoolestFirst/CooldownShortest/DamageHighest/AoeLargest/Random）；调用 `SkillSystem.CastSkill()`；**不影响战斗帧预算** |
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
- `GameplayEffectDef` — 效果元数据（Type/Duration/TickInterval/TotalTicks/Modifiers）
- `EffectType` — 效果类型（`Instant`/`Duration`/`Periodic`）
- `GameplayAbilityDef` — 技能元数据（Name/Cooldown/AreaShape/AreaRadius/FixedBaseDamage/HasDot/DotDuration/TickInterval/DamagePerTick/IsShield/ShieldAmount/ShieldDuration）
- `AreaShapeType` — 范围形状（`Single`=0/`Cross`=1/`Box`=2/`Circle`=3/`Chain`=4/`Heal`=5/`Shield`=6/`Line`=7/`Freeze`=8）
- `AbilityInstance` — 技能实例（含 CurrentCooldown）
- `AppliedEffect` — 已应用的效果实例（含 TimeSinceLastTick）

护盾（Shield）施放链路：
1. `SkillSystem.CastShield()` 调用 `store.ApplyPlayerShield(playerId, shieldAmount, duration)`
2. `ComponentStore.ApplyPlayerShield()` 叠加护盾值和持续时间
3. 伤害结算：`ComponentStore.DecreasePlayerHealth()` 优先扣除护盾，剩余穿透到生命值
4. 护盾消散：`ComponentStore.SetTurnCCFlags()` 每回合递减 duration，为 0 时清零 shield + 打印 `[SHIELD] 护盾消散！`

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

## 7.1 连击系统（ComboConfig）

配置文件：`game_config.json` → `Combo` 节

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `comboWindowSeconds` | float | 3.0 | 击杀后保留连击的秒数 |
| `comboDamageBonusPerKill` | float | 0.05 | 每次连击击杀的伤害加成（+5%/次） |
| `comboGoldBonusPerKill` | float | 0.10 | 每次连击击杀的金币加成（+10%/次） |
| `comboMaxMultiplier` | float | 3.0 | 连击最大伤害倍率上限 |

加载链路：`GameConfigLoader.ParseComboConfig()` → `GameConfig.Combo`（启动时执行一次，无性能影响）。

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

### 运行时配置（Data/Configs/）

| 文件 | 内容 |
|------|------|
| `behavior_trees.json` | 行为树定义 |
| `phase_behavior.json` | 相位行为 |
| `player.json` | 玩家属性 |
| `skills.json` | 技能定义（AreaShape/AreaRadius/DoT 参数） |
| `tech_tree.json` | 科技树节点 |
| `tower_placement.json` | 塔位规则 |
| `wave_spawn.json` | 波次生成 |
| `auto_skill.json` | 自动技能配置（BuildPhase 策略） |

配置类：`Core/GameConfig.cs`、`Core/TechTreeDef.cs`

### 静态数据（Data/，auto-gen，勿手动编辑）

| 目录 | 内容 |
|------|------|
| `Data/Monsters/` | 200 种怪物定义 |
| `Data/Skills/` | 150 种技能定义 |
| `Data/Towers/` | 150 种塔定义（all_towers.json） |
| `Data/Levels/` | 5 个关卡配置 |

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

|| 路径 | 原状态 | 说明 |
|------|--------|------|
| `System/` (大写) | 未编译 | 全目录已删除，原 5 个死文件 |
| `GridSpatialHash.cs` | 空桩 | 已删除，Spatial Hash 在 range=3 场景是反模式 |
| `Components/Components.cs` | 老架构 | 已删除 |
| `Components/BuffDebuffComponents.cs` | 老架构 | 已删除 |
| `Components/GameStateComponent.cs` 等 9 个 | 老架构 | 已删除（仅保留 BuffData/EnemyActionType/EnemyComponent/SkillComponent）|

## 12b. 目录结构重组（2026-05-17，`c36747b`）

|| 原路径 | 现路径 | 说明 |
|------|--------|--------|------|
| `Configs/GameConfig.cs` | `Core/GameConfig.cs` | 配置类归入 Core/ |
| `Configs/GameConfigLoader.cs` | `Core/GameConfigLoader.cs` | Loader 归入 Core/ |
| `Configs/TechTreeDef.cs` | `Core/TechTreeDef.cs` | 配置类归入 Core/ |
| `Configs/all_towers.json` | `Data/Towers/all_towers.json` | 静态数据归入 Data/ |
| `Configs/game_config.json` | 根目录 + bin/ | 运行时配置保留在 Configs/ 或根目录 |
| `Research/bench2.log` 等 | `Research/logs/` | 日志归入子目录 |
| `Research/*.ps1` | `Research/scripts/` | 构建脚本归入子目录 |

---

## 13. Freeze（冰冻）机制（2026-05-23）

Freeze 使用与 Stun 相同的底层基础设施，不新增独立字段。

### 设计

- `Freeze` 通过 `ApplyEnemyFreeze()` 应用，内部调用 `ApplyEnemyStun()`，共享 `EnemyStunDurationLeft` / `EnemyStunFlag`
- 技能系统 `CastFreezeArea()` 根据 `FreezeChance` 概率掷骰，命中时调用 `ApplyEnemyFreeze()`
- `IsEnemyFrozen()` 是 `IsEnemyStunned()` 的别名
- `EnemyAISystem.Update()` 冻结敌人直接跳过 BT 评估，输出 `EnemyActionType.None`
- `EnemyMovementSystem.Update()` 冻结敌人跳过移动
- `DestroyEntity` 无需单独清理冻结字段（随 `EnemyStunDurationLeft` / `EnemyStunFlag` 一起被清理）

### 缓存一致性

`EnemyAISystem` 的 BT 评估缓存增加了 `_enemyStunDurationCache[]`，与 `_enemyStunFlagCache[]` 一起追踪冻结状态变化，确保敌人被冻结后立即失效缓存。

### 配置

`skills.json` 中技能通过 `FreezeDuration`（秒）和 `FreezeChance`（概率）控制。

---

## 14. 更新记录

||| 日期 | commit | 变更 |
|------|--------|------|------|
|| 2026-05-23 | `HEAD` | Freeze 机制：共享 Stun 基础设施（ApplyEnemyFreeze/IsEnemyFrozen），EnemyAISystem 缓存加入 stunDuration，CastFreezeArea 调用 ApplyEnemyFreeze；`docs/architecture.md` 新增 Freeze 章节 |
| 2026-05-17 | `c36747b` | TechTreeSystem O(N)→O(1) Dictionary 查找；配置类迁移 Core/；目录结构重组（Data/, Research/） |
| 2026-05-13 | `c4c360b` | 清理死代码（System/、GridSpatialHash、9个旧组件、EntityManager精简） |
| 2026-05-13 | `2ce3352` | README 更新（添加 TechTree） |
| 2026-05-13 | `5e01a26` | 新增科技树系统（3分支 × 5节点） |
| 2026-05-12 | `79fea25` | BT Cache fix + Merged pipeline，FPS 8334 |
| 2026-05-12 | `01c05a7` | chargeParams SOA 化 |
| 2026-05-12 | `04c50a6` | P0 Bug 修复（GetAllActiveEnemyIds 副本） |

## 14. Phase 阶段循环系统（2026-05-22）

`phase_behavior.json` → `GameConfig.PhaseBehaviors` → `StateMachine` → `FrameScheduler.Phase`

### 设计

- `PhaseBehaviorDef`：每个阶段的配置（EnterMessage、AutoAdvance、UnlockTowers 等）
- `StateMachine`：管理 `BuildPhase / WavePhase / Intermission / LevelComplete / GameOver / Victory` 状态转换
- `FrameScheduler.Phase`：当前 phase，Tick() 按此门控系统调度
  - `BuildPhase`：只运行 Gold/Upgrade/Skill(cd)，**不运行** WaveSpawning/EnemyAI/Combat
  - `WavePhase`：完整战斗管道
  - `Intermission`：同 WavePhase（仍运行战斗引擎显示信息）

### 文件

| 文件 | 改动 |
|------|------|
| `Core/GameConfig.cs` | 新增 `PhaseBehaviorDef` 类 + `GetPhaseBehavior()` + `PhaseBehaviors` 字段 |
| `Core/GameConfigLoader.cs` | 新增 `LoadPhaseBehaviors()` / `ParsePhaseBehaviors()` / `ParseStringList()` / `ExtractBool()` |
| `Core/FrameScheduler.cs` | 新增 `Phase` 字段，`Tick()` 按 phase 门控系统 |
| `Core/GameManager.cs` | 初始化 `StateMachine`，`scheduler.Phase` 与状态机同步，`Run()` 中触发 BuildPhase→WavePhase 转换并显示消息 |
| `Data/Configs/phase_behavior.json` | 已存在，配置各阶段行为参数 |

### 状态转换图

```
Init → BuildPhase → WavePhase → Intermission → WavePhase → ... → LevelComplete → BuildPhase
                                                    ↓
                                              GameOver / Victory
```

---
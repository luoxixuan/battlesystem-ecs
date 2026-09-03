# BattleSystem-ECS ECS + GAS 终态架构

> 状态：目标架构决策（本文定义终态，不代表当前代码已经全部实现）
> 更新日期：2026-09-03
> 相关审查：[skill-combat-arch-review.md](skill-combat-arch-review.md)
> 迁移计划：[ecs-gas-migration-plan.md](plan/ecs-gas-migration-plan.md)

## 1. 决策摘要

本项目采用 **一个数据导向 ECS World + 一个 ECS 原生的窄 GAS + 一个声明式帧图 + 一个统一战斗结算管线**。

- ECS 负责实体、数据存储、生命周期、空间查询、调度和并行边界。
- GAS 负责能力激活、Gameplay Effect、Modifier、Tag、触发器和叠层语义。
- Combat/Resource Resolver 负责所有伤害、治疗、护盾、资源和死亡的权威写入。
- GAS 不维护第二个 World，不为每个实体创建对象图，也不拥有独立的 Tick 循环。
- Archetype 不是语义层的必需品；动态效果使用稀疏数据池。

终态的目标不是照搬 Unreal GAS，而是把当前项目已经需要的能力规则集中到一个可注册、可验证、数据导向的模块中。

## 2. 设计目标与非目标

### 2.1 目标

- 新增已有目标形状和效果的技能时主要修改配置，不修改多个业务系统。
- 所有属性修饰、效果生命周期和叠层规则只有一个解释者。
- 所有伤害来源拥有相同的免疫、抗性、护盾、血量和死亡语义。
- 并行计算保持只读和命令收集，提交顺序可验证且可复现。
- 实体 ID 回收不会遗留能力、效果、Tag 或触发器状态。

### 2.2 非目标

- 不实现 Unreal 的 `AbilitySystemComponent`、UObject 对象树、反射式 Gameplay Cue 或网络复制模型。
- 不把移动、寻路、生成、空间索引和渲染逻辑搬进 GAS。
- 不把每一个 Buff、层数或临时状态设计成 Archetype 组件。
- 不以“所有字段都进入一个通用 Attribute 数组”为目标；高频固定属性仍可使用领域专用 SOA 列。

## 3. 领域词汇

| 术语 | 规范含义 |
|---|---|
| Entity | ECS 中具有代数的运行时实体句柄 |
| Ability | 可被激活的动作，包含成本、冷却、条件和效果引用 |
| Targeting | 从 ECS 世界收集目标的规则，不负责施加效果 |
| Gameplay Effect | 对目标施加的状态变化描述，可为瞬时、持续、永久或周期 |
| Modifier | 对 Attribute 的数值修饰 |
| Trigger | 监听已提交 Gameplay Event 并产生效果申请的规则 |
| Execution | 需要代码计算的特殊效果算法，例如链式跳转或时间回溯 |
| Attribute | 可被聚合的数值，例如攻击力、暴击率、移动速度 |
| Resource | 可直接消耗或恢复的动态池，例如当前生命、法力和护盾 |
| Tag | 分类状态或条件，例如 `Stunned`、`Silenced`、`Boss` |
| Request | 当前帧尚未提交的意图 |
| Event | 已经经过验证并提交的事实 |

`Request` 和 `Event` 不能混用：请求可以被拒绝，事件表示事实已经发生。

## 4. 分层与依赖方向

```text
Content / Config
  -> CatalogCompiler + Validator
  -> AbilityId / EffectId / AttributeKey / TagId

FrameGraph（唯一帧调度入口）
  -> ECS 领域系统（AI、移动、空间、攻击）
  -> GAS 运行时系统（激活、效果、Tag、属性）
  -> Combat Resolver
  -> ECS World 状态与死亡队列
  -> 已提交事件 / 渲染桥接
```

依赖约束如下：

- `ComponentStore`/`WorldStore` 不引用具体技能或 Buff 系统。
- GAS 只能通过受控的 World View、Attribute View 和 Command Sink 访问 ECS。
- 空间模块向 GAS 提供目标查询适配器；GAS 不直接操作 `SpatialGrid` 的内部结构。
- `FrameGraph` 只表达系统依赖和执行策略，不知道具体技能名称。
- 展示层只消费已提交事件，不读取或修改模拟状态。

## 5. 运行时存储

终态的物理存储仍然是 ECS World 内的扁平数据结构。逻辑上同一个 World，物理上可以拆成领域存储模块：

```text
BattleWorld
  EntityStore
  ComponentStore       # 高频领域 SOA
  GasStore             # GAS 稀疏 SOA 池
  CommandBuffers
  SpatialIndex
```

`BattleWorld` 统一拥有这些存储的创建、清理和实体代数。`GasRuntime` 是访问 `GasStore` 的规则模块；它不拥有独立实体表，也不自行启动帧循环。所有运行时引用都通过 `BattleWorld` 提供的 `WorldView`、`AttributeView` 和 `CommandSink` 访问。

### 5.1 Dense SOA

以下数据适合继续使用按实体索引的连续数组：

- Position、Health、Armor、基础 Damage；
- 塔和敌人的稳定高频状态；
- 供攻击热路径读取的 computed 属性缓存。

### 5.2 Sparse SOA

以下数据使用预分配的稀疏池和空闲链表：

- `AbilityState`；
- `ActiveGameplayEffect`；
- `Modifier`；
- `TagState`；
- `TriggerState`；
- 动态 Shield、DoT 和其他效果实例。

GAS 池中的外部句柄必须包含 `index + generation`。实体销毁会使关联代数失效；槽位耗尽必须返回明确错误并记录诊断，不能静默覆盖或丢弃。
大逻辑容量 pool 的 handle 元数据和 runtime payload 可以分别按需分页；分页是该 module 的
implementation，不能改变 handle、容量、失败或回收 interface。

### 5.3 属性与资源

基础值只有一个来源，Modifier 只存在于 GAS 运行态：

```text
ECS base columns
  + GAS ModifierPool
  -> AttributeAggregator
  -> ECS computed cache
```

`CurrentHealth`、`Mana`、`Shield` 是 Resource，不应被每帧属性重算覆盖。它们只能通过对应的 Resolver 修改。`MaxHealth` 等可聚合属性变化后，应由资源规则决定当前值是否需要裁剪。

所有可被 Modifier 访问的属性必须先注册到 `AttributeSchema`。属性键带有领域和数值语义，例如 `Combat.DamageOutputMultiplier`（基础值为 `1.0`），不能依赖玩家和敌人各自从 `0` 开始的重叠整数索引。

## 6. 静态定义与运行态

所有 JSON 和静态资源在启动时编译成只读 Catalog，运行时只使用整数 ID 和预构建数组。

### 6.1 AbilityDefinition

```text
AbilityDefinition
  abilityId
  cooldown
  costs[]
  activationPolicy
  allowedPhases         # Wave / Build / Intermission 等显式位集
  requiredTags / blockedTags
  targetingId
  effectIds[]
  triggerIds[]          # 被动能力可选
```

`allowedPhases` 是运行时激活合同，不只是 UI 提示。战斗型 Ability 默认只允许在 Wave；Build 中的资源、建设和准备类 Ability 必须显式声明。`AbilityCommit` 在产生任何 Effect、Damage 或 Resource Request 前校验当前阶段，拒绝时产生带原因的诊断事实。

### 6.2 TargetingDefinition

```text
TargetingDefinition
  targetingId
  shape
  radius / width / angle
  relationFilter
  tagFilter
  maxTargets
```

几何形状和效果必须分离。`Circle + Damage`、`Circle + Slow` 和 `Circle + ApplyPoison` 是不同的组合，而不是三个技能派发分支。

### 6.3 GameplayEffectDefinition

```text
GameplayEffectDefinition
  effectId
  durationPolicy       # Instant / Duration / Infinite
  duration
  clockId              # Enemy / Combat / RealTime / Global
  firstTickPolicy      # NextInterval（默认）/ Immediate
  applicationRequirements
  modifiers[]
  periodicSpec?
  executions[]
  stackingPolicy
  stackKey
  maxStacks
  refreshPolicy
  grantedTags
  blockedTags
  sourceDeathPolicy
```

定义只保存不可变数据。剩余时间、tick 累加器、层数和来源目标都放在运行态 `ActiveGameplayEffect` 中。

`PeriodicSpec` 必须包含周期效果的完整语义，而不是只保存一个通用数值：

```text
PeriodicSpec
  period
  payloadKind          # Damage / Heal / Resource / GameplayEvent
  magnitudeSource      # 常量、Attribute 或 Execution
  damageType / elementType
  catchUpPolicy        # CatchUpAll / OnePerFrame / SkipMissed
```

`firstTickPolicy = NextInterval` 是默认值；需要施加时立即触发的效果必须显式选择 `Immediate`。`clockId` 决定 duration 和 period 使用哪一个模拟时钟，不能由调用方临时传入 `enemyDt` 或 `combatDt`。

Modifier 的定义也必须独立于效果生命周期：

```text
ModifierDefinition
  attributeKey
  operation            # Add / Multiply / Override
  magnitudeSource      # 常量、Attribute、Curve 或 Execution
  priority
  snapshotPolicy       # CaptureOnApply / ReevaluateOnRead
```

### 6.4 TriggerDefinition

触发器与效果是独立定义，关系可以是多对多：

```text
TriggerDefinition
  triggerId
  eventType             # HitConfirmed / DamageApplied / KillConfirmed ...
  scope                 # PerSource / PerTarget / PerSourceTarget / PerPlayer
  filterTags
  threshold
  triggerMode           # Once / EveryN
  preserveRemainder
  effectId
  effectStackDelta
  resetPolicy
```

触发器负责“何时施加”；效果负责“施加什么”；效果的 stacking policy 负责“重复施加怎么办”。三者不能合并成一个含义模糊的字段。

### 6.5 Runtime state

```text
AbilityState
  ownerHandle
  abilityId
  cooldown
  charges

ActiveGameplayEffect
  effectId
  sourceHandle
  targetHandle
  remainingTime
  clockId
  tickAccumulator
  nextTickTime
  ticksProcessed
  stackCount
  applicationSequence
  capturedValues
  modifierHandles[]
  tagContributionHandles[]
  generation

TriggerState
  triggerId
  ownerHandle
  scopeKey
  counter
  generation
```

## 7. 属性聚合规则

属性聚合器是唯一解释 `ModifierOp` 的模块。默认规则应固定为：

```text
overrideBase = highest-priority Override, or base when none exists
computed = (overrideBase + sum(Add)) * product(Multiply)
computed = Clamp / domain rule
```

`Override` 替换的是聚合起点，不是最后一步覆盖结果；同优先级时按 `applicationSequence` 决定胜者。若某个属性需要最终覆盖，必须注册独立的 `FinalOverride` 语义，不能复用 `Override` 猜测。

属性定义必须声明范围、默认值和裁剪规则。`MaxHealth` 下降时，`ResourceResolver` 将 `CurrentHealth` 裁剪到新的上限；`MaxHealth` 上升不会自动治疗，除非效果明确声明对应的资源策略。

属性变化采用 dirty 重算，而不是移除 Modifier 时做浮点逆运算。添加、刷新、移除或过期效果都会标记相关实体 dirty；聚合器从基础值重新计算，避免跨帧累加误差。

## 8. 请求、事实与统一战斗管线

### 8.1 请求

```text
AbilityRequest
  sourceHandle
  abilityId
  input / target hint
  sequence

EffectRequest
  sourceHandle
  targetHandle
  effectId
  stackDelta
  clockId
  executionContext

DamageRequest
  sourceHandle
  targetHandle
  rawAmount
  damageType
  elementType
  flags
  abilityId / effectId
  ownerPlayerId
  sequence

HealRequest
  sourceHandle
  targetHandle
  rawAmount
  flags
  abilityId / effectId
  sequence

ShieldRequest
  sourceHandle
  targetHandle
  amount
  duration
  clockId
  abilityId / effectId
  sequence

ResourceRequest
  sourceHandle
  targetHandle
  resourceKey
  delta
  operation
  causeId
  sequence
```

`ExecutionContext` 是值类型上下文，至少包含 source/target handle、ability/effect ID、事件序号、时间域、快照值和 owner player ID。它不能在热路径中临时创建字典或字符串上下文。

### 8.2 已提交事实

```text
HitConfirmed
DamageApplied
AbilityActivated
AbilityRejected
EffectApplied
EffectRejected
EffectExpired
EffectRemoved
HitMissed
DamageBlocked
HealApplied
ShieldChanged
ResourceChanged
DeathQueued
KillConfirmed
GameplayLoopAborted
```

`HitConfirmed` 表示命中验证通过；`DamageApplied` 表示实际造成了有效伤害；`DeathQueued` 表示已经进入死亡队列；`KillConfirmed` 只在死亡解析和奖励归属确定后产生。触发器必须选择正确的事实类型，不能用 `KillConfirmed` 提前触发效果。

内部 Gameplay Event Queue 与 `IBattleEventBus` 是两个不同的 seam：前者供模拟系统触发规则，后者只在提交后向渲染层发送展示事实。并行阶段不得直接广播任一总线。

### 8.3 Resolver

所有伤害来源都进入一个 `DamageResolver`。Resolver 负责：

1. 验证实体句柄和目标状态；
2. 应用无敌、免疫、命中和闪避规则；
3. 应用暴击、护甲、抗性和元素规则；
4. 将护盾消耗、治疗和法力/资源变化委托给 `ResourceResolver`；
5. 应用血量下限；
6. 产生 `DamageApplied` 和 `DeathQueued`；
7. 将死亡放入统一死亡队列；
8. 在死亡解析完成后由生命周期模块产生 `KillConfirmed`，再发布领域事件。

`ResourceResolver` 是所有可变资源的唯一写入者，负责 `CurrentHealth`、`Shield`、`Mana`、Gold 和回溯恢复等资源操作。`DamageResolver` 只计算伤害规则、生成资源操作并决定顺序；实际的 Resource 写入和血量下限裁剪由 `ResourceResolver` 完成，不允许其他系统直接修改这些数组。

一次攻击的事实顺序是：

```text
HitValidation
  -> HitConfirmed（命中成功）
  -> DamageCalculation / ResourceResolver
  -> DamageApplied（有效伤害）
  -> DeathQueued（进入死亡队列）
  -> DeathResolve
  -> KillConfirmed（击杀和奖励已确定）
```

同一目标的后续请求如果在前一个请求后已经死亡，则被拒绝并产生相应的 `EffectRejected`、`DamageBlocked` 或 `HitMissed` 事实。若某个多段技能需要“原子批量命中”，必须在 AbilityDefinition 中显式声明快照规则。

任何业务系统都不能直接写 `EnemyHealth`、`PlayerCurrentHealth`、`PlayerMana` 或绕过 Resolver 发布伤害事实。

## 9. 帧图与 SystemGroup

`FrameScheduler` 仍是唯一入口。现有 Group 作为粗粒度阶段保留，但最终由声明式节点组成，而不是由大量 nullable 属性和手写调用顺序组成。

每个帧图节点必须声明完整的调度契约：

```text
FrameNodeDefinition
  nodeId
  groupId / phase
  reads[]
  writes[]
  runsAfter[]
  executionPolicy     # ReadParallel / EmitParallel / CommitSerial / Presentation
  clockId
  enabledWhen          # feature gate 或运行时条件
```

启动时的 `FrameGraphValidator` 必须检查：节点 ID 唯一、依赖引用存在、`Reads/Writes` 冲突已由依赖覆盖、没有环、没有重复注册，并以稳定的 node ID 作为拓扑排序的平局裁决。Group 是这些节点的组织和阶段门控，不是隐含依赖的替代品。

```text
Build / PreGame / Spawning
        |
AI / Movement / Terrain
        |
Prepare
  Spatial + AttributeAggregate(previous dirty)
        |
PreCombatGameplay
  AbilityCommit + AttributeAggregate(new dirty)
        |
CombatEmit
  Tower / Projectile / Skill target requests
        |
GameplayResolve
  EffectTick
  DamageResolve
  ResourceResolve
  GameplayEventCommit
  EffectCommit
  AttributeAggregate(post-combat dirty)
        |
DeathResolve
  PostDeathEventCommit / PostDeath
        |
Presentation events
```

执行策略必须显式标注：

- `ReadParallel`：只读组件和 computed 属性；
- `EmitParallel`：写线程局部命令缓冲；
- `CommitSerial`：按确定顺序修改共享状态；
- `Presentation`：只消费已提交事实。

GAS 的 `AbilityCommit`、`EffectTick`、`GameplayEventCommit` 和 `AttributeAggregate` 都是 ECS 帧图中的系统，不拥有第二个调度器。`ResourceResolve` 处理非伤害的治疗、护盾、法力和其他资源请求。

每个 active phase 必须满足提交闭包：该阶段允许产生的 Request 必须在同一帧存在对应的 Effect、Damage、Resource、Event 和 Death commit，或在入口被明确拒绝。帧级命令缓冲不得静默跨越 Build/Wave/Intermission 边界；显式延迟命令必须使用另一种带目标帧和生命周期的合同。

帧内可见性规则固定如下：

- 节点只读取该节点开始时的属性快照；同一并行批次中产生的修改不可互相观察。
- `AbilityCommit` 在后续的 `AttributeAggregate` 后可对同帧后续消费者可见。
- `DamageResolve` 之后由命中事件触发的新效果，默认在下一次属性读取边界可见，不回写当前已完成的攻击批次。
- 新创建的 `Periodic` 效果默认在下一个周期到达时首次 tick；`Immediate` 必须在定义中显式声明。
- Commit 期间产生的递归事件进入下一事件队列；默认不在同一调用栈内递归。单帧递归深度和事件处理量必须有上限，超限产生诊断事件。

`HitConfirmed`、`DamageApplied` 等战斗事件由 `GameplayEventCommit` 消费；`KillConfirmed` 只有在 `DeathResolve` 完成后才由 `PostDeathEventCommit` 消费。这样 OnHit、OnDamage 和 OnKill 的时机不会混在同一个回调里。

`DeathResolve` 在 Prepare 节点以固定锁序同时预留 `DamageResolver` 与 `ResourceResolver`
事件槽位。预留只减少其他生产者的可用容量，不公开任何事实；Dispatch 暂存本批
`ResourceChanged`/`KillConfirmed`，完成奖励、生命周期回调和实体销毁后才原子发布并释放预留。
容量不足时不翻转死亡 ping-pong buffer，`BeginFrame` 保留原批并清理瞬态事实后重试。
成功提交只清 prepared read bag；生命周期回调重入写入的 alternate bag 保留到同帧 cascade
或下一帧。死亡/塔杀订阅者列表只在注册变更时重建，dispatch 逐项执行且在整批事实提交后重抛首异常。

定义的消费者必须由 Catalog 和 FrameGraph 同时校验：

| 定义 | 主要消费者 | 产出或写入 |
|---|---|---|
| `AbilityDefinition` | `AbilityCommit`、`Targeting` | `AbilityState`、Ability/Effect Request |
| `TargetingDefinition` | `Targeting`、`SpatialQuery` | Hit/Target Request |
| `TriggerDefinition` | `GameplayEventCommit`、`PostDeathEventCommit` | `TriggerState`、`EffectRequest` |
| `GameplayEffectDefinition` | `EffectCommit`、`EffectTick`、`EffectExpire` | ActiveEffect、Tag、Modifier、Resource Request |
| `ModifierDefinition` | `AttributeAggregate` | computed Attribute cache |
| `ExecutionDefinition` | 注册的 Execution 节点 | Damage/Heal/Effect/Resource Request |

## 10. 示例：命中 10 次后伤害增加 30%

在内容层可以把它们归为一个规则包：

```text
GameplayRule: ComboMastery
  triggers = [combo_hit_10]
  effects  = [combo_damage_boost]
```

但 Catalog 和运行态仍然分开：

```text
combo_hit_10
  event             = HitConfirmed
  scope             = PerSource
  threshold         = 10
  mode              = EveryN
  preserveRemainder = true
  effectId          = combo_damage_boost

combo_damage_boost
  durationPolicy = Infinite
  stackingPolicy = AddStack
  maxStacks      = 5
  modifier       = DamageOutputMultiplier Add 0.30
```

一帧内累计 `deltaHits` 时，触发器按批次计算：

```text
crossings = floor((oldCount + deltaHits) / threshold)
          - floor(oldCount / threshold)
remainder = (oldCount + deltaHits) % threshold
```

所有命中先使用本帧的属性快照；阈值效果在 `GameplayEventCommit` 和 `EffectCommit` 中提交，下一次属性读取边界可见。若玩法明确要求同一多段攻击的后续子命中立即享受效果，必须显式拆成有序子批次，不能依赖并行回调顺序。

## 11. 生命周期与清理

实体销毁是 GAS 生命周期的一部分：

- 使实体句柄代数失效；
- 删除或转移以该实体为目标的 ActiveEffect；
- 按 `sourceDeathPolicy` 处理其施加的效果；
- 清理 AbilityState、TriggerState 和 TagState；
- 清空相关命令和待处理事件；
- 然后才允许实体 ID 被回收。

清理操作必须幂等。效果槽位不能用裸数组索引作为长期外部引用。

`sourceDeathPolicy` 的允许值必须是显式枚举：

```text
Remove       来源死亡时移除效果
Persist      来源死亡后效果继续存在
Transfer     效果转移到定义指定的 owner 或目标
```

目标死亡时默认移除以目标为作用对象的效果；需要保留在尸体或区域上的效果必须使用独立的区域实体和明确的 `KeepForCorpse`/`KeepInWorld` 执行语义。实体销毁会先使句柄代数失效，再过滤待处理的 Request/Event；任何带有旧代数的命令都被丢弃并记录诊断。

触发器产生的链式效果不允许无限递归。默认规则是新事件进入下一事件队列、下一提交边界再处理；FrameGraph 为单帧设置最大提交轮数和最大事件数，超限时停止该链并产生 `GameplayLoopAborted` 诊断。

## 12. 内容扩展模型

新增内容遵循以下结构：

```text
Ability / Effect / Trigger config
  -> startup validation
  -> registered integer IDs
  -> existing Targeting + Effect executors
```

只有真正的新算法才实现并注册 `Execution`。例如：

- `Circle + ApplyPoison`：纯配置组合；
- `TimeRewind`：注册一个专用 Execution；
- `ChainLightning`：Targeting 或 Execution 的可复用实现；
- `DamageOutputMultiplier`：通用 Modifier，不应新增技能专用字段。

未知的目标形状、效果 ID、属性键或 Tag 必须在启动时报告错误，禁止静默退回默认行为。

## 13. Archetype 决策

Archetype 不是本架构的必要层。当前和终态都可以使用：

```text
dense SOA core columns
+ sparse GAS pools
+ active entity lists
```

如果未来性能分析证明需要按稳定组件签名分块，可以在 `WorldStore` 下替换存储实现；GAS 的定义、句柄、命令和 Resolver 接口不应因此改变。动态 Buff、层数和周期计时不应通过增加或移除结构组件来表达。

2026-09-03 的 M8 有界 profile 未达到 Archetype 量化闸门。当前实现选择继续使用上述组合，
并仅将 Effect handle 元数据在既有 pool interface 后改为按需分页。该结论不删除未来 profile
重新打开闸门的可能，也不把尚未迁移的公开 dense niche 数组声明为已完成。

M8 的稳定观察是显式的只读 snapshot：它不进入生产 Tick 热路径，按实际 trigger definition
消费数量和 Resolver publication failure 计数区分配置/运行事实，并在 harness 显式开启 digest 后
通过无分配的状态与 Gameplay event sequence digest（按 publication 顺序累计）复核多轮 soak 的确定性；队列容量不足时关键
事实在状态写入前以 `RequestQueueOverflow` 拒绝。这些字段不改变 Ability/Effect/Trigger/Request/
Resolver contract。

## 14. 架构不变量

以下规则是终态的强约束：

1. 一个运行时事实只能有一个权威写入者。
2. `EnemyHealth`、护盾和死亡只能由 Resolver/生命周期模块修改。
3. `ModifierOp` 只能由 AttributeAggregator 解释。
4. 效果计时和层数只能由 Gameplay Runtime 修改。
5. 并行阶段不得修改共享状态、广播事件或增删效果。
6. 所有跨帧引用都必须验证实体或效果代数。
7. 定义和运行态分离；静态定义不得保存倒计时或当前层数。
8. 同一帧的提交顺序必须由 FrameGraph 声明并可测试。
9. 内容系统依赖 Engine contract，不直接依赖其他内容系统的具体实现。
10. 新增配置在启动校验失败时必须显式失败，而不是静默降级。

本文只定义终态架构和不变量，不规定从当前代码迁移到终态的步骤、兼容层或删除顺序；迁移规划另行记录。

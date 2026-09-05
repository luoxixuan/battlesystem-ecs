# BattleSystem-ECS ECS + GAS 终态架构

> 状态：目标架构决策（本文定义终态，不代表当前代码已经全部实现）
> 更新日期：2026-09-05
> 相关审查：[skill-combat-arch-review.md](skill-combat-arch-review.md)
> 迁移计划：[ecs-gas-migration-plan.md](plan/ecs-gas-migration-plan.md)
> Lumio 对照收口：[plan/ecs-gas-lumio-contract-alignment.md](plan/ecs-gas-lumio-contract-alignment.md)

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
| Modifier | 对 Attribute 的数值修饰；无独立生命周期，见 §5.2 / §5.3 |
| Trigger | 监听已提交 Gameplay Event 并产生效果申请的规则 |
| Execution | 需要代码计算的特殊效果算法，例如链式跳转或时间回溯 |
| Attribute | 可被聚合的数值，例如攻击力、暴击率、移动速度 |
| Resource | 可直接消耗或恢复的动态池，例如当前生命、法力和护盾 |
| Tag | 分类状态或条件；运行时是计数容器，层级见 §3.1 |
| Request | 当前帧尚未提交的意图 |
| Event | 已经经过验证并提交的事实 |

`Request` 和 `Event` 不能混用：请求可以被拒绝，事件表示事实已经发生。

### 3.1 Tag

`Tag` 是 `(entity, tagId)` **计数容器**：授予 +1，移除 −1，`HasTag` 读计数是否大于 0。同一实体多个来源授予同一标签时，摘除一层不得清掉其余贡献。

层级不在运行时扫字符串。Catalog 持有带 `parent` 的 Tag **词汇表**；编译期对每个 `TagId` 展开祖先闭包。授予叶标签（例如 `Stun`）时，祖先（例如 `Control`、`Debuff`）的贡献计数各 +1，移除对称 −1。运行时 `HasTag` / Required / Blocked 仍是整数键计数。

祖先展开**只**作用于：

- 贡献计数；
- Required / Blocked 匹配。

祖先展开**不**作用于：

- `stackKey` 身份（叠层键仍是定义自己的键，不因祖先相同而合并）；
- 事件上的 `definition.Tag`（`EffectApplied` 等仍发布叶标签，不改写成祖先）。

未知 Tag 启动硬失败（见 §12）。

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
- `ActiveGameplayEffect`（账本条目：层数、时长、周期、捕获值、Tag 贡献都挂在这条上）；
- `TagState`（计数容器，见 §3.1）；
- `TriggerState`；
- 动态 Shield、DoT 和其他效果实例。

`Modifier` **没有独立生命周期**，不得作为与 `ActiveGameplayEffect` 并列的一等池对象按层 `new` 或扩 handle。`stackCount` 是该条目对 ΣAdd / ΣPercent 的乘数，不把同一条展开成 N 份独立 modifier。

GAS 池中的外部句柄必须包含 `index + generation`。实体销毁会使关联代数失效；槽位耗尽必须返回明确错误并记录诊断，不能静默覆盖或丢弃。
大逻辑容量 pool 的 handle 元数据和 runtime payload 可以分别按需分页；分页是该 module 的
implementation，不能改变 handle、容量、失败或回收 interface。

### 5.3 属性与资源

基础值只有一个来源。运行时 Modifier 从属于 ActiveEffect，不单独存活：

```text
ECS base columns
  + ActiveGameplayEffect（账本）
  + GAS ModifierPool（仅 AttributeAggregator 派生缓存，可丢弃后从账本重建）
  -> AttributeAggregator
  -> ECS computed cache
```

`ModifierPool` 只能是 Aggregator 派生缓存，不是第二份账本。移除效果或 `ClearEntity` 后必须能从 ActiveEffect 全量重建；禁止只改池、不改条目而导致 computed 与层数分叉。

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

`allowedPhases` 是运行时激活合同，不只是 UI 提示。战斗型 Ability 默认只允许在 Wave；Build 中的资源、建设和准备类 Ability 必须显式声明。

#### 激活准入序（提示优先级）

准入检查按下列顺序，**先匹配的原因先返回**。这是整张冻结表，不是「只把 `PhaseNotAllowed` 提前」一句。实现必须与本表对齐。

```text
第一段（冷却之前，与当前 ActivateCore 前半段对齐）：
  InvalidRequest（空 store/catalog、句柄无效、catalog 缺失、请求畸形）
  NoTarget（ValidateShape 空列表等形状失败）
  UnsupportedDefinition（ForbidEffects 带 effects、heal-only 形状不匹配）

第二段：
  PhaseNotAllowed
  Cooldown
  Cost（含同帧预留：后请求看到的可用 = 当前值 − 已预留）
  NoTarget（目标实体无效 / RequireEnemy 未激活）
  TagRequirementsNotMet
  UnsupportedDefinition（时长合同 / 内容 CanCommit / 未知 execution）

第三段（末尾，独立原因，不复用 InvalidRequest）：
  QueueOverflow（语义对齐 Resolver 的 RequestQueueOverflow：
    AbilityRequests / EffectRequests / Damage·Resource 队列 / modifier 槽）
```

`NoTarget` 与 `UnsupportedDefinition` 在第一段与第二段各出现一次，细类不同：第一段是形状/定义合法性，第二段是目标实体与定义合同。调用方按返回的原因枚举区分。容量失败不得报成 `InvalidRequest`。

#### Commit 复查与支付

已入队请求在入队时预留资源与容量。Commit **必须先**复查冷却 + 预留消耗/容量，**再 Spend，再 `CommitPlan`**（硬要求）。复查不查 Tag：当前 granted effect 到 `effect.commit` 才 `TryApply`，`ability.commit` 时 Tag 还不存在；跨帧蓄力的 commit 才会踩到 Tag 变化。复查失败发 `AbilityCancelled` 且不 Spend、不 `CommitPlan`。`CommitPlan` 失败则 `Add` 退回已 Spend 的费用。载荷 `Commit` 返回 -1 时按 Resolver **新**拒绝原因映射（`RequestQueueOverflow`→`QueueOverflow`，目标无效→`NoTarget`，无新拒绝则 `UnsupportedDefinition`），不再一律 `QueueOverflow`。多目标 `CommitPlan` 中途 -1 时前面目标的当场载荷可能已提交（无法整单撤回伤害），费用已退。退款若事件队列已满，`Add` 可能发不出事实，资源列以 Resolver 结果为准。不 throw、不半价、不把 World 作废。

支付走 `ResourceOperation.Spend`：不足即拒、原子、实际扣减必须等于请求。禁止改通用 `ResourceResolver` 夹紧语义：`BossTrailAoeSystem` / `SuicideBombSystem` 用负增量打血，夹到 0 仍 `Accepted` 是预期。

预留在 Commit 时对每条已提交请求逐条 `Release`（复查只看到更晚预留）。`RejectQueuedAbilities` 与 `frame.begin` 走 `ClearAbilityQueue` → `Clear()` 兜底。漏绑会把预留漏到下一帧。

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
  stackSnapshotPolicy  # V1 仅 Replace（默认）；KeepPerLayer 不进 V1
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

静态 `ModifierDefinition` 与效果的 duration / period 字段分离（定义层拆分）。运行时 Modifier 实例**没有**独立于 `ActiveGameplayEffect` 的生命周期（见 §5.2 / §5.3）。

```text
ModifierDefinition
  attributeKey
  operation            # Add / Percent / Override
  magnitudeSource      # 常量、Attribute、Curve 或 Execution
  priority             # Override 必填；数字大者赢，同级后写赢
  snapshotPolicy       # CaptureOnApply / ReevaluateOnRead
```

#### 同帧效果顺序

生产路径上，AI 组 `EnemyAbilitySystem.RemoveDispellableEffects` 是 **批外 remove-first**：在 `effect.commit` **之前**执行。此处不单独改 FrameGraph。

```text
AI 组 dispel（批外，remove-first）：
  RemoveDispellableEffects → 之后同帧才入队 / effect.commit 施加

effect.commit 批内（结果不得依赖遍历顺序）：
  堆叠命中
    → 溢出判定
    → 按 stackSnapshotPolicy 应用捕获（V1 = Replace，且为默认）
    → 时长策略
    → 周期策略
    → 过期 / 批内显式 Remove 垫后
```

禁止「同帧施加又移除 = 抵消」。`EffectApplied` / `EffectRemoved` 都是已发生的事实，Trigger 与 digest 按发布顺序消费；抵消会吞掉事实。两条事件都必须发布。

#### 堆叠快照（V1）

| 值 | V1 | 含义 |
|---|---|---|
| `Replace` | **实现，默认** | 新快照整张替换当前条目的捕获 |
| `KeepPerLayer` | **不做** | 每层各自捕获。单条目 × 乘数无法表达，除非条目内 `maxStacks` 长度捕获数组 |

默认必须是 `Replace`，不得写成 `KeepPerLayer`。catalog modifier 全为 `ReevaluateOnRead`，聚合器忽略 `Captured`；唯一 `CaptureOnApply` 是 `LegacyEffectAdapter`，且走 `TryAdopt` / `TryRestack` 不加层。因此默认 `Replace` 对 shipped 内容零可见变化。`KeepPerLayer` 若将来要做，必须先规定条目内定长捕获数组，并单列 CHANGELOG。

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
  # 仅时长能力（蓄力 / 读条 / 引导）才有：
  # Executing / Completed / Cancelled / Expired
  # 瞬时能力不背状态机；不设 RolledBack；不为 10K 瞬发路径引入八态机
  # 实现：AbilityState.Phase + AbilityDurationKind.Timed；瞬发 Activate 保持 None

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

瞬时能力只占用冷却 / 充能，**不背状态机**。`Executing` / `Completed` / `Cancelled` / `Expired` 只给蓄力、读条、引导等时长能力。不引入 `RolledBack`，不为 10K 瞬发路径套八态机。`ActiveGameplayEffect.modifierHandles[]` 若保留，只指向 Aggregator 派生缓存，不是独立账本身份。摘除式抑制在 Active 槽内去掉 modifier 与 granted tag 贡献（`TryInhibit`），不新增状态机枚举。

## 7. 属性聚合规则

属性聚合器是唯一解释 `ModifierOp` 的模块。同一 AttributeKey 的求值**整段**采用 Lumio 冻结公式，不是「只改乘区、覆盖仍换起点」：

```text
aggregated     = (Base + ΣAdd) × max(PercentFloor, 1 + ΣPercent)
winningOverride = 配表 priority 最高的 Override；同级后写赢（applicationSequence）
computed       = winningOverride 若存在，否则 aggregated
computed       = Clamp / domain rule
```

`Override` 是最终覆盖：有它则「这个值就是该 magnitude」，Add / Percent 全部不生效。不存在换聚合起点的 Override，也不另注册 `FinalOverride`。priority 必须写在 Modifier 定义上，禁止靠容器插入顺序隐式重排。

`Percent` 的 magnitude 是加项：`+30%` 配 `0.30`，不是 `1.30`。两个 `+30%` 得到 `1.60`，不是 `1.69`。`stackCount` 是该条目对 ΣAdd / ΣPercent 的乘数，不把同一条展开成 N 份独立 modifier。Override 条目若叠层，只比较各层给出的覆盖值，胜者仍是最终值，不会在覆盖值上再加 Percent。

`PercentFloor` 由属性定义声明，**默认 `0`**：百分比不能把符号翻负（两个 `−60%` 是 `max(0, 1 − 1.20) = 0`，不是 `−0.20`）。移速等属性可以声明更高下限（例如 `0.1`）。这与属性自身的 Clamp 是两道裁剪：先保证百分比因子合法，再套值域。

跨通道仍按 gap Phase 1 已定：`computed(AttackDamage) × computed(DamageOutputMultiplier)`。本公式只约束**同一 AttributeKey 内部**；通道之间的乘积不是 `Percent` 聚合。某一通道上的 Override 只盖住该通道的 computed，不会跨通道生效。

属性定义必须声明范围、默认值、`PercentFloor` 和裁剪规则。`MaxHealth` 下降时，`ResourceResolver` 将 `CurrentHealth` 裁剪到新的上限；`MaxHealth` 上升不会自动治疗，除非效果明确声明对应的资源策略。

属性变化采用 dirty 重算，而不是移除 Modifier 时做浮点逆运算。添加、刷新、移除或过期效果都会标记相关实体 dirty；聚合器从基础值重新计算，避免跨帧累加误差。实现已切到本公式。切公式是数值语义变更，已在 `CHANGELOG.md` 单列；生产路径上的 `Multiply(1 + x)` 已迁成 `Percent(x)`（`SkillSystem` Instant 仍写 `Multiply(1.1)`，由 `LegacyEffectAdapter` 映射为 `Percent(0.1)`）。

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

`operation` 含 `Spend`（不足即拒、原子，能力支付只走这条）与既有夹紧类增减。负增量打血等旁路继续走通用夹紧，夹到边界仍可 `Accepted`（见 §6.1）。

`ExecutionContext` 是值类型上下文，至少包含 source/target handle、ability/effect ID、事件序号、时间域、快照值和 owner player ID。它不能在热路径中临时创建字典或字符串上下文。

### 8.2 已提交事实

```text
HitConfirmed
DamageApplied
AbilityActivated
AbilityRejected
AbilityCancelled
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

`AbilityRejected` 表示**准入**失败（未入队或入队前拒绝），原因见 §6.1 冻结表。`AbilityCancelled` 表示已入队请求在 Commit 复查失败，或复查通过后 Spend/`CommitPlan` 失败（含退款后的取消），整单取消，不 throw、不半价。不得用 `AbilityRejected` 表示复查失败，也不得用 `AbilityCancelled` 表示准入失败。

内部 Gameplay Event Queue 与 `IBattleEventBus` 是两个不同的 seam：前者供模拟系统触发规则，后者只在提交后向渲染层发送展示事实。并行阶段不得直接广播任一总线。内部 Event 带 `GameplayEventCause`（Mutation / Initial / Replay）；sequence digest 只累计 Mutation，避免初次快照或回放被当成又一次变化。

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

时长与周期效果按 `clockId` 分别推进。子弹时间缩放 `enemyDt`；按帧号排期不是合法实现。`GameplayScheduleBook` 是派生缓存（Timed Ability 虚拟到期、诊断 `Rebuild`），不登记闭包、**不在 `effect.tick` 热路径每帧 Sync/Clear**。效果到期与 Periodic 跳伤走 float `RemainingTime` / `TickAccumulator`（与 P6 前同一套边界帧语义）。`CollectDue` 不是生产 Tick 取件路径。实现仍挂在现有 `GameplayEffectRuntime.Tick`，不另开 FrameGraph 节点。

模拟路径的随机数只从 Frame `DeterminismContext` 领号，且**仅**在 `CommitSerial` 相取数。`Rng.Shared`（墙钟 `TickCount xor ManagedThreadId`）与无种子 `new Random()` 都不是确定性资产，不得进入 GAS、战斗公式、或决定实体数量与位置的模拟路径。

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

墓碑查询必须能区分「这个句柄曾经活过、现在死了」与「这个句柄从未存在」。实体 ID **仍然回收**，代数靠 `generation` 失效旧句柄；不把 ID 永不复用当成合同。过期 generation 的命令丢弃并记诊断。实现：`ComponentStore.QueryEntityTombstone`（NeverExisted / Dead / Alive / PendingDeath）。结构事务半实体泄漏未核实，未开卡，不作为现状。

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

该 digest 合同**不**把 `Rng.Shared` 或无种子 `new Random()` 当成确定性资产。模拟随机见 §9：只从 Frame `DeterminismContext` 在 `CommitSerial` 领号。

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
11. 同一 AttributeKey 内部使用 Lumio 公式：先 `(Base + ΣAdd) × (1 + ΣPercent)`，有 `Override` 则最终覆盖，Add / Percent 不再生效；`Percent` 不得实现为连乘。
12. Modifier 无独立生命周期；账本是 `ActiveGameplayEffect`，`ModifierPool` 只能是 Aggregator 派生缓存。
13. Event 是已发生的事实，不得因同帧施加又移除被抵消吞掉。
14. 模拟随机禁止墙钟种子与无种子 `new Random()`；只从 Frame `DeterminismContext` 在 `CommitSerial` 领号。

本文只定义终态架构和不变量，不规定从当前代码迁移到终态的步骤、兼容层或删除顺序；迁移规划另行记录。

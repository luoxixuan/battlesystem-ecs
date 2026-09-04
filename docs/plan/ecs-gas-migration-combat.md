# ECS + GAS 迁移：战斗与效果阶段（M3-M4）

> 上级总览：[ecs-gas-migration-plan.md](ecs-gas-migration-plan.md)
>
> 前置阶段：[ecs-gas-migration-foundation.md](ecs-gas-migration-foundation.md)
>
> 终态约束：[ecs-gas-final-architecture.md](../ecs-gas-final-architecture.md)

本文覆盖统一伤害/资源/死亡管线，以及 Gameplay Effect 和 Trigger runtime。它是行为风险最高的阶段，必须建立在 M1 的请求/句柄合同和 M2 的属性语义之上。

## 1. M3：统一伤害、资源和死亡管线

### 1.1 进入条件

- M0 的 Damage 顺序、DamageFlags、护盾策略、死亡事件时点已经冻结；
- M1 的 `DamageRequest`、`ResourceRequest`、EntityHandle、sequence 和命令缓冲可用；
- M2 的 computed 属性可以由攻击路径读取，但不要求所有属性已经迁移；
- golden 场景能区分初始化、资源恢复和真伤害三类写入；
- 当前 `FrameScheduler` 的并行收集 → 串行提交顺序仍保持不变。

### 1.2 目标

所有真伤害来源只产生 `DamageRequest`。一个 `DamageResolver` 负责解释命中、免疫、暴击、护甲、抗性、元素、护盾、下限和死亡入队；`ResourceResolver` 负责实际写入 HP、Shield、Mana、Gold 等资源；生命周期模块负责死亡解析、奖励和击杀事实。

`ApplyEnemyDamage` 如果需要保留为公共入口，只能变成明确的 adapter，不能继续成为另一套规则解释器。

迁移 `ApplyEnemyDamage` 的调用方时要先删除其已有的抗性、下限或护盾重复分支。当前部分路径（例如 PlayerTower/Skill）已经在入队前计算过抗性，再把它们原样转发到包含同一规则的 Resolver 会造成双重减伤；每个 source 必须在台账中标出“已计算字段”和“由 Resolver 计算字段”。

### 1.3 先分类，后替换

审查中发现的直接写入必须先分类，避免把初始化或资源恢复误当成伤害：

| 类别 | 示例 | 迁移目标 |
|---|---|---|
| Spawn/初始化/实体迁移 | `AddEnemy`、召唤、Morph、Burrow、Mine、Ascension | 仍由生成/生命周期模块写入；不进入 DamageResolver |
| 治疗、护盾、法力和资源恢复 | EnemyHealer、EnemyAbility heal、EmergencyHeal、`BuffSystem.HealPlayer` | `HealRequest`/`ShieldRequest`/`ResourceRequest` → ResourceResolver |
| 真伤、DoT、处决、反射和伤害转移 | Tower/Skill/Projectile/Bleed/Frostbite/Thorns/Weather/Meteor/LifeLink 等 | `DamageRequest` → DamageResolver |

只有第三类必须经过 DamageResolver。第一类不能为了通过静态扫描而机械改成伤害请求。

### 1.4 Resolver 合同

`DamageRequest` 至少携带：source/target handle、raw amount、DamageType、ElementType、flags、ability/effect id、owner player、sequence、父请求/链路信息和 execution context。M3 必须另外冻结 `rawAmount` 的金额语义：推荐它表示“来源快照后的基础量、尚未应用目标侧护甲/抗性/护盾且尚未应用暴击”的值，由 Resolver 统一处理暴击；若某个过渡来源仍提交已含暴击的金额，必须显式标记 `AmountStage=PostCrit`，Resolver 不得再次乘算。切流完成后应只保留一种 stage，不能靠调用方约定。

来源归属不能从默认 `playerId` 猜测。Request 要显式传递 source handle、owner player、tower/ability/effect id 和 parent sequence；尤其 `BuffSystem` 的 DoT 不能把所有来源固定成 player，也不能因为 Firewall 没有传 tower id 就丢失 sourceDeathPolicy、Kill attribution 或 Trigger scope。

Resolver 的顺序固定为：

1. 验证 source/target handle 和目标活跃状态；
2. 应用无敌、免疫、命中和闪避规则；
3. 应用暴击、护甲、抗性、元素和暴露规则；
4. 生成护盾消耗或其他 Resource operation；
5. 按 policy 应用血量下限；
6. 由 ResourceResolver 写入资源并产生 `DamageApplied`；
7. HP 达到死亡条件时只产生一次 `DeathQueued`；
8. `DeathResolve` 完成奖励和归属后，才产生 `KillConfirmed`。

技能跳过护甲、荆棘是否过护盾、塔是否过护盾、特殊处决是否忽略无敌等取舍，必须通过显式 `DamageFlags` 或 resolver policy 表达。不能以“某系统没有调用某函数”作为语义。

元素转换必须保留完整类型信息：一击拆成两种实际伤害时，使用两条带类型的请求，或使用能表达转换组成的明确字段；不能将两种类型合并成一个无类型 tuple 后再猜测。

### 1.4.1 Resolver 与 commit boundary

统一的是伤害规则和唯一 writer，不是所有请求都必须被拖到同一个晚期节点。每个 producer 必须声明 `commitBoundary`/`applyPhase`，Resolver 在该边界消费请求：

| Producer | 现有语义位置 | 迁移约束 |
|---|---|---|
| Weather DoT | `PreGame` | 在原有天气更新结束处提交，不能让敌人多走一阶段后才受伤 |
| Movement/Wound | `Movement` | 在移动/伤口规则要求的边界提交，保持目标和路径判断时序 |
| Enemy ability/Burrow/LifeLink | `AI`/`Movement` | 按原阶段提交，死亡和后续 AI 是否可见必须由 golden 场景确认 |
| Tower/Projectile/Skill | `Combat`/`SkillBuff` | 可映射到 `CombatEmit` 后的 `GameplayResolve`，但要保持现有同帧可见性 |

兼容期可以在原 Group 末尾调用同一个 Resolver，也可以定义 `EarlyResolve` 和 `GameplayResolve` 两个声明式节点。Resolver 的规则实现只能有一份；节点只是消费边界，不能各自再解释护甲、抗性或死亡。Weather/Wound/EnemyAbility 至少各有一条“旧时序 vs 新时序”的 golden 测试，验证后续 AI、目标选择、死亡入队和事件顺序没有未批准变化。

当前 `ComponentStore.BeginFrame` 对未完成的死亡队列有保护性检查。引入多个伤害消费边界时，仍必须保持每帧唯一的 death queue commit；如果未来需要多队列，必须先定义新的双缓冲协议并补充回归测试，不能绕过这个保护。

推荐的过渡协议是“多个 producer/Resolver 消费边界，共享一个当帧死亡队列，单次 `DeathResolve`”：

1. `EarlyResolve`、`GameplayResolve` 等边界各自消费本阶段请求，并把首次归零的目标标记为 `PendingDeath`；
2. 同一目标只追加一次 `DeathQueued`，后续阶段看到 `PendingDeath` 后拒绝新的伤害/效果请求，避免 AI 或目标选择再次使用它；
3. 所有边界完成后，由唯一的 `DeathResolve` 提交当前帧死亡队列；`KillConfirmed` 只能在奖励归属和生命周期处理完成后产生；
4. 下一帧 `BeginFrame` 仍必须看到已清空的死亡双缓冲。若确实需要跨帧队列，先设计新的队列协议，不得绕过现有保护。

至少补充 `WeatherKillsEnemy_BeforeNextAISelection`、`EarlyDamage_DoesNotDoubleQueueDeath` 和 `KillConfirmed_IsAfterRewardAndDestroy` 三类时序测试。

BuildPhase 是同一规则的特殊边界：当前 BuildGroup 仍可能 tick Skill/GlobalSkill，但 `FrameScheduler.Tick` 可能在 BuildGroup 后早退。M3 不得让战斗请求或死亡队列无意跨 Build 帧；要么在 Ability gate 中拒绝战斗型请求，要么为 BuildPhase 增加明确的 commit/清理节点，并为两种状态写 golden 测试。

### 1.5 Shadow 和切流

每个来源单独切流，推荐顺序：

1. `BuffSystem` DoT（已有队列，最适合验证 Periodic → DamageRequest）；
2. `WeatherSystem` 和 `GlobalSkillSystem` Meteor（验证已修复的死亡入队契约，并把直接 HP 写迁入 Resolver）；
3. Projectile、Bleed、Frostbite、Thorns（验证 flags、护盾和特殊规则）；
4. `PlayerTowerAttackSystem`；
5. `TowerAttackSystem` 主路径及 chain/splash/bounce/link/transfer 等复杂队列；
6. Elemental reaction、Reflect、Boss/塔特殊路径和其他低频来源。

顺序可以由 M0 基线调整，但每个来源必须完成以下闭环：

- legacy adapter 将旧输入转为 `DamageRequest`；
- shadow resolver 只计算并记录差异，不写任何共享状态；
- 差分覆盖最终伤害、护盾消耗、DamageType/ElementType、死亡队列和事件顺序；
- 在帧边界打开该来源的 cutover flag；
- 关闭该来源的旧 drain loop/直接写入；
- 加一条从真实 `FrameScheduler` 入口驱动的集成测试。

生产路径不能同时让 legacy 和 new 写 HP 或发布同一事实。未切流来源可以继续经过 `LegacyDamageAdapter`，但不得直接绕过 Resolver 写入资源。

2026-09-03：对**玩家**的真伤害不再走生产侧 `DecreasePlayerHealth`。10 个站点统一经
`ComponentStore.ApplyPlayerDamageAuthority` → `ResourceResolver.TryApply(PlayerDamageRequest)`。
预检 `CanApplyPlayerDamageAuthority` 必须同时覆盖目标合法性与 `CanAccept(0,2)`（致死会发 2 个
critical 事实），否则 `EnemyAISystem` 会在 overflow 拒绝前吃掉 `EnemyStealthMultiplier`。
`DecreasePlayerHealth` 只保留为 ResourceResolver 内部 writer。BossTrailAoe / SuicideBomb 已迁到
`ApplyPlayerDamageAuthority`，会吃护盾/下限/复活。
FrameGraph：`effect.tick` / `build.effect.tick` / `skill-buff.skill.update` 已替换旧
`effect.commit` / `build.effect.commit` / `ability.commit`；`build.skill.update`、
`build.auto-skill.update`、`build.global-skill.update` 不再声明 AbilityRequests，
`post-death.corpse.update` 不再声明 EffectRequests。
`BuffSystem.ApplyDot` 的 None 走 `TryAdopt`，叠层刷新走 `TryRestack`；该路径 timer 只由
`GameplayEffectRuntime` 写。`TryAdopt` 已校验 BlockedTags 与 Periodic payload。尚未 catalog 化。
死亡回调不再声明 `AbilityRequests`。不是 F4–F9 终态收口。
2026-09-03 再续：生产 DoT 经 `ProductionDotCatalog` 物化后走 Periodic 空 modifier；
Rally 改消费 `DamageApplied`，新增 `combat.rally.consume`（`tower-attack` 前）；
`AbilityRequests` 入队。`EffectRequests` 仍是死 token。仍不是终态收口。
2026-09-04：`AbilityRequests` 改为 accept 后旁路日志（拒绝不占槽）；Rally writes 改为
`PlayerAttributes` + `TowerState`；`ApplyDot` None 改回 `TryAdopt`（尸体区/Firewall/岩浆
脉冲重挂可并存多份）。仍不是终态收口。
2026-09-04 终态收口续：`ApplyDot` 经 `EffectRequest` → `TryApply`；`Stacking.None` 同 key
不刷新不新开槽。`ability.commit` 在 Combat 前，技能伤害请求先于塔攻入队。技能 Periodic
同帧 `effect.tick`。catalog 敌方技能延到 PreCombat。仍不是 F4–F9 终态收口。
2026-09-04 能力 GE 解耦：granted effect 与世界 DoT 同在 Combat 后 `effect.commit` 挂槽；
Combat 段无当帧能力 modifier/tag。执行项仍在 `ability.commit`。仍不是 F4–F9 终态收口。

M3 就要建立事件迁移表，逐项标记旧 `EventBus` 的 `LegacyOnly`/`Bridge`/`GameplayOnly` 状态；M4 才允许 Trigger 消费 `GameplayOnly`。Bridge 期间由新事实单向转发旧事件，按 sequence 去重，不能让旧 publisher 和新 publisher 各发一份。

`TowerAttackSystem.Update` 不能作为一次性“大改”处理：这是一个 2923 行的系统，暴露 16 个 system dependency setter，另有 `SetGameConfig`、`SetTurn`、`SetWaveNumber`，共 19 个 `Set*` 方法；构造函数还接收 Store、renderer、TechTree、EventBus 等依赖。它在同一串行调用中按顺序消费主伤害、Tesla chain、splash、bounce、lifesteal、thorns、debuff、knockback 和 fragment 队列，部分分支还依赖本帧目标的死亡状态/位置。迁移时先给 queue item 增加 `parentSequence`/`phase` 等上下文，用 `TowerAttackLegacyAdapter` 保留原有 Phase 2a-3d 消费顺序并逐项提交统一 Resolver；等每类 queue 都有 golden 测试后，才拆成 FrameGraph 节点。这里的行数、setter 数和队列数都是当前快照的定位指标，不是迁移完成度指标。

当前 `TowerAttackSystem` 明确有 8 类双缓冲队列：damage、debuff、heal、thorns、chain、splash、bounce 和 fragment；不要把“队列数量”写成固定七类。台账脚本还应按逻辑队列去重，并记录每类的 producer、drain、容量和是否会直接写资源。

Tower 路径至少要冻结两组顺序样例：`Chain_DoesNotRetargetAlreadyDeadTarget`（链式伤害遇到已标记死亡目标）和 `Splash_PreservesStablePropagationOrder`（溅射/反弹的 parent sequence 顺序）。测试要断言目标状态、请求序列、实际伤害和死亡队列，而不是只断言最终存活数量。

### 1.6 当前代码触点

- `Core/FrameScheduler.cs`：保留唯一帧入口和现有两阶段边界；
- `Core/ComponentStore.cs`、`ComponentStore_Enemy.cs`：保留死亡队列兼容入口，逐步让 Resolver 成为唯一调用者；
- `Systems/SkillSystem.cs`：先改 `ResolveSkillDamage` 的 tuple/直接写路径；
- `Systems/BuffSystem.cs`、`BleedSystem.cs`、`FrostbiteSystem.cs`、`ElementalReactionSystem.cs`：改为生成请求；
- `Systems/ProjectileSystem.cs`、`WeatherSystem.cs`、`GlobalSkillSystem.cs`：关闭直接 HP 写；
- `Systems/PlayerTowerAttackSystem.cs`、`TowerAttackSystem.cs`：先由 legacy adapter 承接并扩充 queue item contract，冻结现有 drain 顺序，再逐个迁移主、链、溅射、反弹和生命链接分支；
- `Systems/ObstacleSystem.cs`、`HeroSystem.cs`、`InventorySystem.cs`、`ReflectTowerSystem.cs`、`SuicideBombSystem.cs`、`ThornsAuraSystem.cs` 等：按上述三类台账迁移；
- `Core/MovementGroup.cs` 中可能 lazy construction 的 `DeployableTrapSystem`：先登记为明确 producer，再把其 Movement 阶段伤害纳入同一请求管线；
- `BenchmarkSystem` 的 mode 2/4/5 harness：同步使用和生产相同的 resolver composition，避免只改生产 Registry。

### 1.7 生命周期兼容注意事项

当前 `ResolveEnemiesKilledThisFrame` 会在奖励和 `OnEnemyKilled` 订阅后执行 `DestroyEntity`。M3 先保留它作为唯一的兼容生命周期提交点，给新 Resolver 增加 `DeathQueued`；不要在同一阶段同时改变回调和销毁顺序。

Combo、Necromancer、Culling、SoulHarvest 等旧订阅者先通过 adapter 消费兼容事件。`KillConfirmed` 的终态时点和 post-death adapter 留到 M5 的 `PostDeathEventCommit` 再统一调整。

### 1.8 M3 退出门槛

- 生产代码中除 Spawn/初始化例外外，真伤写入点都变成 `DamageRequest`；
- Resource 写入集中到 ResourceResolver，治疗/护盾/法力不再由内容系统直接改数组；
- 每个伤害来源只有一个 writer，旧 drain loop 在 cutover 后不再运行；
- 护甲、抗性、护盾、免疫、下限、暴击、元素归因和死亡奖励测试通过；
- `DamageType` 的位标志、Holy/True 特殊分支和 immunity mask 兼容测试通过；
- 同一目标先死亡后续请求被拒绝；批量命中、14 hits、ID 回收和 deterministic replay 通过；
- `DamageApplied`、`DeathQueued`、`KillConfirmed` 时点符合冻结语义；
- 静态扫描和架构测试能发现新增的直接资源写点；
- 全套 build/test/rules/diff-check/mode 2/4/5 通过，性能无超过 ±5% 的回退。

### 1.9 M3 回滚

按 source 关闭 cutover flag，在下一帧边界恢复该来源的 legacy adapter。确认旧路径重新成为唯一 writer 后才继续运行。已由新 Resolver 提交的当前帧结果不能再由旧路径补写；必要时丢弃尚未提交的请求并记录诊断。

### 1.10 M3 删除条件

M3 不立即删除全部旧 drain loop。某个来源只有在 100% 切流、真实帧测试和观察期都通过后，才可删除该来源的直接写入和旧队列；全局旧入口留到 M7 收口。

## 2. M4：Gameplay Effect 和 Trigger runtime

### 2.1 进入条件

- M1 的 Effect/Ability/Tag ID、generation、命令和事件合同已可用；
- M2 的 AttributeAggregator 已能接收 Modifier 并标记 dirty；
- M3 的 Resolver 能产生权威 `HitConfirmed`、`DamageApplied`、`DeathQueued` 等事实；
- 每种已迁移 DoT 都已关闭旧 timer 或旧伤害 owner。

### 2.2 运行时产物

- immutable `GameplayEffectDefinition` 与稀疏 `ActiveGameplayEffect` 池分离；
- `AbilityState`、`TriggerState`、Tag contribution、Modifier handle 的生命周期管理；
- `EffectCommit`、`EffectTick`、`EffectExpire`、`GameplayEventCommit` 节点；
- 固定的 `clockId`、首次 tick、catch-up、snapshot、叠层/刷新和 source death policy；
- Instant effect 不占 active slot；Periodic runtime contract 已支持 Damage/Heal/Resource/GameplayEvent 四类 dispatch；
- 单帧提交轮次、事件数量、池容量和递归深度上限。

`GameplayEffectDefinition` 不保存 remaining time、当前 stack、tick accumulator 或当前 source/target 实例；这些字段只存在于 `ActiveGameplayEffect`。

### 2.3 第一条垂直切片：命中 10 次后伤害增加 30%

用一个 Trigger 和一个 Effect 验证事件、叠层和属性边界：

- Trigger：`HitConfirmed`、scope=`PerSource`、threshold=10、mode=`EveryN`、保留余数；
- Effect：`Infinite`、`AddStack`、明确 `maxStacks` 和 stack key；
- Modifier：`DamageOutputMultiplier`、`Add 0.30`。

同一帧 14 次命中时，必须按批次得到 1 次 crossing，余数为 4。默认所有命中读取帧开始属性快照，Effect 在下一次 AttributeAggregate 边界可见。若设计要求第 11 次命中立即影响第 12 次，必须把多段攻击声明为有序子批次。

### 2.4 效果迁移顺序

1. Poison Nova/Firewall 等单一 Periodic DoT；
2. 一个带 Infinite Modifier 的攻击 Buff；
3. Freeze/Slow/Root 等 Tag/控制效果；
4. Heal、Shield 和资源类效果；
5. 需要 Execution 的链式、回溯、召唤和特殊 Boss 效果。

每类效果都要验证 apply → tick/aggregate → expire/remove → 资源/伤害 → 死亡清理完整链路。

`Mark`/`DeathMark` 的类和部分 Registry 接线已经存在，但层数写入、增伤消费和 Trigger 事实链仍不完整；`SystemRegistry` 现在已注册默认 `HitConfirmed`/`PerSource`/`EveryN(10)` 垂直切片，并由 `FrameScheduler` 驱动。`HitTriggerSystem` 仍是 legacy EventBus 路径，不能把其 getter 测试当成新机制；未接入 typed Catalog 的其他 Trigger 仍标为 disabled。

### 2.5 旧 Buff/Effect 适配

- `BuffSystem.ApplyDot` 先转换成 `EffectRequest`；
- 旧 `GameplayEffectDef` 构造函数可以保留兼容 facade，但新代码不能把运行态字段写回 Definition；
- 旧 `Enemy*DurationLeft` 只能作为 projection/调试数据，不能继续倒计时；
- `BuffSystem.ResolveDotDamage` 只负责提交请求，不能直接 `EnemyHealth -=`；
- Elemental reaction 的 Freeze/Stun 通过 Effect Registry 创建，不能从系统内部偷偷构造另一种效果实例。

### 2.6 Trigger 和事件权威

Trigger 只消费已经提交的 Gameplay Event，不直接订阅多个旧系统回调。事件顺序由 M3 Resolver 和生命周期 adapter 统一产生：

- `HitConfirmed`：命中验证通过后；
- `DamageApplied`：ResourceResolver 实际产生有效伤害后；
- `DeathQueued`：目标进入死亡队列后；
- `KillConfirmed`：死亡解析和奖励归属完成后。

由 Trigger 产生的新 EffectRequest 进入下一事件队列，默认不在同一调用栈递归。超过单帧轮次、事件数或池容量时，停止该链并产生诊断。

M0 先用压力场景测量，再冻结初始上限；建议起始值为每帧最多 8 个提交轮次、每帧最多 8192 个 Gameplay Event，且可按配置覆盖但必须启动校验。`GameplayLoopAborted` 的默认语义是“已提交的前序结果保留，当前轮尚未提交的链式请求全部丢弃，实体和资源不做隐式回滚”，并记录触发链、sequence、剩余队列和原因。若产品需要原子回滚，必须另行设计事务缓冲，不能在现有 commit 逻辑中假装支持。

补充一个自触发 OnKill → Effect → Damage → Kill 的无限递归失败测试，以及“达到上限后下一帧可恢复”的测试。

注意 `HitTriggerSystem` 当前只是定义存在、没有完整生产接线；不能把现有 `EventBus` 的少量频道当成 GAS Trigger runtime。新 Trigger 必须以 Resolver 产生的内部 Gameplay Event 为唯一输入。

### 2.6.1 旧 EventBus 并存协议

每种事件在台账中标注 `LegacyOnly`、`Bridge` 或 `GameplayOnly`，并明确唯一 publisher：

- `GameplayEventCommit` 是新事实的唯一 publisher；
- 兼容期只允许单向 `GameplayEvent → Legacy EventBus adapter`，禁止旧 EventBus 反向触发新 Trigger；
- adapter 以 `(eventType, source/target, sequence)` 做去重，已由新 Trigger 消费的事件不能再执行一次旧规则；
- `IBattleEventBus` 仍只接收 Presentation adapter 的已提交展示事件，不直接替代内部 Gameplay Event。

补充 `OnHit_IsPublishedOnce_WithLegacyBridge`、`OnDamage_IsNotDoubleConsumed` 和 `PresentationBus_DoesNotActivateGameplayRule` 测试，并在每个事件完成切流后把状态从 `Bridge` 改为 `GameplayOnly`。

当前实现台账：`GameplayEffectRuntime` 已成为 typed effect 的 apply/remove/expire owner，`ActiveGameplayEffectStore` 持有 generation-safe runtime；`GameplayTriggerRuntime` 只读取 Resolver 队列并以 `(type, sequence, source, target)` 去重。Trigger 链式结果保留在 `NextEvents`，事件上限为 8192，超限发布 `GameplayLoopAborted`。`FrameScheduler.ConfigureGameplayRuntime` 提供真实入口；默认 scheduler 不注入完整 Catalog，但 `SystemRegistry` 会显式注册 9001 连击 runtime effect，现有 `HitTriggerSystem` 与旧 `EventBus` 其余内容仍为 `LegacyOnly`，没有宣称完成切流；旧 `BuffSystem` 仅继续推进未切流的 legacy projection，`RuntimeOwned` effect 不再由其计时。
M6 延后边界：完整 typed Catalog 接管、源/目标 tag state、Transfer source-death policy 和内容配置编译仍未切流；本阶段只实现 event-tag seam，并对未支持的 tag filter 明确拒绝。

本轮 M4 契约复核补充：`GameplayEffectRuntime` 是 `RuntimeOwned` effect 的唯一 timer/damage owner；legacy `BuffSystem` 仅推进未标记的兼容 projection。Periodic runtime 已按四类 payload dispatch，Trigger 的 effect-tag 即使没有 filter-tags 也必须匹配。`SourceDeathPolicy.Persist` 的 RuntimeOwned tick 可通过 `AllowMissingSource` 使用 target 代理索引结算，事件仍保留 stale source；其他请求默认拒绝。完整生产内容/Catalog 接管、Transfer/反射 source-death 语义和 legacy Trigger/DoT 删除继续延后至 M6/M7，未宣称完成切流。
效果注册入口会 fail-fast 拒绝负 ID、非 Constant MagnitudeSource 及非法 Periodic 数值，并发布可观察拒绝诊断；zero-based 的 ID=0 合法。兼容构造器仅负责旧数据映射，不再静默修正坏定义。
容器边界：Runtime 的 active entity 列表按申请/移除维护且固定容量受实体上限约束；Trigger 的 counters/seen/definitions/reset 目前使用预分配稀疏字典/集合，满载会拒绝并发布诊断，但 Consume 仍可能触发哈希查询。迁移为固定容量值类型开放寻址表属于后续性能债务，需单独基准验证。

Runtime 事件队列在 `FrameScheduler` 每帧边界显式 `ResetFrame`，不会跨长局累积；modifier capacity 和 effect-definition capacity 在写入前拒绝，并通过 `EffectRejected`/诊断计数保持可观察。

Periodic payload runtime 已按 Damage、Heal、Resource、GameplayEvent 分派：Heal 走独立 `HealRequest` 语义，Resource 必须使用已注册资源键且不能伪装 CurrentHealth，四个运行时 clock（Combat/Enemy/RealTime/Global）由实例定义驱动。轮次、seen、counter 和 definition 容量超限统一发布 `GameplayLoopAborted` 并保留 durable abort 诊断；完整生产内容/Catalog 接管、Transfer/反射和 legacy owner 清理仍延后至 M6/M7。

当前 Periodic `MagnitudeSource` 仅支持 `Constant`；Attribute/Multiplier/Execution 求值留待后续 Catalog/Execution 迁移，注册时 fail-fast 并发布拒绝诊断，不以零 magnitude 静默降级。Runtime.Events 默认容量为 8192，也可通过构造参数覆盖；容量耗尽会进入独立 abort 诊断队列并保留拒绝计数。

Runtime/Trigger 的字典按固定容量预分配；ActiveGameplayEffect sparse pool 只在 apply/remove/expire 等非遍历热路径使用，容量耗尽和状态更新失败均保留计数与拒绝事实。ActiveGameplayEffect 使用 `TickAccumulator` 表达 next-tick 时间，不另存未维护的 `NextTickTime` 字段。

Trigger 消费采用固定输入快照，链式事件保留至下一轮；`SkipMissed` 每个周期最多补发一次并清除欠账。`KillConfirmed` 的 stale target 仅在 Source 目标策略下允许继续，ResourceResolver 的 Heal/Shield/Resource 事实在 GameplayResolve 与 PostDeath 边界消费；完整 Catalog、Transfer/反射及旧 owner 清理仍延后至 M6/M7。

### 2.7 M4 退出门槛

- Effect apply/remove/expire、叠层、刷新、满层、来源死亡和目标死亡都有测试；
- clock、首 tick、catch-up、snapshot 和同帧可见性都有测试；
- 14-hit、实体 ID 回收、stale handle、池耗尽和递归上限测试通过；
- 真实施放 → tick → 伤害/资源 → 死亡 → 清理链路通过；
- 每个已切流效果只有一个 timer/damage owner；
- 旧 EventBus 不会和新 Gameplay Event 重复触发同一规则；
- 全套门禁和性能对照通过。

### 2.8 M4 回滚

按 Effect/Ability ID 关闭新 runtime 消费，恢复对应旧 Buff/Skill adapter。保留池和 Definition 不代表启用；关闭状态下不得 tick、改属性、发伤害或发布重复事件。

### 2.9 M4 删除条件

M4 不删除旧 `GameplayEffectDef`、旧 timer 或旧 Trigger 代码。对应 Effect/Trigger 完成切流、stale/generation/递归测试和观察期后，才可删除其旧 owner；公共兼容 facade 留到 M7/M8 再决定。

## 3. 战斗阶段禁止事项

- 不在 shadow 阶段双写 HP、护盾、层数或事件；
- 不在 generation 句柄稳定前把 ActiveEffect 索引暴露给跨帧调用方；
- 不让旧 timer 和新 Effect runtime 同时推进同一个效果；
- 不在 M3 同时重排完整 FrameGraph 和改变死亡事件顺序；
- 不把初始化写点、治疗写点和真伤写点用一个“统一伤害”类型粗暴覆盖；
- 不为了让测试变绿而把 `KillConfirmed` 提前到 `DeathQueued`；
- 不在同一帧依赖并行回调顺序来决定 14 hits 的效果可见性。

## 4. 2026-08-31 契约复核记录

本次 Damage/Resource/Death resolver 语义收口后，Core build 通过；EXE 显式构建 0 error，但 SDK 对现有 `net6.0` 目标框架报告 1 条 EOL warning。全量 xUnit 为 1428/1428，测试静态规则 0 违规，`git diff --check` 通过。

同一构建产物的五轮压测结果如下（原始完整输出保存在 `artifacts/benchmark-final-20260831.log`；本轮 mode 2/4 复测未发现明显回退）：

| 模式 | 五轮 FPS | 说明 |
| --- | --- | --- |
| mode 2 | 42335 / 46879 / 41627 / 39715 / 47678 | 合并热路径，硬门禁通过 |
| mode 4 | 9826 / 9303 / 9455 / 10075 / 9935 | 真实系统链路，硬门禁通过 |
| mode 5 | 4681 / 6835 / 6840 / 5637 / 6258 | 原始五轮记录，保留为后续性能债务；本任务不以其阻塞语义收口 |

mode 5 不得伪造为通过，也不因该债务删除或改写历史日志；待后续稳定观察/专门性能阶段再处理。

### 4.1 历史语义收口复测记录

本机历史执行记录（2026-08-31，构建产物未改阈值）：mode 2 五轮为 49295 / 46601 / 48944 / 42434 / 49940 FPS；mode 4 五轮为 10088 / 10123 / 10424 / 10019 / 10292 FPS。mode 5 按用户决定只执行一轮，结果为 7137 FPS（5/5 关卡胜利）；该单轮结果不能替代历史五轮基线，mode 5 性能债务继续保留。

### 4.2 生命周期与请求边界历史复核（2026-08-31）

- `KillConfirmed` 由唯一 `ResolveEnemiesKilledThisFrame` 路径在奖励、生命周期回调、塔击杀结算和 `DestroyEntity` 完成后发布；事件保留死亡队列中的旧 source/target generation handle。
- `DamageResolver` 的 validated adapter 仍解析完整 `index + generation + active` handle；stale handle 即使索引复用也会拒绝。
- Damage/Resource 提交边界先摘出当前批次，提交期间产生的新请求继续进入 deferred queue，下一边界消费；帧开始发现未消费请求会增加诊断计数，不再静默丢弃。
- `ResourceRequest.OwnerPlayerId` 默认无效值为 `-1`，资源写入必须显式提供 owner；内部玩家适配器已传递目标玩家 ID。
- Reflect/Transfer 作为未迁移语义仍被 Resolver 明确拒绝并返回 `UnsupportedFlags`；Reflect 系统产生的可迁移真伤继续构造普通 `DamageRequest`，未发生静默降级。
- 新增 stale generation、未消费 deferred queue 和缺失 owner 的 golden 覆盖；全量测试计数以实际门禁输出为准。
- 本轮门禁单轮性能记录：mode 2 = 36816 FPS、mode 4 = 9222 FPS；mode 5 = 5848 FPS，仅作性能债务观测，不替代历史多轮基线。

### 4.3 M4 历史验收快照（不可复核）

本节整段仅保留历史快照，不是当前结果或退出证据。引用的 benchmark artifact 在当前工作树不可定位，故其中的 mode2/4/5 数值不可复现；监督复核曾指出 mode4 约回退 33%、mode5 约回退 55%，这些历史结论不得与 4.5 的当前审计样本混用。

| 模式 | 历史记录（不可复核） | 备注 |
| --- | --- | --- |
| mode 2 | 23444 / 23081 / 20807 | 历史快照，不判定门禁 |
| mode 4 | 5141 / 5513 / 4415（历史快照，不可复现） | 监督记录约回退 33%，但不可据此判定当前闸门 |
| mode 5 | 3292（5/5 关卡 Victory，历史快照） | 按用户决定仅登记观察债务，不阻塞本轮语义修补 |

本阶段已知未切流项：Trigger 的稀疏 `Dictionary/HashSet` 将在 M8 性能工作包替换为固定容量值类型表；完整 typed Catalog、Transfer/Reflect source-death 语义和 legacy owner 清理继续留在 M6/M7，不能在 M4 退出时虚报完成。该债务不改变本阶段已验证的 Effect/Trigger 生命周期合同；语义测试通过，但性能连续样本仍需满足统一 ±5% 闸门并完成双轴 Standards/Spec 复审后才能宣称 M4 退出。

### 4.4 本机门禁执行记录（2026-08-31）

文档引用的旧 `artifacts/benchmark-final-20260831.log` 在本工作树不可定位，不能用来复现上节历史数值。相对计算统一采用 [ecs-gas-m0-baseline.md](../ecs-gas-m0-baseline.md) 的最新 M0 复跑基线（mode2 `14953`、mode4 `7699`、mode5 `7342`）。先按门禁顺序执行 `dotnet build BattleSystemECS.Core`，再执行 `dotnet build BattleSystemECS.csproj`，随后使用同一 Debug 构建产物和默认 `game_config.json` 配置运行：

```text
cmd /c "echo 2|dotnet run --no-build --project BattleSystemECS.csproj"  -> mode 2: 39946 / 41585 / 41801 FPS (median 41585)
cmd /c "echo 4|dotnet run --no-build --project BattleSystemECS.csproj"  -> mode 4: 9027 / 8024 / 7253 FPS (median 8024)
dotnet run --no-build --project BattleSystemECS.csproj -- 5              -> mode 5: 1509 FPS, 5/5 Victory
```

相对统一 M0 基线的计算为：mode2 `(41585 - 14953) / 14953 = +178.10%`，mode4 `(8024 - 7699) / 7699 = +4.22%`，mode5 `(1509 - 7342) / 7342 = -79.45%`。这些先前结果没有形成可审计的连续样本；mode5 按用户豁免登记为观察债务，不阻塞本轮，也不等于规范门禁通过。独立 Standards 复跑 mode4 `8048 / 6076 / 7864`（median `7864`，`+2.14%`）也不足以证明稳定。旧 5141 数值及其约 -33% 的监督差异仅作为不可复核历史保留，不得与当前结论混用。

原始临时日志还保留了 build 后 mode2 首轮 `5163 FPS` 的异常低值；随后连续三轮为 `39946 / 41585 / 41801`，因此采用连续稳定三轮的中位数而非单次幸运值。该波动说明压测仍受运行环境影响，后续比较必须继续使用同一构建、配置和多轮中位数。

### 4.5 dirty working-tree 执行记录（2026-08-31）

以下结果是 dirty working-tree 执行记录：结果对象为 `HEAD=e0bb4f4d2439c8773f4823a03ce2d87b62512429` 加 3 个未提交文件：`Core/GAS/GameplayRuntime.cs`、`BattleSystemECS.Tests/Framework/GameplayRuntimeTests.cs`、`docs/ecs-gas-migration-combat.md`。DLL 在这些源码改动存在时重新构建，故包含前两个代码文件的未提交改动；文档文件不进入 DLL。默认 `game_config.json` SHA-256 为 `CF235A2627E0D0513717350161A2BD40ACC1E9AA913E141A1CB5585E41CAC978`，`.NET SDK 9.0.311`，`bin/Debug/net6.0/BattleSystemECS.dll` SHA-256 为 `B39DA88249A69E7CC5DAA046BD5E27903B752864E9FA874EB3D9C6F4F3E29416`。顺序为 `dotnet build BattleSystemECS.Core` → `dotnet build BattleSystemECS.csproj`；每个模式先预热 1 次，再连续测量（mode5 按豁免测量 1 次）。完整原始输出仅作为执行机器的临时本地附录，提交后不可独立复核：`C:\Users\ADMINI~1\AppData\Local\Temp\battlesystem-ecs-audit-20260831.log`。

```text
cmd /c "echo 2|dotnet run --no-build --project BattleSystemECS.csproj"  # warmup 46109; measured 40792, 46561, 42000, 37433, 45214
cmd /c "echo 4|dotnet run --no-build --project BattleSystemECS.csproj"  # warmup 8914; measured 9488, 9183, 8262, 9599, 9639
dotnet run --no-build --project BattleSystemECS.csproj -- 5              # measured 6299 FPS, 5/5 Victory
```

统计规则：丢弃每个模式的预热值，五次测量取中位数；spread 为 `(max-min)/median`。相对 [M0 基线](../ecs-gas-m0-baseline.md)（mode2 `14953`、mode4 `7699`、mode5 `7342`）分别为：mode2 median `42000`，`+180.88%`，spread `21.74%`；mode4 median `9488`，`+23.24%`，spread `14.51%`；mode5 `6299`，`-14.21%`，按用户豁免保留为观察债务，不等于规范通过。按 plan/AGENTS 已定义的硬门禁，mode2/mode4 的相对基线中位数及逐轮结果均未出现超过 5% 的回退；spread 本身没有被定义为硬失败阈值，现作为可审计性/稳定性观察债务记录。mode4 的运行环境波动仍需后续稳定观察，不能仅凭单次结果或 spread 宣称性能改善。

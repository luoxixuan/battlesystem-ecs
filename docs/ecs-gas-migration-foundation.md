# ECS + GAS 迁移：基础阶段（M0-M2）

> 上级总览：[ecs-gas-migration-plan.md](ecs-gas-migration-plan.md)
>
> 终态约束：[ecs-gas-final-architecture.md](ecs-gas-final-architecture.md)
>
> 本文范围：基线、语义冻结、Catalog/ID/Handle/Request/Event 合同、属性和资源基座。

## 1. 阶段目标

基础阶段只建立“可验证的共同语言”和最小运行时土台，不把任何一条业务伤害路径提前切到新实现。完成 M2 后，后续阶段可以在不复制 World、不复制属性真相的前提下接入 Resolver 和 Gameplay Runtime。

基础阶段的关键结果是：

- 配置、定义和运行态的边界清楚；
- 旧实体 ID 回收不会污染新一代状态；
- Request/Event/Command 的结构和顺序固定；
- 属性聚合有唯一解释器，资源写入有明确 owner；
- feature-off 时战斗行为与迁移前一致。

## 2. M0：基线与语义冻结

### 2.1 进入条件

无代码进入条件。M0 是所有深改前的测量和决策闸门。

### 2.2 必做工作

#### 记录环境和基线

记录当前 commit、工作树已有改动、配置版本、.NET/Unity 版本和运行参数。工作树已有改动属于用户状态，不得为了建立基线而回滚或覆盖。

按仓库门禁执行：

- `dotnet build BattleSystemECS.Core`
- `dotnet build`
- `dotnet test BattleSystemECS.Tests`
- `pwsh -File tools/check-test-rules.ps1`
- `git diff --check`
- `echo 2 | dotnet run`
- `echo 4 | dotnet run`
- `dotnet run -- 5`

保存测试总数、失败测试名称、mode 2/4/5 的 FPS、运行时配置摘要和日志诊断计数。审查文档中的 1317 等数字是历史快照，不能直接当作当前基线。

#### 建立 golden 场景

至少覆盖以下真实入口：

1. `FrameScheduler` 驱动的普通塔攻击、护甲/抗性和死亡奖励；
2. `BuffSystem` 的 Firewall/Poison 类周期伤害；
3. Projectile 命中和命中后附加效果；
4. Weather/Meteor 直接伤害和死亡入队；
5. 护盾、元素转换、免疫、I-frame 和下限；
6. Effect/实体 ID 回收；
7. 同一帧 14 次命中后的计数和触发顺序。

另外单独覆盖 BuildPhase：当前 BuildGroup 可能更新 GlobalSkill 和 Skill，若它们产生战斗性请求而 scheduler 早退，死亡或技能队列可能跨帧滞留。必须先决定 BuildPhase 是否允许产生战斗 Ability/Effect；若允许，必须有通用的 resource/death commit，若不允许，Ability gate 必须明确拒绝战斗效果并定义请求丢弃规则。

golden 结果不只记录最终 HP，还要记录请求、资源变化、死亡队列、奖励、Gameplay Event 类型和 sequence 顺序。确定性 replay 应能在相同输入下重现这些结果。

#### 建立迁移台账

用脚本或架构测试记录以下数量，后续每个阶段更新。直接写入要按生产方法/来源去重，并排除初始化、注释和测试；不能把一次 grep 的行数当成伤害路径数量：

- 直接写 `EnemyHealth`、`PlayerCurrentHealth`、`Shield`、`Mana` 等资源的生产点；
- damage queue/drain loop 数量及来源；
- 技能入口和绕过统一入口的调用点；
- `GameplayEffectDef`/`AppliedEffect` 的运行态字段；
- parser、配置数据源、Group nullable 槽位、Registry setter/injector；
- 旧效果/DoT timer 的 owner；
- benchmark 是否使用与生产相同的 system composition。

直接写资源的生产点先分为三类，不能机械地全部替换：

| 类别 | 处理方向 |
|---|---|
| Spawn/初始化/实体迁移 | 保留在生命周期/生成模块，不进入 DamageResolver |
| 治疗、护盾、法力和其他资源恢复 | 迁移为 `ResourceRequest` |
| 真伤、DoT、处决、反射和伤害转移 | 迁移为 `DamageRequest` |

### 2.3 语义冻结项

在 M0 结束前，为每一项写测试样例和选择：

| 项目 | 必须回答的问题 |
|---|---|
| Damage 顺序 | 无敌、命中、暴击、护甲、抗性、元素、护盾、下限的顺序是什么？ |
| DamageFlags | 技能是否跳护甲？荆棘和塔是否过护盾？哪些是真伤？ |
| 元素转换 | 一次攻击拆成多条请求，还是保留主类型和转换元数据？ |
| Death authority | `DeathQueued`、奖励、`OnEnemyKilled` 和实体销毁的精确时点是什么？ |
| BuildPhase 能力 | BuildPhase 是否允许 Meteor/攻击/伤害型 Ability？早退时死亡和技能队列如何提交或丢弃？ |
| 属性聚合 | Add/Multiply/Override、优先级和快照策略是什么？ |
| Effect | 重复、刷新、满层、stack key 和来源死亡如何处理？ |
| Periodic | 使用哪一个 clock、首次 tick、catch-up 和 snapshot 规则？ |
| 可见性 | 同帧 14 hits 是否全部读帧开始快照？何时看见新 Modifier？ |
| 资源 | MaxHealth 改变时是否裁剪 CurrentHealth？MaxHealth 上升是否自动治疗？ |
| 失败和容量 | stale handle、池耗尽、递归超限如何报告？ |

### 2.4 M0 退出门槛

- 基线命令都有可复现输出；
- golden 场景可从真实帧入口运行；
- 语义表每项都有明确选择和测试样例；
- 台账完成首次记录；
- benchmark 覆盖差异已列出并有修复计划。

### 2.5 M0 回滚

不改变运行路径。若基线不可复现，停止后续迁移，先修复测量或测试环境。

### 2.6 M0 删除条件

M0 不删除任何生产路径、公共 API、旧配置或兼容字段；它只建立基线和决策记录。

## 3. M1：Catalog、ID、Handle 和请求/事件合同

### 3.1 进入条件

M0 的基线、golden 场景和语义选择已冻结。

### 3.2 范围和产物

#### 稳定标识和定义

- `AbilityId`、`EffectId`、`AttributeKey`、`TagId`、`ClockId`；
- immutable `AbilityDefinition`、`TargetingDefinition`、`GameplayEffectDefinition`、`ModifierDefinition`、`TriggerDefinition`；
- `EntityHandle(index, generation)` 和 `EffectHandle(index, generation)`；
- `ExecutionContext` 和确定性 `sequence`。

定义只保存静态配置。剩余时间、tick 累加器、层数、捕获值、来源/目标实例和 generation 必须留在运行态池。
`DamageType` 在现有代码中包含位标志语义，不能按连续 ordinal 重新编号；M1 的 schema/validator 要保留 Physical/Magic/Fire/Ice/Lightning/Holy/True 以及现有 immunity mask 的兼容规则，并为 Holy/True 的特殊分支写测试。

#### 请求、事实和命令

新增值类型 contract：

- `AbilityRequest`、`EffectRequest`；
- `DamageRequest`、`HealRequest`、`ShieldRequest`、`ResourceRequest`；
- 内部 `GameplayEvent` 队列；
- 线程局部收集和串行提交用的 `CommandBuffer`/`CommandSink`。

Request 可以被拒绝，Event 只表示已验证事实。内部 Gameplay Event Queue 与 `IBattleEventBus` 展示事件保持两条独立 seam。
现有 `Core/EventBus.cs`/`GameEvents.cs` 的 class DTO 和旧订阅器只能作为兼容 adapter；热路径的新 Request/Event 必须使用可复用的值类型缓冲，不能把旧 EventBus 直接当作零分配并行队列。

#### Catalog 编译和校验

`CatalogCompiler + CatalogValidator` 在启动时校验：

- ID 重复、引用缺失、未知 shape/effect/tag/attribute；
- duration、period、stack 上限、范围和互斥字段；
- alias 冲突和旧 skill id/name 的映射；
- 节点/执行器是否有对应消费者。

错误必须包含配置路径和 ID，不能静默退回 `Single`、0 或空效果。当前 `GameConfigLoader.LoadConfig` 的通用 catch/default fallback 不能直接承载这个约束；M1 必须新增显式 strict bootstrap。strict 模式对 Catalog/Validator 错误抛出带路径和 ID 的诊断，旧 fallback 只允许在明确的 legacy/test 模式使用。

### 3.3 实施顺序

1. 先冻结 ID、generation、sequence、Clock 和错误语义；
2. 先把 `Data/Configs/skills.json` + `Data/Skills` 作为候选 canonical `SkillDefs`，将 `game_config.json` 的旧 `Skills` 通过显式 legacy importer/alias 导入；当前旧表缺少完整 `AreaShape`/`AreaRadius`/DoT 字段，不能把缺省值伪装成已校验的新定义；
3. 给坏配置、旧代数、池耗尽写失败测试；
4. 用 adapter 把旧 tuple queue、旧 `GameplayEffectDef` 和旧 skill id 转为新 contract；
5. 在 feature-off 下跑 golden 场景，确认无行为漂移。

### 3.4 当前代码触点

- `Core/GAS/Attributes.cs`、`GameplayEffect.cs`、`GameplayAbility.cs`：从示例/混合 struct 演进为静态定义 contract；
- `ComponentStore_World.cs` 的 `AbilityInstances`、`ActiveEffects` 和 count/accessor：先保留兼容 facade；
- `ComponentStore.DestroyEntity`：继续清理旧 count，并在新 pool 中实现 generation 失效；
- `GameConfig.cs`、`GameConfigLoader.cs`：新增编译入口，旧 parser 暂不删除；
- `SystemRegistry`：只增加 Catalog/CommandSink 注入，不在 M1 彻底重写接线。

### 3.5 M1 退出门槛

- canonical 配置可编译且坏配置测试能 fail-fast；旧 `game_config.Skills` 只能通过显式 legacy importer，并对缺失字段给出诊断，不得静默伪装成完整新定义；
- 实体和效果 ID 回收不会继承旧 generation；
- command/event 在单线程和并行收集下 sequence 一致；
- feature-off golden 结果与 M0 一致；
- sparse pool、命令队列、事件队列的峰值和溢出有诊断；
- 仓库全套门禁通过，mode 2/4/5 相对基线无超过 ±5% 的回退。

### 3.6 M1 回滚

关闭 Catalog bootstrap 和新 adapter 的消费开关，继续由旧定义和旧队列运行。新类型可以留在代码中，但不得让旧系统读取半初始化的新池。

### 3.7 M1 删除条件

M1 不删除旧 parser、旧 ID、旧 queue 或旧 Definition。只有在 M6 对应数据源完成切流并经过观察期后，才可删除相应 alias/兼容导入器。

## 4. M2：属性基座和资源策略

### 4.1 进入条件

M1 的 ID/Handle/Request 合同已冻结，且没有旧代数污染。

### 4.2 范围和产物

- `AttributeSchema`：域、默认值、单位、范围和是否允许 Modifier；
- `AttributeStore` adapter：基础值继续来自现有 dense SOA；
- `AttributeAggregator`：唯一解释 Add/Multiply/Override、优先级和 snapshot；
- dirty 标记、computed cache 和明确的属性聚合边界；
- `ResourcePolicy`/`ResourceResolver` contract；
- MaxHealth/CurrentHealth、Shield、Mana、Gold 等资源的裁剪和写入规则。

高频稳定属性（Position、基础 Damage、Armor、Computed 输出）继续放在 dense SOA。Modifier、Tag 和运行态效果不塞进每个实体的固定结构列。

### 4.3 首个属性切片

只接一个真实的 Infinite Modifier，例如 `DamageOutputMultiplier` 或攻击力：

`Effect/Modifier apply → dirty → AttributeAggregate → 真实塔/技能读取 computed`。

旧 `PlayerBuffFlags`、硬编码倍率和专用数组在过渡期只能作为 projection 或兼容输入，不能与新 Modifier 各算一次。先接攻击、暴击、护甲三个属性的读路径，其他属性保持旧路径。

### 4.4 资源所有权

- `CurrentHealth`、`Shield`、`Mana`、`Gold` 不由普通属性聚合覆盖；
- `ResourceResolver` 是唯一资源写入者；
- MaxHealth 下降时按冻结规则裁剪 CurrentHealth，上升不自动治疗，除非 Effect 明确产生 Heal/Resource request；
- M2 只建立 contract，完整 DamageResolver 在 M3 接管伤害写入。

### 4.5 当前代码触点

- `Core/GAS/Attributes.cs`：保留兼容构造，但新增 schema/aggregator contract；
- `ComponentStore_Player.cs`、`ComponentStore_Enemy.cs`、`ComponentStore_Tower.cs`：增加 base/computed projection，不删除旧列；
- `SkillSystem`、`BuffSystem` 和塔攻击系统：选定 getter 改读 computed cache；
- `FrameScheduler`/现有 Group：先手工插入最小 `AttributeAggregate` 节点，顺序不变。

### 4.6 M2 退出门槛

- Add/Multiply/Override、优先级、snapshot 和 dirty 重算都有单元测试；
- 攻击、暴击、护甲各有一条从真实 `FrameScheduler` 入口驱动的测试；
- 添加、刷新、移除 Modifier 都从 base 重算，不使用浮点逆运算；
- 资源数组没有被 AttributeAggregate 覆盖；
- feature-off/on 的目标切片结果一致；
- 全套门禁和性能对照通过。

### 4.7 M2 回滚

按属性域关闭 computed 读取，恢复旧 getter/projection。回退时清除或忽略新 projection，确保旧 flag 不会和残留 Modifier 叠加。

### 4.8 M2 删除条件

M2 不删除旧属性列、旧倍率字段或旧 getter。对应属性在 M3/M4 完成所有来源切流、差分验证和观察期后，才能移除旧 projection。

## 5. 基础阶段禁止事项

- 不在 M1/M2 直接迁移所有伤害来源；
- 不在 generation 合同之前建立长期 ActiveEffect 外部引用；
- 不让 AttributeAggregator 和旧倍率字段同时成为数值来源；
- 不把初始化写点误迁为 DamageRequest；
- 不因为“以后可能需要”把所有可选字段改成 Archetype；
- 不删除旧公共 API、旧 parser 或 Unity DLL facade，直到对应域完成切流和观察期。

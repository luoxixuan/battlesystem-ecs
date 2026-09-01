# ECS + GAS 迁移：编排、内容和收口阶段（M5-M8）

> M5 方案 2 收口记录（2026-09-01）：旧 manual merged-loop 基线与当前 ProductionRegistry/FrameGraph FixedPopulation composition 不同，不能作为直接父切片；因此 `ParentRelative=UNADJUDICABLE`，不将同源 control 或旧 manual 数据冒充 parent。用户批准暂缓 mode4 的 3,000 FPS 绝对门禁，标记为 `FAIL/Deferred to M8`，不得改阈值或伪造 PASS；性能优化延期至 M8，架构迁移完成后再处理。当前候选修复后的 Release mode4 五次为 `2095/1843/2024/1991/1927 FPS`（中位数 1991、最小 1843），绝对观察如实保留为 `FAIL/Deferred to M8`。非性能硬门禁与 mode2 已通过；mode5 仅观察、不阻塞。本记录覆盖 Pickup active-enemy、PathModifier active-index/lifecycle、AddEnemy fission sentinel 及 runner BuildHost 白名单候选，提交仍以独立 Spec/Standards 复审通过为前提。

> 上级总览：[ecs-gas-migration-plan.md](ecs-gas-migration-plan.md)
>
> 前置阶段：[ecs-gas-migration-combat.md](ecs-gas-migration-combat.md)
>
> 终态约束：[ecs-gas-final-architecture.md](ecs-gas-final-architecture.md)

本文覆盖 FrameGraph、技能/目标/配置收口、系统注册、Engine/Content 分层、旧路径清理、稳定观察和 Archetype 决策。它假定 M3 的 Resolver 和 M4 的 Effect/Trigger runtime 已经在至少一条真实内容切片上稳定运行。

## 1. M5：SystemGroup 和 FrameGraph 渐进替换

### 1.1 进入条件

- M3/M4 的 Request、Event、clock 和 commit 边界已经冻结；
- 至少 V1 基础攻击、V2 周期伤害和 V3 命中计数效果通过真实帧链路；
- 当前 `FrameScheduler` 的 legacy 顺序有 golden/replay 记录；
- 所有将被迁移的节点都能列出自己的读写数据和执行策略。

### 1.2 过渡策略

现有 11 个 Group 保留为粗粒度容器和兼容入口。不能把所有系统直接改成一个统一签名：当前系统同时存在 `SetTurn`、`Update`、`ResolveX`、订阅回调，以及 `IBuildPhaseGroup` 与普通 `ISystemGroup` 两类接口。

按以下顺序过渡：

1. 引入最小 `ISystem` contract；用 `FrameNodeAdapter`/`DelegateSystem` 包装现有调用，保留 `CombatSetup` 的 Prepare 和 `SkillBuff` 的 Resolve 次序；
2. Group 内部从逐字段 nullable 调用改为可迭代容器，但字面执行顺序暂不变；
3. 为节点声明 `Reads`、`Writes`、`RunsAfter`、`ActivePhases`、`executionPolicy` 和 `clockId`；
4. `FrameGraphBuilder` 启动时校验重复节点、缺失依赖、读写冲突、环和稳定 tie-break；
5. 以 legacy/graph 双组合做 shadow 顺序和状态对比，但只有一套路径可以提交写入；
6. 图稳定后再把 GAS 节点和死亡/展示提交节点切到声明式图。

### 1.2.1 M5 技术合同补充

- 引入不可变的 `TimeContext`（原始 delta、`enemyDt`、`combatDt`、Build/RealTime/Global clock），节点只读 context；禁止继续用 `ref deltaTime` 隐式修改后续节点输入；
- 节点声明 `optionalDependencies` 及缺失时的明确策略（disabled、no-op 或启动失败）。例如 Blinker 依赖 Pathfinding 时，不能只在运行时 null-check 而让图谱看不见这条依赖；
- i-frame、Phaser、Blinker、全局时间缩放和位置事件包装为显式 `GlobalStateNode`/serial node，声明它们的读写和执行策略；
- `UpdateTimeScale` 产生的 TimeContext 必须在同一帧固定，后续节点不能各自重新计算缩放。

shadow 期间先保持当前字面顺序：`BeginFrame/CC flags → i-frame/Phaser/Blinker(raw delta) → GlobalTimeScaleAdvance → immutable TimeContext → Build/Wave branch`。`GlobalTimeScaleAdvance` 显式写 duration/scale 并返回缩放结果，不再通过 `ref deltaTime` 改写 caller 局部变量；Blinker 把 Pathfinding 声明为 optional dependency，并由图校验其 `disabled/no-op/fail` 策略。

这里的迁移理由是所有权、可选依赖和时序可见性，不是把当前成本描述成固定的 `O(MAX_ENTITIES)`：i-frame/Phaser/Blinker 目前主要遍历活跃实体列表，实际复杂度和 profile 结果应在 M0 记录。

### 1.3 目标节点映射

最终至少要有以下可观察节点：

- `AbilityCommit`；
- `AttributeAggregate`（按 dirty 边界）；
- `CombatEmit`/Targeting；
- `EffectTick`；
- `DamageResolve`；
- `ResourceResolve`；
- `GameplayEventCommit`；
- `EffectCommit`/`EffectExpire`；
- `DeathResolve`；
- `PostDeathEventCommit`；
- Presentation event consumer。

节点属于 ECS 帧图，不属于 GAS 自己的 Tick。Build/Wave 的阶段门控和 `enemyDt`、`combatDt`、RealTime 的 clock 映射必须由图声明，不能继续由 Group 字段和 scheduler 早退各写一份。尤其要明确 BuildPhase 是否允许产生战斗 Ability/DamageRequest：若允许，图中必须有该阶段的资源/死亡提交；若不允许，`AbilityCommit` 必须在入口拒绝并清理未提交请求。

### 1.4 必须处理的现状问题

- `FrameScheduler.Tick` 的 Build/Wave 二元分支；
- `PostDeath.Phase` 等第二真相源；
- `FrameScheduler` 内联的 i-frame、Phaser、Blinker 和位置事件机制；
- `MovementGroup` 内部的 lazy system construction（例如 `DeployableTrapSystem`）：迁移前必须显式注册，不能让节点在首次 Execute 时偷偷出现；
- `CombatGroup`、`SkillBuffGroup` 中仅靠注释表达的“runs last/before/after”；
- `SystemRegistry` 中 21 次 `= null` group-slot 赋值（18 个独立槽位名，`TowerIncome`/`TowerLink`/`TowerOvercharge` 跨 group 重复）：删除，或改成有日志的 Feature flag 注册；这些槽位对应的实现文件可能已经存在，必须以“是否实例化/注册”判断 disabled，而不是以文件是否存在判断；
- `SystemRegistry.CreateAll/WireDependencies/AssignToGroups` 的构造顺序和 setter 注入；
- benchmark 手工 composition 与生产 Registry 不一致的问题。

这里有两种不能混用的计数：当前 `SystemRegistry` 有 106 个 `XxxSystem?` 属性，另有一个 `EventBus?`，合计 107 个 nullable 顶层属性；21 是 `AssignToGroups` 中显式写成 `= null` 的 group-slot 赋值次数，对应 18 个独立槽位名。前者用于完整 composition/installer 台账，后者用于 disabled-slot 清理，不能互相推翻。

### 1.5 M5 退出门槛

- 非法图定义在启动和测试中失败；
- legacy/graph 在 golden 场景中状态、事件和顺序一致；
- 并行节点没有未声明共享写入，Commit 顺序稳定；
- 阶段门控只有一个事实源；
- scheduler 内联机制已包装为可注册节点；
- 所有节点使用同一 `TimeContext`，optional dependency 的缺失策略可由启动校验发现；
- benchmark 使用与生产相同的 composition/FrameGraph，或差异有明确测试；
- deterministic replay、全套门禁和 mode 2/4/5 通过。

### 1.6 M5 回滚

保留 legacy scheduler adapter，通过启动开关选择 legacy 或 graph。遇到差异时在下一帧边界切回，并保留节点快照和诊断；禁止同时运行两套会写状态的图。

### 1.7 M5 删除条件

M5 不删除现有 Group、legacy scheduler 或 `SetTurn`/`ResolveX` facade。只有所有节点的顺序、读写和阶段门控都已由 FrameGraph 覆盖，并完成观察期后，才可移除对应 adapter。

## 2. M6：Ability、Targeting 和配置收口

### 2.1 进入条件

- M3 的伤害语义和 M4 的 Effect Registry 已稳定；
- `GameplayEvent` 已由 Resolver/生命周期模块权威产生；
- M5 至少提供稳定的 `AbilityCommit`、`EffectCommit` 和 `AttributeAggregate` 边界；
- 玩家技能栏数据源的选择已经冻结。

M6 还必须先完成 `SkillSystem` 的迁移状态审计：当前文件已经引用 GAS 并使用 `AbilityInstances`，但仍保留 `_skillDamageQueue`、`AreaShapeType` 大 switch、Chain Lightning 常量和多处直接效果逻辑。因此 M6 是“完成半迁移”，不是把一个纯 legacy 系统从零替换；审计表要分别标出已接 GAS、仍是硬编码、需要进入 Targeting 的形状和需要进入 DamageResolver 的队列。

### 2.2 统一能力入口

以下入口最终都调用 `GasRuntime.TryActivate(request)`：

- `SkillSystem`；
- `GlobalSkillSystem`；
- `HeroSkillSystem`；
- `TowerActiveSkillSystem`；
- `EnemyAbilitySystem`；
- `AutoSkillSystem`。

入口只做能力请求、冷却/资源/标签检查和目标提示。Targeting 只收集目标 ID，Effect 列表负责产生效果/资源/伤害请求。

### 2.3 派发迁移顺序

1. 玩家技能栏并入共享 `SkillDefs`（推荐），或建立显式兼容导入器；
2. 将 `AreaShapeType`/`FromString`/`ExecuteAbility` 的大 switch 包装为 shape registry；
3. 把几何和效果拆开：Single/Circle/多目标只负责目标集合，Damage/DoT/Heal/Shield/CC 由 Effect definition 组合；
4. 先迁简单 Single/Circle/DoT/Heal/Shield，再迁 Chain、CC、TimeWarp、Summon、rewind 和 resurrect；
5. 逐个迁移 Global、Hero、Tower active 和 Enemy ability；
6. 每个 Ability ID 的真实激活、冷却、资源、目标、效果和死亡测试通过后，才关闭旧 case。

`_skillDamageQueue` 必须在 M3 已定义的 `DamageRequest` contract 上收口；Chain Lightning 的“最多目标数、跳转衰减和去重”属于可复用 Targeting/Execution 定义，不应继续作为 `SkillSystem` 私有常量和 switch case 的隐式语义。

因此 `SkillSystem` 在迁移期只是一层兼容 adapter，并有两个独立 cutover：M3 接管 `_skillDamageQueue` 的伤害 writer，M6 接管激活、Targeting 和 Effect 派发。任一 cutover 单独完成都不能把该系统标记为“已迁移”。

`AutoSkillSystem` 可以继续保留为调用 facade，但不能再产生另一套冷却或效果规则。Hero/Tower active 当前只翻冷却和打印日志，必须在切流时接入同一能力管线。

现有 `MarkSystem`/`DeathMarkSystem` 的添加入口和 `HitTriggerSystem` 并没有完整生产接线；M6 不能只替换旧方法名。必须由 M3 Resolver 产生权威命中/伤害事实，再由 M4 Trigger runtime 创建相应 Effect，并用 Registry → FrameScheduler 的真实集成测试证明规则已启用。

### 2.4 配置迁移

- 用 `System.Text.Json` 类型化模型和 CatalogCompiler 作为唯一新入口；
- 旧手写 `Extract*` parser 在对应数据源 100% 切流前保留，但不再增加新字段；
- 旧 skill id/name 使用显式 alias，不在未知 shape 时静默退回 `Single`；
- 加载后校验必填字段、枚举、范围、互斥字段和引用路径；
- 错误包含配置文件、节点路径和 ID，并在启动时 fail-fast。

新 bootstrap 必须提供 strict 模式；现有 `GameConfigLoader` 的 catch/default fallback 只能留在显式 legacy/test 模式，否则会吞掉 Catalog 校验错误。

### 2.5 当前代码触点

- `Systems/SkillSystem.cs` 的 `InitializePlayerSkills`、`ExecuteAbility`、各 `Cast*` 和 `ResolveSkillDamage`；
- `Systems/GlobalSkillSystem.cs` 的技能类型分派和 Meteor/EmergencyHeal；
- `Systems/HeroSkillSystem.cs`、`TowerActiveSkillSystem.cs` 的冷却/日志 stub；
- `Systems/EnemyAbilitySystem.cs` 和 `AutoSkillSystem.cs`；
- `Core/GAS/GameplayAbility.cs` 的 29 字段 god-struct 和 `AreaShapeType.FromString`；
- `GameConfig.cs`、`GameConfigLoader.cs`、`Data/Configs/skills.json`、`Data/Skills/*.json`、`game_config.json`。

### 2.6 M6 退出门槛

- 所有能力入口都经过统一激活管线；
- 已有 shape/effect 的新技能只改配置即可组合；
- Hero/Tower active 不再只是冷却和日志；
- 三套技能数据源和两个 parser 的差异已消除或有明确兼容语义；
- 未知定义 fail-fast；
- 真实激活 → 目标 → Effect → Damage/Resource → Death/Event 的集成测试通过；
- 全套门禁、Unity `BattleDriver` smoke 和性能对照通过。

### 2.7 M6 回滚

按 Ability ID 关闭新入口，恢复旧 `SkillSystem`/旧入口。旧 god-struct、parser 和 alias 直到该域观察期结束前不得删除；同一 Ability 不能同时走两条入口。

### 2.8 M6 删除条件

旧 `switch`、`Cast*`、冷却 ticker、手写 parser 和 god-struct 只有在所属 Ability/数据源 100% 切流、Unity/benchmark 通过且观察期无 fallback 后，才能分别删除。

## 3. M7：Installer、Engine/Content 边界和旧路径清理

### 3.1 进入条件

- M5 图驱动节点已经成为生产提交路径；
- M6 的能力和配置域已完成切流；
- 主要伤害来源和效果没有 legacy fallback；
- Unity 和 benchmark 已验证新公共 contract。

### 3.2 注册模型

- 引入 `ISystemInstaller` 或等价的注册 contract；
- Installer 声明 system、节点、依赖、阶段、执行策略和 feature flag；
- `SystemRegistry` 先作为 adapter，逐步从 `CreateAll/WireDependencies/AssignToGroups` 三个巨型方法退化为 installer 迭代；
- `HitConfirmed`、`DamageApplied`、`KillConfirmed`、`TowerPlaced` 等成为一等 Gameplay Event；
- 订阅者通过 event contract 注册，不再由内容系统互相持有具体类引用；
- 21 次 group-slot 赋值对应的 18 个独立槽位要么移除，要么在启动日志中明确列为 disabled；实现文件存在但没有 `CreateAll` 实例化的项仍视为未注册，不能把文件存在当成已启用。

M7 要审计全部 107 个 nullable 顶层属性，但不在本阶段重新迁移 106 个 system 的战斗行为：帧节点和 Ability 行为分别已在 M5/M6 切流，M7 负责把既有 composition 收口到 Installer、删除旧 setter/slot 并保留必要 facade。若 M5/M6 尚未完成这些行为切流，不能用扩大 M7 估算来跳过进入条件。

### 3.3 Engine/Content 分层

Engine contract 只暴露实体/存储 view、空间查询、Catalog、CommandSink、Resolver、FrameGraph 和诊断接口。塔、技能、Boss、Buff 等内容只能依赖这些 contract，不能直接依赖另一个内容系统的具体实现。

目录可以分批调整：先用接口和 namespace 建立单向依赖，再移动文件。不要为了目录整洁同时改变战斗语义。

### 3.4 删除旧实现的条件

对应 source/ability/effect 必须同时满足：

1. 100% 走新路径；
2. 观察期内 legacy fallback 次数为 0；
3. 真实帧链路、配置集成、replay、benchmark 和 Unity smoke 通过；
4. 旧路径的测试已迁移到新 contract；
5. 没有外部公共 API 使用旧 facade，或已书面决定保留兼容层。

删除动作按小工作包提交：旧 tuple queue、旧 timer、直接资源写点、旧 switch、旧 parser、旧 setter/injector 分开删除，不能做一个不可回退的大清理提交。

### 3.5 M7 退出门槛

- 启动日志列出已注册、未启用和被拒绝的 system/content；
- Registry 不再靠构造顺序隐式表达依赖；
- Engine/Content 依赖方向有编译或架构测试；
- 迁移台账达到终态目标；
- 全套门禁、性能和 Unity smoke 通过。

### 3.6 M7 回滚

保留旧 Registry adapter、公共 facade 和必要 alias 到 M8 观察期结束。任何删除前先确认对应粒度的 cutover flag 可以在帧边界关闭。

## 4. M8：稳定观察、性能优化和 Archetype 决策

### 4.1 观察期

至少覆盖完整一局、多个 wave、DoT/控制/死亡、实体 ID 回收、配置错误、Unity 展示和压力场景。记录：

- stale generation 丢弃数；
- Effect/Command/Event pool 峰值和溢出；
- 递归事件中止次数；
- Resolver 拒绝请求及原因；
- legacy fallback 次数；
- mode 2/4/5 与 M0 的相对性能；
- benchmark 与生产 composition 的一致性。

任何未预期的 stale、overflow、aborted 或 rejected 诊断都视为未完成，而不是“偶发日志”。

### 4.2 性能优化顺序

先 profile 再改存储：

1. 核对 `AbilityInstances`、Boss phase 等低容量数组是否仍按 `MAX_ENTITIES` 分配；
2. 测量死亡清理、稀疏池利用率和命令队列峰值；
3. 仅将确有收益的 niche 字段移到 capped pool/sparse side table；
4. 重新跑 mode 2/4/5 和完整一局 soak；
5. 记录内存、缓存 miss、清理成本和 FPS 变化。

性能优化不能改变 GAS、Resolver、Request/Event 或 FrameGraph contract。

### 4.3 Archetype 决策闸门

只有在以下条件同时成立时才评估 Archetype/chunk：

- 稳定组件签名的迭代和缓存 miss 已成为主要可测成本；
- dense SOA + sparse side table/capped pool 不能以更小风险解决问题；
- chunk 搬迁的复杂度、调试成本和 Unity/测试影响有量化收益；
- 可以保持现有 GAS 语义和所有 public contract。

### 4.3.1 初始量化门槛

M0 完成后允许基于实际硬件调整一次阈值，但必须在做 prototype 之前冻结。建议使用相同配置、相同种子、至少 3 次运行取中位数：

- 候选组件迭代至少占模拟 CPU 时间的 30%，或占 mode 4/5 总帧时间的 20%；
- 有硬件计数器时，候选路径的 cache miss/内存停顿相对基线至少高 30%；没有硬件计数器时，使用 profiler 的内存带宽、遍历耗时和采样栈作为可复核代理，不得凭感觉判断；
- 最小 Archetype prototype 必须让候选路径 CPU 时间下降至少 20%，并让 mode 4/5 中位 FPS 提升至少 15%；
- 实体结构迁移、维护和序列化的额外开销必须低于总模拟帧时间的 5%；
- mode 2/4/5 任一项不得比基线下降超过 5%，常驻内存不能增加超过 10%；
- prototype 的迁移代码、调试和 Unity/测试适配成本要单独记录，不得用局部微基准收益掩盖整体成本。

未同时达到这些门槛，就记录“本项目不引入 Archetype”，继续使用 dense SOA + sparse/capped pool；阈值本身不能被事后改写来证明方案成功。

如果 profile 不支持引入，保留“dense SOA 核心 + sparse GAS pool + active entity lists”就是完成状态。动态 Buff、层数、周期计时不能通过增删结构组件让实体搬迁。

### 4.4 M8 退出门槛

- 旧兼容路径已删除或有书面保留理由；
- 全量 build、test、规则检查、diff-check、mode 2/4/5 通过；
- Unity `BattleDriver` smoke 和 Core DLL 同步流程通过；
- 性能没有超过 ±5% 回退，优化有 profile 证据；
- 终态架构、迁移总览、阶段文档、测试规范和变更记录同步。

### 4.5 M8 回滚

观察期内保留最后一个可运行 checkpoint 和 legacy facade。性能优化或清理出现差异时，先回退该工作包，不回滚已经验证的 Resolver/Effect 语义，除非 golden/replay 证明语义本身错误。

## 5. 垂直切片与阶段交叉点

| 切片 | 覆盖行为 | 首次接入阶段 | 最终收口阶段 |
|---|---|---|---|
| V1 基础攻击 | 塔 → 命中 → 护甲/抗性 → HP → 奖励 | M3 | M5 |
| V2 周期伤害 | Poison/Firewall → tick → 死亡 | M3/M4 | M6 |
| V3 命中计数 Buff | 10 hits → +30% | M4 | M6 |
| V4 控制效果 | Freeze/Slow/Root 生命周期 | M4 | M6 |
| V5 主动技能 | 玩家/Hero/Tower active | M4/M6 | M7 |
| V6 特殊伤害 | 元素转换、反射、荆棘、Boss 下限 | M3 | M6/M7 |
| V7 事件型内容 | Combo、Mark、DeathMark、OnKill | M4 | M7 |

每个切片都必须从真实入口跑到 Resolver、Effect/Trigger、死亡和展示事件，不能只直接调用 getter 或内部计算函数。

## 6. M5-M8 共同禁止事项

- 不为了统一接口而破坏当前 `SetTurn`/`ResolveX` 的阶段语义；先用 adapter；
- 不在 FrameGraph 切流时同时改变 Damage 顺序或 Effect clock；
- 不在旧路径未完全关闭前删除公共 facade；
- 不让 benchmark 继续使用与生产不同的隐式 composition；
- 不把 Archetype 当成架构正确性的证明，也不把动态 Buff 表达成结构搬迁；
- 不以“测试没抛异常”代替真实状态、事件、顺序和性能验收。

## 7. FrameGraph 当前执行记录（2026-09-01，方案 2 授权，复审完成/待提交）

11 个既有 SystemGroup 中实际配置的 `SetTurn`、`Update`、`Resolve` 等调用已拆成独立 system-node adapter，不要求重写业务 system。`FrameScheduler.Tick` 仍是所有帧路径唯一入口；节点声明真实 `Reads/Writes`、`Before/After`、phase、time domain、execution semantics、required dependency 和 optional dependency。validator 在 composition 阶段拒绝重复节点、缺失 required dependency、缺失或冲突 writer、非法 phase/time domain 与 cycle；拓扑平局按稳定 `FrameNodeId` ordinal 排序，与注册顺序无关。

ProductionRegistry composition 当前包含 193 个启用节点与 33 个 disabled diagnostic，共 226 个唯一 NodeId；数量由 review catalog 与实际 production graph 集合推导，不是独立裸常量。`frame.input.publish` 读取跨帧 persistent SOA 资源，执行可观察 memory barrier，且只写单一 `PersistentStatePublished` 令牌；它不伪装成 HP/position/resource writer。`pregame.random-event.callback-dispatch` 与 `spawning.wave.callback-dispatch` 将同步 delegate/presentation 副作用从 update 节点拆出。每个 profile 都包含稳定 `BindingId`、owner、review artifact/SHA、Reads/Writes、phase/time/execution semantics 和 binding-required 标识。`AbilityCommit`、`AttributeAggregate`、`EffectCommit`、Damage/Resource/Event commit 以及 primary/cascade death prepare + callback dispatch 都有独立真实执行节点；graph 与 scheduler 不构造或匹配具体 Ability、Effect、Trigger 或内容 ID。生产 composition 由 `SystemRegistry.CreateAll → WireDependencies → AssignToGroups` 完成并 Seal，首个 Tick 不构图，未封存、陈旧 review、缺失 active binding 或启用 `DisabledUnsafe` profile 都直接失败。

三份外部审计按生产 NodeId 字面集合复算：early `91`、combat `83`、postdeath `49`（其中 `39` 只是 correction matrix），early∩combat=`0`、early∩postdeath=`12`、combat∩postdeath=`4`，并集 `207`。完整注册图相对并集 Missing=`0`、Extra=`19`；19 个 Extra 已逐项写入 supplemental review，因此最终 Missing=`0`、Unresolved=`0`。完整 226 行 reconciliation 同时记录 Before/After、required/optional dependency、scenario availability 和 review root，位于 `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\framegraph-metadata-audit-20260901\reconciliation.csv`（SHA-256 `68B8F60BAB2837BCFFC4AEC87ECCD40BE10FEC790EBB0C2FEAC1B91C5EBF0A75`）和 `reconciliation.md`（SHA-256 `77FF87D9C63D7E733492C93A1D3DD178B2A0F22AE2CABC8A8CAA61E044DD1FD6`）。同目录 supplemental artifact SHA-256 为 `EA9FA8D9563DD0AEE189B6873573659C8C9CB3EE6E057873EDBD0932754A11FE`；并发/时间/场景人工复核 `concurrency-time-scenario-corrections.md` SHA-256 为 `81F3F695B032EB8230882055F449F2E1BC61D5D4BCAB1380F6BF320EF1170298`。Gameplay topology/review root 为 `843e0c8d139f723d546169d5cb511c834c6df3a82fc705ee72b9e208317cb072` / `f58453e839fd633db0f6cff5a10a3f920318803c37aa5e348705ffca0828f38c`；FixedPopulation 为 `07389da7a39f69abd4fbca960e37a251030bfb8c0700970b1bb2861a7220bd2c` / `33ef79e729702de88f91ce8a97cd8bccbd037a24fbd3c40756d822ba19eba8d1`。

Beam 节点因共享候选缓冲与未完成 damage commit 仍为 `DisabledUnsafe`；LifeLink source-death 因 destroyed-source policy 未定义仍为 reviewed disabled blocker。两者都不计为业务合同完成，显式重新绑定到 production slot 会在 Seal 失败。

每帧只创建一次不可变 `TimeContext`，统一 raw、real、enemy、combat、effect、build、global delta、真实 effect clock 以及 turn、frame 和 phase。effect clock 只能在 scheduler/GameManager 构造时选择，Seal 后不可变；primary effect node 与 legacy adapter 都消费同一 `EffectDelta + EffectClock`，补充 clock 节点跳过已由 primary 消费的时钟。节点只能从受限 `NodeExecutionContext.Delta` 获取其声明的时间域；配置明确为 turns/frames 的 Burrow duration/cooldown、Fear duration 和 enemy channel timer 则声明 `TimeDomain=None`，每次 Tick 固定减一，`dt=1`、`dt=0.016` 与 bullet-time 下均保持原回合数。`SkillBuffGroup` 的可变 delta/fallback 与 `PostDeathGroup.Phase` 已移除。公共 group facade 保留无状态兼容调用，生产 graph 不调用 facade，legacy scheduler 仍只通过 scheduler-owned `TimeContext` adapter 执行一次。`PhaseContext` 继续作为阶段单一事实源，离开 Wave 的 stale request 清理、Build allowlist 与 Wave combat 合同保持不变。

执行模式只能在 scheduler 构造时选择，默认 graph；legacy 与 graph 都要求预先 Seal，并推进一致的 frame identity 与 `LastTimeContext`。完整 Registry composition 的两种模式已有同帧状态、展示事件、死亡与奖励等价验证。Blinker 的 Pathfinding 缺失策略显式为 `NoOp`，不会再从 setter/null fallback 隐式推导执行顺序；Sapper 由 Registry 显式构造和接线。

benchmark composition 由可执行测试锁定：mode 2 保留 manual merged loop 作为性能下界，不作为 Registry/FrameGraph wiring 证据；mode 4 的固定 10K 敌人 ×500 帧负载与 mode 5 的完整局都通过单一 `BenchmarkCompositionFactory`，调用 `SystemRegistry.CreateAll → WireDependencies → AssignToGroups` 生成 sealed `ProductionRegistry` graph。mode4 使用不可变 `FixedPopulationBenchmark` 场景，图中保留 `spawning.wave.update`，但由 graph gate 禁止生成和 WaveStart；真实 500 Tick 回归逐帧断言人口保持 10K、callback/presentation 均为零。mode5 使用 Gameplay 场景并保留真实波次。两者 topology/review root 与 marker 明确不同，mode4 marker 包含 `Scenario=FixedPopulationBenchmark;WaveSpawning=Suppressed;Population=10000;WaveStart=Suppressed`。01:50-01:51 的 `46224/9585/5840 FPS` 与外部 `capacity-evidence-20260831T175006511Z` 写入重叠，全部是 invalid overlap sample，不用于门禁或授权结论。

生产代码在下列窗口之后继续变化，因此表内数据与 DLL hash 全部只保留为历史样本，不能作为当前实现的性能授权；当前方案 2 窗口另行记录，允许运行门禁并必须重新采集身份与样本。

| 模式 | 全部有效样本 FPS | 统计值 | 相对直接父切片 | 相对 M0 | 判定 |
|---|---|---:|---:|---:|---|
| mode 2 | 47220 / 47073 / 45227 / 45272 / 46671 | 中位 46671 | +4.43%（44689.6） | +212.12%（14953） | 旧 DLL，当前门禁无效；manual composition |
| mode 4 | 9768 / 9675 / 8675 / 9703 / 9256 | 中位 9675 | +2.18%（9468.8） | +25.67%（7699） | 旧 DLL，当前门禁无效；manual composition |
| mode 5 | 6204 | 单次观察 6204 | -0.63%（6243.2） | -15.50%（7342） | 旧 DLL，仅历史观察；从不作为当前阻塞门禁 |

没有筛选有效样本。第一组 mode 4 `8832/9709/9669/9807/9672` 在 `20:37:40Z-20:37:45Z` 运行时与 integrated final manifest/diff-check 的 `20:37:41Z` 写入重叠，因此五个日志全部保留并标为 invalid，不参与门禁计算；更早 `46224/9585/5840` 仍是 capacity overlap invalid sample。该段属于历史证据，不限制方案 2 当前复跑；mode 5 继续按授权仅观察，本记录不宣称统一门禁通过。

gate4 双轴通过后的最终有界性能尝试位于 `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\frame-graph-time-context-perf-final-20260901T011250646Z`。窗口先通过 10/10 静默观察与 623 个 candidate/control JSON 输入逐项 hash 校验，4 个声明 warmup 和 28 个 paired 样本均 exit 0、`ExternalOverlapCount=0`；但仓外 runner 的汇总表达式 `Where-Object Mode-eq$mode` 返回空数组，使 `paired-summary.json` 错写 0 ratio 并以 exit 2 结束。按“任何脚本错误整组作废”口径，该目录为 `INVALID`，不得作为性能授权。保留样本的诊断复算为：mode2 candidate 中位/最小 `41127/40237`、paired 中位 `1.041825`、相对父切片 `-7.972%`；mode4 candidate 中位/最小 `7685/6956`、paired 中位 `0.856626`、相对父切片 `-18.839%`。这些数字既因 runner 错误作废，mode4 stdout 又是旧 manual-system-chain composition，不能作为当前生产 graph 证据。正式相对门禁以 M0 `14953/7699` 为基线，直接父切片 `44689.6/9468.8` 只作附加信号。仓外 runner v5 位于同目录，SHA-256 `3565D006A83948E98AFBFCC5CA7062F06AC5F4F04DCF78009F827AAF2447276F`：强制 `Pairs==7`，mode2 只接受 `manual-merged-loop:v1`；mode4 分别锁 candidate/control 的真实 FixedPopulation topology，并精确要求 `Scenario=FixedPopulationBenchmark;WaveSpawning=Suppressed;Population=10000;WaveStart=Suppressed`；mode5 candidate 精确要求 Gameplay topology 与 `WaveSpawning=Enabled;Population=Dynamic;WaveStart=Enabled`。candidate/control DLL 与 candidate/control/M0 input manifest 不一致时 exit 1，汇总非空/7-pair 失配时写 `INVALID-summary.json` 并 exit 2。v5 已通过 parser 与 `Pairs=6 → exit 1` 负测，尚未运行性能样本；v4 保留为历史且不再用于最终窗口。

此前 `frame-graph-time-context-20260831T214218800Z`、`frame-graph-time-context-20260901T000312189Z` 与 gate5 的 build/test/diff/DLL 证据均早于本轮并发、scenario、timer 和 validator 返修，不能作为当前门禁。当前非性能原始日志位于 `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\frame-graph-time-context-gate6-20260901T032847989Z`：定向 graph/production/concurrency tests `106/106`，Core 与 EXE 均 0 warning / 0 error，全量 tests `1578/1578`，test rules 扫描 114 文件 / 1427 测试方法且 0 违规，diff-check exit 0；这些数字仍是历史快照。方案 2 当前门禁由最新 manifest 与原始日志裁决，mode 5 仍只观察、不阻塞规范门禁。
当前生产图复核更新（2026-09-01）：由于 dodge facts 与资源写集纳入封存身份，最新 Gameplay topology/review root 为 `48157d9be763dea081931e605f0ec7cf7b28519301da63a81e5bd9ccc23a1947` / `a7af17a23675ed882163e21313e90693aff244767be74a7b632a56ed0aa96d9c`；FixedPopulation 为 `1d58de2b31fa183aee5ce138559fc30aa3745f6943ed84fa98577a3a625892b4` / `4d44ebf3f9384e36f657eb2d5257e617286d0253d45474db0fee7c96a3d3f20f`。生产死亡 leak 事实必须通过 `FrameScheduler.QueueCurrentFrameEnemyDeath` 与同帧 resolve 闭环；source-death LifeLink 仍为 `DisabledUnsafe`。
最新 topology（串行护盾/累计伤害/元素/Vanguard 提交 closure 纳入后）：Gameplay `85f5d36d45eac52ec1271149e8da6fa9d6f6a56655b4d27b078153842320e8ff`，FixedPopulation `1c78440d8676d79ec71ca65f49aa45bcdfab28662f6a5a183f8418e8d5f8e96b`；reconciliation.csv SHA `D3044E01200E15C1D1B1428779EA4FF0E0A57043A5F9D484C97FBD8D6953DA87`，reconciliation.md SHA `D36809F3B34D437A7152C949EB5374EDB5B1D1F8F3F866520770FF512065924A`。

M6 strict 入口闭环后，`combat.hero-skill.update` 保持原节点 ID、顺序、依赖与串行语义，只把访问声明从 cooldown-only 扩展为读取 `EntityLifecycle/EnemyHealth` 并写入 `DamageRequests/ResourceRequests/GameplayEvents`；生产图仍为 193 个节点、33 个 `DisabledUnsafe`，没有新增或删除节点。重新封存的 Gameplay topology/review root 为 `0673aea3dfd0e07b3937a058ee1baa4ce30f4a37655e9fa72fc097a683a553b2` / `faac9f641daf06598a0269518d482475d1f14d1ed9a2fea7146d19dd0c421546`；FixedPopulation 为 `f4d99c36cf616eb2d4310b85f6ce227cf8e7e807fc195d88a806c226af9e6715` / `1079c3c7e49862731d334a88fb8a8a6f215dd34befa88afe28b596321c4e9a18`。

最终并发阻塞收口证据位于 `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\frame-graph-time-context-final-p1-20260901T042628835Z`。`TowerAttackSystem` 的塔并行段只向当前 active-tower index 独占的 collect buffer 写入 damage/on-hit 等请求；屏障后按活跃塔索引与塔内请求顺序合并，N-hit shield 消费、`EnemyRecentDamageSum/Frame` 累计、`EnemyElementStatus/Timer` 合并及 Vanguard 转移均由串行 damage commit 唯一执行。16 塔同时攻击同一敌人的真实 `Update` 回归连续 12 轮验证三层护盾、130 累计伤害、Fire/Ice OR 与 max timer、32.5 Vanguard 转移完全一致；定向测试 `96/96`，Core/EXE 0 warning/0 error，全量测试 `1579/1579`，test rules 扫描 114 文件/1428 测试方法且 0 违规，diff-check exit 0。28 个包含 `Parallel.For` 的 system 已复扫，active 路径未发现 `ConcurrentBag`、`ThreadLocal.Values`、`TryTake`、worker 内共享 List/lock/Random/callback；Beam 的两个 lock 站点仍只存在于 `DisabledUnsafe` 节点，source-death LifeLink 同样保持 blocker。Core/EXE/Test DLL SHA-256 分别为 `DB4EFA032ED5BD0BA8988A9A18EF9806D2F8A452F18CFB593F4639329DE5FFD7`、`33A2B75BF1F9FA5BADEC30C74716773A7BE07E4F634BCC3541D59BE67D1564D2`、`DC9AA74339210DCEFCBDA047434A52B367A87F21540E81E10AB3641CF0485435`。本轮未运行 mode2/4/5，未提交或暂存。

# ECS + GAS 迁移：编排、内容和收口阶段（M5-M8）

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

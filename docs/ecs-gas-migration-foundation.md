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

### 2.1.1 M0 前置清单

- [ ] 运行全套仓库门禁并保存完整日志（不只记录 FPS）；
- [ ] 记录 `git status`、当前 commit、未提交文件和配置版本；
- [ ] 确认 Core、EXE、Tests 的 .NET 版本以及 Unity `BattleDriver` 使用的 DLL 版本；
- [ ] 确认 mode 2/4/5 的 system composition 是否与生产一致；
- [ ] 备份 golden 场景输入、输出、事件序列和性能结果；
- [ ] 运行 `tools/inventory-ecs-gas-migration.ps1` 生成第一版台账；
- [ ] 给台账确认“定义存在但未启用”的系统（当前至少包括 `HitTrigger`；`Mark`/`DeathMark` 的状态需以本次 Registry 扫描为准）标上 `disabled` 状态，不把 getter 测试当作生产接线证据。

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
4. Weather/Meteor 直接伤害、已修复的死亡入队契约和后续 Resolver 迁移；
5. 护盾、元素转换、免疫、I-frame 和下限；
6. Effect/实体 ID 回收；
7. 同一帧 14 次命中后的计数和触发顺序。

另外单独覆盖 BuildPhase：当前生产 Registry 明确装配 `Skill`、`AutoSkill` 和 `GlobalSkill`，BuildGroup 会执行三者。`SkillBuildBoundaryTests.BuildPhase_PublicCastIsRejectedAndCannotReachFirstWave` 使用真实 Registry/FrameScheduler 验证公开 `CastSkill` 的攻击性能力在入口早拒绝，不扣 mana、不设 cooldown、不执行效果；并验证已在 Wave 产生的旧 damage request 在 BuildGroup 结束时由 `RejectPendingSkillDamage()` 同边界拒绝并清空双缓冲，pending/rejected/consumed、HP、死亡、奖励、渲染副作用和 Resolver pending 均符合预期，首个 Wave不会重放。`SkillBuildBoundaryTests.WavePhase_PublicCastIsConsumedByFramePath` 验证合法 Wave 请求由真实帧路径消费并造成伤害；未绑定的 `CastSkill`、`AutoCastBestSkill`、`TryActivateGlobalSkill`、`CastChainHealPublic`、Passive、HeroSkill、TowerActiveSkill 和公开 AoE CC 入口全部拒绝且无资源/冷却/效果副作用。`DirectResolveInBuildContextRejectsPendingDamage` 覆盖直接 resolver 调用。AutoSkill 只调用 SkillSystem，因此与 SkillSystem 共用同一 allowlist；`CastChainHealPublic` 仅在绑定 Build/Wave 上下文时作为资源恢复能力可用。所有非 Wave tick 都清理 Skill damage queue 和未消费的 GlobalSkill 输入，不能把请求带到下一 Wave。后续仍需为非伤害能力补充统一 GAS contract 和完整覆盖测试。

这里的“前置”是 M0 的第一批工作，不是进入 M0 之前必须完成的代码重构。当前 BuildPhase 伤害边界已冻结并由生产代码执行；后续 M1 结构改动和任何 cutover 仍需沿用该合同，不能把“不要立即进入 M0”理解成先改生产路径。
当前 `game_config.json` 没有 `GlobalSkills` 配置，Meteor 的 BuildPhase 测试需要显式注入测试定义；这说明默认生产路径暂未启用 Meteor，不代表 BuildGroup 的早退语义可以忽略。

阶段上下文合同由 `PhaseContext` 显式表达：Unbound、Intermission、LevelComplete、GameOver 和其他未定义状态拒绝全部公开能力入口；Build 仅允许 Skill/AutoSkill 的 Heal、Shield、ChainHeal、TimeRewind 以及 GlobalSkill 的 EmergencyHeal、GoldBurst；Wave 才允许 combat。HeroSkill 和 TowerActiveSkill 当前只有 combat stub，因此仅 Wave 可触发。Passive Skill 复用同一 Skill allowlist。各能力系统的阶段写入口只在 Core 程序集中可见；交互游戏的 `GameManager.Initialize()` 和完整局压测通过 `FrameScheduler` 同步，并由真实组合回归钉住。mode2/4 是 Core 程序集内的手工 benchmark harness，为保持固定 benchmark 定义而直接把 `SkillSystem` 的 internal `PhaseContext` 设为 Wave；它们只证明各自测量路径选择了合法战斗上下文，不是生产 Registry、FrameScheduler 或声明式帧图接线证据。此切片只统一入口、冷却、资源和现有效果副作用边界；Hero/Tower effect dispatch、非伤害能力的声明式 GAS definition、统一 rejection event/count、`FrameScheduler` 对四个能力系统的重复显式广播，以及 `DamageResolver`/`ResourceResolver` 重复的 `RejectAllPending` 清理结构，仍是后续架构判断项，不能据此宣称 FrameGraph、TimeContext 或能力迁移完成。本文测试数量和性能数字均为历史记录，实际门禁以当前工作树运行结果为准。

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

当前快照的候选扫描结果必须带口径，不能把一个数字直接称为“伤害路径”：

| 扫描口径（Core/Systems，排除测试） | 当前结果 | 解释 |
|---|---:|---|
| 所有 `EnemyHealth[...]` 原始索引匹配 | 145 行 / 45 个文件（151 次 occurrence） | 同时包含读、判断、初始化、治疗、写入和注释，不能称为“直接写”或“伤害路径” |
| 严格的 `EnemyHealth[...] -= ...` | 29 行 / 19 个文件 | 包含 `ComponentStore` 的 authority 和 `MovementGroup`，不含等价的 `old - damage`/`newHp` 赋值 |
| 所有 `EnemyHealth[...]` 赋值或复合赋值候选 | 59 行 / 37 个文件 | 混有初始化、生命周期、治疗、恢复、基准 harness 和真正伤害，不能直接当路径数 |
| `ApplyEnemyDamage` 生产调用点 | 5 个 | `EnemyAI` 两处、`PlayerTowerAttack`、`Skill`、`TerrainZone`；定义、重载转发、注释和测试不计入 |

审查报告中的“145 行直接写”实际对应 Core/Systems 的原始索引匹配，包含读取和注释；“23 次调用”则可通过把测试调用和重载转发混入统计得到。两者都不能作为生产伤害来源数。把等价减法、置零、禁用系统和已注册系统分别列出后，只能说当前有数十个候选来源；完整的语义路径数必须等 M0 分类，不能由 grep 行数代替。M0 台账必须保存 raw occurrence、生产方法、状态（active/disabled）和唯一 writer，避免把访问行、写点、文件数和路径数混在一起。
同理，不能用 5 个 `ApplyEnemyDamage` 调用点与 29 个 `-=` 写点直接计算“84% 绕过”；两者的统计单位不同，比例必须基于同一组已定义的语义伤害请求。

推荐使用只读脚本 `tools/inventory-ecs-gas-migration.ps1` 快速生成候选清单。脚本只做静态候选统计，不替代人工审查：它应输出生产方法名、文件和行号，并将写入点分成 `Init`、`Resource`、`DamageCandidate`、`Unknown` 四类；分类不确定的项必须保留在 Unknown，不能为了让数字好看而自动排除。
脚本对行注释有基本过滤，但不是 C# 语法解析器；块注释、预处理分支和跨行表达式仍可能出现在候选清单中。`disabledDefinitions` 只探测少数已知系统，完整的 active/disabled 状态必须由 M0 人工确认并记录。

Windows PowerShell 5.1 的最小用法是 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/inventory-ecs-gas-migration.ps1 -OutputPath artifacts/gas-migration-ledger.json`；安装 PowerShell 7 后也可把入口替换为 `pwsh`。输出至少包含 `generatedAt`、`commit`、`filesScanned`、`enemyHealthAccesses`、`directWrites`、`applyEnemyDamage`、`damageLoops`、`abilityEntrypoints`、`effectTimerOwners`、`registryProperties`、`registrationModel`、`groupAssignments`、`nullableGroupSlots`、`registryInjectors` 和 `disabledDefinitions`。M7 以后当前注册事实以 schema-v3 `registrationModel` 为准；旧 `groupAssignments`/`registryInjectors` 文本扫描只为历史消费者保留，不能再用其 0 值代表生产未接线。`OutputPath` 是可选的；不传时只打印摘要，不修改源码或配置。

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

### 2.3.1 建议的 BuildPhase 决策模板

在产品语义尚未确认前，采用以下默认候选，M0 由设计最终签字：

| Ability/Effect 类别 | BuildPhase 默认行为 | 必须验证的结果 |
|---|---|---|
| 资源操作、商店、建设和 UI 冷却 | 允许，在 Build commit 提交 | Mana/Gold/冷却不跨帧丢失 |
| Passive 属性聚合 | 允许，在声明的 AttributeAggregate 提交 | 下一次可见边界正确 |
| 伤害、投射物、攻击性 DoT、战斗性召唤 | 默认拒绝，产生 `AbilityRejected(PhaseNotAllowed)` | 不产生 HP 写入、DamageRequest 或死亡队列 |
| 明确允许在 Build 执行的战斗 Ability | 只有图中存在 Build Resource/Death commit 才允许 | 当帧完成唯一 death queue commit，下一帧不抛保护异常 |

无论选择哪一列，都要补充 `BuildPhase_CombatAbility_IsRejectedOrCommitted`、`BuildPhase_NoStaleDamageRequest` 和 `WavePhase_CombatAbility_Commits` 三类真实帧测试。请求被拒绝时必须记录原因并按定义丢弃；不能静默滞留到下一 Wave。

### 2.3.2 时钟映射表

M0 要为每个周期 Effect 选定一个时钟，而不是由调用方临时传入 delta：

| ClockId | 当前来源/映射 | bullet-time 规则 |
|---|---|---|
| `Build` | BuildGroup 的阶段 delta，仅用于允许的资源/冷却规则 | 不受敌人侧减速影响 |
| `Enemy` | `enemyDt`，覆盖 PreGame/Spawning/AI/Movement/Terrain 等敌人阶段 | 按当前 enemy time scale 减速 |
| `Combat` | `combatDt`，覆盖 Combat/SkillBuff/PostDeath 的战斗阶段 | 保持玩家/塔侧全速，除非定义另行声明 |
| `RealTime` | 外部 fixed timestep/输入时钟 | 不受模拟 bullet-time 影响 |
| `Global` | 全局时间缩放节点 | 只按明确的全局规则变更 |

至少补充“时停期间 Poison 是否 tick”“塔攻击是否继续”“Weather 与 Wound 是否同一 clock”的行为测试。

### 2.4 M0 退出门槛

- 基线命令都有可复现输出；
- golden 场景可从真实帧入口运行；
- 语义表每项都有明确选择和测试样例；
- 台账完成首次记录；
- benchmark 覆盖差异已列出并有修复计划。

在进入任何结构性 M1 改动前，以下三项 P0 必须已完成并留有证据：BuildPhase 战斗请求的真实失败/提交测试、`GameplayEffectDef` 使用点和兼容 facade 的影响范围清单、台账脚本可重复生成报告。M0 可以先做这些只读/测试工作，但不能以“正在收集基线”为由提前切流。

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
迁移测试应对现有枚举底层值做快照，并验证组合 mask 的 `HasFlag`/按位与结果；只有新增值才允许追加，不得重排或把 `True` 当作普通可抗性类型。
当前 `DamageImmunityFlags` 只声明 Physical/Magic/Fire/Ice/Lightning，`True`（32）按约定绕过 mask，Holy（64）则使用独立抗性/分支；这属于必须先冻结的兼容语义，不能在 Catalog 迁移中顺手补位或重编号。

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

### 3.3.0 M1 的真实范围：结构拆分而非单纯加 adapter

当前 `GameplayEffectDef` 同时保存静态字段和 `RemainingTime`、`TicksRemaining` 等实例计数器，`AppliedEffect` 又嵌入整个可变 Definition；`BuffSystem` 会直接修改这些嵌套字段。`RefreshDuration` 虽被 legacy 注释称为 runtime 字段，实质是由 `StackingBehavior` 派生的重复策略，终态应归一为 immutable Definition 中唯一的 `stackingPolicy/refreshPolicy`，由 runtime 执行而不是作为当前实例状态保存。这个事实意味着 M1 的 GAS 部分是一次需要完整影响范围清单的结构拆分，不能按“新增几个类型、旧代码原样继续用”估算为低风险小改动。
其中 `RefreshDuration` 在当前生产代码里主要由构造/工厂写入，实际刷新分支按 `StackingBehavior` 直接修改 `RemainingTime`；拆分时必须把刷新规则归一到 Definition，把倒计时和规则执行放在 Effect runtime，不能只把字段机械搬家。
当前生产代码对这些类型的引用需要按方法去重后重新统计；粗扫约为十余处，测试、注释和兼容构造另计。“20-30 处”可以作为待核查的工作量上界，不能当作已经验证的事实。

建议把 M1 的 GAS 工作拆成三个子工作包：

1. **M1a 冻结与扫描**：列出所有 `GameplayEffectDef`/`AppliedEffect` 构造、读取和写入点，尤其是 `BuffSystem` 的 timer/stack 逻辑；冻结旧 public facade，不再新增对运行态字段的依赖；
2. **M1b 定义/实例分离**：引入 immutable `GameplayEffectDefinition`、`ActiveGameplayEffect` 和转换 adapter；旧 `GameplayEffectDef` 暂作为明确的 `Legacy` wrapper，保证 Core DLL/测试可编译；
3. **M1c caller 切换**：按 DoT、控制、技能初始化和尸体效果逐类改 caller；当生产代码不再读写旧运行态字段后，才删除 wrapper 中的运行态成员。

因此 M1 的风险级别应标为“高（L）”，而不是仅有 ID/Catalog 的增量工作。兼容 facade 使编译和行为可以渐进切换，但不改变“最终必须完成类型拆分”这一事实。

### 3.3.1 Generation 访问规则

`EntityHandle` 是跨帧 contract，方法内短暂使用的 index 不是。M1 要同时落地三层保护：

当前代码仍以裸 `int` entity id 为主；下面的 generation 规则是 M1 的目标合同和未来风险控制，不是声称当前已经存在 generation 校验。阶段 0 的清理修复只能阻止已知槽位继承，不能替代句柄代数。

- 所有跨帧字段、Command、Event 和 ActiveEffect 引用只能保存 handle，禁止保存裸 `int`；
- 所有 `TryGet`/写入入口验证 index、generation、active 状态，失败返回明确的 stale/invalid 原因；
- 架构测试扫描新增代码中的持久化 `int entityId` 字段和未经过 `TryResolve` 的外部访问，允许在同一帧局部循环中使用的 index 必须有注释或 contract 标记。

补充压力测试：快速创建/销毁/回收同一批实体，同时投递旧代数的 Effect、Damage 和 Event，确认新实体不继承旧状态，旧请求只被拒绝一次并可观测。

### 3.3.2 Pool 和队列容量策略

当前 `ComponentStore.AddAbility`/`AddEffect` 在槽位满时会直接返回，缺少 `PoolExhausted` 诊断；这是已核实的静默失败风险，但不是“已经覆盖所有战斗场景”的事实。M1 必须先记录容量和调用方，再把返回值/诊断接入统一命令合同。

容量不能在 M1 里凭感觉写死。先用 M0 的完整一局和压力场景记录峰值，再按“观测峰值 × 2 + 固定保底”设置初始容量，并把容量写入配置摘要和启动日志。关键死亡/资源提交保留少量 reserved slots，避免非关键效果耗尽池后阻塞生命周期。

推荐的耗尽语义：

- 非关键 Effect/Tag/Trigger：拒绝申请，产生 `EffectRejected(PoolExhausted)` 或对应诊断，游戏继续；
- Damage/Resource/Death command：使用 reserved slots；reserved 也耗尽时，开发/测试模式 fail-fast，生产模式停止当前提交轮并产生明确 `GameplayLoopAborted`；
- 不在热路径隐式扩容，不覆盖旧槽，不静默丢请求；
- 槽位释放后下一帧可正常恢复，压力测试需验证“耗尽 → 释放 → 再申请”。

具体数值、reserved 比例和生产模式策略必须在 M1 退出前冻结并测试。

### 3.4 当前代码触点

- `Core/GAS/Attributes.cs`、`GameplayEffect.cs`、`GameplayAbility.cs`：从示例/混合 struct 演进为静态定义 contract；
- `ComponentStore_World.cs` 的 `AbilityInstances`、`ActiveEffects` 和 count/accessor：先保留兼容 facade；
- `ComponentStore.DestroyEntity`：继续清理旧 count，并在新 pool 中实现 generation 失效；
- `GameConfig.cs`、`GameConfigLoader.cs`：新增编译入口，旧 parser 暂不删除；
- `SystemRegistry`：只增加 Catalog/CommandSink 注入，不在 M1 彻底重写接线。

### 3.5 M1 退出门槛

- canonical 配置可编译且坏配置测试能 fail-fast；旧 `game_config.Skills` 只能通过显式 legacy importer，并对缺失字段给出诊断，不得静默伪装成完整新定义；
- 新 `GameplayEffectDefinition` 不含倒计时/当前层数；生产代码不再写旧 `GameplayEffectDef` 的运行态字段；
- 实体和效果 ID 回收不会继承旧 generation；
- command/event 在单线程和并行收集下 sequence 一致；
- feature-off golden 结果与 M0 一致；
- sparse pool、命令队列、事件队列的峰值和溢出有诊断；
- generation 访问、DamageType 位标志、Holy/True 特殊分支和 pool 耗尽/恢复测试通过；
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

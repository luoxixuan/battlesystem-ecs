# BattleSystem-ECS ECS + GAS 迁移规划总览

> 状态：执行计划（不代表迁移已经开始）
>
> 更新日期：2026-08-30
>
> 终态约束：[ecs-gas-final-architecture.md](ecs-gas-final-architecture.md)
>
> 审查依据：[skill-combat-arch-review.md](skill-combat-arch-review.md)

## 1. 文档职责

终态架构文档回答“最终是什么样”，本组文档回答“从当前代码怎样安全地走到那里”。

本文件只保留跨阶段信息：阶段依赖、迁移不变量、切流规则、统一门禁和文档导航。具体代码触点、工作包、测试和回滚动作分散在阶段文档中，避免单个文档成为无法执行的清单。

### 1.1 阶段文档

| 文档 | 覆盖范围 | 目的 |
|---|---|---|
| [foundation](ecs-gas-migration-foundation.md) | M0-M2 | 基线、语义冻结、Catalog/ID/Handle、属性和资源基座 |
| [combat](ecs-gas-migration-combat.md) | M3-M4 | Damage/Resource/Death Resolver、Gameplay Effect、Trigger runtime |
| [orchestration](ecs-gas-migration-orchestration.md) | M5-M8 | FrameGraph、能力和配置收口、注册模型、清理、性能与 Archetype 闸门 |

每份阶段文档都使用相同模板：进入条件 → 范围和产物 → 切流策略 → 退出门槛 → 回滚 → 删除旧路径的条件。

## 2. 目标和非目标

### 2.1 迁移完成的判定

迁移完成时应满足：

1. 运行时只有一个 ECS World；GAS 不拥有第二个 World 或独立 Tick。
2. Ability、Gameplay Effect、Modifier、Tag、Trigger 定义由 Catalog 编译并启动校验。
3. 跨帧的 Entity、Effect、Ability、Trigger 引用都带 generation。
4. 属性聚合、效果计时、叠层、触发器和资源写入各有唯一运行时所有者。
5. 所有伤害、治疗、护盾和资源变更通过 Resolver/生命周期模块提交。
6. 所有能力入口都进入统一激活管线，Targeting 与 Effect 正交组合。
7. 帧顺序、读写冲突、阶段门控和并行策略由可验证的 FrameGraph 声明。
8. 旧实现只在有明确公共兼容理由时保留；否则在观察期后删除。

### 2.2 不在范围内

- 不照搬 Unreal `AbilitySystemComponent`、UObject 对象图、Gameplay Cue 或网络复制模型。
- 不把移动、寻路、行为树、空间索引和渲染逻辑搬入 GAS。
- 不把 Archetype/chunk/DOTS 作为 GAS 迁移前置条件。
- 不一次性重写全部 `Systems/`，也不在同一变更中同时重排所有帧顺序和改变战斗数值。
- 不让 legacy 和新路径同时写同一份运行时状态。

## 3. 当前起点

当前可以继续利用的基础：

- `ComponentStore` 的 dense SOA 与活跃实体列表；
- `FrameScheduler` 作为唯一帧入口；
- “并行读取/收集值类型命令，串行提交共享状态”的两阶段模型；
- `TestWorld` 的隔离测试能力；
- JSON 技能、塔、怪物和波次数据源；
- `IBattleEventBus` 的逻辑到 Unity 展示 seam。

迁移起点的主要债务：

| 债务 | 终态方向 | 详述 |
|---|---|---|
| 伤害散落在数十个生产写点和语义来源（当前严格 `-=` 为 29 行/19 文件；完整路径数待 M0 分类，口径见 foundation） | 单一 `DamageResolver` | combat 文档 M3 |
| GAS 定义外形存在但没有真实属性运行时 | Attribute + Effect runtime | foundation M2、combat M4 |
| 技能入口和 shape switch 分散 | `GasRuntime.TryActivate` + registry | orchestration M6 |
| Group/Registry 依赖靠手工顺序 | FrameGraph + Installer | orchestration M5/M7 |
| 多套技能解析器和数据源 | 单一 typed Catalog | orchestration M6 |
| 可选字段按最大实体数铺开 | dense 核心 + sparse/capped pool | orchestration M8 |

审查文档记录的若干阶段 0 修复已经可能存在于当前工作树。不能按历史数字重复实施；M0 会重新记录实际 commit、测试和性能基线。

## 4. 阶段依赖

迁移阶段按以下顺序推进：

`M0 基线与语义冻结 → M1 Catalog/ID/Handle/Request/Event → M2 属性基座 → M3 统一伤害与资源 → M4 Effect/Trigger runtime → M5 FrameGraph → M6 Ability/Config → M7 注册与清理 → M8 稳定观察与性能决策`。

M5 的 `ISystem` 容器和节点元数据可以在 M1 后并行搭脚手架，但真正切换提交顺序必须等 M3/M4 的请求、事实和时钟语义稳定。M6 的静态 Catalog 编译可以和 M2/M4 的部分工作并行，能力激活不能早于 Effect Registry 和权威 Gameplay Event。

审查文档的 Stage 0-4 与本文的对应关系是：Stage 0 的剩余止血项归入 M0/M1，Stage 1 的数据和 `ISystem` 脚手架归入 M1/M5，Stage 2 的统一伤害管线归入 M3，Stage 3 的声明式编排和 Installer 归入 M5/M7，Stage 4 的技能/效果正交化归入 M4/M6。

### 4.1 阶段执行卡

规模使用相对级别而不是未经基线验证的人天承诺；M0 完成后再按实际代码量拆成迭代任务。

| 阶段 | 相对规模 | 主要风险 | 主要工具/证据 |
|---|---|---|---|
| M0 | M | 候选点需人工分类、基线不可复现、语义未冻结 | 门禁命令、台账脚本、golden/replay |
| M1 | L | 混合 Effect 定义拆分、ID/旧配置兼容、容量策略 | 影响范围清单、Catalog Validator、handle 压力测试 |
| M2 | M | 新旧属性重复贡献 | Aggregator 单测、computed projection 差分 |
| M3 | XL | 双重减伤、死亡/时序漂移 | shadow resolver、source cutover、真实帧集成测试 |
| M4 | L | timer/事件重复、递归链 | Effect/Trigger 测试、事件队列诊断 |
| M5 | XL | 顺序和阶段门控变化 | FrameGraph 校验器、legacy/graph replay、同构 benchmark |
| M6 | XL | 数据源不一致、技能行为缺失 | typed Catalog、Ability 级开关、Unity smoke |
| M7 | L | 删除过早、外部 API 破坏 | installer 审计、依赖架构测试、兼容 facade |
| M8 | M | 优化收益不足、观测盲区 | profile、soak、mode 2/4/5 A/B |

每张阶段执行卡都必须补充：负责人、具体工作包、输入/输出、进入条件、退出门槛、回滚 SOP 和删除旧路径条件。相对规模仅用于排期讨论，不是完成承诺。

阶段数量不是迭代数量，也不能从仓库静态信息推出“10-14 个月”的确定工期。M0 之后应按实际团队人数、并行度、Unity 联调窗口和每个切片的吞吐重新估算；附件报告给出的月份只能作为风险情景，不能写成项目承诺。

可并行的工作只限于不共享同一 legacy writer 的工作包：M2 的属性 schema 与 M5 的 adapter 脚手架可以在 M1 后并行；M4 的静态 Definition/Catalog 编译可以和 M3 的 resolver skeleton 并行；同一伤害队列、同一 Effect timer 或同一配置 parser 不允许多人并行改写，合并前必须先完成 source 级 golden 测试。

## 5. 跨阶段不变量

### 5.1 所有权

- 任一资源、属性缓存、效果计时器或伤害结果在任一时刻只有一个 writer。
- Shadow resolver 只能计算和比对，不能写 HP、护盾、事件、效果池或死亡队列。
- 旧字段保留期间只能作为兼容 projection，不能与新状态各自贡献一次数值。

### 5.2 可见性

- Request 是未提交意图，Event 是已经验证并提交的事实。
- 并行批次只读取节点开始时的快照；同批生成的修改不互相观察。
- 新 Modifier 默认在下一次属性聚合边界可见；同帧多段的即时可见性必须显式声明并拆成有序子批次。
- 开关只在帧边界读取和切换，不能半帧切流。
- Resolver 的规则只有一份，但请求可在 producer 声明的 `commitBoundary` 消费；统一 writer 不等于把 Weather、Wound 或敌方能力强行延迟到同一个晚期节点。

### 5.3 生命周期

- 所有跨帧句柄都带 generation；旧代数请求必须被拒绝并产生诊断。
- 销毁顺序是“失效句柄 → 清理效果/触发器/标签/命令/事件 → 完成死亡解析 → 回收实体 ID”。
- Pool、Command、Event 和递归提交都有容量上限；耗尽不能静默丢弃。

### 5.4 性能和确定性

- 并行段不得修改共享状态、GAS 稀疏池或事件总线。
- 命令使用值类型和确定性 sequence；提交排序有稳定 tie-break。
- 每个垂直切片都要比较状态、事件顺序和性能，不只比较最终 HP。

## 6. 统一切流协议

每个 source、Ability 或 Effect 按以下顺序推进：加 adapter 保留 legacy → shadow 计算 → 帧边界打开最小 cutover flag → 新路径成为唯一 writer → 观察期记录诊断和性能 → 满足条件后删除 legacy。

发生差异时，下一帧边界关闭该 flag 并恢复旧路径；不能在一次攻击或一次提交中途回退。一个来源切流完成前，其他来源可以继续使用 `LegacyDamageAdapter`，但不能直接绕过新 Resolver。

## 7. 统一门禁

每个阶段和每个垂直切片至少执行仓库现有门禁：

- `dotnet build BattleSystemECS.Core`
- `dotnet build`
- `dotnet test BattleSystemECS.Tests`
- `pwsh -File tools/check-test-rules.ps1`
- `git diff --check`
- `echo 2 | dotnet run`
- `echo 4 | dotnet run`
- `dotnet run -- 5`

此外必须满足：golden 场景和 deterministic replay 没有未批准差异；14-hit 批量触发、属性/效果叠层、实体 ID 回收和 stale handle 测试通过；mode 2/4/5 相对 M0 的任一回退不超过仓库规定的 ±5%；Core DLL 变化后 Unity `BattleDriver` smoke 通过；benchmark 使用与生产相同的 composition/FrameGraph，或明确记录覆盖差异。

任一硬门禁失败，阶段状态保持“进行中”，不得进入下一阶段。

特别闸门：M0 在 BuildPhase 战斗语义、`DamageType` 位标志兼容、死亡队列边界和各类时钟没有决策及真实测试前不得退出；M1 在 `GameplayEffectDef` 的定义/运行态拆分影响范围没有清单前不得按“低风险 adapter”推进。这里的 P0 属于 M0 内的只读/测试工作，不应被误读为“先改生产代码才能进入 M0”。

## 8. 迁移台账

M0 记录实际数量，后续每阶段更新。不要用删除行数代替完成度。

| 指标 | 终态目标 |
|---|---|
| 直接写 `EnemyHealth`/可变资源 | 仅 Resolver、Resource、生命周期例外 |
| 独立 damage drain loop | 0；所有请求进一个权威 Resolver |
| 技能入口绕过 `TryActivate` | 0 |
| 每类效果的 timer owner | 1 个 Gameplay Runtime owner |
| 定义中的运行态字段 | 0；定义与实例分离 |
| 新配置解析入口 | 1 个 typed Catalog 入口 |
| 静默 nullable Group 槽位 | 0 |
| 未声明的 FrameGraph 读写冲突 | 0 |
| legacy fallback | 0，或有书面公共兼容理由 |

## 9. Archetype 闸门

Archetype 不属于 M1-M7 的迁移必需步骤。M8 只在 profile 证明稳定组件签名的迭代和缓存 miss 已成为主要成本，且 sparse side table/capped pool 不能以更小风险解决时评估。无论最终采用与否，Ability/Effect/Trigger/Request/Resolver contract 不应改变；动态 Buff、层数和周期计时也不通过结构组件增删来表达。

## 10. 下一步

真正开始实施时，先打开 [foundation](ecs-gas-migration-foundation.md) 的 M0，保存实际基线和 golden 场景，再依据结果更新后续工作包。本总览不替代阶段文档中的退出门槛。

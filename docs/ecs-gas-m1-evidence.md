# ECS + GAS M1 Evidence

记录日期：2026-08-30。本轮在基础 contract、strict catalog、实体代数和 pool 诊断之上完成活动效果运行态迁移；业务伤害 writer、旧 parser、旧 ID、旧 queue 以及 `GameplayEffectDef`/`AppliedEffect` facade 仍保留。

## 已完成

- `Core/GAS/GameplayIdsAndHandles.cs`：`AbilityId`、`EffectId`、`AttributeKey`、`TagId`、`ClockId`、`EntityHandle`、`EffectHandle`、`ExecutionContext`、请求值类型、`GameplayEvent`、可复用 `CommandBuffer<T>`、确定性 sequence、immutable `GameplayEffectDefinition` 与 `ActiveGameplayEffect`。
- `Core/GAS/GameplayDefinitions.cs`、`GameplayCommandBuffer.cs`、`GameplayEventQueue.cs`、`EffectPool.cs`：定义、固定容量命令/事件队列和效果代数池。
- `Core/GAS/CatalogCompiler.cs` / `CatalogValidator.cs`：canonical `Data/Configs/skills.json` 优先合并按 ordinal 排序的 `Data/Skills/*.json`；缺文件、根类型、名称、shape、数值、周期/持续时间、重复 ID 和 alias 冲突均抛出包含路径与 ID 的 `CatalogValidationException`。旧 `game_config.json` 只可通过显式 `LegacySkillImporter` 使用。
- `GameConfigLoader.LoadStrictCatalog`：strict bootstrap 在调用 legacy loader 前完成 catalog 编译/校验，错误不会被通用 fallback 吞掉；默认 `LoadConfig` 路径保持不变。
- 精选能力 typed graph：canonical 20 条能力均至少有 effect 或 execution；Heal/Shield/Resurrect/CrowdControl/Resource payload 不再退化为 Damage。Chain Heal 的 Shield execution 保留 15/3s，Energy Shield 保留 50/5s，Slow Nova 使用 multiplier execution，Time Rewind 保留源数据 HealPercent=3。
- 当前验证证据：Catalog 过滤测试 24/24；Core build 0 warning/0 error；静态规则最近一次扫描 1258 个测试方法、0 违规；`git diff --check` 通过。
- `Core/ComponentStore.cs`：实体槽位 generation、`GetEntityHandle`、`TryResolve`，回收后旧 handle 返回 `StaleGeneration`。
- `Core/ComponentStore_World.cs`：旧 GAS 槽位满时返回 `false` 并累计 `AbilityPoolRejections`/`EffectPoolRejections`，不覆盖已有槽；释放后可再次申请。
- `Core/GAS/ActiveGameplayEffectStore.cs`：以 `EffectHandle` 直接定位按需分页的运行态槽，独占 remaining time、tick accumulator、ticks remaining、stack、clock、first-tick/catch-up、source-death 和 capture；immutable definition 与 legacy snapshot 分开保存。
- `ComponentStore`：每实体只持有效果 handle 槽；typed Add/Get/Update/Remove/Release 统一验证实体与效果 generation，目标销毁、能力重置、过期和重复清理均通过同一生命周期入口。旧 `GetEffect`/`SetEffect`/`AddEffect` 只做兼容投影。
- `BuffSystem`、`CorpseEffectSystem`、`TowerDemolishSystem`、`ElementalReactionSystem`、`SkillSystem`：生产 caller 已通过明确的 `LegacyEffectAdapter` snapshot 创建 typed runtime；计时、tick、叠层刷新和过期不再读取 `AppliedEffect.Definition` 作为状态 owner。
- `BattleSystemECS.Tests/Framework/GameplayContractTests.cs`：generation、pool exhaustion/recovery、DamageType 位值和 sequence 测试。

## 尚未满足的退出门槛

活动效果定义/实例分离与生产 caller 切换已完成；Buff/Skill/ElementalReaction/Corpse/TowerDemolish callers 通过 adapter 创建 typed runtime，计时和叠层由 `ActiveGameplayEffectStore`/`BuffSystem` 运行态字段负责。Effect/Trigger catalog 与执行器消费者检查仍待后续工作；Unity 工程路径 `F:\AI\BattleSystem-ECS-Unity` 当前不存在，无法执行 Unity smoke。显式 EXE build 成功但 .NET SDK 对既有 `net6.0` 目标报告 3 条 `NETSDK1138` 生命周期告警。全量测试本轮启动后未产生汇总，不能用定向结果替代。

## 回滚

Catalog 仍可通过不调用 `CatalogCompiler` 保持 legacy loader 行为。活动效果运行态已参与业务结算，回退必须将 typed caller、ComponentStore handle 槽和 runtime store 作为一个原子变更处理，不能只关闭 Catalog。未执行 commit、push、reset 或回滚。

## 确定性命令与容量诊断

`CommandBuffer<T>` 使用固定数组，不隐式扩容、不覆盖旧值；提交端可在串行边界通过 `TryMerge(..., comparison)` 合并各 producer 缓冲，并以 `GameplayEventOrdering.Compare` 按 sequence、事件类型、目标句柄、来源句柄稳定排序。`ProducerSequence` 将 producer/local 序号编码在同一个值类型中，线程调度顺序不会影响结果。

当前可重复压力场景的启动容量记录为：命令缓冲 256、事件队列 256、效果 handle pool 按 `MAX_ENTITIES * MAX_ACTIVE_EFFECTS_PER_ENTITY` 固定容量初始化，实际 `ActiveGameplayEffect`/definition/snapshot 以 256 槽分页按需分配。运行时仍应以压测采集峰值的两倍加保底重新配置；本轮尚未取得完整生产组合的峰值样本，因此不宣称容量门禁已完成。普通命令/效果耗尽返回明确 rejection 并保持运行；关键命令满载返回 `CriticalCapacity`，由上层决定开发 fail-fast 或发布 `GameplayLoopAborted`。

本工作包在同一构建产物上重跑 10K 敌/500 帧压力：原始输出保存于 `artifacts/capacity-runs-20260830.log`。三轮吞吐中位数为 mode 2 **13,959 FPS**（11,902/14,486/13,959）、mode 4 **7,949 FPS**（8,577/7,529/7,949）、mode 5 **7,239 FPS**（7,107/7,239/7,288）。现有 BenchmarkSystem 尚未创建新的 `CommandBuffer`、`GameplayEventQueue` 或 GAS `EffectPool` 提交链，因此该生产组合观测到的 command/event/effect 峰值与拒绝次数均为 **0**；这证明了压测入口与新管线尚未同构，不能据此计算容量×2 配置，容量门禁保持未满足。

新增真实组合 probe 使用 `ComponentStore`、`TowerAttackSystem`、空间网格、1 座塔、10,000 个敌人和 500 帧固定输入；展示 bus 只作 feature-off 适配，不写入任何战斗资源。原始结果见 `artifacts/capacity-probe-20260830.log`：`DamageRequest` 峰值 **1**、`GameplayEvent` 峰值 **1**，各自容量 2048、reserved 128、overflow 0；其余 Ability/Effect/Heal/Shield/Resource/Death producer 当前未接入，已明确记录。按 `capacity = peak * 2 + 32` 得到这两个已接入类别的建议容量 **34**，关键 reserved 保持 **128**（初始固定容量仍保留 2048 以覆盖未来 producer）。

`ContractAdapterProbe` 以真实有效实体句柄构造并入队每一种尚未接入的 request/event 合同，逐类 peak=1，仅证明值类型合同和容量路径可用，不冒充生产 producer 峰值；因此这些类别的生产容量门禁仍未通过。

复核修正：生产 probe 现以固定数组逐项记录 `OnDamageDealt`，不再重复使用最后一条事件；本次塔攻击组合实际每帧展示事件仍为单条，因此观测 peak=1 是生产系统输出事实而非空缓冲合成。`GameplayRequestSubmissionSession` 将 rejection 去重限制在提交会话内，成功提交失败时不会返回成功 fact。最终压测原始日志为 `artifacts/capacity-runs-20260830-final.log`，三轮中位数：mode 2 **14,946 FPS**（12,118/15,273/14,946），mode 4 **8,748 FPS**（8,748/8,782/7,885），mode 5 **7,531 FPS**（7,435/7,589/7,531）。

清空只重置当前条目，`OverflowCount` 默认累计；需要新诊断窗口时调用 `ResetDiagnostics`。效果池提供 `InvalidIndex`、`StaleGeneration`、`Inactive` 与 `Capacity` 诊断计数，旧代数解析/释放只会失败，不会作用于新槽位。

活动效果迁移的最终门禁：Core build 0 warning/0 error；显式 EXE build 0 error（既有 net6.0 EOL 告警 3 条）；串行全量测试 1391/1391；测试静态规则 1266 个方法、0 违规；`git diff --check` 通过。清理残留 `vstest.console` runner 后的三轮吞吐为 mode 2 **15,661/16,232/16,162 FPS**，mode 4 **8,801/9,214/8,738 FPS**，mode 5 **7,647/7,944/7,418 FPS**，相对基线 14,953/7,699/7,342 均无超过 5% 的回退。清理前 mode 5 曾出现一次 6,364 FPS 调度异常值，未隐藏该诊断记录。

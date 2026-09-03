# ECS + GAS M0 基线与语义冻结

> 首次记录：2026-08-30；在 `e0bb4f4` 后于 2026-08-31 复核，并在当前
> `715f9a2f85d75c2b1fbb76768dfa499ea091f86e` 集成树上收口。本文记录历史 M0
> 基线及退出依据，不把后续阶段的实现或证据倒写成旧基线。

## M0 状态

M0 的基线、golden replay、容量/失败合同、语义表和 inventory 首次记录已经收口；
Build→Wave 边界现由 `SkillBuildBoundaryTests` 通过真实 `GameManager.Initialize` 和生产
composition 验证，不再依赖复制 callback 或手工装配的替代测试。2026-09-01 的 M0
证据仍是历史基线，其中 mode 5 只有观察豁免，不能改写为规范性能 PASS。

当前树已经包含后续 M1-M8 工作，不能把当前生产实现冒充 M0 当时的 baseline。最近完整门禁
证据目录为 `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m8-player-damage-concurrency-20260903T031142Z`，
记录 1805/1805 full tests 和 29/29 focused tests，但它早于 `715f9a2` 的跨 resolver
共享提交锁修复。按本次用户要求，`715f9a2` 之后没有重新运行 build、test、rules、soak、
capture、code review 或 diff-check，因此本文不声称当前 HEAD 获得 fresh PASS。mode 2/4/5
仍为 `DEFERRED`，Unity 仍为 `UNAVAILABLE/BLOCKED`；完整 M8 phase exit 也未宣称完成。

## 基线证据

文档旧引用的 `artifacts/m0-baseline-20260830-031715/`、`artifacts/m0-final-gates-20260830-0715/` 和 `artifacts/benchmark-final-20260831.log` 在当前树不可复核；整个 `artifacts/` 被 `.gitignore` 排除且 `git ls-files artifacts` 为空。这些路径仅是历史引用，不再声称本机仍存在或由仓库交付。

本轮稳定原始证据位于工作树外：`C:\WorkSpace\AI\battlesystem-ecs-gate-logs\baseline-evidence-gates-20260831T170634542Z`。环境记录 `environment.txt` 的 SHA-256 为 `8854B37166EB4095946905EDB631E88C8C13B204BA2E842147229CB83D7CF417`；记录 commit `e0bb4f4d2439c8773f4823a03ce2d87b62512429`、Windows `10.0.19045`、dotnet SDK `9.0.311`、Core/EXE/Tests TFM 分别为 `netstandard2.1`/`net6.0`/`net9.0`。当前机器上 `F:\AI\BattleSystem-ECS-Unity` 不可用，因此 Unity 版本、`BattleDriver.cs` 和 Core DLL 版本/hash 均明确记录为 `unavailable`，没有以文档值代替实测。

历史日志中的 `d2d3b61` 数据保留如下，仅用于趋势背景：

| 项目 | 结果 |
|---|---|
| commit | `d2d3b61` |
| Core build | 通过，0 warning / 0 error |
| EXE build（仓根 `dotnet build`） | MSB1011：多项目歧义；命令不可复现，未改项目文件 |
| EXE build（明确 `dotnet build BattleSystemECS.csproj`） | 通过，0 errors，3 条现有 net6 EOL warnings |
| Tests（基线 / 最终） | 1325/1325；最终 1335/1335 通过 |
| test rules | 0 违规 |
| diff check | 通过 |
| mode 2 | 11,418 FPS |
| mode 4 | 6,244 FPS |
| mode 5 | 5,076 FPS，5/5 Victory，410 frames |

最新复跑：tests `1335/1335`；mode 2 `14,953 FPS`；mode 4 `7,699 FPS`；mode 5 `7,342 FPS`，5/5 Victory，410 frames。FPS 随机器负载波动，后续比较必须使用同一运行环境和该目录的完整日志。

### 2026-09-01 `e0bb4f4` 后复核

复核时工作树只包含本页、golden/capacity 测试和 inventory 脚本的未提交改动；未修改生产源码、`artifacts/` 或 `TestResults/`。可复现命令使用显式项目和 CLI benchmark 参数，避免根目录多项目解析及 stdin 尾随空格差异。

| 命令/证据 | 原始结果 |
|---|---|
| `dotnet build BattleSystemECS.Core/BattleSystemECS.Core.csproj --no-restore` | exit 0；0 warning / 0 error；log SHA-256 `8A33D52F4055B8F3BD70938B2B1B2BB6298D40AD02CD8CBE817DAEFD218583DE` |
| `dotnet build BattleSystemECS.csproj --no-restore` | exit 0；1 条旧基线 `NETSDK1138` net6 EOL warning / 0 error；log SHA-256 `E5B469125712AC0327620E55B9FAF452EBF61717D01926C08702F2E7AA1AAAF1`。M0 不修改项目策略；受保护 M6 commit `1afac05` 在整合树负责 0 warning |
| 受影响定向测试 | exit 0；19 passed / 0 failed / 0 skipped |
| `dotnet test BattleSystemECS.Tests/BattleSystemECS.Tests.csproj --no-build --no-restore --verbosity minimal` | exit 0；1474 passed / 0 failed / 0 skipped；log SHA-256 `7E477F15773534EB284E969F3E9E83939AC3EC171D6725D2F4830D0A05FCC8E3`。该结果不包含尚待整合的 production-bootstrap Build→Wave 合同 |
| `pwsh -NoProfile -File tools/check-test-rules.ps1` | 109 files；1349 test methods；0 missing asserts；0 constant asserts；0 violations；log SHA-256 `E99860670B3F675776E7C9BC51BA52F746E0298724A2BDAB4321301A5E7B0641` |
| `git diff --check` | exit 0，无输出 |
| `dotnet run --project BattleSystemECS.csproj --no-build -- 2` | exit 0；10,000 enemies x 500 frames；44,756 FPS；log SHA-256 `1850192168A4B9A687ADF59ED7B96AEF23839FD7BD5F28E2BC65AB8C75C5AED4` |
| `dotnet run --project BattleSystemECS.csproj --no-build -- 4` | exit 0；real system chain 10,000 enemies x 500 frames；8,787 FPS；log SHA-256 `761B3BC0555434811707645A858F4173AE98F9CE6E8D225F116B3548BCACE9FF` |
| `dotnet run --project BattleSystemECS.csproj --no-build -- 5` | exit 0；Victory 5/5；410 frames；77.31ms wall-clock；5,303 FPS；log SHA-256 `EDB163A4D28071FE40CD0C0F5A51DF2B212B9CF1ECA0A1C1427FD9DC1EB25705` |

本轮 FPS 是同机单次观察，不替换 `AGENTS.md` §7 的历史硬门禁基线。mode 2/4 高于硬门槛。mode 5 虽高于 2,500 绝对门槛，但相对文档最近历史值 7,342 约 -25.4%，超过默认 ±5% 范围，不能记为规范通过。

### mode 5 观察豁免

| 项目 | 记录 |
|---|---|
| authority | 2026-09-01 本次用户指令延续：“mode5 暂不纠结、先完成迁移”；授权 ECS + GAS 迁移继续复审并允许后续阶段提交 |
| 范围 | 只豁免本次 mode 5 相对值；不修改 `AGENTS.md` 门禁，不覆盖 mode 2/4；不适用于发布、建立新基线或后续性能验收 |
| 性质 | 非规范通过、非新基线；记录为观察债务，M0 退出结论必须携带此例外 |
| owner | 性能基准维护者负责复测，战斗架构维护者负责解释 composition 差异 |
| 触点 | `Systems/BenchmarkSystem.cs` 的 full-game composition、`Core/SystemRegistry.cs`、`Core/FrameScheduler.cs`、`Program.cs` mode 5 入口 |
| 解除条件 | 同一构建与同一 composition、空闲机器至少 5 轮，记录中位数和离散度；若仍超过 -5%，必须定位回退或由用户另行批准新基线 |

台账复核分别用 Windows PowerShell 5.1 与 `pwsh` 各连续运行两次；四份原始 JSON 位于上述证据目录的 `inventory-final/`，均为 461,824 bytes，SHA-256 都是 `A76CE1207812DD07EAF52E04573144A6BF2565AC1C09905A4DE73739B02C880B`。输出固定 source commit 时间、ordinal 排序、压缩 JSON、UTF-8 无 BOM/无尾随换行；`filesScanned=212`、扩展 surface files=837、`benchmarkComposition.status=manual-composition-gap`。扫描覆盖 PlayerCurrentHealth、Shield、Mana、typed Attribute/Effect/Trigger runtime、damage/resource writers、Gameplay/legacy event bridge、phase/state、ability/config parser/source、Registry/Scheduler composition；每个 surface 都有 `known/unknown`，Unity 为 `unavailable`，整体 semantic completeness 明确为 `unknown`。

四份 canonical 原始文件的稳定绝对路径如下，hash 均为上一段所列值：

- `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\baseline-evidence-gates-20260831T170634542Z\inventory-final\windows-powershell-1.json`
- `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\baseline-evidence-gates-20260831T170634542Z\inventory-final\windows-powershell-2.json`
- `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\baseline-evidence-gates-20260831T170634542Z\inventory-final\pwsh-1.json`
- `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\baseline-evidence-gates-20260831T170634542Z\inventory-final\pwsh-2.json`

安全负向验证位于 `inventory-safety-final/`：已有输出未带 `-Force`、tracked `BattleSystemECS.csproj` 即使带 `-Force`、以及非 Git 根目录缺失 commit 元数据三种情况均 exit 1；tracked 文件前后 SHA-256 不变，失败时没有生成输出。脚本只扫描并写调用者指定的单一输出文件。

mode 2/4/5 都是 `Systems.BenchmarkSystem` 的手工 composition；mode 4 调用实际 system Update 链，但三者都不通过生产 `SystemRegistry`/`GameManager` 完整装配。该差异是 M0 已知覆盖盲区，M5 前不得宣称 benchmark 与生产 composition 同构。mode 5 的日志还显示实际波次与完整战斗管线，但入口仍由 benchmark 自行接线。

## Golden 场景与证据

新增 `BattleSystemECS.Tests/Framework/CombatGoldenReplayTests.cs`，当前 16 条测试覆盖真实 `FrameScheduler.Tick`/SystemRegistry 入口及紧邻的 resolver/runtime 兼容合同。测试每次 replay 都创建独立 `TestWorld`，并用 `Assert.NotSame` 证明两次 Store 初态互不复用；snapshot 同时比较实际 active enemy count/IDs、HP、kills、gold、渲染事件顺序、完整 resolver facts（type/sequence/parent/source/target/owner）和 deferred request 计数。后续 M5/M7 的 legacy/graph parity 与 installer composition 断言由各自的 FrameGraph/production-flow 测试拥有，不计入 M0 golden baseline。

| 场景 | 证据 |
|---|---|
| 普通塔分层减伤、击杀、奖励、active 列表、事件顺序、fresh-store 确定性重放 | `TowerFrame_MitigationLayersEachReduceNonLethalDamage` 用四个独立 fresh world 锁定 control 100 → armor 87.5 → typed resist 62.5 → generic resist 50，逐层严格下降，删除任一层即失败。`WaveFrame_ReplaysOnFreshStores_WithExpectedKillRewardAndEvents` 使用相同注入公式与 45 HP/7 gold，锁定最终伤害 50、HP=0、active count/list、kills=1、`damage → killed → destroyed`；期望只从测试注入值推导 |
| BuildPhase GlobalSkill 拒绝合同 | `CombatGoldenReplayTests.BuildPhase_GlobalSkillIsRejected_WithNoHpOrDeathWork`；HP/active/kills/死亡事件不变，记录 `PhaseNotAllowed` |
| BuildPhase Skill/AutoSkill 真实 Registry 拒绝且无伤害工作 | `CombatGoldenReplayTests.BuildPhase_RegistrySkillAndAutoSkillAreRejectedWithoutDamageWork`；分别出现 `source=Skill`/`source=AutoSkill` 拒绝诊断，两个 resolver pending count 都为 0 |
| Build→首个 Wave 合同 | `SkillBuildBoundaryTests.BuildPhase_PublicCastIsRejectedAndCannotReachFirstWave` 和 `WavePhase_PublicCastIsConsumedByFramePath` 锁定拒绝请求不会跨阶段重放，合法 Wave 请求由真实帧路径消费；`GameManagerInitializationBindsStateMachineToAbilitySystems` 经过真实 `GameManager.Initialize` 验证生产 composition 的状态机绑定。pre-fix 失败日志仍保留在历史基线 evidence 中，只作修复前证据 |
| BuildPhase 明确允许的资源行为 | `CombatGoldenReplayTests.BuildPhase_EmergencyHealRemainsAllowed_AndChangesPlayerHealth`；玩家 HP 正向变化并产生 `ResourceChanged` fact，不产生 deferred damage |
| DoT/Weather 死亡入队 | `Sandstorm_LethalDot_QueuesDeathAndResolves`、`GlobalSkillMeteorTests` |
| Projectile/附加效果 | `ProjectileSystemTests`、`EnchantSystemTests`、`TowerAttackSystemTests` |
| 护盾、元素、免疫、I-frame、血量下限 | `DamageFormulaTests`、`ElementalResistanceTests`、`InvulnerabilityFramesTests`、`ExecuteImmunityTests` |
| 实体/效果 ID 回收 | `ComponentStoreTests`、`GameplayEffectTests` |
| 同帧 14 hits 计数/同目标 characterization | `CombatGoldenReplayTests.SameFrameFourteenHits_EmitsFourteenDamageEventsForSameTarget`，锁定 14 个同目标 damage event、HP=986、kills=0；`HitTriggerSystem` 仍 disabled |

普通塔仍从 legacy facade 进入，但 facade 已提交真实 `DamageRequest`。Golden replay 在击杀帧结束立即读取事实，锁定 `HitConfirmed -> DamageApplied -> DeathQueued -> KillConfirmed`、同一稳定 sequence、同 sequence 的 `ResourceChanged` 奖励事实和两个 pending count=0。更细的 reject/commit/flags 合同由 `DamageResolverGoldenTests` 和 `GameplayRequestSubmissionTests` 覆盖，不以最终 HP 代替 request 证据。

同输入的 golden 输出只包含稳定状态与提交事实；时间戳、FPS 和随机日志不纳入比较。后续结构改动必须在旧路径启用时保持这些事实、sequence 和事件顺序。

AutoSkill 的拒绝诊断 latch 是实例生命周期级别。生产 FrameScheduler 只有 `allowCombat:false` 的 Build caller，没有 `allowCombat:true` 的 Wave caller，因此不会按 Wave reset；测试只锁定连续 Build tick 的一次性诊断，不声称存在 phase reset。

## 容量证据取舍

删除的 `GameplayCapacityProbeTests` 在当前集成树中包含两个 probe：一个以 256 敌、120 帧
检查 production composition 的非零观测峰值，另一个只验证多类 adapter 各能入队一项。
前者的生产 composition、sealed graph、峰值和零 overflow 覆盖已由
`FrameGraphProductionFlowTests.FixedPopulationBenchmarkScenarioKeepsPopulationStableAndSuppressesWaveSpawning`
以及 `GameplayObservationTests`/`GameplayStabilitySoakTests` 承接；后者没有验证容量失败、保留项
或 handle/context 内容，不作为正式合同保留。更早曾写仓内 artifact 的 10,000 敌/500 帧
probe 版本也不恢复。

替代的 `GameplayCapacityContractTests` 使用全非默认 `GameplayEvent`/`EffectRequest`/
`ExecutionContext`，逐字段锁定 EffectHandle/definition/flags/parent/provenance/tag/owner、
ActiveEffect、context source/target/ability/effect/clock/sequence/owner/snapshot/provenance，且
rejected item 不替换 accepted item、overflow 精确 +1。另锁定触发器递归预算的
reason/remaining/sequence、持久 abort 与下一帧恢复；单测不写文件。

完整容量证据位于 `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\capacity-evidence-20260831T175006511Z`，汇总 `capacity-telemetry.json` SHA-256 为 `710164FBE9CD0F4844ACF76EE4526996ADC6D8EA976258E61A8407457CF24AE7`。真实 mode 5 完整局为 Victory 5/5、410 帧、5,303 FPS；当前 benchmark 不输出 Effect/Trigger/command/event/resolver pending 峰值，因此这些完整局字段明确为 `unavailable`，没有写成 0。仓外固定压力 harness 输入 SHA-256 `0196F1007118771F5C638434329C33DAEC8B4E7D4F93262B713578C8B811E66F`，观察到 Effect active peak 2,048（pool 800,000）、Effect event peak 2,048/8,192、Trigger input/seen peak 4,096（seen cap 8,192）、Command peak 4,096/4,096、GameplayEvent peak 8,192/8,192；后两者额外请求均拒绝且 overflow 精确为 1。压力值只表示固定合成输入，不冒充完整局峰值；resolver deferred 模式不是 public seam，pending peak 只观察为 0，不声称完成饱和测量。

## 语义决策表

| 项目 | M0 冻结选择 | 测试证据/后续约束 |
|---|---|---|
| Damage 顺序 | 先无敌/命中门控，再暴击/护甲/抗性/元素，护盾吸收后应用血量下限；死亡入队在 HP 归零后 | `DamageFormulaTests`、`ExecuteImmunityTests` |
| DamageType / DamageFlags | `DamageType.Holy=64` 是正式 legacy Holy immunity mask 兼容值，即使 `DamageImmunityFlags` 暂无同名成员也必须接受整数 64。优先级冻结为：非 True 类型先检查通用整数 immunity mask；命中 Holy 64 时整次拒绝、不得再计算抗性；未免疫时读取独立 `EnemyHolyResist`；True=32 绕过 immunity/护甲/抗性。M0 不新增 enum 名称，不改变该兼容语义 | `CombatGoldenReplayTests.HolyLegacyMaskBlocksBeforeHolyResistance` 同场景锁定 mask=64 时 HP 不变、清 mask 后 50% HolyResist 生效；另见 `HolyDamageTests.GetElementResist_Holy_ReturnsField`、`HolyDamageTests.PlayerTowerAttack_DamageTypeContract` |
| 元素转换 | M0 保留当前合并结果和主类型上报行为，不拆队列 | `EnchantSystemTests`；M3 决定 `DamageInstance` 表达 |
| Death authority | `DamageResolver` 在 GameplayResolve commit 后调用 `QueueEnemyDeath`；`FrameScheduler` 在 SkillBuff 后首次解析，在 PostDeath deferred commit 后再解析一次。奖励、`OnEnemyKilled`、销毁、`KillConfirmed` 只在 resolve 中发生；未闭合死亡工作不得跨 `BeginFrame` | `CombatGoldenReplayTests.WaveFrame_ReplaysOnFreshStores_WithExpectedKillRewardAndEvents`、`DamageResolverGoldenTests.BuffDotThroughScheduler_CommitsOnceBeforeDestroyAndPreservesAttribution`、`CombatResolutionTests.ResolveEnemiesKilledThisFrame_Idempotent`；后续改动不得增加旁路 sweeper 或把奖励提前到 request 阶段 |
| BuildPhase | 真实 Registry 中 GlobalSkill/Skill/AutoSkill 都会经过 BuildGroup；攻击、Meteor、攻击性 DoT 应拒绝并记录 `PhaseNotAllowed`，EmergencyHeal 等资源行为允许并产生 `ResourceChanged`。切到首个 Wave 前不得保留 `_skillDamageQueue`、deferred request 或死亡工作 | 独立 Build/EmergencyHeal 测试和 `SkillBuildBoundaryTests` 的真实帧、`GameManager.Initialize`/production composition 合同已覆盖；本次收口未重跑门禁，不把历史运行结果表述为当前 HEAD 的 fresh PASS |
| 属性聚合 | M0 不改变旧 getter/缓存；Add/Multiply/Override 的新解释器留给 M2 | 现有 GAS 定义测试仅作 facade 约束 |
| Effect source-death | `SourceDeathPolicy.Remove` 在 source 失效后、下一次对应 clock tick 前移除 effect；`Persist` 的 runtime-owned damage/resource effect 在 source 销毁后继续结算，并保留旧 source handle、owner 和 provenance。普通外部 stale request 仍拒绝 | `GameplayRuntimeTests.SourceDeathRemovePolicyClearsEffectBeforeNextTick`、`GameplayRuntimeTests.RuntimeOwnedPersistEffectTicksAfterSourceDeath`；该内部 missing-source 例外不得扩散到普通 request |
| Effect 重复、stack-key 与满层 | target 内以显式 `StackKey` 匹配，未配置时回退 `EffectId`；不同 EffectId 只要显式 key 相同，也正式视为同一兼容 effect 并复用首个 active handle。`None`/`DurationRefresh` 不加层，`MaxStacks`/`MaxStacksRefresh` 至多到 `MaxStacks`；满层再次申请不超 cap | `CombatGoldenReplayTests.SharedStackKeyReusesEffectAndRefreshesAtMaximumStacks` 锁定跨 EffectId、单 active、cap=2、满层；`GameplayRuntimeTests.TriggerStackAddsModifierPerLayerAndHonorsMaximum` 锁定每层 modifier |
| Effect 刷新 | 兼容分支冻结为：`DurationRefresh` 搭配任意非 None refresh 时重置运行态时长且不加层；`MaxStacks + Duration` 当前加层但不刷新；`MaxStacks + StacksAndDuration` 刷新；`MaxStacksRefresh` 每次申请（包括已满层）都刷新。刷新会重置 RemainingTime、TicksRemaining、TickAccumulator、FirstTickPending | `CombatGoldenReplayTests.EffectRefreshPoliciesPreserveCurrentCompatibilityBranches` 锁定前三个分支；`SharedStackKeyReusesEffectAndRefreshesAtMaximumStacks` 锁定满层 MaxStacksRefresh；`EffectRuntimeStateTests.StackRefreshUpdatesOnlyTypedRuntimeState` 锁定周期运行态重置 |
| Periodic / clocks | Effect 只能消费声明的 clock：Enemy 使用 bullet-time 后 `enemyDt`，Combat 使用全速 `combatDt`，RealTime 使用外部 delta，Global 使用全局缩放后 delta，Build 只在 BuildPhase 推进；Poison 属于 Combat，Weather 属于 Enemy，Wound 是 dt-free 状态转换 | `GameplayRuntimeTests.SchedulerTicksAllDefinitionDrivenGameplayClocks`、`GameplayRuntimeTests.BuildPhaseAdvancesBuildClockAndRejectsEnemyDamageInFrame` 和四条 `CombatGoldenReplayTests.BulletTime_*`；后续 producer 不得直接传任意 dt 绕过 definition clock |
| Periodic 首次 tick / catch-up | `Immediate` 在首次 tick 调用即额外结算一次，`NextInterval` 等满 period；`CatchUpAll` 补齐全部到期 tick，`OnePerFrame` 每帧最多一次，`SkipMissed` 每帧一次并清空积压 | `GameplayRuntimeTests.PeriodicFirstTickAndCatchUpPoliciesHaveDistinctResults`、`GameplayRuntimeTests.SkipMissedTicksOnceAndClearsAccumulatedDebt`；这些枚举语义已冻结 |
| Periodic magnitude snapshot | 当前兼容 catalog 只接受 Constant periodic magnitude；runtime 在 apply 时捕获 definition constant，显式 snapshot 参数优先覆盖，后续 tick 只读 `CapturedMagnitude` 并乘 stack count，不按来源属性重算。Attribute magnitude definition 继续拒绝 | `CombatGoldenReplayTests.PeriodicExplicitMagnitudeIsCapturedAtApplication` 同时锁定默认 2 与显式 7 的 capture/tick；`GameplayRuntimeTests.InvalidEffectDefinitionIsRejectedAtRegistration` 锁定 Attribute magnitude 拒绝 |
| 14-hit 同帧可见性 | 同帧 14 个 hit facts 全部在 Trigger consume 边界可见；EveryN=10 触发一次并保留 remainder=4，同来源只增加一层 effect。Modifier 在随后的 `AttributeAggregator.AggregateDirty` 边界才可见，不反向影响本批 14 hits | `CombatGoldenReplayTests.SameFrameFourteenHits_EmitsFourteenDamageEventsForSameTarget`、`GameplayRuntimeTests.TriggerFourteenHitsLeavesRemainderAndAggregatesOneSourceStack`；后续若改变可见边界，必须作为显式语义变更更新两条 golden |
| MaxHealth 资源行为 | 玩家与敌人都采用同一兼容选择：MaxHealth 降低时 CurrentHealth 裁剪到新上限；MaxHealth 上升时 CurrentHealth 保持原值，不自动治疗 | `CombatGoldenReplayTests.MaxHealthIncreaseDoesNotHealPlayerOrEnemy` 锁定双方从 max/current=25 提升 max=80 后 current 仍为 25；另见 `AttributeResourceContractTests.ResourceResolver_ClampsHealthAndMaxHealthRules` |
| 递归/容量 | 触发器每帧最多消费 `MaxEventsPerFrame`；超限在首个未消费事件处 abort，reason=1，remaining 含该事件，诊断写入不参与递归消费的 `AbortEvents`；`ResetFrame` 清本帧 sequence/reason/remaining/事件并恢复消费，累计 `LoopAborts` 保留。队列满时 accepted payload/handle 不得被 rejected item 覆盖，overflow 每次拒绝精确 +1 | `GameplayCapacityContractTests.TriggerFrameBudgetPublishesExactAbortAndRecoversOnNextFrame`、`EventQueueReportsCriticalOverflowWithoutDroppingAcceptedEvent`、`CommandBufferReportsCapacityOverflowDeterministically` |

## 时钟映射

| ClockId | 当前来源 | bullet-time |
|---|---|---|
| Build | BuildPhase 中 `BuildGroup` 与 typed runtime 的阶段 delta | 不受敌人减速；非 BuildPhase 不推进 Build effect |
| Enemy | `enemyDt`：PreGame/Spawning/AI/Movement/Terrain 及声明 Enemy 的 effect | 按敌人 time scale |
| Combat | `combatDt`：Combat/SkillBuff/PostDeath 及声明 Combat 的 effect | 玩家/塔侧全速 |
| RealTime | `_externalDeltaTime`，即进入 scheduler 前的 fixed timestep/input delta | 不受 Global 或 bullet-time 缩放 |
| Global | `UpdateTimeScale` 后的 scheduler delta | 只受显式 GlobalTimeScale 规则 |

时钟证据：`BulletTime_PoisonUsesCombatClock_AtFullTickRate`、`BulletTime_TowerAttackUsesCombatClock_AndHitsNormally`、`BulletTime_WeatherUsesEnemyClock_ScalingDamageByQuarter`、`BulletTime_WoundTransitionIsDtFree_AndDoesNotShareWeatherClock` 均从真实 `FrameScheduler.Tick` 驱动并锁定数值。EnemyWound 是 dt-free 状态转换，不与 Weather 共用 dt 缩放；该结论已冻结。

## Benchmark composition 修复计划

| 项目 | 明确计划 |
|---|---|
| 当前差异 | `Systems/BenchmarkSystem.cs` 手工 new/接线，未引用 `SystemRegistry`；台账 JSON 的 `benchmarkComposition.status=manual-composition-gap` 为静态证据 |
| 负责人 | 战斗架构维护者负责 composition factory 与 Registry 同构；性能基准维护者负责 mode 2/4/5 日志、fingerprint 和 ±5% 比对 |
| 代码触点 | `Systems/BenchmarkSystem.cs`、`Program.cs`、`Core/SystemRegistry.cs`、`Core/FrameScheduler.cs`；不由文档清单驱动运行时 |
| 阶段归属 | 基座阶段保持 benchmark 只读并持续记录差异；编排收口阶段建立 `BenchmarkCompositionFactory`，由 `CreateAll/WireDependencies/AssignToGroups` 生成 mode 4/5 scheduler。mode 2 保留为纯合并下界 |
| 验收 | 日志写入稳定排序的 group/system composition fingerprint；切换前后跑 golden 状态/fact/sequence diff 与 mode 2/4/5 ±5% 门禁 |
| 回滚 | factory 后保留 legacy composition 开关一个迁移窗口；任一 golden 差异、缺槽、mode 4/5 超过 5% 回退时立即切回 legacy benchmark，生产 Registry 不回滚 |

## GAS facade 影响范围

首次静态扫描的本机历史副本位于 ignored 的 `artifacts/gas-migration-ledger.json`；它不是 tracked 证据。可复核来源是 `tools/inventory-ecs-gas-migration.ps1`，输出可写到任意临时路径，不能依赖 docs 文本作为运行时输入。

- 构造/生产：`Systems/BuffSystem.cs`、`Systems/SkillSystem.cs`、`Systems/ElementalReactionSystem.cs`、`Systems/CorpseEffectSystem.cs`。
- 存储兼容 API：`Core/ComponentStore_World.cs` 的 `AppliedEffect[]`、`GetEffect`、`SetEffect`、`AddEffect`。
- 运行态字段写入：`BuffSystem` 直接更新 `RemainingTime`、`TicksRemaining`、`TimeSinceLastTick`、`StackCount`；定义中混有 `RemainingTime`、`TicksRemaining`、`RefreshDuration`。
- M1a 结论：旧 `GameplayEffectDef`/`AppliedEffect` 必须保留为 legacy facade；M1b 才引入 immutable definition + active instance，M1c 完成 caller 切换后删除运行态字段。

## 退出状态与整合边界

M0 的历史退出依据已经形成：真实帧 golden/BuildPhase 合同、语义选择、首次 inventory 与
benchmark composition 差异均有记录；2026-09-01 的 Core/EXE、19 条定向合同、1474 条旧树
全量测试、inventory 双 shell 重复性和 mode 2/4/5 结果仍按各自日志解释。mode 5 只获观察
豁免，不构成规范性能通过，也不是新基线。

当前 `715f9a2` 已包含真实 production-bootstrap phase 合同及后续 M1-M8 实现。M0 收口提交
只涉及以下测试/文档/工具路径，不修改生产代码，也不把 M7/M8 行为倒写成旧 baseline：

- `M BattleSystemECS.Tests/Framework/CombatGoldenReplayTests.cs`
- `D BattleSystemECS.Tests/Framework/GameplayCapacityProbeTests.cs`
- `M docs/ecs-gas-m0-baseline.md`
- `M tools/inventory-ecs-gas-migration.ps1`
- `?? BattleSystemECS.Tests/Framework/GameplayCapacityContractTests.cs`

本次按用户明确要求不重新运行任何门禁；提交后的验证状态必须继续引用上文最近 evidence
及其时间边界。未执行 `push`、`reset` 或 `clean`，也未改动 `artifacts/`、`TestResults/` 或
仓外 recovery/stash。

# ECS + GAS M0 基线与语义冻结

> 记录日期：2026-08-30。M0 冻结现有行为，并包含四处最小 legacy phase gate；未进入 M1 结构迁移。

## M0 状态

语义与测试证据已达到 M0 退出要求；根目录 `dotnet build` 的既有 MSB1011 命令歧义仍单独保留，显式 EXE 项目构建通过。未执行任何 M1 改动。

## 基线证据

完整原始日志位于 `artifacts/m0-baseline-20260830-031715/`，环境快照包含 commit、工作树、.NET SDK、配置文件和 Unity DLL 检查。诚实说明：首个 `git-status.txt` 是创建 artifacts 目录后才写入，未在第一个 artifacts 写入前保存；监督会话在 M0 启动前观察到 `d2d3b61` clean，但不存在更早的原始 status 文件。

最新完整门禁日志位于 `artifacts/m0-final-gates-20260830-0715/`，并包含 `command-manifest.txt` 与空的 `diff-check.log`。其中 step02 原样记录根目录 `dotnet build` 的 MSB1011/exit 1；step03 是明确 `BattleSystemECS.csproj`/exit 0。

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

mode 2/4/5 都是 `Systems.BenchmarkSystem` 的手工 composition；mode 4 调用实际 system Update 链，但三者都不通过生产 `SystemRegistry`/`GameManager` 完整装配。该差异是 M0 已知覆盖盲区，M5 前不得宣称 benchmark 与生产 composition 同构。mode 5 的日志还显示实际波次与完整战斗管线，但入口仍由 benchmark 自行接线。

## Golden 场景与证据

新增 `BattleSystemECS.Tests/Framework/CombatGoldenReplayTests.cs`，当前 10 条测试均从 `FrameScheduler.Tick` 或真实 SystemGroup 入口驱动：

| 场景 | 证据 |
|---|---|
| 普通塔命中、击杀、奖励、active 列表、事件顺序、fresh-store 确定性重放 | `CombatGoldenReplayTests.WaveFrame_ReplaysOnFreshStores_WithExpectedKillRewardAndEvents`，明确锁定 HP=0、active count/list、kills=1、gold=7、`damage → killed → destroyed` |
| BuildPhase GlobalSkill 拒绝合同 | `CombatGoldenReplayTests.BuildPhase_GlobalSkillIsRejected_WithNoHpOrDeathWork`；HP/active/kills/死亡事件不变，记录 `PhaseNotAllowed` |
| BuildPhase Skill/AutoSkill 真实 Registry 拒绝且无伤害工作 | `CombatGoldenReplayTests.BuildPhase_RegistrySkillAndAutoSkillAreRejectedWithoutDamageWork` |
| DoT/Weather 死亡入队 | `Sandstorm_LethalDot_QueuesDeathAndResolves`、`GlobalSkillMeteorTests` |
| Projectile/附加效果 | `ProjectileSystemTests`、`EnchantSystemTests`、`TowerAttackSystemTests` |
| 护盾、元素、免疫、I-frame、血量下限 | `DamageFormulaTests`、`ElementalResistanceTests`、`InvulnerabilityFramesTests`、`ExecuteImmunityTests` |
| 实体/效果 ID 回收 | `ComponentStoreTests`、`GameplayEffectTests` |
| 同帧 14 hits 计数/同目标 characterization | `CombatGoldenReplayTests.SameFrameFourteenHits_EmitsFourteenDamageEventsForSameTarget`，锁定 14 个同目标 damage event、HP=986、kills=0；`HitTriggerSystem` 仍 disabled |

同输入的 golden 输出只包含稳定状态事实（active、HP、kills、gold、activeEnemies）；时间戳、FPS 和随机日志不纳入比较。M1 结构改动必须在 feature-off 下保持这些事实和事件顺序。

AutoSkill 的拒绝诊断 latch 是实例生命周期级别。生产 FrameScheduler 只有 `allowCombat:false` 的 Build caller，没有 `allowCombat:true` 的 Wave caller，因此不会按 Wave reset；测试只锁定连续 Build tick 的一次性诊断，不声称存在 phase reset。

## 语义决策表

| 项目 | M0 冻结选择 | 测试证据/后续约束 |
|---|---|---|
| Damage 顺序 | 先无敌/命中门控，再暴击/护甲/抗性/元素，护盾吸收后应用血量下限；死亡入队在 HP 归零后 | `DamageFormulaTests`、`ExecuteImmunityTests` |
| DamageFlags | 保留现有取舍：技能跳护甲、塔伤害与荆棘护盾语义不重写；真伤走现有 True 分支 | `DamageFormulaTests`、`ElementalResistanceTests`；M3 显式化 |
| 元素转换 | M0 保留当前合并结果和主类型上报行为，不拆队列 | `EnchantSystemTests`；M3 决定 `DamageInstance` 表达 |
| Death authority | `QueueEnemyDeath` 是唯一提交入口；帧末 `ResolveEnemiesKilledThisFrame` 发奖励/事件并销毁实体 | `FrameSchedulerTests`、Meteor/Sandstorm 回归 |
| BuildPhase | 资源/建设允许；攻击/Meteor/攻击性 DoT 拒绝并记录 `PhaseNotAllowed`，不写 HP、不留死亡工作 | `CombatGoldenReplayTests.BuildPhase_GlobalSkillIsRejected_WithNoHpOrDeathWork`、`BuildPhase_RegistrySkillAndAutoSkillAreRejectedWithoutDamageWork` |
| 属性聚合 | M0 不改变旧 getter/缓存；Add/Multiply/Override 的新解释器留给 M2 | 现有 GAS 定义测试仅作 facade 约束 |
| Effect stacking | 保留当前 `StackingBehavior`：None/Refresh/MaxStacksRefresh 及现有 cap | `GameplayEffectTests`、Buff tests |
| Periodic | Poison 使用 Combat clock；bullet-time 不降低 tick 速率，首次 tick/间隔保持现状 | `CombatGoldenReplayTests.BulletTime_PoisonUsesCombatClock_AtFullTickRate`，两次真实 scheduler tick 后 HP=80 |
| 同帧可见性 | 并行收集读取帧开始快照，串行提交按现有队列顺序；14 hits 已锁定为 14 个同目标事件 | `CombatGoldenReplayTests.SameFrameFourteenHits_EmitsFourteenDamageEventsForSameTarget` |
| 资源 | MaxHealth 变化沿用现有裁剪/恢复规则，不在 M0 引入新 projection | `ComponentStoreTests`、TimeRewind tests |
| 失败/容量 | 无效实体安全返回；池耗尽保留现有 sentinel/计数行为，M1 增加诊断合同 | pool exhaustion tests |

## 时钟映射

| ClockId | 当前来源 | bullet-time |
|---|---|---|
| Build | BuildGroup 阶段 delta | 不受敌人减速 |
| Enemy | `enemyDt`：PreGame/Spawning/AI/Movement/Terrain | 按敌人 time scale |
| Combat | `combatDt`：Combat/SkillBuff/PostDeath | 玩家/塔侧全速 |
| RealTime | 外部 fixed timestep/输入 | 不受模拟时停 |
| Global | 全局缩放节点 | 仅按显式规则 |

时钟证据：`BulletTime_PoisonUsesCombatClock_AtFullTickRate`、`BulletTime_TowerAttackUsesCombatClock_AndHitsNormally`、`BulletTime_WeatherUsesEnemyClock_ScalingDamageByQuarter`、`BulletTime_WoundTransitionIsDtFree_AndDoesNotShareWeatherClock` 均从真实 `FrameScheduler.Tick` 驱动并锁定数值。EnemyWound 是 dt-free 状态转换，不与 Weather 共用 dt 缩放；该结论已冻结。

## Benchmark composition 修复计划

1. M1 前保持 benchmark 只读，继续记录 `BenchmarkSystem` 手工 composition 与生产 Registry 的差异。
2. M5 建立 `BenchmarkCompositionFactory`，由 `SystemRegistry.CreateAll/WireDependencies/AssignToGroups` 生成与 GameManager 同构的 scheduler；mode 4/5 切换到该 factory。
3. 保留 mode 2 作为纯合并下界，同时新增 composition fingerprint（group/system 名称稳定排序）写入每次日志。
4. 在切换前后跑 golden 状态/事件 diff 与 mode 2/4/5 ±5% 门禁；差异未批准时保持 legacy benchmark。

## GAS facade 影响范围

首次静态扫描见 `artifacts/gas-migration-ledger.json`；原始引用另存为 `artifacts/m0-baseline-20260830-031715/gas-effect-refs.txt`。

- 构造/生产：`Systems/BuffSystem.cs`、`Systems/SkillSystem.cs`、`Systems/ElementalReactionSystem.cs`、`Systems/CorpseEffectSystem.cs`。
- 存储兼容 API：`Core/ComponentStore_World.cs` 的 `AppliedEffect[]`、`GetEffect`、`SetEffect`、`AddEffect`。
- 运行态字段写入：`BuffSystem` 直接更新 `RemainingTime`、`TicksRemaining`、`TimeSinceLastTick`、`StackCount`；定义中混有 `RemainingTime`、`TicksRemaining`、`RefreshDuration`。
- M1a 结论：旧 `GameplayEffectDef`/`AppliedEffect` 必须保留为 legacy facade；M1b 才引入 immutable definition + active instance，M1c 完成 caller 切换后删除运行态字段。

# 能力阶段上下文与请求边界证据

> 记录日期：2026-09-01。本文只记录当前未提交工作树的复核证据；测试数量和性能值不是长期基线。

## 构建来源

- HEAD：`21fc9017e2b26febe75f7f73cd64134580a53bd0`
- `game_config.json` SHA256：`CF235A2627E0D0513717350161A2BD40ACC1E9AA913E141A1CB5585E41CAC978`
- Core DLL SHA256：`A09A1E978B7D279EA1E600EF83B180CACE08CCB36DFDCD059991A2B6BC8FAA52`
- EXE DLL SHA256：`E6C4339DFE20763540CB392842E704A27A1BCB43AF4A5FBA9DD8060C4A6D4DC5`
- 构建配置：Debug；性能命令均使用显式 build 产物并加 `--no-build`，同机串行执行。

## Dirty manifest

修改文件：

- `BattleSystemECS.Tests/Features/Buffs/AdrenalineSystemTests.cs`
- `BattleSystemECS.Tests/Features/Skills/GlobalSkillMeteorTests.cs`
- `BattleSystemECS.Tests/Features/Skills/HeroSkillSystemTests.cs`
- `BattleSystemECS.Tests/Features/Skills/MassResurrectTests.cs`
- `BattleSystemECS.Tests/Framework/SkillSystemTests.cs`
- `BattleSystemECS.Tests/Integration/GameSimulationTests.cs`
- `BattleSystemECS.Tests/Mechanisms/Combat/ChainHealTests.cs`
- `BattleSystemECS.Tests/Mechanisms/Control/AoeCcTests.cs`
- `BattleSystemECS.csproj`
- `Core/BuildGroup.cs`
- `Core/ComponentStore.cs`
- `Core/FrameScheduler.cs`
- `Core/GAS/DamageResolver.cs`
- `Core/GAS/ResourceResolver.cs`
- `Core/GameManager.cs`
- `Core/SystemRegistry.cs`
- `Systems/AutoSkillSystem.cs`
- `Systems/BenchmarkSystem.cs`
- `Systems/GlobalSkillSystem.cs`
- `Systems/HeroSkillSystem.cs`
- `Systems/SkillSystem.cs`
- `Systems/TowerActiveSkillSystem.cs`
- `docs/plan/ecs-gas-migration-foundation.md`

新增且未跟踪，以下三个文件均列入后续提交清单：

- `BattleSystemECS.Tests/Framework/SkillBuildBoundaryTests.cs`
- `Core/PhaseContext.cs`
- `docs/ability-phase-boundary-evidence.md`

## 正确性门禁

- Core build：0 warnings，0 errors。
- EXE build：0 warnings，0 errors。`CheckEolTargetFramework=false` 只关闭 SDK 对 net6.0 生命周期的专项提示，没有使用 `NoWarn`，其他编译器或 SDK warning 仍保持可见。
- 全量测试：1498/1498 通过。
- 测试静态规则：1363 个测试方法，0 违规。
- `git diff --check`：通过。
- 源码阶段标签审计：`rg -n "M-[0-9]|\bM[0-9]+\b" Core Systems BattleSystemECS.Tests Program.cs --glob '*.cs'` 为 0 命中。
- `SetCombatAllowed` 审计为 0 命中；四个能力系统的 `SetPhaseContext` 均为 assembly-internal，Core 之外没有等价 public 阶段写入口。`PhaseContextWritersAreAssemblyInternal` 以反射锁定该合同。
- 完整局压测由生产实际调用的 `BenchmarkSystem.CreateFullGameRuntime` 完成组合，测试调用同一 factory 并钉住三个 group 引用和 scheduler 注册。mutation 删除 factory 中的注册时，测试以 `Build expected / Unbound actual` 失败。
- `GameManagerInitializationBindsStateMachineToAbilitySystems` 经过真实 `GameManager.Initialize()`；mutation 删除生产 `BindStateMachine` 时，测试以 `Init expected / WavePhase actual` 失败。`SystemRegistry.AssignToGroups` 仍由这两条真实组合路径执行。
- `GlobalSkillSystem.IsCombatSkill` 私有死代码已删除。

## 性能样本

最近可比窗口来自同一工作树、同一 HEAD、同一 Debug 配置在本次 Standards 修补前紧邻完成的已验证运行；其五轮均值已由上一版本文和当前任务终端同时记录：mode2 `46903`、mode4 `9260`、mode5 `6066` FPS。历史基线来自仓库 `AGENTS.md`：mode2 `8333`、mode4 `5212`、mode5 `4874` FPS。

最终性能窗口在 2026-09-01 00:05:59–00:06:17（Asia/Hong_Kong）串行执行，启动前确认没有其他 `dotnet run` 或 BattleSystemECS benchmark 进程。并发门禁会话的旧 benchmark 位于 2026-08-31 23:47:39–23:48:24，其新 turn 到 2026-09-01 00:07:19 才启动，两个区间均与本窗口无 overlap。预热值完整保留：mode2 `45161`、mode4 `9574`、mode5 `6486` FPS。

| 模式 | 五个测量样本 FPS | 均值 | 最近可比窗口 | 相对变化 | 历史基线 |
|---|---|---:|---:|---:|---:|
| mode2 | 47243 / 44180 / 46495 / 45131 / 40399 | 44690 | 46903 | -4.7% | +436.3% |
| mode4 | 9172 / 9522 / 9443 / 9818 / 9389 | 9469 | 9260 | +2.3% | +81.7% |
| mode5 | 6114 / 6174 / 6372 / 6369 / 6187 | 6243 | 6066 | +2.9% | +28.1% |

mode2 均值 `44690`，相对最近可比窗口 `46903` 为 `-4.7%`；mode4 均值 `9469`，相对 `9260` 为 `+2.3%`。两者均超过绝对硬门禁且相对变化在 ±5% 内，性能门禁通过。mode5 均值 `6243`，按用户豁免只记录观察值，不作规范性能门禁结论。

## 原始日志摘要

完整原始日志的稳定审计副本位于 `C:\WorkSpace\AI\battlesystem-ecs-gate-logs\ability-phase-boundary-20260901-000559`，不属于 Git 工作树，也不进入业务提交。每条命令都有独立日志，包含开始/结束时间、cwd、原命令、完整 stdout/stderr 和退出码；`provenance.log` 保存 HEAD、dirty manifest、配置与最终 DLL hash。工作树内 ignored artifact 保留为原始镜像，但不是唯一 provenance。

- 每个日志/原 manifest 的 SHA-256 清单：`C:\WorkSpace\AI\battlesystem-ecs-gate-logs\ability-phase-boundary-20260901-000559\audit-files.sha256`
- `audit-files.sha256` 自身 SHA-256：`A7BD08A35BF7FC9D49E8723ED55A80FAD6771B520093E823A10302F22147728E`
- 23 条命令日志的退出码均为 0，另有 1 条 provenance 日志；完整样本没有删减或按中位数替换。

采样机有 16 个逻辑核；无并发 benchmark，但两个 VS Code Node 服务持续各占约一核。未终止用户进程、未改变优先级或 affinity。当前改动不进入 mode2/mode4 每命中热循环：阶段同步只在一次性创建/阶段边界发生，完整局 factory 只由 mode5 使用。

## 未完成边界

此证据只授权能力阶段上下文与请求边界切片。`FrameScheduler` 对四个能力系统的重复显式广播，以及 `DamageResolver`/`ResourceResolver` 重复的 `RejectAllPending`，仍待后续架构切片判断是否统一；FrameGraph、TimeContext、Hero/Tower 完整效果派发及非伤害能力 GAS contract 均未完成，不能把本轮门禁解释为整体迁移退出。

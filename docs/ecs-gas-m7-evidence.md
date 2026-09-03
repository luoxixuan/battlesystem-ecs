# M7 Installer Registration Boundary Evidence

状态：已提交。基线 commit 为 `4bebc43024a74fd52462d6cb31a19ed0aa34efa3`，
父提交为 `85139a4a1c169da6a8e3334d480882f10759f03e`；保护分支
`codex/installer-registration-boundary-protected` 固定在该 commit。

生产组装由 `ProductionSystemInstaller` 唯一负责。installer 在 mutation 前校验 schema v3
manifest、typed recipes、依赖图和 FrameGraph binding；按 Construction、Wiring、Binding
三阶段调用 registry，并在 composition seal 后返回。失败会保留原异常类型，同时记录
session、registration、stage、exception type 和 reason。重复 Wire/Bind、重复安装、空依赖、
未知 binding ID、manifest-only 节点、disabled owner 和 binder 未执行均有回归覆盖。

`FrameScheduler.RegisterFrameBinding(string, Action<NodeExecutionContext>)` 现在对未知
ID 显式抛出 `FrameGraphValidationException`，且在抛出前不修改 binding 或 declaration。
typed `FrameBindingRegistration` 入口保留其受控 disabled 兼容策略；生产 explicit binding
合同中的 legacy 与 unknown-no-op 均为零。

本轮唯一 fresh evidence 目录为：
`C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m7-installer-registration-final-20260902T231500Z`。
目录中的 `dirty-inventory.txt` 按行包含 `HEAD=<sha>`、`INDEX_SHA256=<sha>`、
`INDEX_STATUS=<状态>`、`TRACKED_PATCH_SHA256=<sha>`，随后是 `STATUS_BEGIN` 与 `STATUS_END` 标记；
两标记之间逐行保存 `git status --short` 原文（空状态时两行相邻），以及每个
`PATH<TAB>SHA256=<hash>` 的 untracked 文件记录；`index.txt` 与 `index-sha256` 可独立
复核。`dirty-inventory-schema` manifest 命令验证标记顺序及内容与实时
`git status --short` 完全一致。`command-manifest.json` 的每条 PASS 记录包含 command、cwd、startUtc/endUtc、
startLocal/endLocal、exitCode、semanticStatus、stdout/stderr/result/hash 和 `fresh=true`。
失败或尝试只位于 `attempt-failures/`；zero-match `rg` 的 exit 1 单列为 semantic PASS。

证据目录同时保存 Engine/Core/EXE/Tests 构建、focused/full tests、测试规则、diff-check、
generator 双次同根与跨根一致性、真实递归 IL↔JSON/metadata scanner、registration/node/content/phase/binding/
BuffType 扫描的原始日志和摘要。测试数量以本轮 command-manifest 的实测结果为准。mode2/4/5 仍按用户决定延期，Unity smoke 仍不可用；这些
状态不会被写成通过。最终文件清单和聚合 SHA-256 见目录内 `evidence-sha256`（聚合文件自身排除）。

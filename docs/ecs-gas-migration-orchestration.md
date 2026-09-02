# ECS + GAS 迁移编排

本文记录当前 M7 注册边界和可复核门禁。生产组装唯一入口是
`ProductionSystemInstaller`；它按 `Construction -> Wiring -> Binding` 顺序驱动
`SystemRegistry`，完成 FrameGraph 合同校验后封存 composition。`GameManager` 和
benchmark 入口不直接组装三段 registry facade。

## 当前边界

- schema v3 只允许 typed Factory/Wire/Bind recipe，禁止自由 C# 语句。
- 依赖图使用稳定拓扑顺序；重复、越序、未知 owner、缺失节点和未执行 binder 均 fail-fast。
- FrameScheduler 的字符串兼容注册 API 必须先解析 `FrameBindingFacts`；未知 binding ID
  抛出 `FrameGraphValidationException`，不会写入 runtime declaration 或 delegate 表。
- 生产节点必须拥有 manifest 合同和实际 binding；disabled 项必须带明确原因。
- FrameGraph/FrameSystemGraph/FrameScheduler 的签名和递归 IL 闭包不暴露具体
  `BattleSystemECS.Systems.*` 内容类型；业务依赖通过 `BattleSystemECS.Content.Contracts`。
- strict production 路径的 legacy damage apply 计数为零；compat 入口仅在显式测试中可用。

## M7 复核

本轮唯一 fresh evidence 位于仓外目录：
`C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m7-installer-registration-final-20260902T231500Z`。
该目录包含真实命令 manifest、原始 stdout/stderr、退出码、UTC/local 时间、工作目录、
结果与逐条可复核 hash、HEAD/index/status、tracked patch 与 untracked inventory，以及所有扫描结果。
`dirty-inventory.txt` 在固定的 `STATUS_BEGIN`/`STATUS_END` 行之间保存
`git status --short` 的原文（空状态时两行相邻），并由 `dirty-inventory-schema` 命令复核。
文档不复制会随工作树变化的派生摘要或二进制快照；以该目录中的
`command-manifest.json` 和 `evidence-sha256` 为准。

## 后续门禁

Engine/Core/EXE/Tests 构建、focused/full tests、测试规则、diff-check、生成器双根/跨根
一致性、真实递归 IL↔JSON/metadata scanner、registration/content/phase/binding/BuffType 扫描均在同一 fresh 目录记录并通过。
测试数量以该目录 command-manifest 的实测结果为准；mode2/4/5 按用户决定延期，Unity
smoke 在当前主机不可用；二者不能冒充通过。

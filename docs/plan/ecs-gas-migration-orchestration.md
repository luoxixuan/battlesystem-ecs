# ECS + GAS 迁移编排

本文记录当前 M7 注册边界、M8 稳定观察/存储决策和可复核门禁。生产组装唯一入口是
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
- 2026-09-03 缺口修正 Phase 2：`effect.commit`/`build.effect.commit`/`ability.commit`
  已更名为 `effect.tick`/`build.effect.tick`/`skill-buff.skill.update`；`combat.player-attack`/
  `combat.tower-attack` 补齐 `PlayerResources`。随后清掉 `build.skill.update` /
  `build.auto-skill.update` / `build.global-skill.update` 上不存在的 `AbilityRequests`
  声明，以及 `post-death.corpse.update` 上不存在的 `EffectRequests` 声明，并重算批准根
  与 topology hash。
- 2026-09-03 缺口修正 Phase 3：玩家伤害收口到 `ApplyPlayerDamageAuthority`；
  `CanApplyPlayerDamageAuthority` 预检 `CanAccept(0,2)`，队列溢出时不消耗 stealth；
  近战 `AttackInterval` 冷却门控首次生效（含裂变/克隆/死灵继承）；`PlayerDamaged`
  四站点改发 `applied`；thorns/trample/leap/projectile 保持静默；`EnemyProjectile`
  改打 `PlayerEntityId`；BossTrailAoe/SuicideBomb 已迁到 `ApplyPlayerDamageAuthority`。
- 2026-09-03 F4–F9 进度：`ApplyDot` None→`TryAdopt`、叠层→`TryRestack`；Transfer / BlockedTags /
  Attribute magnitude / Explicit 计数器合同 / 嵌套 `AbilityState` 已落地。随后 `TryAdopt` 补上
  BlockedTags 与 Periodic 校验，`HasTag` 去掉槽位回退，死亡回调去掉假 `AbilityRequests`。
  再续：`Activate(AbilityRequest)` 主入口；敌方/英雄/全局/塔
  主动技能冷却并进 `AbilityState`；Rally 拆出 `combat.rally.consume` 消费 `DamageApplied`；
  lava/firewall/corpse/demolish/skill 走 catalog Periodic + 空 modifier。
  2026-09-04：`AbilityRequests` 是 accept 后当帧日志，不是独立 consume/commit 管线；
  Rally writes 改为 `TowerState`；`ApplyDot` None 改回 `TryAdopt`（脉冲重挂可并存）。
  仍不是终态：`AbilityState` 非稀疏池；机制 SOA 冷却未并；`EffectRequests` 死 token 保留；
  `TryAdopt` 不 `ApplyModifiers`；Skill 无 catalog Periodic 时仍 fallback。
- 2026-09-04 终态收口续：`AbilityRequests`/`EffectRequests` 真 buffer；`ability.commit` 在
  Spatial 之后、Combat 之前；`effect.commit` 在 `effect.tick` 前；Build 有 `build.ability.commit`。
  稀疏 AbilityState 池保留 `AbilityInstances` facade。burrow/leap/totem 冷却列合并。
  仍不宣称 M5/M6/F4–F9 完成。
- 2026-09-04 能力 GE 解耦：`CommitPlan` 只入队 granted effect，`effect.commit` 才 `TryApply`；
  Combat 看不到当帧能力 modifier。敌方不再走 `Execute*` 当场结算。仍不宣称 F4–F9 完成。

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

## M8 有界工作包

M8 当前只完成稳定观察、profile 支持的 Effect handle 分页，以及历史 evidence 缺口回收；
完整证据与决策见 [ecs-gas-m8-evidence.md](../ecs-gas-m8-evidence.md)。

- 生产 FixedPopulation graph 以 10K 敌运行 500 帧并记录 Resolver、Effect、Trigger 和 pool
  high-water；另有 128 轮 Periodic death/entity recycle soak。
- 两类 soak 及 storage profile 各运行三轮；snapshot 的稳定实体 digest、按 publication 顺序累计的
  Gameplay event sequence digest、published count 和结构化 profile 签名必须一致，publication failure 也必须
  单独为零或可解释地记录。digest 仅由 harness 显式启用，生产默认不承担 hash 成本；关键
  Damage/Resource 事实在无法预留事件槽时于状态写入前返回 `RequestQueueOverflow`。
- `EffectPool` 在既有 interface 后按 256 槽分页；外部 handle 与失败合同不变。
- 死亡提交通过双事件队列 reservation token 收口：容量不足不执行奖励、回调或销毁，
  原死亡 batch 跨 `BeginFrame` 可重试；成功路径在全部副作用完成后一次发布
  `ResourceChanged` 与 `KillConfirmed`，重入生产者不能偷取已预留槽位。
- prepared read bag 提交后单独清理，回调重入的 alternate write bag 保留；生命周期订阅者
  逐个执行，异常在整批销毁和事实提交后重抛。1/2/3-fact queue overload 保持 resolver 成功热路径零分配。
- 最近一次完整验证证据：`C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m8-player-damage-concurrency-20260903T031142Z`，
  当时 full tests 为 1805/1805、focused tests 为 29/29。该证据早于本次跨 resolver 共享提交锁修复；
  本次修复后按用户要求未重跑门禁，不能将该目录冒充为 post-fix fresh PASS。更早的
  `m8-observation-*` / `m8-death-*` 目录只作历史参考。
- dense Ability、legacy Effect projection 和 Boss phase 公共数组只记录 profile 候选，不在
  Unity `UNAVAILABLE/BLOCKED` 时改动其公开 surface。
- Trigger 当前利用率和零故障样本不支持固定开放寻址表改写；Archetype 量化闸门也未触发。
- mode 2/4/5 继续按用户决定延期。因此本轮不能宣称完整 M8 phase exit。

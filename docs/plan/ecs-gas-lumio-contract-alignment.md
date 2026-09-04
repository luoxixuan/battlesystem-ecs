# ECS + GAS：Lumio 合同对照收口计划

> 状态：P1（F10 账本）、P2（F11/F12 Commit 预留 + 禁 throw）、P4（Tag 层级 A2 + 准入序 A5）已实施；P3 / P5–P6 未宣称完成
>
> 更新日期：2026-09-05（P4 落地：Tag parent 词汇表 + 冻结准入序）
>
> 基线 commit：`b5cfe52`（对照审查时的 HEAD）
>
> 终态约束：[ecs-gas-final-architecture.md](../ecs-gas-final-architecture.md)
>
> 迁移总览：[ecs-gas-migration-plan.md](ecs-gas-migration-plan.md)
>
> 既有缺口：[ecs-gas-gap-remediation-plan.md](ecs-gas-gap-remediation-plan.md)
>
> 对照源：LumioGameEngine `.spec/knowledge/features/gas.md`、`ecs.md`（2026-08-30 定稿口径）

## 1. 文档职责

终态文档回答「最终是什么样」。本文回答：对照 Lumio 之后，**哪些合同要写进终态、哪些代码缺口要修、按什么顺序切、什么不准搬**。

本文不替代 M0–M8 总览，也不替代 F0–F9 缺口计划。它只覆盖 2026-09-04 对照审查成立的那一批会计合同。F0–F9 未完成项继续走原计划；本文件新增的 **F10 / F11 / F12** 登记到缺口计划 §7。

每阶段模板与其他阶段文档相同：进入条件 → 范围和产物 → 切流策略 → 退出门槛 → 回滚 → 删除旧路径的条件。

## 2. 对照边界

Lumio 是联网体素引擎；本仓是单进程塔防基准。只吸收与 10K 敌、统一结算、配置扩展相关的深度。

### 2.1 已拍板（写入终态，实施按阶段走）

| 合同 | 终态写法 | 文档状态 |
|---|---|---|
| 属性公式整段 | `aggregated = (Base + ΣAdd) × max(PercentFloor, 1 + ΣPercent)`；有 Override 则最终覆盖，Add / Percent 忽略 | §7 已写 |
| 账本即条目 | Modifier 无独立生命周期；`stackCount` 是该条目对 ΣAdd / ΣPercent 的乘数，不把同一条展开成 N 份独立 modifier；`ModifierPool` 只能是 Aggregator 派生缓存 | §5.2 / §5.3 / §14.12 已写 |
| Commit 消耗 | 已入队请求预留资源与容量；Commit **先**复查冷却 + 预留，**再** `CommitPlan`；失败走 `AbilityCancelled`，不 throw、不半价。支付走 `ResourceOperation.Spend`（不足即拒），不改通用夹紧 | §6.1 / §8.1 / §8.2 已写 |
| 同帧效果顺序 | `effect.commit` 批内：结果不得依赖遍历顺序；移除垫后。AI 组 dispel 是批外 remove-first，单独写进顺序表。禁止「施加又移除 = 抵消」 | §6.3 已写 |
| 堆叠快照 | V1 只实现 `Replace`（新快照整张替换当前条目捕获）。默认 `Replace`。catalog modifier 全为 `ReevaluateOnRead`，shipped 零可见变化。`KeepPerLayer` 不进 V1 | §6.3 已写 |
| 随机领号 | 只从 Frame 确定性上下文领号，仅 `CommitSerial` 相取数；`Rng.Shared` 与无种子 `new Random()` 都不是确定性资产 | §9 / §13 / §14.14 已写 |
| Tag | 计数容器（代码已有）；层级用 Catalog 带 parent 的词汇表做编译期祖先展开。展开只作用于贡献计数和 Required/Blocked 匹配 | §3.1 已写 |
| 准入序 | 见 §5.2 冻结列表：形状类检查属第一段；`PhaseNotAllowed` 先于 `Cooldown` / `Cost`；容量独立原因放末尾 | §6.1 已写（整张表） |

### 2.2 明确不搬

Sync 字段、AOI、整帧预测回滚、13 相 Tick、Voxel 提交、Rust 求值内核、Fail-stop 毁世界、NetEntityId 永不复用、同帧施加又移除抵消、给瞬发技能背八态机、按帧号而不是 clock 虚拟时间做排期本。

消耗失败走 `AbilityCancelled` 事实，不要把 World 作废。P2 必须清掉生产路径上「预校验通过后 throw」的炸帧（含容量与 dispel），不把 Fail-stop 引入本仓。

### 2.3 应保持（比 Lumio 深，对照后不削弱）

Damage / Resource Resolver、Targeting 与 Effect 正交、Trigger 一等公民、ReadParallel / EmitParallel、dense SOA、子弹时间多 `clockId`、`allowedPhases`、单 World。

「Lumio 把 Targeting / 战斗结算留给内容层」是从 `gas.md` 未收进框架模块表作出的推断，即使推断成立也不削弱上述 seam。

### 2.4 不要原样照搬的两条 Lumio 规则

1. **同帧施加又移除 = 抵消**：复制带宽优化。本仓 Trigger 消费 `EffectApplied` / `EffectRemoved`，digest 按发布顺序累计，抵消会吞掉事实，与终态「Event 是已发生的事实」矛盾。`effect.commit` 批内保留「移除垫后」，禁止抵消。
2. **整张快照替换作硬规则**：这是玩法可见选择（弱毒换强毒），不是确定性修正。V1 默认 `Replace` 是因为单条目 × 乘数**无法**表达逐层捕获，不是因为要把玩法钉死。`KeepPerLayer` 若将来要做，必须先规定条目内定长捕获数组（`maxStacks` 长度），并单列 CHANGELOG；本计划 V1 不做。

## 3. 当前缺口（代码已核实，对照审查）

基线 `b5cfe52`。下列坐标是路径判断，F11 / F12 未构造运行时复现。

| ID | 缺口 | 坐标 | 危害 |
|---|---|---|---|
| F10 | `TryApply` 叠层每层展开 modifier handle；`TryRestack` 只 `StackCount++`；周期 tick 用 `StackCount` 乘。`TryApply` / `TryAdopt` 把同一个 `snapshot` 既当周期伤害又当 modifier 捕获值 | `GameplayRuntime.cs` `:115-129` / `:143` / `:157` / `:190-192` / `:242-249` / `:531` | 带 modifier 的 Periodic 走哪条入口，层数和加成分叉；在 DoT 跳伤参数上定义 `stackSnapshotPolicy` 会定义错对象 |
| F11 | 同帧多 `AbilityRequest` 各自 `ValidateCosts`，不为已入队请求预留；`CommitCosts` 失败 throw。当前先 `CommitPlan` 再 `CommitCosts`，无法整单取消 | `GameplayAbilityRuntime.cs` `:457-464` `:779` | 第二技能可少付钱也生效；Resolver 拒绝则炸帧 |
| F12 | 生产路径上全部 `prevalidated * during commit` throw（审查点名容量 + dispel；grep 另有伤害/预警/召唤/复活/回放） | 见 §7.2 表 | 与「不把 Fail-stop 毁世界引入本仓」矛盾 |
| A1 | 属性公式实现仍是 Override 换起点 + `ΠMultiply`。生产 Multiply：**两处** | `Attributes.cs` `:100`；`CatalogCompiler.cs:212` BuffAllies；`SkillSystem.cs:333`（`ATTACK_DAMAGE, Multiply, 1.1f`，经 `LegacyEffectAdapter` 且 `CaptureOnApply`） | 与终态 §7 不一致；Catalog 校验看不到运行时构造的 legacy def |
| A2 | Tag 无层级；`TagId` 是 `CatalogRegistries` 散列常量，无 parent 词汇表 | `GameplayTagRuntime.cs` | 计数已实现；`Debuff.Control` 不能匹配 `Stun` |
| A3 | 随机源不止 `Rng.Shared`。`Rng.Shared` 用 `TickCount xor ManagedThreadId`；`Systems/` 另有无种子 `new Random()` | `Rng.cs` `:19-21`；Rng.Shared 生产：EchoClone / EnemyTeleport / Portal / PointDefense / Skill / Upgrade。无种子 `new Random()`：Weather / EnemyFission / EnemyClone / AutoSkill（static）/ Pickup / EnemyAffix / RandomEvent / WaveSpawning（`??= new Random()`）/ Crafting（`seed == 0`） | 分裂/克隆/波次/词缀直接决定实体数量和位置；只修 `Rng.Shared` 过不了「固定种子多轮 soak 与 digest 一致」 |
| A4 | 生产运行时之外**只有一处**显式 `Remove`：`EnemyAbilitySystem.RemoveDispellableEffects`（`:509-519`），在 AI 组、`effect.commit` **之前**，语义是先移除后施加 | `EnemyAbilitySystem.cs:509-519` | 「移除垫后」若只钉 `effect.commit` 批内，与这条批外 remove-first 接不上 |
| A5 | 准入序：形状类 `NoTarget` / `UnsupportedDefinition` 已在冷却之前；`PhaseNotAllowed` 在 `ValidatePlan` 里，晚于 `ActivateCore` 的 `Cooldown` 和 `BuildActivationPlan` 的 `Cost`；容量不足返回 `InvalidRequest` | `GameplayAbilityRuntime.cs` `:348-360` `:363-364` `:518` `:532` `:542-543` | P0 若只冻结「Phase 先于冷却」会与代码差三处；队列满被报成请求畸形 |

`AbilityActivationRejectReason` 已有 8 值，不是「失败原因未分层」。Tag 已是 `(entity, tag)` 计数，「双沉默摘一层就解控」不成立。Override 平局已是 `Sequence` 更大者胜，与 Lumio 同级后写赢同构。生产里 `AddAttributeModifier` 只有 `GameplayRuntime` 三处调用，无绕过 effect 的 modifier 写入方——「Modifier 无独立生命周期」没有隐藏受害者。

结构事务「半实体泄漏」**未核实路径**，不进本计划实施范围；要做先单独举证。

通用 `ApplyMana` 等夹到 0 仍 Accepted（`ResourceResolver.cs:457`）对 **负增量伤害旁路是预期**（缺口计划 §6.6：`BossTrailAoeSystem` / `SuicideBombSystem` 用负 `AttributeKey(3)` 打玩家血）。这不是 F11 的修法；F11 用 `Spend`，不改通用夹紧。

## 4. 阶段依赖

```text
P0 终态合同冻结
  → P1 账本（F10）     → P3 属性公式（A1）
  → P2 Commit 消耗 + 预校验后禁止 throw（F11 / F12）
  → P4 Tag 层级 + 准入序对齐冻结列表（A2 / A5）   【可与 P1/P2 并行，不共享同一 writer】
  → P5 同帧顺序 + 随机领号（A4 / A3）             【P1 后；顺序表依赖账本语义稳定】
  → P6 后置（时长 Ability 态 / 排期本 / 墓碑）
```

P0 只改文档。P1 与 P2 不共享同一运行时 writer，可并行，但不要同一 commit。

P3 排在 P1 后的正当理由只有一条：不在同一变更中同时改公式数值和叠层展开策略。Percent 是加性的，N 份 `Percent(0.3)` 求和与 `stackCount × 0.3` 结果相同，**不会因为仍按 N 份 handle 展开就把公式切错**。

P5 的顺序表依赖 P1 的单一叠层入口。P5 默认快照必须是 `Replace`，与 P1「单条目 × 乘数」相容；不得在 P5 再把默认改成 `KeepPerLayer`。

## 5. P0 — 终态合同冻结

### 5.1 进入条件

对照审查结论已被接受；§7 公式已按 Lumio 整段写入。无代码进入条件。

### 5.2 范围和产物

只改 [ecs-gas-final-architecture.md](../ecs-gas-final-architecture.md) 与本文件 / 缺口计划交叉引用。不改生产代码。

写入位置：

| 节 | 写入 |
|---|---|
| §3 Tag | 计数容器；Catalog 带 parent 的 Tag 词汇表；编译期展开祖先。祖先展开只作用于贡献计数和 Required/Blocked 匹配，**不碰** `stackKey` 身份和事件上的 `definition.Tag` |
| §5.2 / §5.3 | Modifier 无独立生命周期；池 = 派生缓存；`stackCount` 乘数 |
| §6.1 | 准入序冻结（下表）；Commit 复查与 `AbilityCancelled`；容量独立原因 |
| §6.3 | 同帧顺序表：`effect.commit` 批内移除垫后；AI dispel 为批外 remove-first；禁止抵消；V1 `stackSnapshotPolicy = Replace` |
| §6.5 | 瞬时能力不背状态机；时长能力才有 Executing / Completed / Cancelled / Expired |
| §8.2 | 增补 `AbilityCancelled`（准入失败仍是 `AbilityRejected`） |
| §9 | 排期本按 clock 虚拟时间；随机只在 `CommitSerial` 领号 |
| §11 | 墓碑：「死了」vs「从未存在」；不写未核实的半实体泄漏 |
| §13 | 删任何把 `Rng.Shared` 当成确定性资产的表述 |
| §14 | 不变量：账本无独立生命周期；Event 不得因同帧抵消被吞；模拟随机禁墙钟种子与无种子 `new Random()` |

准入序冻结为（**提示优先级**，先匹配的原因先返回）：

```text
第一段 形状 / 请求合法性（冷却之前，与当前 ActivateCore 前半段对齐）：
  InvalidRequest（空 store/catalog、句柄无效、catalog 缺失、请求畸形）
  NoTarget（ValidateShape 多目标空列表等形状失败）
  UnsupportedDefinition（ForbidEffects 带 effects、heal-only 形状不匹配）

第二段 阶段 / 冷却 / 支付 / 目标实体 / Tag / 定义合同：
  PhaseNotAllowed
  Cooldown
  Cost（含同帧预留）
  NoTarget（目标实体无效 / RequireEnemy 未激活——BuildActivationPlan 循环内）
  TagRequirementsNotMet
  UnsupportedDefinition（时长合同 / 内容 CanCommit / 未知 execution）

第三段 容量（独立原因，列表末尾；不复用 InvalidRequest）：
  QueueOverflow（语义对齐 Resolver 的 RequestQueueOverflow：
    AbilityRequests / EffectRequests / Damage·Resource 队列 / modifier 槽）
```

P4 必须把实现与这张表对齐，**不只**把 Phase 挪到冷却之前。P2 引入 `QueueOverflow` 枚举值；入队失败（今日 `:370` 的 `InvalidRequest`）与 `ValidateCapacityPlan` 失败（今日 `:532`）都改走该原因。

Commit 复查刻意不查 Tag。理由标为 UE 经验（Lumio 文未展开）。当前 granted effect 到 `effect.commit` 才 `TryApply`，`ability.commit` 时 Tag 还不存在；跨帧蓄力的 commit 才会踩到。

### 5.3 切流策略

一次文档 commit。不切运行时。`docs/ecs-gas-m7-nullable-ledger.md` 是生成文件，文档-only 提交不得夹带其 BOM/换行噪音。

### 5.4 退出门槛

终态正文与本文件 §2.1 表逐条可对上；§7 公式不被改回「换起点」；§6.1 准入序与上表一致（不是「只有 Phase 提前」一句）。`git diff --check` 通过。

### 5.5 回滚

还原该文档 commit。

### 5.6 删除旧路径的条件

终态里删除「换起点 Override / `FinalOverride` / `ΠMultiply`」作为目标语义。实现保留到 P3 删除。终态删除「`KeepPerLayer` 为 V1 默认」。

## 6. P1 — 账本（F10）

### 6.1 进入条件

P0 退出。F0–F3 若仍在飞行，不与本阶段改 `GameplayRuntime` 叠层路径抢同一文件；先完成或显式串行。

### 6.2 范围和产物

`GameplayRuntime.TryApply` / `TryRestack` 对同一定义只保留一种叠层语义：

- `stackCount` 是条目乘数；Aggregator 从 ActiveEffect 推导加成，不为每层 `new` 一批 handle。
- `_modifiers` 字典若保留，只能当派生缓存：移除效果或 `ClearEntity` 后可从 ActiveEffect 全量重建。
- 周期 tick 继续 `magnitude * StackCount`，与面板加成使用同一 `stackCount`。
- **拆分参数**：`periodicMagnitude`（DoT 跳伤）与 modifier 捕获值不得共用同一个 `snapshot`。今日 `TryApply` `:143` / `:157` 与 `TryAdopt` `:190-192` 把周期幅度传给 `ApplyModifiers`。P1 改为两个参数；`stackSnapshotPolicy`（V1 仅 `Replace`）只作用于 modifier 捕获值。
- **容量按定义计**：今日 restack 按层数扩 handle（`:121` `added = (next - previous) * Modifiers.Count`）。`ValidateCapacityPlan` 本身按定义 `Modifiers.Count` 一次，但 restack 会在 commit 时再按层数要槽。P1 后每个 ActiveEffect 只占一份定义条数的 modifier 槽；`CanApplyPlan` / `ValidateCapacityPlan` / `TryApply` restack 三处必须统一，否则会出现「预校验过了、叠层却因层数扩容失败」或预留过宽。

不在本阶段改 Add / Percent / Override 算术（那是 P3）。V1 不实现 `KeepPerLayer`。

测试：

- 同一 Periodic 定义分别走 `TryApply` 叠层与 `TryRestack` 叠层，`StackCount`、computed 属性、下一次 tick 伤害三者一致。
- 撤层 / 过期后 computed 回到无该效果时的值（dirty 全量重算，不做浮点逆运算）。
- 叠层不增加 `_modifierHandleCount`（相对第一层）。
- `ApplyModifiers` 不再读 periodic magnitude 当作 CaptureOnApply 的捕获值。
- 「撤销修复 → 测试变红 → 恢复」防假绿。

### 6.3 切流策略

内部切换。无 feature flag：旧展开路径与乘数路径不能同时贡献。先改 `TryApply` 不再扩 handle，再让 `TryRestack` 走同一函数。

### 6.4 退出门槛

仓库门禁 §12。新增叠层一致性测试全绿。CHANGELOG：若有任何面板数值变化必须单列；纯路径合并且 golden 无 diff 则记「无数值变化」。

**实施状态（2026-09-04）**：已落地。`RestackLedger` 为唯一叠层函数；`TryApply` 扩 handle 分支已删；参数拆为 `periodicMagnitude` / `modifierCapture`；容量按定义条数。KeepPerLayer 未做。未改终态 §7 公式。

### 6.5 回滚

还原该 commit。派生缓存允许丢弃重建，回滚不涉及存档。

### 6.6 删除旧路径的条件

`TryApply` 中「每层 `Array.Copy` 扩 modifier handle」的分支删除；测试证明无生产调用依赖 N 份 handle 身份。

## 7. P2 — Commit 消耗复查（F11）与预校验后禁止 throw（F12）

### 7.1 进入条件

P0 退出。`AbilityRequests` 仍是真 buffer（`ability.commit` 批量提交）。

### 7.2 范围和产物

**预留与支付（F11）**

- 入队时按 sequence 累加预留（法力等 `Spend` 资源 **和** `EffectRequests` / Resolver 队列 / modifier 槽）。后请求看到的可用 = 当前值 − 已预留。
- 新增 `ResourceOperation.Spend`：不足即拒、原子、实际扣减必须等于请求。`CommitCosts` 只走 `Spend`。
- **禁止**改通用 `ResourceResolver.TryApply(ResourceRequest)` 的夹紧语义。`ApplyMana` 等夹到边界且实际扣减小于请求时仍可 Accepted——负增量打血（`BossTrailAoeSystem` / `SuicideBombSystem`）依赖该行为。
- 也可用仅给 `CommitCosts` 的 `requireFull` 标志实现同等原子性，但推荐独立 `Spend`，避免支付路径与夹紧路径共用一个开关被后来者误用。

**提交顺序（硬要求，不是推荐）**

- 今日：先 `CommitPlan`（伤害/CC 载荷当场提交）再 `CommitCosts`（`:457-464`）。「整单取消」只有复查放在 `CommitPlan` **之前**才可能。
- P2：对每条已入队请求，先复查冷却 + 预留（资源与容量）仍有效 → 失败则该请求整单 `AbilityCancelled`，不调用 `CommitPlan` → 成功才 `CommitPlan`，再 `Spend` 扣费、提交冷却。

**预留释放**

- 释放点钉在 `ClearAbilityQueue`（`ComponentStore_World.cs:923`）。三个调用方：`CommitQueuedAbilities:469`、`RejectQueuedAbilities:478`、`ComponentStore.cs:373`（`frame.begin` 清未消费）。漏绑则 `build.ability.reject` / `non-wave.ability.reject` 会把预留漏到下一帧。

**F12：所有 `prevalidated * during commit` 提交路径不得 throw**

审查原文点名容量 throw 与 dispel throw。仓库内同形字符串不止这两处，P2 **一并清掉**，不另开 F13、不留「已知炸帧」到 P6。

| 路径 | 坐标 | P2 结论 |
|---|---|---|
| `CommitCosts` 失败 | `GameplayAbilityRuntime.cs:464` | `AbilityCancelled`（`Cost`） |
| `CommitPlan` 失败（容量，与 F11 同形） | `:460` | 入队即 `QueueOverflow`，或 commit 前复查失败 → `AbilityCancelled` |
| dispel `Remove` 失败 | `EnemyAbilitySystem.cs:518` | 记失败事实 / 跳过该 slot |
| 玩家伤害 `TryApply` 未 Accepted | `EnemyAbilitySystem.cs:438` | 记拒绝事实，不 throw |
| 预警队列满 | `EnemyAbilitySystem.cs:465` | `QueueOverflow` / 跳过 |
| 召唤 `CreateEntity` 失败 | `EnemyAbilitySystem.cs:661` | `QueueOverflow` / 跳过 |
| 群体复活容量耗尽 | `NecromancerSystem.cs:216` | 停止继续复活，已成功的保留（或整单取消——实施时选一种并写测试） |
| 回放资源写入失败 | `TimeRewindSnapshot.cs:152` | 拒绝该次 Restore |
| 回放快照不可用 | `ProductionAbilityPayloadHandler.cs:57` | 拒绝该次 Restore |

仍允许 throw 的是**编程错误**（队列里的 ability 不在 catalog、`summon multipliers must be validated before commit`、FrameGraph seal 后变异）。那些不是「预校验通过、运行时竞争失败」。

**准入原因**

- 容量失败新增 `AbilityActivationRejectReason.QueueOverflow`（语义对齐 `RequestQueueOverflow`），放在冻结列表末尾。入队满槽与 `ValidateCapacityPlan` 失败都用它，不再混进 `InvalidRequest`。

测试：

- 同帧两技能总消耗超过法力：第一成功、第二 `AbilityCancelled`，法力只扣第一份；无异常；第一的载荷已提交、第二的载荷未提交。
- 单技能消耗超过法力：入队即 `Cost` 拒绝，或 commit 前取消，法力不变。
- 同帧两技能把 `EffectRequests` / modifier 槽预满：第二入队或复查得 `QueueOverflow` / `AbilityCancelled`，无 throw。
- 上表全部 `prevalidated * during commit` 路径都不再 throw（专门测试替换 `InvalidOperationException`）。
- `RejectQueuedAbilities` 与 `frame.begin` 清队列后，下一帧预留为零。
- 负增量 `AttributeKey(3)` 夹到 0 仍 Accepted（旁路回归，证明未改通用夹紧）。
- 防假绿：撤销预留逻辑后「两技能超法力」必须红。

未构造运行时复现；实施前用上述测试作为第一个证据。

### 7.3 切流策略

无半帧切换。预留表是 commit 当帧瞬态，不进存档。`Spend` 与 `Add` 并存期间，只有 `CommitCosts` 改走 `Spend`。

### 7.4 退出门槛

门禁 §12。`Core/` 与 `Systems/` 中不再存在 `prevalidated` 且 `during commit` 的 throw（编程错误字符串除外）。CHANGELOG 记行为变化（少付钱不再生效；容量满报 `QueueOverflow`；commit 竞争失败不炸帧）。

**实施状态（2026-09-04）**：已落地。预留表 `ComponentStore.AbilityCommitReservation`，释放只走 `ClearAbilityQueue`。Commit 每条先 `Release` + 复查冷却/Cost/容量，失败 `AbilityCancelled` 且不 `CommitPlan`；成功才 `CommitPlan` → `Spend` → 冷却。`CommitCosts` 只走 `ResourceOperation.Spend`。入队满槽与 `ValidateCapacityPlan` 走 `QueueOverflow`。F12 九处生产 throw 改为拒绝/跳过；`summon multipliers must be validated before commit` 与 catalog 缺失仍是编程错误 throw。未改通用夹紧，未改 P1 叠层/`CountPlanOccupancy`。mode 2/4/5 保持 DEFERRED。

### 7.5 回滚

还原 commit。预留表无持久态。

### 7.6 删除旧路径的条件

删除「校验看当前值、提交再夹紧」的支付双路径；`CanCommit` 注释「返回 true 后 Commit 不得拒绝」改为「不得 throw；资源竞争走 Cancelled」。删除用 `InvalidRequest` 表示队列满。

## 8. P3 — 属性公式（A1）

### 8.1 进入条件

P0、P1 退出。终态 §7 未被改回。

### 8.2 范围和产物

`AttributeAggregator.Aggregate` 改为：

```text
aggregated = (Base + ΣAdd) × max(PercentFloor, 1 + ΣPercent)
computed   = winning Override 若存在，否则 aggregated
computed   = Clamp
```

- `AttributeModifierOp.Multiply` 迁为 `Percent`；magnitude 是加项。
  - `CatalogCompiler` BuffAllies：`Multiply(1 + x)` → `Percent(x)`（`:212`）。
  - `LegacyEffectAdapter`：**运行时**映射 `Multiply(m) → Percent(m − 1)`。`SkillSystem.cs:333` 的 `Multiply, 1.1f` 经 adapter 且 `CaptureOnApply`，Catalog 校验看不到它。
- `AttributeAggregator.AddModifier`（或 `ComponentStore.AddAttributeModifier`）加运行时守卫：残留 `Multiply` 拒绝或断言失败。退出门槛「Catalog 校验拒绝残留 Multiply」**不够**。
- `PercentFloor` 进 `AttributeSchema`，默认 `0`。
- Override 不再换起点。删除任何 `FinalOverride` 草稿。
- 跨通道 `computed(AttackDamage) × computed(DamageOutputMultiplier)` 不动。

测试：

- 两个 `Percent(0.30)` → 1.60，不是 1.69。
- 两个 `Percent(-0.60)` → 因子 0，不是 −0.20。
- Override(999) 存在时 Add / Percent 不影响结果。
- 同 priority 两个 Override，sequence 更大者胜。
- BuffAllies 编译产物为 `Percent`，满层与 `MaxStacks=1` 下与旧 `Multiply(1+x)` 数值一致（允许 ulp）。
- `SkillSystem` Attack+10% 经 adapter 后等价 `Percent(0.1)`，与旧 `Multiply(1.1)` 在无其它乘区时一致（允许 ulp）。
- 直接 `AddModifier(Multiply, …)` 被运行时守卫拒绝。
- combo 仍走 `DamageOutputMultiplier` 的 Add（gap Phase 1 语义），本阶段不改 cap 公式。

### 8.3 切流策略

一次切换 Aggregator。旧 `ΠMultiply` 与新公式不能同时对同一 key 贡献。无影子对比要求——用单元测试钉公式。

### 8.4 退出门槛

门禁 §12。CHANGELOG **必须**单列数值语义（即使 shipped 内容预期零可见变化）。Catalog 校验拒绝残留 `Multiply`；运行时守卫覆盖 adapter / `SkillSystem` 路径。

### 8.5 回滚

还原 Aggregator、compiler 映射与 adapter 映射。无存档 Current（Current 本就不落盘）。

### 8.6 删除旧路径的条件

`AttributeModifierOp.Multiply` 枚举值删除或编译期拒绝；`product(Multiply)` 代码路径删除。adapter 不再接受 `Multiply` 作为稳定输入（映射保留到枚举删除）。

## 9. P4 — Tag 层级与准入序对齐（A2 / A5）

### 9.1 进入条件

P0 退出。可与 P1/P2 并行。`QueueOverflow` 枚举若尚未由 P2 落地，本阶段不得继续用 `InvalidRequest` 表示容量——与 P2 串行该枚举，或本阶段先加枚举、P2 接预留。

### 9.2 范围和产物

**Tag**

- Catalog 增加带 parent 的 Tag 词汇表（今日 `TagId` 只是 `CatalogRegistries` 散列常量）。
- 对每个 `TagId` 编译祖先闭包；授予 `Stun` 时 `Control` / `Debuff` 计数各 +1，移除对称 −1。运行时 `HasTag` 仍是整数键计数，不跑字符串。
- **祖先展开只作用于**：贡献计数、Required/Blocked 匹配。
- **不得碰**：`stackKey` 身份、事件上的 `definition.Tag`（仍是定义自己的叶标签）。
- 未知 Tag 启动硬失败（终态 §12 已有，补测试）。

**准入序（对齐 §5.2 整张冻结列表，不只搬 Phase）**

- 形状类 `NoTarget` / `UnsupportedDefinition` 保持第一段（今日 `:348-360` 已在冷却前；P0 列表必须承认它们，不要把 `NoTarget` 只写在 Cost 之后）。
- 把 `PhaseNotAllowed` 移到 `Cooldown` / `Cost` 之前（今日 `ActivateCore:363` 先 Cooldown，`BuildActivationPlan:518` 先 Cost，`ValidatePlan:542` 才 Phase）。
- 容量走 `QueueOverflow`，放在列表末尾；`:532` 与 `:370` 不再返回 `InvalidRequest`。
- `ClearEntity` 分配 `List` 是实现热点，**不在本阶段范围**；可另开性能卡。

测试：授予子标签后 `HasTag(祖先)` 为真；只移除一个沉默来源时另一来源仍计数；祖先匹配为真时 `stackKey` 与 `EffectApplied.Tag` 仍是叶 id。建造期战斗技能在冷却未转好时仍报 `PhaseNotAllowed`。空目标列表仍是 `NoTarget` 且发生在冷却检查之前。队列满报 `QueueOverflow`。

### 9.3–9.6

内部切换；无存档 Tag 推导值。回滚还原祖先表与检查顺序。删除「运行时扫平列表做层级匹配」若被引入。删除「用 `InvalidRequest` 表示队列满」。

**实施状态（2026-09-05）**：已落地。词汇表 `GameplayTagVocabulary`（Stun ⊂ Control ⊂ Debuff，既有 0–10 无 parent）；授予叶标签时祖先计数 +1。`ActivateCore` 第二段 `PhaseNotAllowed` → `Cooldown` → `Cost` → 实体 `NoTarget` → Tag → `UnsupportedDefinition`（时长合同 / CanCommit / 未知 execution），容量仍 `QueueOverflow`。Commit 复查仍不查 Tag。未改 P3 公式、未改 FrameGraph / Spend / RestackLedger。mode 2/4/5 保持 DEFERRED。

## 10. P5 — 同帧顺序与随机领号（A4 / A3）

### 10.1 进入条件

P0、P1 退出。P3 不阻塞本阶段，但不要同一 commit。

### 10.2 范围和产物

**同帧顺序（A4 已选定机制）**

生产运行时之外只有一处显式 `Remove`：`EnemyAbilitySystem.RemoveDispellableEffects`，在 AI 组、`effect.commit` 之前，语义是 **remove-first**。本阶段 **收窄合同**，不为此处单独改 FrameGraph：

```text
AI 组 dispel（批外，remove-first）：
  RemoveDispellableEffects → 之后同帧才入队 / effect.commit 施加

effect.commit 批内：
  堆叠命中 → 溢出判定 → 按 stackSnapshotPolicy（V1 = Replace）应用
    → 时长策略 → 周期策略 → 过期 / 批内显式 Remove 垫后
```

禁止同帧施加与移除抵消；两条事实都要发布。若未来出现第二处生产显式 Remove，再开 `RemoveRequests` 缓冲 + `effect.tick` 后尾节点（那会改 FrameGraph，按缺口计划 Phase 2 规则重算根哈希）。本阶段不预做尾节点。

**`stackSnapshotPolicy`（与 P1 相容）**

| 值 | V1 | 含义 |
|---|---|---|
| `Replace` | **实现，默认** | 新快照整张替换当前条目的捕获。catalog modifier 全为 `ReevaluateOnRead`（`GameplayDefinitions.cs:88`），BuffAllies / combo 走默认，聚合器忽略 `Captured`；唯一 `CaptureOnApply` 是 `LegacyEffectAdapter.cs:40`，走 `TryAdopt` / `TryRestack` 不加层。故默认 `Replace` shipped 零可见变化 |
| `KeepPerLayer` | **不做** | 每层各自捕获。单条目 × 乘数无法表达，除非条目内 `maxStacks` 长度捕获数组（换种形式的展开 N 份）。将来若做，单独开卡并 CHANGELOG |

不得以「避免无 CHANGELOG 的数值变化」为由把默认设成 `KeepPerLayer`——那会与 P1 打架，且对 shipped 内容不可观测。

**随机（A3 全清单）**

GAS / 战斗公式 / 决定实体数量与位置的模拟路径禁止 `Rng.Shared` 与无种子 `new Random()`。新增 Frame 注入的 `DeterminismContext`（或复用现有若已有），仅 `CommitSerial` 取数。

必须逐个分类，不能笼统替换：

| 来源 | 调用点 | P5 动作 |
|---|---|---|
| `Rng.Shared` | EchoClone / EnemyTeleport / Portal / PointDefense / Skill / Upgrade | 模拟路径改领号；纯表现可迁出战斗公式并注明 |
| 无种子 `new Random()` | WeatherSystem、EnemyFissionSystem、EnemyCloneSystem、AutoSkillSystem（static）、PickupSystem、EnemyAffixSystem、RandomEventSystem、WaveSpawningSystem（`??= new Random()`）、CraftingSystem（`seed == 0`） | 同上。分裂/克隆/波次/词缀优先 |
| 有种子但非 Frame 领号 | PreFightBuffSystem、ReforgeSystem、TowerModifierSystem、ShopRerollSystem | 分类：模拟则迁 DeterminismContext；非模拟可保留但不得进 digest |
| 压测固定种子 | BenchmarkSystem `new Random(42)` | 排除生产战斗，可不迁 |

**文档**：更新 `AGENTS.md` §5.2——今日写「禁止 `new Random()`；使用 `Rng.Shared`」，P5 后改为「模拟热路径禁止 `new Random()` / `Rng.Shared`；只从 Frame `DeterminismContext` 领号」。本文件 §12 门禁后的文档清单包含这一条。

**digest 前置（实施测试前必须先确认）**

固定种子多轮 soak 与 digest 一致，在 `Parallel.For` 下要求线程局部缓冲的**合并顺序稳定**。实施前先读现有 soak / observation harness：若今日靠调度运气，P5 先钉合并顺序（例如按 entity id / sequence 归并），再写一致性测试，否则测试会抖。

测试：同帧 apply+remove 两条事件都在 digest 里；dispel 后再 apply 的顺序与顺序表一致；固定种子多轮 soak 与 event sequence digest 一致（harness 显式开启）。`Rng.Shared` / 无种子 `new Random()` 在 `Core/GAS` 与伤害/技能/生成生产路径上的守卫测试。

### 10.3–10.6

顺序表无 flag。RNG 切流按调用点逐个替换，禁止半帧混用墙钟种子与确定性流。删除条件：GAS 热路径与上表模拟路径零 `new Random` / `Rng.Shared`。

排期本按 clock 虚拟时间、批量取件只覆盖纯时长效果：本阶段只写终态约束，实现放到 P6。

## 11. P6 — 后置（不阻塞 P1–P5）

| 项 | 合同 | 不做 |
|---|---|---|
| 时长 Ability 态 | Executing / Completed / Cancelled / Expired 只给蓄力/读条/引导 | 瞬发 10K 路径背状态机；不搬 `RolledBack` |
| 排期本 | 派生缓存；按每个 `clockId` 的虚拟时间取件；不登记闭包 | 按帧号排期（`enemyDt` 会被子弹时间缩放） |
| 墓碑查询 | 「死了」与「从未存在」可区分；实体 ID 仍回收 + generation | NetEntityId 永不复用 |
| 摘除式抑制 | Active 内摘除加成/Tag，不新增状态 | 新 `Inhibited` 枚举值 |
| 表现原因 | 内部 Event 增加变化 vs 回放/初次，避免 digest/回放重播爆炸 | 13 相 Ingress/Egress 命名；Gameplay Cue |
| `KeepPerLayer` | 若玩法需要逐层捕获 | V1 不做；要做先规定条目内定长捕获数组 |

结构事务亮相屏障：先举证分裂/召唤/弹道同帧秒杀路径，再开卡。未核实不得写进终态当现状。

## 12. 跨阶段约束与门禁

- 每个 phase 独立通过门禁才进入下一个；硬门禁失败则该 phase 保持「进行中」。
- 数值语义变化必须在 `CHANGELOG.md` 单列，不与重构混写。
- 防假绿：撤销修复 → 对应测试变红 → 恢复。
- mode 2/4/5 与 Unity smoke 按现行约束为 `DEFERRED` / `UNAVAILABLE`，不得写入 PASS manifest。
- 并行阶段不得改共享 GAS 池、事件总线或 Aggregator 公式。
- 不把 Lumio 的 Fail-stop 毁世界引入本仓；P2 退出时生产路径上不得再有「预校验通过后 throw」。

```bash
dotnet build BattleSystemECS.Engine
dotnet build BattleSystemECS.Core
dotnet build
dotnet test BattleSystemECS.Tests
powershell -File tools/check-test-rules.ps1
git diff --check
```

文档更新清单（阶段退出时按需）：

- `CHANGELOG.md`（数值语义单列）
- `AGENTS.md` §5.2（P5：随机规则从 `Rng.Shared` 改为 Frame 领号）
- 终态 / 本文件 / 缺口计划交叉引用

P3 / P5 若改 FrameGraph 声明，按缺口计划 Phase 2 规则一次重算批准根哈希。本计划 V1 的 A4 收窄合同预期 **不** 改 FrameGraph；若推翻该选择改走 `RemoveRequests` 尾节点，则必须重算。

## 13. 与 F0–F9 的关系

| 既有项 | 关系 |
|---|---|
| F0 Periodic magnitude | 独立；已有修复计划，本文件不重做 |
| F1–F3 | 不与 P1 抢 `GameplayRuntime` 叠层；不与 P3 抢 combo 通道 |
| F4 legacy Periodic | TryRestack 是其入口之一；P1 统一叠层后 F4 收口更简单，但不把 F4 并进 P1 |
| F6 Tag 扫描 | 计数已落地；P4 只补层级与词汇表。ClearEntity 热点另开 |
| F9 AbilityState | P6 时长态依赖稀疏 AbilityState，不倒过来改 P2 |

缺口计划 §7 增补 F10 / F11 / F12 登记，避免只活在对照笔记里。

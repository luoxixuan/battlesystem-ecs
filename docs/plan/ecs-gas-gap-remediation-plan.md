# ECS + GAS 终态缺口修正计划

> 状态：执行计划（本文不代表已实施）
>
> 更新日期：2026-09-04
>
> 基线 commit：`dd55f619`（F0–F9 核实）；F10 / F11 / F12 对照基线 `b5cfe52`
>
> 终态约束：[ecs-gas-final-architecture.md](../ecs-gas-final-architecture.md)
>
> 迁移总览：[ecs-gas-migration-plan.md](ecs-gas-migration-plan.md)
>
> Lumio 对照收口：[ecs-gas-lumio-contract-alignment.md](ecs-gas-lumio-contract-alignment.md)

## 1. 背景与本文职责

双轴审查列出 9 项与终态架构的实质偏差。本文记录对这 9 项的**代码级核实结论**，以及可交付执行的分阶段修正方案。

核实结果：9 项全部成立。其中 2 项被低估，另**发现 1 个审查未覆盖、会在帧内抛异常的 P1**。

| 项 | 审查结论 | 核实后的实际情况 |
|---|---|---|
| F1 玩家伤害绕过统一管线 | 只点了 `EnemyAISystem` | **10 个生产绕过点**；`ProjectileSystem.cs:487` 不在 `GameplayCommitLock` 下；另有 2 个走负 `ResourceRequest` 的旁路，减伤语义不同 |
| F2 FrameGraph 声明与实现不符 | 成立 | `AbilityRequest` 全仓库**零使用**（仅有定义）；`EffectRequest` 仅测试使用；另发现 2 个节点写 `PlayerResources` 未声明 |
| F3 combo 语义与塔维度不一致 | 成立 | 更严重：`maxStacks` 公式与自身 `Multiply` 语义互相矛盾（详见 §4） |
| F4 legacy Periodic owner | 3 个调用点 | **5 个**（另有 `TowerDemolishSystem.cs:180`、`SkillSystem.cs:894`） |
| F5–F9 | 成立 | 坐标已逐项核实，见 §7 |
| **F0（新增）** | 未发现 | Periodic magnitude 编译丢失 → 帧内抛异常，见 §3 |

**本轮范围**：F0 → F3 → F2 → F1。F4–F9 本轮只登记坐标与结论，不改生产代码。

**分阶段顺序的理由**：F0 独立且最危险，先行。F3 无 FrameGraph 变更，可独立验证。F2 与 F1 的图声明修正合并为一个 commit，因为节点 `reads/writes` 进入 `ComputeFingerprint`，改动一次就要重算一次批准根哈希——分两次做会付两次重算成本。F1 的站点切流放在图声明诚实之后。

---

## 2. 跨阶段约束

- 每个 phase 独立通过 §8 门禁后才进入下一个；任一硬门禁失败则该 phase 保持"进行中"。
- 每项修复按仓库既有防假绿做法执行"撤销修复 → 对应测试变红 → 恢复 → 全绿"的回退验证。
- 数值语义变化必须在 `CHANGELOG.md` 单独列出，不与重构条目混写。
- mode 2/4/5 与 Unity smoke 在本轮为 `DEFERRED` / `UNAVAILABLE`，必须与 PASS command manifest 分开记录，不得以未运行冒充通过。

---

## 3. Phase 0 — Periodic magnitude 编译丢失（新增 P1）

### 3.1 故障链

1. [`Core/GAS/CatalogCompiler.cs:701`](../../Core/GAS/CatalogCompiler.cs) 构造 Periodic `GameplayEffectDefinition` 时**未传 `periodicMagnitude`**，落到 [`Core/GAS/GameplayDefinitions.cs:189`](../../Core/GAS/GameplayDefinitions.cs) 该重载的默认值 `0f`；同一重载合成的 `PeriodicSpec` 其 `Payload` 为 `EffectPayloadKind.Damage`。
2. [`Core/GAS/CatalogValidator.cs:57-60`](../../Core/GAS/CatalogValidator.cs) 只校验 `Period`/`Duration`，**不校验 `Magnitude`** → 无效定义通过启动校验。
3. [`GameplayAbilityRuntime.ValidatePlan`](../../Core/GAS/GameplayAbilityRuntime.cs) 对 effect 只查 `IsDurationContractValid` → 同样放行。
4. `CommitPlan` 调 [`GameplayRuntime.cs:109`](../../Core/GAS/GameplayRuntime.cs) 的 `TryApply`：`Payload != GameplayEvent && magnitude <= 0` → reject reason 4 → 返回 `-1`。
5. 调用方 [`GameplayAbilityRuntime.cs:261`](../../Core/GAS/GameplayAbilityRuntime.cs) **抛 `InvalidOperationException("prevalidated ability plan was rejected during commit")`**。

### 3.2 命中范围

`Data/Configs/skills.json` 中 **Poison Nova**、**Dragon Breath**、**Meteor Strike** 三个技能命中该路径（`DotTickInterval = 1`，且其 `Debuff` modifier 的 `Duration > 0`，因此 `IsDurationContractValid` 通过、走到 commit 才炸）。

配置中的 `Value`（8 / 5 / 4）**已被正确编译进 `ExecutionDefinition`**（`CatalogCompiler.cs:696`），只是没有传给 effect 的 `PeriodicSpec`——数据存在，丢在传参上。

三者当前不在默认技能栏，故全量测试仍为绿；一旦进入任一玩家能力槽，激活即抛异常。这同时违反终态不变量 10：新增配置在启动校验失败时必须显式失败，而不是静默留到运行期。

### 3.3 改动

1. `CatalogCompiler.cs:701`：补**命名实参** `periodicMagnitude: magnitude`。必须用命名实参，避免与 `grantedTags` / `blockedTags` / `stackKey` 的位置参数混淆。
2. `CatalogValidator.cs`：新增一条与运行期 reason 4 对齐的规则——`Type == Periodic && Periodic.Value.Payload != EffectPayloadKind.GameplayEvent` 时 `Magnitude` 必须有限且 `> 0`，否则抛 `CatalogValidationException`，消息中带 `effectId` 以便定位配置。
3. 顺带修正 [`Systems/HitTriggerSystem.cs:34`](../../Systems/HitTriggerSystem.cs) 的失效注释（声称 scheduler 每帧调用 `ResetCounters()`，实际该类在生产从未实例化，且已被 [ecs-gas-migration-combat.md](ecs-gas-migration-combat.md) 明确记为 disabled）。**仅改注释，不动行为。**

### 3.4 测试

- `Framework/CatalogCompilerTests.cs`：编译带 DoT + `Debuff` modifier 的 curated skill 后，断言 `effect.Periodic.Value.Magnitude` 等于该 modifier 的 `Value`（期望值从读入配置推导，不钉具体常量）。
- 校验层测试：构造 `Payload = Damage`、`Magnitude = 0` 的 Periodic 定义，断言 `CatalogValidator.Validate` 抛出——这条测试锁定"启动显式失败"合同。
- `Integration/`：以 Poison Nova 形状的技能走生产激活路径，断言**不抛异常**、periodic effect 成功挂载、后续 tick 产生伤害事实。

---

## 4. Phase 1 — 属性键空间与 combo 语义

**已确认语义决策**：采用终态文档的 `DamageOutputMultiplier Add`，**塔也享受 combo 增伤**。

### 4.1 当前错用

`AttributeSchema.Default`（[`Core/GAS/Attributes.cs:46-58`](../../Core/GAS/Attributes.cs)）声明 key 0 = `AttackDamage`（Points，默认 0），key 8 = `DamageOutputMultiplier`（Scalar，**默认 1.0**）。

但 [`Core/ComponentStore_Attributes.cs`](../../Core/ComponentStore_Attributes.cs) 的实际用法：

- `:69` 塔把**绝对**伤害 `TowerAttackDamage[towerId]` 写进 key 8（一个声明为 1.0 基准标量的槽位）
- `:74` / `:79` 敌人与玩家用 key 0
- `:90` 塔 projection 读 key 8；`:95` / `:100` 玩家与敌人读 key 0

后果：combo modifier 编译到 key 0，落在塔实体的 key 0 槽位上，**无任何代码读取，完全惰性**——塔累计命中、挂上 effect，伤害却不变。

同时，[`CatalogCompiler.cs:113`](../../Core/GAS/CatalogCompiler.cs) 的 `maxStacks = ceil((MaxMultiplier - 1) / DamageBonusPerKill)` **只在 Add 语义下自洽**。当前 `:116` 编译为 `Multiply (1 + b)`：按 shipped 配置 `b = 0.05`、`max = 3.0` 得 `maxStacks = 40`，实际满层为 `1.05^40 ≈ 7.04×`，而 cap 意图是 `3.0×`。即当前实现与它自己算出的 cap 互相矛盾。

### 4.2 改动

1. [`Core/GAS/CatalogRegistries.cs`](../../Core/GAS/CatalogRegistries.cs)：补 `DamageOutputMultiplier` 访问器（键表已含 key 8，仅缺 property），避免继续散落 `new AttributeKey(8)` 字面量。
2. `ComponentStore_Attributes.cs` 的 `SyncComputedAttributeBases`：塔 base 从 key 8 迁到 `CatalogRegistries.AttackDamage`（key 0），与敌人 / 玩家统一。key 8 **不** `SetBase`——schema 已把其默认值定为 `1f`，projection 读取时显式传 `1f` 作为 base。
3. 三条 projection 统一为 `computed(key0, base) * computed(key8, 1f)`：`GetTowerAttackDamage`、`GetPlayerAttackDamageProjection`、`GetEnemyAttackDamageProjection`。key 8 由此恢复"唯一通用增伤倍率"语义，且 `ModifierOp` 仍只由 `AttributeAggregator` 解释（不变量 3）。
4. `CatalogCompiler.cs:116`：改为 `new ModifierDefinition(CatalogRegistries.DamageOutputMultiplier, AttributeModifierOp.Add, spec.DamageBonusPerKill)`。`maxStacks`（`:113`）公式**不动**——改成 Add 后它才成立。
5. **待决（见 §9）**：[`GameplayAbilityRuntime.cs:645-648`](../../Core/GAS/GameplayAbilityRuntime.cs) 的 `ResolveMagnitude` 对敌人 / 塔读**裸字段** `EnemyDamage` / `TowerAttackDamage`，只对玩家读 projection。为满足"单一解释者"应改为统一读三条 projection，但这会让 ability magnitude 也吃增伤，属额外数值变化。

### 4.3 数值影响（必须写入 CHANGELOG）

- 玩家 combo 由 `(1+b)^n` 变为 `1+n*b`：**变弱**，但与 `maxStacks` 公式一致，也与 legacy `ComboSystem.GetComboDamageMultiplier` 的 `1 + count * bonus` 一致。
- 塔**首次**获得 combo 增伤：变强。

### 4.4 测试

- 更新既有编译形状断言（改为新语义下从配置推导的期望值，**不是**改数字迁就实现）：
  - `Framework/SystemRegistryTests.cs:107`
  - `Framework/CatalogCompilerTests.cs:98`
  - `Integration/GameplayCatalogProductionFlowTests.cs`（现断言 `Multiply` / `1.5f` / projection `150f`）
- **补审查指出的塔侧覆盖缺口**（当前唯一完全没有测试的方向）：新增塔维度生产入口测试——塔命中累计到阈值后 `GetTowerAttackDamage` 反映 `1 + n*b`，且实际造成的伤害随之提高。
- 新增 key 空间守卫：断言塔 / 敌人 / 玩家的 base 都写在 key 0，key 8 只承载倍率类 modifier。

---

## 5. Phase 2 — FrameGraph 声明诚实化（**一次** fingerprint 重算）

以下 (a)(b)(c) 必须放在**同一个 commit**。

### 5.1 (a) 让声明匹配真实执行

[`Core/FrameSystemGraph.cs`](../../Core/FrameSystemGraph.cs)：

| 节点 | 声明 | 实际执行 | 改动 |
|---|---|---|---|
| `effect.commit`(:323) | read `EffectRequests` | `GameplayEffectsRuntime.Tick`（[`FrameScheduler.cs:447`](../../Core/FrameScheduler.cs)），不消费任何 `EffectRequest` | 移除该 read |
| `build.effect.commit`(:112) | read `EffectRequests` | `Tick(delta, ClockId.Build)` | 移除该 read |
| `ability.commit`(:344) | read/write `AbilityRequests`、write `EffectRequests` | `SkillSystem.Update`（[`SkillBuffGroup.cs:52`](../../Core/SkillBuffGroup.cs)） | 移除这三项 |
| `build.ability.reject`(:116)、`non-wave.ability.reject`(:123) | read/write `AbilityRequests` | `RejectNonWaveAbilityWork()` | 移除 |
| `CombatWrite`(:13) | 含 `EffectRequests` | 无任何 `Register*` 引用 | 删除死代码 |

更名去掉误导性命名：`effect.commit` → `effect.tick`（与既有 `effect.tick.combat` 等同族）；`ability.commit` → 反映真实行为的 id。需同步：[`FrameBindingFacts.cs`](../../Core/FrameBindingFacts.cs)、[`FrameAdapterBindingCatalog.cs`](../../Core/FrameAdapterBindingCatalog.cs)、`SkillBuffGroup.cs:52`、`tools/system-registration-spec.json`，并运行 `tools/generate-system-registry-ledger.ps1` 同步 manifest 与 nullable ledger。

若移除后 `FrameResource.AbilityRequests` / `EffectRequests` 再无引用，从 [`Core/FrameGraph.cs:26`](../../Core/FrameGraph.cs) 一并删除，避免留下"看起来存在管线"的 token。

### 5.2 (b) 补 Phase 3 需要的漏声明

两个节点调用 `DecreasePlayerHealth` 却未声明 `PlayerResources`：

- `combat.player-attack.update`(:273)：reads 与 writes **都**缺 `PlayerResources`，补上；另补 `ResourceRequests` 写以对齐同族节点。
- `combat.tower-attack.update`(:282)：writes 缺 `PlayerResources`，补上（reads 已有）。

正确形状参照 `spatial.telegraph.update`(:265)。

### 5.3 (c) 重算两个批准根哈希

节点 `reads` / `writes` 进入 `ComputeFingerprint`（[`Core/FrameAccessReviewCatalog.cs:363-389`](../../Core/FrameAccessReviewCatalog.cs)），其结果进入 `ValidateApprovedSnapshot`。**不重算则 `SealGraphComposition` 直接抛 `FrameGraphValidationException`**：

- `FrameAccessReviewCatalog.cs:267` `ApprovedFingerprintRootGameplay`
- `FrameAccessReviewCatalog.cs:268` `ApprovedFingerprintRootFixedPopulation`

### 5.4 明确不在本轮

`GameplayEvents` 的声明在现有图中本就不一致：`combat.mana.update`(:297) 与 `combat.pickup.update`(:296) 声明 `PlayerResources + ResourceRequests` 而不声明 `GameplayEvents`；`spatial.telegraph.update`(:265) 反之。无法从代码断定 `ResourceRequests` 是否被当作"提交给 resolver、由 resolver 发布"的代理声明。**留作单独一致性 pass**，以免本轮付第二次 fingerprint 重算。

### 5.5 测试

- `Framework/FrameGraphTests.cs`：用既有 `AssertProfileResources` 辅助钉住两个 combat 节点修正后的完整 reads / writes。将来漏声明会给出可读 diff，而不是不透明的哈希不匹配。
- 断言 `SealGraphComposition()` 成功——即两个根常量确实已更新。这是最容易漏的一步。

---

## 6. Phase 3 — 玩家伤害收口到统一事实管线

### 6.1 现状

[`Core/GAS/ResourceResolver.cs:87-123`](../../Core/GAS/ResourceResolver.cs) 的 `TryApply(PlayerDamageRequest)` 已是权威写入者：`GameplayCommitLock` + 完整 generation 校验（`CanApplyPlayerDamage`，`:76-85`）+ 6 字段快照 + 原子批量发布 + 发布失败时全字段回滚。

**10 个生产绕过点全部位于串行段**（已逐一核实其外层结构），因此**不需要**为并行安全做 collect / drain 重构。只有 site D 因锁嵌套需要重构。

### 6.2 新增共享 seam

在 `ComponentStore_Attributes.cs` 中紧随 `ApplyDamageAuthority`(:12-19) 之后加入，与之成对：

- `ApplyPlayerDamageAuthority(sourceId, playerId, amount, out float applied, ...)`
- 无 `out` 的重载（供无需发布事件的站点使用）
- `CanApplyPlayerDamageAuthority(...)`

三条硬约束：

1. `ownerPlayerId` 固定为 `playerId`。`ResourceResolver.cs:81` 要求 `OwnerPlayerId == targetId`，传别的值必然被拒，因此 seam 不能暴露这个旋钮。
2. **sequence 必须在 seam 内、提交时刻分配**。`AllocateGameplaySequence` 是共享 `Interlocked` 计数器；任何调用点预先分配都会破坏 Gameplay event sequence digest 的确定性。
3. `out applied` 供 4 个需要发布 legacy `PlayerDamaged` 的站点使用**真实减伤后**数值。

### 6.3 切流顺序

| Tier | 站点 | 说明 |
|---|---|---|
| A1 | `EnemyAbilitySystem.cs:712` | 最佳 canary：strict catalog 下该分支生产不可达，零爆炸半径 |
| A2 | `EnemyMovementSystem.cs:949`、`:1135` | source id 已在手，无队列改动，低频 |
| B1 | `EnemyProjectileSystem.cs:185` | 队列元组补 owner enemy id；**同时修** `:192` 硬编码 `playerId = 1` 与 `:155` `PositionX[1]`，改用 `store.PlayerEntityId`——迁移后该错配会被 `CanApplyPlayerDamage` 拒绝并静默丢伤害 |
| B2 | `TowerAttackSystem.cs:2703` | `_thornsQueue` 元组补 enemy id（3 处 add） |
| B3 | `PlayerTowerAttackSystem.cs:371` | `_thornsQueue` 由 `List<float>` 补成带 enemy id（2 处 add） |
| C | `EnemyAISystem.cs:1056`、`:1094`、`:1156` | 三处同形，一起改；分开会让 stealth 预检在三个兄弟方法间不一致。**须先满足 §6.4 两个前置** |
| D | `ProjectileSystem.cs:487` | 唯一结构改动：新增 `_thornsQueue` ping-pong，drain 放在 `:421` 之后**无系统锁**的串行尾部，避免把 `GameplayCommitLock` 嵌进 `_damageQueueLock` |

### 6.4 Tier C 的两个前置

**(1) 状态先于写入被消耗。** `EnemyAISystem.cs:1053-1055` 先吃掉并重置 `EnemyStealthMultiplier`，再写血。`DecreasePlayerHealth` 不会失败，`TryApply` 会（玩家已死、容量溢出）。必须在**重置倍率之前**用 `CanApplyPlayerDamageAuthority` 预检并 `return`，否则隐身 buff 会在拒绝时白白消失——只在队列压力或玩家死亡那一帧才暴露的静默回归。

**不要复制** `EnemyAbilitySystem.cs:469` 的 `throw`。那里的 `throw` 源于 `GameplayAbilityRuntime` 的两阶段合同；这 10 个站点没有该合同，被拒绝就等于"这次没打中"：跳过、不发事件、不改状态。

**(2) 容量 gate。** `ResourceResolver.Events` 为 `(8192, 64)`，每帧清空；每次接受的 apply 花 1 个事实（致死 2 个）。而 [`Systems/BehaviorTreeEvaluator.cs:249-253`](../../Systems/BehaviorTreeEvaluator.cs) 的 `can_attack` 是纯 1.5 曼哈顿距离判定、**无冷却**（`SetEnemyAILastAttackTurn` 被写入但从不作为门控读取），所以接触中的敌人每帧都攻击——Tier C 的事件量是 O(接触敌人数)。

落 Tier C 前必须在 A / B 完成后测出 `Events.PeakCount`；若逼近 8192，在**同一 commit** 内提升 `ResourceResolver.cs:48` 的容量，使证据与改动配对。mode 4/5 本轮 DEFERRED，改用测试 harness 驱动 `FrameScheduler` + N 个接触敌人，读 `PeakCount` / `EventOverflowCount` / `GetRejectionCount(RequestQueueOverflow)` 取证。

**明确不做**：不把多次命中合并成每 producer 每帧一个 `PlayerDamageRequest`。合并会破坏事实与 apply 的 1:1 关系（digest 依赖它）、丢掉 `DamageApplied.Source` 的逐攻击者归属，且与 `DecreasePlayerHealth` 中 `PlayerMinHealthFloor` 和一次性复活闩锁的非线性裁剪语义不等价。

### 6.5 legacy `PlayerDamaged` 通道

10 个站点中**只有 4 个**发布该事件（`EnemyAISystem` 三处 + `EnemyAbilitySystem.cs:715`）；thorns / trample / leap / projectile 从不发布。因此 `RallySystem` 目前只对近战 / 远程 / 蓄力 / legacy-AoE 起效。**给沉默站点补发布会新启用 Rally，是伪装成重构的数值变更——本轮保持现有分布不变。**

本轮只做一件事：把这 4 处发布移到 accept 之后、改用 `result.Applied`。当前发布的是**未减伤**的原始值，Rally 与渲染层看到的数字与玩家血条实际变化不一致。参照已落地两次的 [`Systems/TelegraphSystem.cs:242-259`](../../Systems/TelegraphSystem.cs)。

把 `RallySystem` 迁到消费 `DamageApplied`、删除该通道及其 `ResolvePlayerIdFromEvent` 启发式（按血量最接近猜玩家，`MAX_PLAYERS = 10` 下同血量会解析错人）**留作后续里程碑**：它需要在 `combat.tower-attack.update` **之前**新增一个消费点，是一次独立的图拓扑变更，捆进来会让本 phase 无法独立测试。

### 6.6 明确不在本轮

`BossTrailAoeSystem.cs:142` 与 `SuicideBombSystem.cs:204` 通过负 `AttributeKey(3)` `ResourceRequest` 写玩家血量，落在 `ResourceResolver.ApplyCurrentHealthDelta`，**绕过护盾 / 护甲 / 血量下限 / 复活**。迁移到 `PlayerDamageRequest` 会让这两类伤害首次吃这些规则，属数值变更，需独立取证。

本轮只加守卫测试冻结白名单（该名单只能变短）。

### 6.7 测试

- `Framework/ResourceLifecycleAtomicityTests.cs` 扩展：sequence 在提交时刻分配且严格递增；`applied` 含护盾消耗；容量耗尽时 6 字段零变更且无事实入队；致死发 2 个事实且后续命中被拒。
- `Framework/` 守卫测试：`DecreasePlayerHealth` 在 `ResourceResolver.cs` 之外无调用方；负 `AttributeKey(3)` 的玩家 `ResourceRequest` 只允许 §6.6 白名单两处。
- `Mechanisms/Combat/PlayerDamageAuthorityTests.cs`（新）：thorns 反伤的 `DamageApplied.Source` 是**敌人**而非玩家（验证元组加宽）；被拒绝时 `EnemyStealthMultiplier` 保持原值（Tier C 预检位置放错即红）；敌方投射物命中 `PlayerEntityId` 而非槽位 1。
- `Features/Buffs/RallySystemTests.cs`：Rally 收到的是减伤后数值；Rally **不**由 thorns / trample 触发（不对称守卫）。
- `Integration/`：整帧多路径下 `EventOverflowCount == 0`；玩家 HP 总变化等于所有 `applied` 之和。

---

## 7. 本轮登记、不改代码的 P2

坐标均已核实，供后续轮次直接执行。

- **F4 legacy Periodic owner**：仍有 **5** 个生产创建点——`TerrainSystem.cs:83`、`TowerAttackSystem.cs:2094`、`CorpseEffectSystem.cs:203`、`TowerDemolishSystem.cs:180`、`SkillSystem.cs:894`。全部经 `BuffSystem.ApplyDot` → `LegacyEffectAdapter.CreateApplication`，后者刻意不置 `RuntimeOwned`。双计时已由 `BuffSystem.cs:81-83` 的 guard 避免，但"单一 Gameplay Runtime owner"未达成。收口需要把这些 DoT catalog 化并直接调 `GameplayEffectsRuntime.TryApply`，或新增一个标记 `RuntimeOwned` 的 adapter。
- **F5 Effect 生命周期策略**：`SourceDeathPolicy` 只有 `Persist` / `Remove`，缺终态要求的 `Transfer`；4 个消费点全是 `==` 比较、无 `switch`。`CatalogCompiler` 8 处硬编码 `ClockId`、**所有** effect 硬编码 `SourceDeathPolicy.Persist`；`FirstTickPolicy` / `CatchUpPolicy` 在该文件零出现，由 `GameplayDefinitions.cs:189` 重载写死为 `NextInterval` / `CatchUpAll`。typed DSL 的三张属性白名单均无 `Clock` / `FirstTick` / `CatchUp` / `SourceDeath` 键，且 `ValidateProperties` 拒绝未知键——**当前无法通过配置表达这些策略**。
- **F6 Tag contribution**：`GameplayTagRuntime.HasTag` 每次查询做全槽位扫描；`Matches` 对 source 与 target 各做 (R+B) 次扫描，且在每次 ability 校验与每个 targeting 候选上被调用——兼有正确性与热路径成本问题。无 `TagState` / 贡献计数 / contribution handle。`GameplayEffectDefinition.BlockedTags` 已编译、已校验、**运行期零读取**（配置可写、校验通过、然后被静默忽略，违反不变量 10）。
- **F7 Periodic magnitude 来源**：非 `Constant` 在 apply（`GameplayRuntime.cs:153`）与 register（`:391`）两处被拒。`MagnitudeSource.Attribute` **生产零实现**——连 ability 侧的 `ResolveMagnitude` 也把它落进 `else if` 当常量处理，是第二处静默降级。
- **F8 Trigger reset 合同自相矛盾**：`TriggerResetPolicy` 的命名与行为相反——`Explicit` 的计数器被每帧 `ResetFrame` **自动**清除，`None` 反而跨帧保留（这正是 `EveryN` 阈值累计得以工作的原因）。`ResetCounters()` 无条件全清且**无生产调用方**。`Framework/GameplayRuntimeTests.cs:693` 名为 `ExplicitTriggerCounterResetClearsOnlyWhenRequested`，但从不调用 `ResetFrame`，改成 `None` 也照样通过——该合同实际未被测试。
- **F9 AbilityState**：无该类型。cooldown 分散在三套存储：`AbilityInstance.CurrentCooldown` 位于 `MAX_ENTITIES * MAX_ABILITIES_PER_ENTITY` 数组、按值内嵌 29 字段的 `GameplayAbilityDef`，而所有调用方只传 `playerId`；调用方自有 `float[]`（`EnemyAbilitySystem`、`HeroSkillSystem`）；另有约 40 条专用 SOA cooldown 列。无 charges。主激活入口 `AbilityActivationRequest` 全程用裸 `int`，`AbilityRequest`（handle 版）零使用。`SkillSystem.FindSlot` 每次激活按**字符串比较**查槽位。

2026-09-03 后续进度（登记快照之后，未宣称 F4–F9 终态收口）：ApplyDot 的 None 走 `TryAdopt`、叠层走 `TryRestack`；`TryAdopt`/`TryRestack` 已跑 BlockedTags 与 Periodic payload 校验。`HasTag` 只走 `TagState`。死亡回调节点已去掉假 `AbilityRequests`。5 个创建点尚未 catalog 化。`AbilityRequest` 仍无 command buffer；主入口仍是 `AbilityActivationRequest`。`PlayerDamaged` / Rally 未拆通道。

2026-09-03 再续（仍未宣称终态收口）：lava / firewall / corpse / demolish 经 `ProductionDotCatalog` 物化后 `TryApply`（空 modifier）；Skill 有 catalog Periodic 时直接 `TryApply`，否则 fallback adapter。`AbilityRequest` 已入 command buffer，`Activate(AbilityRequest)` 为主入口（多目标仍 `AbilityActivationRequest`，经 `ActivateCore` 入队）。敌方/英雄/全局/塔主动技能冷却并进 `AbilityState`/`AbilityCooldownColumn`；burrow/leap/totem 等机制 SOA 未并。Rally 不再订 `PlayerDamaged`，改消费 `DamageApplied`，新增 `combat.rally.consume`。`TryRestack` 同 StackKey 还要比 Name。`EffectRequests` 仍是死 token。`AbilityState` 仍嵌在 `AbilityInstance`。`TryAdopt` 仍不 `ApplyModifiers`。

2026-09-04 诚实化（仍未宣称终态收口）：`AbilityRequests` 不是 command buffer——`ActivateCore` 同步 `CommitPlan`，只在 accept 后写入当帧日志，拒绝不占槽，满槽不得回滚。`build.skill/auto-skill/global-skill.update` 补 `AbilityRequests` 写声明。Rally 节点 writes 改为真实 `PlayerAttributes` + `TowerState`（不再假写 `TowerCombatCache`）。`ApplyDot` 的 None 改回 `TryAdopt`（尸体区/Firewall/岩浆脉冲重挂可并存多份）；GAS `TryApply` None 仍是同 key 不刷新。Skill catalog Periodic 仍走 `TryApply`。无 consume/commit 节点；`TryAdopt` 仍不 `ApplyModifiers`；稀疏池与机制 SOA 冷却未做；`EffectRequests` 死 token 保留。

2026-09-04 终态收口续（仍未宣称 F4–F9 / M5 / M6 完成）：`AbilityRequests`/`EffectRequests` 成为真 buffer；`ability.commit` 在 Combat 前，`effect.commit` 在 `effect.tick` 前；`TryAdopt` 补 `ApplyModifiers`；稀疏 `AbilityState` 池 + `AbilityInstances` facade；burrow/leap/totem 并进 `MechanismCooldownColumn`。`Stacking.None` 同 key 不叠槽。catalog 敌方技能延到 PreCombat commit。未把 Periodic 改成 `ENEMY_HEALTH` modifier；未合并其余机制冷却族；未删 `FrameResource.EffectRequests`。

2026-09-04 能力 GE 解耦（仍未宣称 F4–F9 / M5 / M6 完成）：`CommitPlan` 对 granted effect 只 `EnqueueApply`，与 `ApplyDot` 同在 `effect.commit` `TryApply`。Combat 段读不到当帧能力 modifier/tag。敌方 `Execute*` 当场结算已去掉。技能伤害执行仍在 `ability.commit`，不是 `damage.commit` 缓冲。

2026-09-04 对照登记（不改生产代码，实施走 [lumio-contract-alignment.md](ecs-gas-lumio-contract-alignment.md)）：

- **F10 TryApply / TryRestack 叠层加成不一致**（**P1 已实施 2026-09-04**）：`TryApply` / `TryRestack` 共用 `RestackLedger`；`stackCount` 乘数；不再每层扩 handle；`periodicMagnitude` 与 modifier 捕获分列。原路径判断坐标作废。
- **F11 同帧多 AbilityRequest 消耗无预留**（**P2 已实施 2026-09-04**）：入队按 sequence 预留 Spend 资源与容量；Commit 先复查再 `CommitPlan`；`CommitCosts` 只走 `Spend`。原路径判断坐标作废。通用 `ApplyMana` 夹紧未改。
- **F12 预校验通过后 throw**（**P2 已实施 2026-09-04**）：`Core/`/`Systems/` 不再有 `prevalidated` 且 `during commit` 的 throw。容量/消耗竞争走 `AbilityCancelled` 或 `QueueOverflow`；dispel/预警/召唤跳过失败 slot；群体复活保留已成功单位；回放拒绝该次 Restore。
- **F6 更正**：`HasTag` 已走 `TagContributionState` 计数；最初「无贡献计数」只适用于登记当时。剩余是层级（平 `TagId`、无 parent 词汇表）与 `ClearEntity` 扫表分配。

---

## 8. 门禁

每个 phase 独立执行，全部通过才进入下一个（[AGENTS.md](../../AGENTS.md) §8）：

```bash
dotnet build BattleSystemECS.Engine        # 0 warnings, 0 errors
dotnet build BattleSystemECS.Core          # 0 warnings, 0 errors
dotnet build                               # 0 warnings, 0 errors
dotnet test BattleSystemECS.Tests
powershell -File tools/check-test-rules.ps1
git diff --check
```

> `pwsh` 在当前主机不可用，测试静态规则门禁用 `powershell` 执行。

阶段附加项：

- **Phase 2**：确认 `SealGraphComposition` 通过（两个根 fingerprint 已重算）；运行 `tools/generate-system-registry-ledger.ps1` 同步 manifest 与 nullable ledger。
- **Phase 3 Tier C 之前**：用测试 harness 取得 `ResourceResolver.Events.PeakCount` 证据。
- mode 2/4/5 与 Unity smoke 保持 `DEFERRED` / `UNAVAILABLE`，单独记录，不并入 PASS command manifest。
- 文档同步：`CHANGELOG.md`（Phase 1 的数值变化单列）、[ecs-gas-migration-combat.md](ecs-gas-migration-combat.md)、[ecs-gas-migration-orchestration.md](ecs-gas-migration-orchestration.md)、`AGENTS.md`（若节点 id 更名影响关键文件速查表）。

---

## 9. 执行前需决定的事项

1. **§4.2 第 5 项**：`GameplayAbilityRuntime.ResolveMagnitude` 是否一并改为读三条 projection。改则满足"单一解释者"，但 ability magnitude 也会吃增伤，是额外数值变化。
2. **§6.4 (2)**：`BehaviorTreeEvaluator.cs:249-253` 的 `can_attack` 缺攻击冷却是否按 bug 修。若修，会显著改变战斗数值，但同时直接消除 Tier C 的容量风险；若不修，Tier C 必须带容量提升。
3. **对照 P0**：准入序里 `PhaseNotAllowed` 先于 `Cooldown` / `Cost` **已写入**终态 §6.1（整张冻结表：形状类 `NoTarget` / `UnsupportedDefinition` 归第一段；容量独立 `QueueOverflow` 放末尾）。若要改回「先冷却后阶段」，必须先改终态再改代码。

F10 / F11 / F12 与属性公式的实施顺序以 [ecs-gas-lumio-contract-alignment.md](ecs-gas-lumio-contract-alignment.md) 为准，不在本文件的 F0→F3 飞行中插入。

第 1、2 项都属数值语义变更，不应在实施中静默决定。第 3 项是合同文档，P0 已写入终态。

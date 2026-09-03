# M8 稳定观察与存储决策证据

状态：有界候选实现，尚未满足整个 M8 退出门槛，也未提交或推送。

死亡生命周期事实采用 reservation commit：Prepare 对 Damage/Resource 两条队列按稳定锁序
预留整批 required facts，Dispatch 完成奖励、回调与销毁后才公开事实。queue-full、预检后
受控生产者、回调重入/读取/异常、跨帧重试与重复 Resolve 均由确定性回归测试覆盖。
prepared read bag 是本次唯一清理目标；回调写入的 alternate bag 保留到同帧 cascade resolve
或下一次 `BeginFrame`。生命周期订阅者在注册期形成数组快照，dispatch 逐个捕获异常并在整批
销毁/事实 commit 后重抛首异常。Damage/Resource/Shield 的 1/2/3-fact 提交不构造临时数组，
warmup 后 256 轮真实成功循环的 managed allocation 为 0 bytes。reservation 的 buffer rent 先于
slot 占用，受控 rent exception、幂等 Commit/Dispose、反向锁序与 hash collision 分支均有测试。

基线为 `4bebc43024a74fd52462d6cb31a19ed0aa34efa3`。本轮保持
Ability/Effect/Trigger/Request/Resolver 的行为合同和 FrameGraph topology 不变，且不修改
`Core/BuffType.cs`。唯一生产存储改动位于 `EffectPool` interface 后：逻辑容量、
`EffectHandle(index, generation)`、失败原因、释放/复用和 stale 拒绝语义不变，handle 元数据从
一次性 dense 数组改为 256 槽按需分页。新页 free-list 槽位统一初始化为 `-1`，这是防御性
invariant hardening；基线中 reviewer 给出的 `capacity=1` 自环序列本身恒绿，因此不将其冒充为
一个已复现的 red→green bug 修复。分页容量、generation、stale handle 与 active 计数合同由现有
跨页/耗尽/复用回归测试覆盖。

## 稳定观察

`GameplayObservation.Capture(store)` 是内部、显式调用的只读观测 interface；生产 Tick 不自动
采样。它聚合固定队列 high-water、Damage/Resource pending peak、按 reason 拒绝、stale handle、
Effect handle/runtime 分页利用率、Trigger seen/counter/definition、overflow、unconsumed、abort、
publication failure、legacy apply 和死亡计数。每个事件队列还累计无分配的
`GameplayEventSequenceDigest`/published count（按成功 publication 顺序累计，merge 不改变 source
语义），snapshot 同时提供按稳定实体字段计算的 `StateDigest`。published count 默认准确累计；storage
profile 的 intended invariant 是不伪造 gameplay facts，因此其真实 storage 入口必须满足
`GameplayEventPublishedCount == 0`；`StateDigest` 与 `GameplayEventSequenceDigest` 仍作为显式合同字段输出
（前者描述状态，后者描述空事件序列）。只有
digest hash 由 soak harness 显式调用 `GameplayObservation.EnableDigests(store)` 开启，默认生产 Tick
不承担这段诊断 hash 成本。DamageResolver 的公开 observer 在 HitConfirmed、DamageApplied、
DeathQueued（致死时）和状态提交完成后，仅以 HitConfirmed fact 同步调用一次；observer 不分别接收三个事实，
避免 observer 重入造成半提交；required publication
预留失败会拒绝请求。

稳定场景由测试执行并可选择写仓外 JSON：

- storage profile 在真实 `ComponentStore` inventory 状态上显式调用 `GameplayObservation.EnableDigests`
  后捕获 observation；报告同时写出 top-level 与 nested `StateDigest`、
  `GameplayEventSequenceDigest`、`GameplayEventPublishedCount`。capture 对三轮逐字段校验两层一致性，
  并以缺字段、两层 mismatch、篡改 nested count 三个负例证明解析失败即 FAIL。该场景不伪造战斗事件。

- sealed production FixedPopulation graph：10,000 敌、500 帧，population 固定；每轮稳定得到
  Damage accepted 200,000、pending peak 400、event peak 800，Resource pending/event peak 2，
  Trigger seen peak 842、counter peak 1、event peak 40，Effect pool peak 3；所有 overflow、
  unconsumed、rejection、abort、publication failure 和 legacy apply 为 0；三轮的
  `StateDigest`、Gameplay event sequence digest 和 published count 一致；
- lifecycle soak：128 轮 Periodic Damage → DeathResolve → entity ID 回收；128 次死亡全部解析，
  128 个刻意投递的旧代 target 全部记录为 `InvalidTarget`/stale，下一轮继续成功；容量、事件、
  runtime state 和 legacy 诊断均无意外失败；三轮 digest 和 published count 一致；
- strict Catalog、Ability/Effect/Trigger、Damage/Resource、14-hit、递归上限与恢复继续由 M0-M4
  focused tests 覆盖。生产代码没有独立 `CommandBuffer<AbilityRequest>`，因此不虚构不存在的
  Ability queue peak；Ability 证据使用真实 strict-catalog activation/result 流。

## 存储 profile 与决定

反射 inventory 在当前 `ComponentStore` 中识别 723 个至少达到 `MAX_ENTITIES` 元素规模的数组。
估算 payload 分类为：Ability capped 候选 72.4 MB、legacy Effect projection 109.2 MB、Boss phase
候选 17.3 MB、其余 dense 252.1 MB。单个 store 构造分配由每次隔离 profile 原样记录，不作为
跨机器硬阈值。

同进程可复核 A/B 对 800,000 逻辑 Effect handle 槽测得：旧 dense 元数据分配 7,200,072 bytes，
分页 pool 初始分配 75,160 bytes，避免初始分配 7,124,912 bytes；跨页、满载、释放后同 index
新 generation 和旧 handle stale 拒绝均有测试。生产 soak 实际 peak=3，只分配一页 256 槽。

当前决定：

| 选项 | 决定 | 依据 |
|---|---|---|
| dense 高频核心列 + active lists | 保留 | 10K active-list 遍历持续快于 100K full scan；生产已使用 active lists |
| Effect handle metadata | 按需分页 | 低风险 interface 内替换，A/B 有明确初始内存收益 |
| Ability/legacy Effect/Boss phase 公共数组 | 暂不迁移 | 内存候选明确，但它们是公开 store surface；Unity 不可验证，不能把二进制/调用兼容风险冒充完成 |
| Trigger Dictionary/HashSet | 保留并观察 | production peak 远低于 cap 且无 overflow/abort，没有 CPU hotspot 证据支持自研开放寻址表 |
| Archetype/chunk | 不授权 | 未证明稳定签名迭代占 CPU 30% 或帧时间 20%，无 cache-miss 证据，mode 4/5 又按用户决定延期 |

因此当前完成状态是 `dense SOA core + sparse/paged GAS pool + active entity lists`，不是 Archetype
prototype。动态 Buff、层数和周期计时没有变成结构组件。

## 历史缺口回收

| 阶段 | 本轮回收 | 仍未完成/不冒充通过 |
|---|---|---|
| M0 | capture 在 evidence 首写前记录 HEAD/branch/index/patch/status/untracked hashes；production topology 与 soak 输入可复核 | mode 2/4/5 基线按用户决定延期；Unity 仍不可用 |
| M1 | 真实 production graph 记录 Damage/Resource/Effect/Trigger peak 与零 overflow；pool 分页、耗尽/恢复、generation 测试 | 不存在的 Ability queue 不伪造 peak；公开 legacy projection 未删除 |
| M3 | Damage/Resource rejection-by-reason、stale 和 pending high-water 可观测；strict production legacy apply=0 | mode 性能闸门延期，不据此重写历史退出结论 |
| M4 | Effect/Trigger 生命周期、递归上限恢复、14-hit、128 轮死亡回收与 production Trigger 利用率均可复核 | Trigger 表替换没有 profile 授权；Unity/mode 闸门未通过 |

## 最近完整验证证据与本次修复状态

最近一次完整验证证据目录：
`C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m8-player-damage-concurrency-20260903T031142Z`。
该目录记录 full tests 1805/1805、focused tests 29/29，但生成时间早于本次跨 resolver
共享提交锁修复。本次修复后按用户要求未重跑任何门禁，因此该目录不是 post-fix fresh PASS。

本轮补齐 PlayerDamage 的 mana shield/trigger/reincarnation 全状态回滚，并把 Damage/Resource
在同一 `ComponentStore` 上的目标/来源/死亡校验、快照、写入、事实发布、回滚和死亡入队
收进共享 commit lock；并发致死只允许一次 death reservation。

先前 `m8-observation-*` / `m8-death-*` 目录仅作为历史尝试，不作为本轮 PASS 依据。

`tools/capture-m8-fresh-evidence.ps1` 不覆盖非空目录；先捕获仓库状态再创建证据目录。manifest
记录实际执行 gate 以及 stability/inventory 派生一致性检查；`repository-state-manifest.json` 与
`recovery-snapshot/` 固化 initial/final 仓库状态和内容哈希。capture 完成后禁止任何仓库文件编辑，
若发现 HEAD、index、status、patch 或 untracked 内容漂移则 gate FAIL。该规则描述上述历史 capture，
不代表本次共享锁修复后的工作树已验证。mode 2/4/5 记录为 `DEFERRED`，Unity 记录为
`UNAVAILABLE/BLOCKED`，均位于 `deferred-and-blocked.json`，不会伪装成 PASS。最终以
`command-manifest.json`、三轮 profile/soak JSON 和 `evidence-sha256` 为准。

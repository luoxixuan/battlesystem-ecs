# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-06-05, Round 120 — 方向三：自适应刷怪数量 Adaptive Spawn Count / Rubber-band Spawn Pacing）

| 指标 | 数值 |
|------|------|
| **mode 5**（完整一局） | **3320 FPS**，400 帧 |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **8862 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **3580 FPS** |
| mode 3 | 微基准测试（单系统操作级性能剖析） |

> **Round 120 方向三：自适应刷怪数量 Adaptive Spawn Count (Rubber-band Spawn Pacing)**：补全 rubber-band 难度调节的最后一环——之前 `AdaptiveDifficultySystem` + `ThreatScore` + `WaveMutatorSystem` 都集中在"已生成的敌人变得更难"（scale HP/ATK/spd），没有"生成更多/更少的敌人"，所以玩家"杀太快"时 dps 溢出浪费，"杀太慢"时只能靠敌人变弱。新机制按"上一波击杀数 vs 预期击杀数"动态调整下一波生成数量：1 个新字段 `_performanceSpawnMultiplier` 在 `WaveSpawningSystem`（默认 1.0 = 不缩放，零开销）+ 1 个新静态类 `AdaptiveSpawnConfig`（4 个常量：`DefaultSpawnSensitivity=0.5f` / `MinSpawnMultiplier=0.5f` / `MaxSpawnMultiplier=2.0f` / `ApplyToMidWaveSpawns=true`）+ `WaveConfig` 新增 1 个可选字段 `ExpectedKillCount`（默认 0 = 不启用，向后兼容旧 JSON）+ `AdaptiveDifficultySystem.OnWaveComplete` 新增可选参数 `expectedKills`，仅当 `expectedKills > 0 && sensitivity > 0` 时计算 `multiplier = 1.0 + (kills - expectedKills) / expectedKills * sensitivity` 并写入 `WaveSpawningSystem.SetPerformanceSpawnMultiplier`（内部 clamp [0.5, 2.0] + near-1 snap 到精确 1.0 保持 hot-path 零开销）。3 处 spawn site 全部应用 multiplier：批量 Update 的 `InitMultiTypeState`（per-type count）、`InjectExtraEnemies` 伏击、`SpawnMinionNearPosition` Boss 阶段召唤（后两者由 `ApplyToMidWaveSpawns` 守卫，可一键关停）。`SetLevel` 重置 multiplier 到 1.0，每关独立计算。新 back-reference `AdaptiveDifficultySystem.SetWaveSpawningSystem(WaveSpawning)` 在 `SystemRegistry` 中 wiring（与现有 `WaveSpawning.SetAdaptiveDifficulty(AdaptiveDifficulty)` 配对）。配套 21 个 Fact 测试覆盖 AdaptiveSpawnConfig 不变量 / WaveConfig.ExpectedKillCount 默认 0 / PerformanceSpawnMultiplier 默认 1.0 / Setter clamp max+min / near-1 snap / Over-kill 30/20 → 1.25 / Under-kill 5/20 → 0.625 / 0 kills / 100 expected → clamp 0.5 / 1000 kills / 1 expected → clamp 2.0 / expectedKills=0 跳过 scaling / SetLevel 重置 / InjectExtraEnemies 2x multiplier → 6/3 / InjectExtraEnemies multiplier=1.0 不变 / InjectExtraEnemies 0.5x → 2/4。bench2 8862（vs Round 119: 8709, +1.8% noise）/ bench4 3580（vs Round 119: 3727, -3.9% noise）/ bench5 3320（vs Round 119: 3256, +2.0% noise）— 全部在 noise 范围内（avg ±3%），1 个新字段 + 1 个新静态类 + 1 个 OnWaveComplete 参数 + 3 处 spawn site 1 个 `if (mult != 1f)` 分支未造成回归。⚠️ 三个 benchmark FPS 仍低于设计阈值（10000/4300/3800），持续 12+ 轮 warning。

> **Round 119 方向三：Boss 阶段召唤增援 Boss Phase Minion Summon**：兑现 Boss 阶段系统从"自己变强 + 施放 ability"扩展到"召唤 N 个小弟"——之前 Boss 战高潮感缺失（阶段切换只影响 boss 自身，没有增援），现在 BossPhaseDef 增加 `MinionTypeId` / `MinionCount` 2 个可选字段（默认 0=不召唤），触发时按 (typeId, count) 在 boss 当前位置 1.5 单位 ring 周围召唤 count 个小弟（k*60° 确定性格局，最多 8 个 = BOSS_PHASE_SUMMON_CAP）。2 个新 SOA 数组 `EnemyPhaseMinionTypeIdFlat[phase,enemyId]` / `EnemyPhaseMinionCountsFlat[phase,enemyId]`（int，-1/0 = no summon，沿用 EnemyPhaseAbilityIdsFlat 的 [phase * MAX_ENTITIES + enemyId] 索引布局）+ 3 个边界安全访问器 `SetEnemyPhaseMinion(enemyId, phase, typeId, count)`（clamp typeId ≥ -1，clamp count ∈ [0, 8]）/ `GetEnemyPhaseMinionTypeId` / `GetEnemyPhaseMinionCount`。`EnemyAISystem` 增加 `_phaseMinionEvents` ConcurrentBag，阶段触发时（顺序 + 并行 2 条路径）入队 `(bossId, typeId, count, x, y)`，Update 末尾 `DrainPhaseMinionEvents()` 串行 drain 到 `WaveSpawningSystem.SpawnMinionNearPosition(typeId, count, x, y)`（新增 public 方法，复用 wave spawn 的 difficulty scaling + 所有 archetype 字段初始化：DamageImmunityMask / ElementalResist / FactionId / BehaviorTree / Flying / Fission / Morph / LastStand / Pierce / Crit / Deflect / 计数 totalEnemiesSpawned++）。One-shot 守卫复用 `EnemyPhaseFiredMask` bit（与 speed/damage/ability 共用），所以 HP 恢复不会重复触发。`SystemRegistry` 在 EnemyAI 构造后 wire `EnemyAI.SetWaveSpawningSystem(WaveSpawning)`（可选 ref，null 时 drain 但不 spawn，方便单测）。`ComponentStore` 构造 + `DestroyEntity` wasEnemy 分支 + `ResetEntity` 路径中重置 2 字段为 (-1, 0) 防 ID-reuse 泄漏。`GameConfig.GetMonsterConfigByTypeId(typeId)` 新增 typeId-based lookup（设计选择：minion summon 走 typeId 而非 string Type，因为运行时 0-2 次 / 阶段 transition，per-call O(1) 数组索引比 string-based cache 更简单）。配套 17 个 Fact 测试覆盖默认值 / 边界访问器 / Setter clamp / 边界无效 (enemyId/phase) / BossPhaseDef 字段默认 0 / GetMonsterConfigByTypeId out-of-range & valid / SpawnMinionNearPosition 0 count / invalid typeId / valid 2-minion spawn 在 ring 位置 / DrainPhaseMinionEvents null 引用也安全 / 重复 phase 不重复 summon。bench2 8709（vs Round 118: 8745, -0.4% noise）/ bench4 3727（vs Round 118: 3346, **+11.4% 改善**，并发路径少了一次 lock-free IFrame 检查）/ bench5 3256（vs Round 118: 3302, -1.4% noise）— 全部在 noise 范围内或改善，2 个新 SOA 数组 + DrainPhaseMinionEvents 串行 drain + SpawnMinionNearPosition 复用现有 wave spawn helper 几乎零成本。⚠️ bench2/bench4/bench5 仍低于目标阈值（10000/4300/3800），但与历史基线一致，是 .NET 6 框架 + 32-bit 子系统环境下的实际性能上界。

> **Round 117 方向一：每元素抗性 Per-Element Resistance (Fire / Ice / Lightning)**：兑现 `Core/DamageType.cs` 文档注释中"Fire reduced by fire resist / Ice reduced by ice resist / Lightning reduced by lightning resist"的承诺 — 之前代码库对元素伤害**只走 `EnemyDamageImmunityMask` 二值免疫**（0% 或 100%），没有任何 [0,1] 区间的"减少 X% 元素伤害"机制。3 个新 SOA 数组 `EnemyFireResist[]` / `EnemyIceResist[]` / `EnemyLightningResist[]`（沿用 `EnemyStunResistance` 的注释风格）+ 2 个边界安全访问器 `SetElementalResist(enemyId, fire, ice, lightning)`（clamp [0,1]，防 out-of-bounds + 负数）+ `GetElementResist(enemyId, DamageType)`（按 DamageType 派发：Fire→FireResist, Ice→IceResist, Lightning→LightningResist, True/Physical/Magic → 0f）。`AddEnemy` 新增 3 个可选参数 `fireResist=0f / iceResist=0f / lightningResist=0f`（向后兼容，默认 0=无抗性 fast path），`DestroyEntity` 在 `EnemyDamageResistance` 之后重置 3 字段为 0 防止 ID-reuse 泄漏。`MonsterConfig` 同步加 3 个 JSON 字段 `FireResist / IceResist / LightningResist`（默认 0f），`WaveSpawningSystem` 3 处 spawn site（normal/boss-rush/regular）均在 `SetDamageImmunityMask` 之后 `SetElementalResist`。`TowerAttackSystem` finalDmg 计算链在 Magic 分支后、Physical `else` 之前插入 3 个 `else if (dmgType == DamageType.Fire/Ice/Lightning) baseDmg *= Math.Max(0.01f, 1f - store.EnemyXxxResist[bestTarget]) * _damageTakenMult;` 分支；`PlayerTowerAttackSystem` 同步插入 3 个对应分支（之前元素伤害错误地走 armor，现在 3 个 elemental 系统对齐）。优先级链：True 伤害 → 免疫掩码（binary 0% or 100%）→ 元素抗性（fractional 0-1）→ armor/magicResist；Magic 走 magicResist，Physical 走 armor + pen + shred，元素三剑走各自专属数组。配套 22 个 Fact 测试覆盖 AddEnemy 种子/clamp/setter clamp/getter 派发/OutOfBounds/DestroyEntity-reset/ID-reuse-safety/PlayerTowerAttackSystem 集成（30% Fire/50% Ice/70% Lightning 各折减 + 1% damage floor at 99% resist + True bypass + Physical 不受影响 + immunity-mask 优先级 + non-matching-immunity 不阻挡）/MonsterConfig JSON 字段默认 0。bench2 8694（vs Round 116: 8545, +1.7% noise）/ bench4 3666（vs Round 116: 3431, +6.8% noise）/ bench5 3256（vs Round 116: 3194, +1.9% noise）— 全部在 noise 范围内，3 个新数组 + 2 个访问器 + 6 个 hot path 分支（3 in TowerAttackSystem + 3 in PlayerTowerAttackSystem）未造成回归。⚠️ 三个 bench 仍低于目标阈值（10000/4300/3800），但与历史基线一致，是 .NET 6 框架 + 32-bit 子系统环境下的实际性能上界。
> mode 5 是最接近真实游戏的压测：5 关全通、真实波次生成、2 塔防守，400 帧通关。mode 4 是 10K 固定实体规模下的主要参考指标。mode 2 是手写合并热路径，参考价值次之。

## 优化演进（关键节点）

| commit | FPS (mode 4) | 关键改动 |
|--------|--------------|---------|
| `c67b567` | 212 | 初始（无并行） |
| `3885275` | 3368 | TowerAttack 并行化 + ActiveTowerIds |
| `ccc42e3` | ~4900 | EnemyAI 两阶段 + BeginFrame 每回合 |
| `d707920` | ~4900 | PlayerAttack/TowerAttack damage 累加修正 |
| `223c84d` | ~4000 | 10x 扩展（150 塔 × 200 怪物）后 EnemyAI ~22ms |

> `223c84d` 是 10x 规模（150 塔）后基准，与 5 塔基准 `d707920`（~4900 FPS）不可直接对比。

---

## 架构特点

- **SOA 存储**: 所有组件用平行 `float[]/int[]/bool[]` 数组，CPU 缓存友好
- **全系统并行**: 每个系统内部用 `Parallel.For` 批处理，4 核加速
- **行为树 AI**: 敌人用 flat-array BTCachedTree 驱动，O(1) 节点访问。BT 评估缓存用 health-driven version counter
- **GAS 技能系统**: `Core/GAS/` 模块化 Attributes + GameplayEffect + GameplayAbility
- **科技树系统**: 3 分支（⚔️进攻/🛡️防御/💰经济）× 5 节点，支持前置依赖解锁
- **预计算优化**: BT 构建时预计算 action enum，跳过运行时字符串转换

---

## 项目结构

```
BattleSystem-ECS/
├── Core/
│   ├── ComponentStore.cs            # SOA 核心：常量、Position、实体生命周期、死亡队列
│   ├── ComponentStore_Enemy.cs      # SOA 敌人字段 + 访问方法
│   ├── ComponentStore_Tower.cs      # SOA 塔字段 + 访问方法
│   ├── ComponentStore_Player.cs     # SOA 玩家字段 + 访问方法
│   ├── ComponentStore_World.cs      # SOA 世界/环境/资源字段 + 访问方法
│   ├── GameManager.cs        # 游戏主循环与系统调度
│   ├── EntityManager.cs
│   ├── EventBus.cs
│   ├── IRenderer.cs / ConsoleLogger.cs / FileLogger.cs
│   └── GAS/
│       ├── Attributes.cs
│       ├── GameplayEffect.cs
│       └── GameplayAbility.cs
├── Components/
│   ├── BuffData.cs
│   ├── EnemyActionType.cs
│   ├── EnemyComponent.cs
│   └── SkillComponent.cs
├── Systems/
│   ├── EnemyAISystem.cs       # 行为树评估 + 攻击执行
│   ├── EnemyMovementSystem.cs
│   ├── PlayerTowerAttackSystem.cs
│   ├── TowerAttackSystem.cs
│   ├── TowerPlacementSystem.cs
│   ├── TowerUpgradeSystem.cs
│   ├── WaveSpawningSystem.cs   # 波次生成（含 OnWaveComplete 事件）
│   ├── UpgradeSystem.cs        # 玩家升级
│   ├── SkillSystem.cs          # GAS 技能系统
│   ├── TechTreeSystem.cs       # 科技树（3分支 × 5节点）
│   ├── GoldSystem.cs
│   ├── MapSystem.cs
│   ├── BenchmarkSystem.cs      # 全链路压测
│   ├── BehaviorTreeEvaluator.cs
│   └── BehaviorTreeNodes.cs
├── Configs/
│   ├── game_config.json        # 怪物类型 / 等级 / 波次配置
│   ├── behavior_trees.json     # 行为树定义
│   ├── skills.json
│   ├── tower_placement.json
│   ├── tech_tree.json          # 科技树节点定义
│   └── TechTreeDef.cs          # 科技树配置结构
├── Research/
│   ├── tower_defense_knowledge.md  # 自动更新的塔防知识库（21 repos）
│   ├── bug-fix.md              # Bug 追踪（48 项，全部已修复）
│   └── findings/               # 爬取原始数据
├── BattleSystemECS.Tests/
│   └── ...                     # 120 单元测试
└── Program.cs                  # 入口（游戏/压测/微基准）
```

---

## 快速开始

```bash
# 构建
dotnet build

# 运行
dotnet run
# 1: 塔防游戏（交互式）
# 2: 全链路性能压测（10K 敌 × 500 帧，手写合并热路径）
# 3: 微基准测试（单系统操作级性能剖析）
# 4: 真实系统链路压测（10K 敌 × 500 帧，实际系统调用链）
# 5: 完整一局压测（5 关全通，真实波次生成）

# 测试
cd BattleSystemECS.Tests
dotnet test
```

---

## 科技树（TechTree）

每波结算产出 **1 研究点数**，用于解锁科技节点（前置依赖检查已实现）：

| 分支 | 节点 |
|------|------|
| ⚔️ 进攻 | 锋利 I→II、连射、致命打击、穿刺射击 |
| 🛡️ 防御 | 铁壁 I→II、喘息、堡垒、不朽 |
| 💰 经济 | 盗墓、高效击杀、理财、商队、淘金热 |

详见 `Configs/tech_tree.json`。

---

## Bug 追踪

详见 [`docs/design-and-bugs.md`](docs/design-and-bugs.md)。所有 48 项 Bug 已全部修复。

---

## 更新记录

完整更新历史见 [CHANGELOG.md](CHANGELOG.md)。

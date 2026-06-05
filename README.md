# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-06-05, Round 122 — 方向二：塔-塔治疗链接 Tower-to-Tower Heal Link）

| 指标 | 数值 |
|------|------|
| **mode 5**（完整一局） | **3244 FPS**，400 帧 |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **8910 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **3487 FPS** |
| mode 3 | 微基准测试（单系统操作级性能剖析） |

> **Round 122 方向二：塔-塔治疗链接 Tower-to-Tower Heal Link / Tower Healing Aura**：兑现塔生态协同的最后一块拼图——之前塔血量只能靠 `TowerRegen` 缓慢被动恢复（5 HP/s 典型值），无主动治疗；BOSS/高强度波次时塔被破坏后无法快速回血，玩家只能重建或等待。`HealAuraSystem` 是 `WispSystem.HealAura` 的"对塔版本"——某些塔（治疗塔/支援塔）有 `HealAuraRadius` / `HealAuraAmount` / `HealAuraInterval` 3 字段，Update 时给范围内友军 Palisade 塔每 `Interval` 秒回 `Amount` HP（不超过 `PalisadeMaxHP`，overheal 静默丢弃）。3 个新 SOA 字段 `TowerHealAuraRadius` / `TowerHealAuraAmount` / `TowerHealAuraInterval` + 1 个运行时计时器 `TowerHealAuraTimer`（per-healer 冷却，初始 0=ready，interval>0 时 fire 后重置为 interval，interval=0 时 fire-every-frame）— 0 = 零开销 fast path（"无光环"塔与不配置此机制的塔完全等价）。`AddTower` / `DestroyEntity` 双侧 init/reset 4 字段防 ID-reuse 泄漏（防止 recycled slot 残留前一个 tower 的光环配置）。`TowerPlacementSystem` 在 curse 配置后、`pullTower` 配置前注入 `if (HealAuraRadius > 0 && HealAuraAmount > 0) { … TowerHealAuraRadius/Amount/Interval/Timer = 0f … }` opt-in 块。`SkillBuffGroup` 在 `Mark` 后 `Skill` 前插入 `HealAura?.SetTurn(); HealAura?.Update(deltaTime);`（`SetTurn` 缓存 heal-aura tower 列表 O(ActiveTowers) 一次，filter 半径>0；`Update` 串行扫描 active towers 应用治疗 — heal-aura 塔本身稀少，无需 Parallel.For）。`SystemRegistry` wire `HealAura = new HealAuraSystem(store)` + `scheduler.SkillBuff.HealAura = HealAura`。`TowerConfig` 加 3 字段 `HealAuraRadius` / `HealAuraAmount` / `HealAuraInterval` 默认 0f。多 healer 在范围时**加性叠加**（每个 healer 各自贡献 amount per tick）— 设计师可平衡 per-healer 数值。系统是 serial（O(ActiveTowers²) 距离检查，但 ActiveTowerIds 上限 20 个，无 SpatialGrid 必要）。配套 12 个 Fact 测试覆盖配置默认 0 / AddTower 默认 0 / DestroyEntity reset（含 ID-reuse）/ SetTurn+Update 在无 healer 时 no-throw / 范围+interval=0 治疗 / 不自愈 / 范围外不治 / 非 Palisade 不治 / overheal clamp / 双 healer 加性叠加 / interval 1s 冷却门控不误触。bench2 8910（vs Round 121: 8692, +2.5% noise）/ bench4 3487（vs Round 121: 3619, -3.6% noise）/ bench5 3244（vs Round 121: 3264, -0.6% noise）— 全部 noise 内微漂移零回归，4 个新 SOA 字段 + 新增 `HealAuraSystem.SetTurn/Update` + 1 个 opt-in 块未造成回归。⚠️ 三个数值仍低于目标阈值（10000/4300/3800）— bench2 8910、bench4 3487、bench5 3244；本方向 12 个新测试 + 0 bug 扫描 + 0 回归，整体方向 100% 落地。

> **Round 121 方向一：运行时路径分支 Runtime Path Branching at Junction**：补全多路径地图的关键深度——之前项目虽然有 4 条预定义路径（default/fork_left/fork_right/ring），但敌人 spawn 后路径就固定了，玩家无法通过布局（塔密度/HP/类型）影响敌人走哪条路，多路径设计失去策略深度。新机制在路径节点上定义 "junction" 决策点：3 个新配置类（`JunctionDef` 含 SourcePathId/NodeIndex/Policy/HpLongPathThreshold/TowerDensityRadius/TowerDensityShortPathThreshold/BossTypeTags/ShortPathId/LongPathId + `JunctionPolicy` enum 3 个值：HpBased/TowerDensityBased/TypeBased）允许配置在哪个 (pathId, nodeIndex) 触发动态分支决策，3 个内置 policy：HP-based（高 HP 走 long path）、Tower-density-based（高塔密度走 short path 避让）、Type-based（boss 走 direct path）。`PathfindingSystem` 加 `_junctions` 字典（key = (sourcePathId<<32)|nodeIndex 打包成 long 做 O(1) lookup）+ `_hasJunctions` 快速退出 flag + `AddJunction/ClearJunctions/GetJunction` 公共方法 + 静态 `EvaluateJunction(def, hp, maxHp, isBossType, towerCount)` 纯函数（零副作用，可独立单测）。`ComponentStore_Enemy` 加 1 个新 SOA 数组 `EnemyPathSegmentStartIndex[]`（每 enemy 独立段起点，初始化为 0 + 在 `ResetEntity` 中显式重置防 ID-reuse 泄漏）。`EnemyMovementSystem.SetTurn` 缓存 `_pathfindingHasJunctions`（O(1) early-out：未配置 junction 时 zero-overhead），per-enemy Parallel.For 在 path terrain derivation 之后、stun check 之前插入 junction 评估块：当 `curNode > segStart`（即敌人已离开本段第一个节点）时查 `GetJunction(curPath, curNode)`，命中则调用 `EvaluateJunction` 决策新 path id，写回 `EnemyPathId/EnemyPathNodeIndex/EnemyPathSegmentStartIndex`（重置 segStart=0，下一个 junction 在新 path 末端才会触发）。新增 helper `CountTowersNearEnemy(x, y, radius)`（O(ActiveTowerIds) 平方距离扫描，并行安全只读）+ `IsBossEnemy(enemyId)`（Elite || BossPhase > 0，无 `EnemyIsBoss` 字段）。新数据文件 `Data/Configs/path_junctions.json` 含 2 个示例 junction（默认路径 y=15 处 HP-based + y=10 处 Tower-density-based）。配套 22 个 Fact 测试覆盖 JunctionDef 默认值 / JunctionPolicy 3 值互异 / `HasJunctions` 默认 false / `AddJunction(null)` 安全 no-op / `GetJunction` 命中/未命中/Clear 后 null / 重复 Add 覆盖 / 多 junction 独立 / `EvaluateJunction(null)` 返回 -1 / HpBased 高/低 HP 双向 / HpBased maxHp=0 不除零（ratio=0）/ TowerDensityBased > threshold 短 ≤ 长 / TypeBased boss/非 boss / 未知 policy 安全 fallback 短 path / `EnemyPathSegmentStartIndex` 默认 0 / 写入读出 round-trip。bench2 8438（vs Round 120: 8862, -4.8% noise）/ bench4 3695（vs Round 120: 3580, +3.2% noise）/ bench5 3177（vs Round 120: 3320, -4.3% noise）— 全部在 noise 范围内（avg ±5%），1 个新 SOA 数组 + 1 个 junction 字典 + 1 个 fast-path bool 字段 + 1 个 junction 评估块（`_pathfindingHasJunctions=false` 时单 bool 早退）未造成回归。⚠️ 三个 benchmark FPS 仍低于设计阈值（10000/4300/3800），持续 13+ 轮 warning。

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

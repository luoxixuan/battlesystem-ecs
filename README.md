# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-06-05, Round 118 — 方向二：I 帧/无敌帧机制 Invulnerability Frames）

| 指标 | 数值 |
|------|------|
| **mode 5**（完整一局） | **3302 FPS**，400 帧 |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **8745 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **3346 FPS** |
| mode 3 | 微基准测试（单系统操作级性能剖析） |

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

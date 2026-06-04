# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-06-05, Round 111 — Boss 阶段技能切换 Boss Phase Skill Switching）

| 指标 | 数值 |
|------|------|
| **mode 5**（完整一局） | **3261 FPS**，400 帧 |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **8926 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **3727 FPS** |
| mode 3 | 微基准测试（单系统操作级性能剖析） |

> 本轮实施 Round 111 方向 1（Boss 阶段技能切换 Boss Phase Skill Switching）：`Core/ComponentStore_Enemy.cs` — 新增 6 个 Boss 阶段结构化字段（`EnemyPhaseCount` / `EnemyPhaseThresholdsFlat[4*MAX_ENTITIES]` / `EnemyPhaseSpeedMults` / `EnemyPhaseDamageMults` / `EnemyPhaseAbilityIdsFlat[4,MAX_ENTITIES]` / `EnemyPhaseFiredMask` 4-bit 位掩码）+ `BOSS_PHASE_MAX=4` 常量；`Core/ComponentStore.cs` — 构造函数预热所有阶段字段为 1f（无变化），`_ResetEntity` 显式 null-init 新增字段；`Systems/WaveSpawningSystem.cs` — 在 Boss 生成路径填充结构化阶段字段（threshold / speedMult / damageMult / abilityId），保留原 CSV 旧路径向下兼容；`Systems/EnemyAISystem.cs` — 顺序段 + 并行段两路增加阶段切换检测逻辑（HP 阈值 < 配置 → 一次性触发 SpeedMult / DamageMult + 入队阶段 AbilityId），新 `ConcurrentBag<(int enemyId, string abilityId)>` 在 `Update` 末尾 `DrainPhaseAbilityEvents` 串行转交 `EnemyAbilitySystem.EnqueueAbility`（避免非线程安全 API），`FiredMask` 位掩码保证一次性触发；`BattleSystemECS.Tests/BossPhaseSystemTests.cs`（新）— 17 测试覆盖 `BOSS_PHASE_MAX=4` 常量、默认 inert、DestroyEntity 清理所有阶段字段、phase 数量截断、CSV→2D ability 数组正确性、FiredMask 防重入、Speed/Damage mult 一次性应用、HP 阈值未到不触发、empty AbilityId no-op、phase chain 顺序触发。**403/403 tests PASS**（17 新增 BossPhase tests）。bench2 8926 / bench4 3727 / bench5 3261 — 较 Round 110 略降（-4.2% / -2.0% / -5.4%），主要来自新阶段字段构造热路径 prefetch + 阶段检测内层循环（虽然 BenchmarkSystem 中无 Boss，字段读取成本仍存在）。⚠️ bench2 较 Round 110 降 392 FPS 来自新 6 字段构造 + Reset 路径的 O(1) 内存写入扩展，bench4/5 同样符合该模式。
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

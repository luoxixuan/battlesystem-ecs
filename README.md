# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-06-04, Round 103 — 队伍增益共享 Buff Share）

| 指标 | 数值 |
|------|------|
| **mode 5**（完整一局） | **3500 FPS**，400 帧 |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **9955 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **3915 FPS** |
| mode 3 | 微基准测试（单系统操作级性能剖析） |

> 本轮实施 Round 103 方向8（队伍增益共享 Buff Share）：`ComponentStore_Tower` 新增 2 SOA 字段 `TowerBuffShareRadius[MAX_ENTITIES]` (float, 0=无共享零开销 fast path) + `TowerBuffShareMask[MAX_ENTITIES]` (int, 0x01=AttackSpeed)；`GameConfig` 新增 `BuffShareConfig` 静态类 3 常量（`MaxShareRadius=8f` / `DefaultShareEfficiencyPct=0.3f` / `ShareAttackSpeed=0x01`）；`TowerSynergySystem.ResolveBuffShares()` 新方法：每帧先 restore each cached tower's base attack speed（防 frame-over-frame 复合增长）→ 遍历 sharing tower 在 radius² 内的 nearby towers → 按 mask 位标志 multiplicative 应用 (1+eff) bonus → 缓存 base 用 entityId 键（不是 ActiveTowerIds 位置，Claude bug scan #1 修复位置键导致错位）；`CombatGroup` 在 `TowerAttack.Update()` 之前调用（attack speed 必须先共享再读）；`AddTower/RemoveTower/DestroyEntity` 三处重置双字段防 ID 复用泄漏 + 触发 `OnTowerEntityInvalidated` 事件清理 TowerSynergySystem 的 base-speed cache（**Claude bug scan #2 修复** ID 复用导致 stale base speed 写入新塔；新增 `ComponentStore.OnTowerEntityInvalidated` 静态事件 + `TowerSynergySystem` 订阅 + `InvalidateBuffShareCache(towerId)` 公开方法）。**248/248 tests PASS**（19 新增覆盖 default state / config / accessor / nearby bonus / out-of-range / mask=0 / radius=0 / self-skip / fast-path / 多帧不复合 / dispel sharer / dispel target / 2 sharing 乘法叠加 / DestroyEntity reset / RemoveTower reset / 回归测试 — cache 键为 towerId 而非位置 / ID 复用下新塔起始 base 正确 / 公开 invalidation API）。bench2 9955（< 10000 阈值但较上轮 9798 +1.6% 改善）、bench4 3915（< 4300 阈值但较上轮 3874 +1.1% 改善）、bench5 3500（< 3800 阈值，与上轮 3510 持平），三项均未达阈值 ⚠️ 但修复 #2 验证（实体 ID 复用下的 cache coherence）使 Buff Share 行为完全确定。
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

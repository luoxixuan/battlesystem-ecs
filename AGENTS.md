# BattleSystem-ECS — 项目开发规则

---

## 项目概述

基于 **SOA (Struct of Arrays) ECS 架构**的塔防战斗系统，纯 C# / .NET 6。逻辑与渲染完全分离，全系统并行化 (Parallel.For)，性能为首要优化目标。

---

## 性能基准

| 指标 | 数值 |
|------|------|
| 测试规模 | 10,000 敌人 × 200 帧 × 8 系统 |
| **FPS** | **~8,300**（±5% 正常波动） |
| 平均帧耗时 | ~0.12 ms |
| 主要热点 | EnemyAI ~10ms / MoveAttack ~8ms / TowerAttack ~1.8ms |

---

## 开发规范

### ECS 组件规范
- 所有组件数据存储在 `Core/ComponentStore.cs`，使用平行 `float[]/int[]/bool[]` 数组（SOA）
- 不使用 class 组件，使用直接数组访问
- 组件命名：`XxxComponent`（如 `HealthComponent`）但实际实现为 SOA 数组字段

### ECS 系统规范
- 系统位于 `Systems/` 目录，每个系统职责单一
- 热路径使用 `Parallel.For` 批处理，支持 `MaxDegreeOfParallelism`
- 避免每帧分配：`GetAllActiveEnemyIds()` 在 `SetTurn` 时缓存
- 禁止在系统热路径中使用 `new Random()`，使用类级 `static readonly Random`

### 渲染层规范
- 实现 `IRenderer` 接口，逻辑核心通过接口调用渲染
- 控制台输出用 `[SYSTEM]` 前缀区分系统日志

---

## 每次改动后必做清单

> 严格按顺序执行，才能提交 git。

1. **`dotnet build`** — 确认 0 warnings 0 errors
2. **`dotnet test BattleSystemECS.Tests`** — 确认 27/27 测试全部通过
3. **`echo 2 | dotnet run`** — 运行全链路压测，确认 FPS 没有下降（允许 ±5% 误差）
4. **验证通过后** → `git add -A && git commit -m "描述"`
5. **commit 完成后立即** → `git push`

### 项目文档同步

每次完成代码修改后（commit 前），必须同步更新以下文档：
- `AGENTS.md` — 本文件（若规则有变化）
- `README.md` — 项目说明（性能数字、功能列表更新）
- `docs/architecture.md` — 架构文档（系统结构、关键设计变更）
- `docs/bug-fix.md` — Bug 追踪（若有 Bug 修复）

顺序：**代码完成 → 验证通过 → 更新文档 → git commit → git push**

### 禁止事项

- ❌ 禁止在 build/test/压测未全部通过的情况下提交 git
- ❌ 禁止跳过压测直接提交
- ❌ 禁止 `git reset` / `rebase` 前不 commit 当前改动

### git 提交风格

每次 commit 应为**原子性最小改动**，一个 commit 只做一件事：
- ✅ `fix: DestroyEntity remove from ActiveEnemyIds`
- ❌ `fix and perf various issues`

---

## 项目结构

```
BattleSystem-ECS/
├── Core/
│   ├── ComponentStore.cs     # SOA 数据存储（所有组件的 SOA 数组）
│   ├── GameManager.cs        # 游戏主循环与系统调度
│   ├── EntityManager.cs
│   ├── EventBus.cs
│   ├── IRenderer.cs / ConsoleLogger.cs / FileLogger.cs
│   └── GAS/                  # Gameplay Ability System
│       ├── Attributes.cs
│       ├── GameplayEffect.cs
│       └── GameplayAbility.cs
├── Components/
│   ├── BuffData.cs
│   ├── EnemyActionType.cs
│   ├── EnemyComponent.cs
│   └── SkillComponent.cs
├── Systems/
│   ├── EnemyAISystem.cs       # 行为树评估 + 攻击执行（BT cache）
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
│   ├── game_config.json        # 怪物类型、等级、波次
│   ├── behavior_trees.json     # 行为树定义
│   ├── tech_tree.json          # 科技树节点
│   ├── skills.json
│   ├── tower_placement.json
│   └── TechTreeDef.cs          # 科技树配置结构
├── docs/
│   ├── architecture.md         # 完整架构文档
│   └── bug-fix.md              # Bug 追踪（45 项，27 已修复）
├── Research/
│   ├── tower_defense_knowledge.md  # 自动更新的塔防知识库
│   └── findings/               # 爬取原始数据
├── BattleSystemECS.Tests/
│   └── ...                     # 27 单元测试
└── Program.cs                  # 入口（游戏/压测/微基准）
```

---

## 核心设计决策

1. **ActiveTowerIds 而非遍历全量**: `TowerAttackSystem` 只遍历活跃塔，避免 `NextEntityId` 范围外的空数据
2. **BTCachedTree 预缓存**: `WaveSpawningSystem` 时将 BT 存到 `store.EnemyBehaviorTree`，`EnemyAISystem` 无需 Dictionary 查找
3. **ActionEnum 预计算**: BT 构建时转换 string→enum，热路径无字符串比较
4. **并行合并 MoveAttack**: `BenchmarkSystem` 内置 merged pipeline，单独计时
5. **科技树效果缓存**: `TechTreeSystem` 内部字段存储 computed multiplier

---

## 已废弃模块（勿引用）

| 路径 | 状态 |
|------|------|
| `System/` (大写) | 未编译，旧版本死代码 |
| `GridSpatialHash.cs` | 空桩，range=3 场景是反模式 |
| `Components/Components.cs` | 老架构，新代码直接用 ComponentStore 数组 |
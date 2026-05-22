# BattleSystem-ECS — 项目开发规则

---

## 项目概述

基于 **SOA (Struct of Arrays) ECS 架构**的塔防战斗系统，纯 C# / .NET 6。逻辑与渲染完全分离，全系统并行化 (Parallel.For)，性能为首要优化目标。

---

## 性能基准（2026-05-18, commit `2b9e5e7`）

| benchmark | FPS | 说明 |
|-----------|------|------|
| mode 2（合并热路径 + 完整 skill+buff） | ~13663 | ≥12000 门禁 ✅ |
| mode 4（真实系统链路 + 完整 skill+buff） | ~7096 | ≥7000 门禁 ✅ |

mode 2 和 mode 4 是不同的语义，**不要再用一个 FPS 代表全部性能**。

> 注：Mode2/4 均已含完整 skill+buff 链路，新旧基准不可直接比较。

**门禁**：mode 2 >12000 FPS，mode 4 >7000 FPS。

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
2. **`dotnet test BattleSystemECS.Tests`** — 确认 63/63 测试全部通过
3. **`echo 2 | dotnet run`** — 运行合并热路径压测（mode 2），确认 FPS 没有下降（允许 ±5% 误差）
4. **`echo 4 | dotnet run`** — 运行真实系统链路压测（mode 4），确认主指标没有下降
5. **验证通过后** → `git add -A && git commit -m "描述"`
6. **commit 完成后立即** → `git push`

### 项目文档同步

每次完成代码修改后（commit 前），必须同步更新以下文档：
- `AGENTS.md` — 本文件（若规则有变化）
- `README.md` — 项目说明（性能数字、功能列表更新）
- `docs/architecture.md` — 架构文档（系统结构、关键设计变更）
- `docs/desgin-and-bugs.md` — Bug和设计变更追踪（若有 Bug 修复或者设计变更）

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
├── Core/                     # ECS 核心（ComponentStore, GameManager, EntityManager, EventBus, GAS）
├── Components/               # 组件定义（EnemyComponent, SkillComponent, BuffData, EnemyActionType）
├── Systems/                  # 游戏系统（14 个，参见上方列表）
├── Data/                     # 静态数据（auto-gen，勿手动编辑）
│   ├── Configs/              # 运行时 .json 配置
│   │   ├── behavior_trees.json
│   │   ├── phase_behavior.json
│   │   ├── player.json
│   │   ├── skills.json
│   │   ├── tech_tree.json
│   │   ├── tower_placement.json
│   │   └── wave_spawn.json
│   ├── Monsters/             # 200 种怪物定义
│   ├── Skills/              # 150 种技能定义
│   ├── Towers/              # 150 种塔定义
│   └── Levels/              # 5 个关卡
├── docs/
│   ├── architecture.md
│   ├── bug-fix.md
│   ├── design-and-bugs.md
│   └── philosophy.md
├── Research/                 # 研究与工具
│   ├── tower_defense_knowledge.md  # 塔防知识库（自动更新）
│   ├── findings/             # 爬取原始数据
│   ├── logs/                 # 构建/测试/压测日志
│   ├── scripts/              # 旧脚本（batch_gen, gen_towers, runbench*）
│   └── bug-report-*.md / crawler*.py 等
└── BattleSystemECS.Tests/   # 63 单元测试
└── Program.cs                # 入口（游戏/压测/微基准）
```

---

## 核心设计决策

1. **ActiveTowerIds 而非遍历全量**: `TowerAttackSystem` 只遍历活跃塔，避免 `NextEntityId` 范围外的空数据
2. **BTCachedTree 预缓存**: `WaveSpawningSystem` 时将 BT 存到 `store.EnemyBehaviorTree`，`EnemyAISystem` 无需 Dictionary 查找
3. **ActionEnum 预计算**: BT 构建时转换 string→enum，热路径无字符串比较
4. **并行合并 MoveAttack**: `BenchmarkSystem` 内置 merged pipeline，单独计时
5. **科技树效果缓存**: `TechTreeSystem` 内部字段存储 computed multiplier

---

## 并行安全原则

> 所有涉及并行写共享状态的改动，必须遵循以下原则，否则拒绝合并。

### 两阶段模式（Two-Phase Pattern）

并行段**只读不写**，只收集信息；结构写操作（DestroyEntity、DecreasePlayerHealth、EventBus.Publish）全部推迟到串行阶段，按确定顺序执行。

例外：本地只读计算（如 damage 估算）可以在并行段执行。

### 帧末唯一死亡结算点

实体销毁（DestroyEntity / QueueEnemyDeath）和奖励结算由帧调度层统一在帧末执行。
系统只负责产生"伤害/死亡事件"，不直接调用 ResolveEnemiesKilledThisFrame()。

调用链：
```
GameManager.Run() / BenchmarkSystem
  → BeginFrame()
  → 各系统 Update()（只 queue，不 resolve）
  → ResolveEnemiesKilledThisFrame()（统一结算）
```

### damage queue 存 raw value，不存 derived value

并行收集阶段入队的数据必须是 `(enemyId, damage)`，而不是 `(enemyId, newHealth)`。
串行 apply 必须用 `EnemyHealth[enemyId] -= damage`，而不是 `EnemyHealth[enemyId] = newHealth`。
后者是 last-write-wins，多攻击者打同一目标会丢伤害。

---

## 已废弃模块（勿引用）

| 路径 | 状态 |
|------|------|
| `System/` (大写) | 未编译，旧版本死代码 |
| `GridSpatialHash.cs` | 空桩，range=3 场景是反模式 |
| `Components/Components.cs` | 老架构，新代码直接用 ComponentStore 数组 |
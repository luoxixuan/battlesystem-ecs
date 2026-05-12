# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

## 性能基准（2026-05-12, commit `79fea25`）

| 指标 | 数值 |
|------|------|
| 测试规模 | 10,000 敌人 × 200 帧 |
| 活动系统 | 8 系统（WaveSpawning + EnemyAI + MoveAttack + TowerAttack + Gold + Upgrade + Skill + Map） |
| 平均帧耗时 | 0.12 ms |
| **FPS** | **~8,334** |
| 主要热点 | EnemyAI 9.9ms (41%) / MoveAttack 8.7ms (36%) / TowerAttack 1.9ms (8%) |

## 优化演进

| commit | FPS | EnemyAI | 关键改动 |
|--------|-----|---------|---------|
| `c67b567` | 212 | — | 初始 (无并行) |
| `0b6557e` | 1030 | — | EnemyAI Parallel.For |
| `c8e758f` | 2656 | — | Movement + PlayerAttack Parallel.For |
| `09dd89a` | 2598 | 34.3ms | BT Eval Cache |
| `3885275` | 3368 | 31.7ms | TowerAttack 并行化 + ActiveTowerIds |
| `04c50a6` | ~2879 | 33.2ms | P0 Bug 修复 |
| `c505461` | ~2858 | 33.3ms | Precomputed BT Action Enum |
| `626b13b` | ~2875 | 34.0ms | 移除死写 SetEnemyAIAction |
| `01c05a7` | ~2758 | 35.6ms | chargeParams SOA |
| `79fea25` | **~8334** | **9.9ms** | **BT Cache fix + Merged pipeline** |

> FPS 波动受 Windows Parallel.For 调度和 GC 影响，±5% 属正常范围。

## 架构特点

- **SOA 存储**: 所有组件用平行 float[]/int[]/bool[] 数组，CPU 缓存友好
- **全系统并行**: 每个系统内部用 `Parallel.For` 批处理，4 核加速。Movement+PlayerAttack 合并为一次管线（MoveAttack）
- **行为树 AI**: 敌人用 flat-array BTCachedTree 驱动，O(1) 节点访问。BT 评估缓存用 health-driven version counter 代替 turn 无效化
- **GAS 技能系统**: `Core/GAS/` 模块化 Attributes + GameplayEffect + GameplayAbility
- **预计算优化**: BT 构建时预计算 action enum，跳过运行时字符串转换

## 项目结构

```
BattleSystem-ECS/
├── Core/                    # 核心层
│   ├── ComponentStore.cs    # SOA 数据存储 (核心性能点)
│   ├── GameManager.cs       # 游戏主循环与系统调度
│   ├── GAS/                 # Gameplay Ability System
│   │   ├── Attributes.cs
│   │   ├── GameplayEffect.cs
│   │   └── GameplayAbility.cs
│   ├── EventBus.cs          # 事件总线
│   └── IRenderer.cs         # 渲染接口
├── Components/              # 数据定义 (Structs)
│   └── ...
├── Systems/                 # 逻辑处理器
│   ├── EnemyAISystem.cs     # 行为树评估 + 攻击执行 (BT cache 优化)
│   ├── EnemyMovementSystem.cs
│   ├── PlayerTowerAttackSystem.cs
│   ├── TowerAttackSystem.cs
│   ├── BenchmarkSystem.cs   # 性能测试 (含合并 MoveAttack 管线)
│   ├── BehaviorTreeEvaluator.cs  # BT 构建 + 评估器 (含预计算 enum)
│   ├── GridSpatialHash.cs   # 空间哈希 (未启用)
│   └── ...
├── Configs/                 # JSON 配置
├── Research/                # 研究文档 + Bug 追踪
└── Program.cs               # 入口 (游戏/性能测试/微基准)
```

## 快速开始

```bash
dotnet build
dotnet run
# 1: 塔防游戏
# 2: 全链路性能压测 (10K 敌 × 200 帧 × 9 系统)
# 3: 微基准测试 (单系统操作级性能剖析)
```

```bash
cd BattleSystemECS.Tests
dotnet test                    # 27 单元测试
```

## 已知 Bug

详见 `Research/bug-fix.md`。已修复 2/5 HIGH + 1/3 性能优化。

## 下一步

- Movement system (~16ms): 合并与 PlayerAttack 的循环减少线程调度
- 修复 Bug#6: Dodge 方向参数丢失
- Spatial Hash 正确集成 (需解决 cell 锁竞争)

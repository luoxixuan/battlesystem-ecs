# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构** 实现的塔防战斗系统，纯 C# / .NET 6，独立编译运行。逻辑与渲染完全分离，系统全部并行化 (Parallel.For)，性能为主要优化方向。

---

## 性能基准（2026-06-06, Round 137）

| 模式 | 指标 |
|------|------|
| **mode 5**（完整一局） | **3053 FPS**，400 帧 |
| **mode 2**（合并热路径，10K 敌 × 500 帧） | **8857 FPS** |
| **mode 4**（真实系统链路，10K 敌 × 500 帧） | **3578 FPS** |
| **mode 3**（微基准测试） | 单系统操作级性能剖析 |

> mode 5 最接近真实游戏：5 关全通、真实波次生成。mode 4 是主要参考指标。完整变更历史见 [CHANGELOG.md](CHANGELOG.md)。

---

## 优化演进（关键节点）

| commit | FPS (mode 4) | 关键改动 |
|--------|--------------|---------|
| `c67b567` | 212 | 初始（无并行） |
| `3885275` | 3368 | TowerAttack 并行化 + ActiveTowerIds |
| `ccc42e3` | ~4900 | EnemyAI 两阶段 + BeginFrame |
| `d707920` | ~4900 | damage 累加修正 |
| `223c84d` | ~4000 | 10x 扩展（150 塔 × 200 怪物） |

> `223c84d` 是 10x 规模基准，与前序不可直接对比。

---

## 架构特点

- **SOA 存储**: 所有组件用平行 `float[]/int[]/bool[]` 数组，CPU 缓存友好
- **全系统并行**: 每个系统内部用 `Parallel.For` 批处理
- **行为树 AI**: flat-array BTCachedTree 驱动，O(1) 节点访问
- **GAS 技能系统**: `Core/GAS/` 模块化 Attributes + GameplayEffect + GameplayAbility
- **科技树**: 3 分支（⚔️进攻/🛡️防御/💰经济）× 5 节点

---

## 快速开始

```bash
# 构建
dotnet build

# 运行
dotnet run
# 1: 塔防游戏（交互式）
# 2: 全链路性能压测（手写合并热路径）
# 3: 微基准测试（单系统操作级性能剖析）
# 4: 真实系统链路压测
# 5: 完整一局压测

# 测试
dotnet test BattleSystemECS.Tests
```

---

## 项目结构

```
BattleSystem-ECS/
├── Core/                     # ECS 核心（ComponentStore×5、Entity、FrameScheduler、GAS）
├── Systems/                  # 游戏系统（WaveSpawning、TowerAttack、SkillSystem 等 20+）
├── Data/                     # 静态数据（Monsters×200、Towers×150、Skills×150、Levels×5）
├── BattleSystemECS.Tests/    # xUnit 单元测试
├── docs/                     # 架构文档、设计治理、开发理念
├── Research/                 # 知识库、爬虫日志
└── Program.cs                # 入口
```

---

## 文档

- [架构文档](docs/architecture.md)
- [Bug 追踪](docs/design-and-bugs.md)
- [开发理念](docs/philosophy.md)
- [变更日志](CHANGELOG.md)

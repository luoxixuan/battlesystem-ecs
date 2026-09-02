# BattleSystem-ECS — 塔防 ECS 性能基准

一个使用 **SOA (Struct of Arrays) ECS 架构**实现的塔防战斗系统。战斗逻辑核心库 (`BattleSystemECS.Core`) 采用 netstandard2.1，与渲染完全分离，支持控制台运行和 Unity 2D 消费。全系统并行化 (Parallel.For)，性能为主要优化方向。

---

## 项目分层

```
BattleSystem-ECS/
├── BattleSystemECS.Engine/   # 独立帧执行与内容值合同库（netstandard2.1）
├── BattleSystemECS.Core/     # 战斗逻辑核心库（netstandard2.1，引用 Engine）
├── BattleSystemECS.csproj    # 控制台 EXE（net6.0，引用 Core）
├── BattleSystemECS.Tests/    # xUnit 单元测试（net9.0，四分层；数量以最新门禁日志为准）
├── Systems/                  # 游戏系统（144 个，全部编译到 Core）
├── Core/                     # ECS 核心（ComponentStore×5、FrameScheduler、GAS、EventBus）
├── Data/                     # 静态数据（Monsters×200、Towers×150、Skills×150、Levels×5）
└── docs/ / Research/         # 架构文档、知识库
```

**Unity 渲染端**：`BattleSystem-ECS-Unity/` — 通过 `BattleDriver` 驱动 Tick 并消费 `IBattleEventBus` 事件渲染 2D GameObject。

---

## 性能验证

性能门禁和压测结果以最新一次完整门禁证据为准；易变化的性能指标、帧数和数据规模不在 README 中固化。当前迁移轮次暂不运行 mode2/mode4/mode5，详见 [迁移编排](docs/ecs-gas-migration-orchestration.md)。

当前 M7 fresh evidence：`C:\WorkSpace\AI\battlesystem-ecs-gate-logs\m7-installer-registration-final-20260902T231500Z`。

---

## 架构特点

- **SOA 存储**：所有组件用平行 `float[]/int[]/bool[]` 数组，CPU 缓存友好
- **全系统并行**：每个系统内部用 `Parallel.For` 批处理
- **行为树 AI**：flat-array BTCachedTree 驱动，O(1) 节点访问
- **GAS 技能系统**：`Core/GAS/` 模块化 Attributes + GameplayEffect + GameplayAbility
- **科技树**：3 分支（⚔️进攻 / 🛡️防御 / 💰经济）× 5 节点
- **事件总线**：`IBattleEventBus` 接口 — 逻辑与渲染解耦，Unity 侧通过 `UnityEventBus` 消费
- **netstandard2.1 兼容**：Polyfill（`IsExternalInit`、`Rng`、`PolyfillExtensions`）确保 Unity 可引用

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

## 文档

- [架构文档](docs/architecture.md)
- [Bug 追踪](docs/design-and-bugs.md)
- [开发理念](docs/philosophy.md)
- [变更日志](CHANGELOG.md)

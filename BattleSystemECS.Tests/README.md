# BattleSystemECS.Tests 分层与规范

本文件是单元测试项目的架构约定。所有测试文件必须归入下列四层之一；
不直接放在测试项目根目录（基建除外）。

## 1. 分层结构

```
BattleSystemECS.Tests/
├── Infrastructure/    # 测试基建：共享 Fixture / Spec / TestWorld（不含 [Fact]）
├── Framework/         # 框架层：ECS 存储、帧调度、状态机、配置加载、GAS、曲线表
├── Mechanisms/        # 机制层：跨业务复用的通用机制
│   ├── Combat/        #   伤害/抗性/处决/护盾/标记/光环/多目标等战斗机制
│   ├── Control/       #   控制效果与免疫（CC / Disarm / Debuff Resist）
│   ├── Perception/    #   仇恨与威胁分
│   ├── Movement/      #   寻路分支与移动相关机制
│   ├── Spawning/      #   波次生成与难度缩放
│   ├── World/         #   地形区域 / 地面效果等世界机制
│   └── TowerCore/     #   塔位放置 / 占格 / 建造队列 / 出售等塔公共机制
├── Features/          # 业务层：具名塔 / 敌怪 / Boss / 技能 / 经济 / 具名 Buff
│   ├── Towers/
│   ├── Enemies/
│   ├── Bosses/
│   ├── Skills/
│   ├── Economy/
│   ├── Buffs/
│   └── World/
└── Integration/       # 集成层：真实 JSON 配置 + 多系统多帧场景
```

命名空间与目录一一对应，例如 `Mechanisms/Combat/DamageFormulaTests.cs`
的命名空间为 `BattleSystemECS.Tests.Mechanisms.Combat`。

## 2. 各层职责

| 层 | 测什么 | 不测什么 |
|----|--------|----------|
| **Framework** | `ComponentStore` 生命周期、`FrameScheduler.Tick` 阶段顺序与死亡结算、`StateMachine` 迁移、`GameConfigLoader` 默认/加载语义、GAS 冷却/激活、`CurveTable`、`SystemRegistry` 装配自检 | 具体系统玩法 |
| **Mechanisms** | 一套公式或状态机在任意实体上的行为；边界值、ID 复用、订阅幂等、缓存失效等历史 bug 回归 | 某个具名塔/Buff 的完整身份 |
| **Features** | 具名系统对外契约：默认值、配置读取、关键行为、状态重置 | 通过 Feature 测试去推导通用公式 |
| **Integration** | `game_config.json` 作为输入，断言**结构自洽与相对关系**（引用存在、唯一性、字段有效范围、数量与配置推导值一致） | 任何具体配置值、任何固定数量/固定字符串 |

> `Framework/SystemRegistryTests.cs` 为 144 systems / 11 groups 装配自检；
> 新增 system 时同步在 `CreateAll` / `WireDependencies` / `AssignToGroups` 接线后补充关键槽位断言。

## 3. 编写规范

1. **每个 `[Fact]` / `[Theory]` 至少一条有意义的 `Assert`**。
2. **禁止恒真/恒假断言**（`Assert.True(true)`、`Assert.False(false)`），
   禁止 `try/catch` 包住整个测试主体后"永远通过"，禁止调试用临时测试。
3. 测试名必须描述可观察行为；测试名含 `NoCrash/NoExceptions/WithoutCrash`
   但断言只是"没抛异常"的，视为 smoke 测试，必须补真实断言或删除。
4. **配置数据边界**：允许读取 `game_config.json` / `Data/**` 作为测试输入，
   但禁止把配置里的某个具体值当固定常量断言（如 `Basic cap == 8`、
   `shrine_towers.json 必须包含 "Gold Shrine"`）。期望值必须从读取到的配置
   推导，或由测试代码显式注入。测试 `TowerPlacementSystem` 前用
   `TestWorld.DisablePerTypeTowerCaps(store)` 清除数据 cap；要测 cap 机制再显式写值。
5. 确定性优先：随机数必须固定种子（`new Random(seed)`）；不用 `DateTime.Now`；不依赖测试执行顺序。
6. 优先使用 `BattleTestBase` 的 `World.Enemy/Tower/Player` 工厂，
   避免散落的 `new ComponentStore()` + 魔法数字。
7. 手工 `AddTower` 已自动注册 `ActiveTowerIds`（M-race fix），不要再重复调用
   `AddActiveTowerId`；需要绕过注册的存储层直测用原始数组操作并注释原因；
   驱动 `TowerAttackSystem` 前必须 `store.RebuildSpatialGrid()`（`FrameScheduler` 不负责重建网格）。
8. 修复过的 bug，测试中保留 `// Bug 回归：...` 注释说明回归意图。
9. CI 静态检查门禁：`pwsh -File tools\check-test-rules.ps1` 必须 0 违规
   （零断言测试 + 恒真/恒假断言）。

## 4. 从 git 历史恢复旧测试的规则

2026-08-15 曾删除 57 个业务层测试文件（commit `252093a` ~ `3d89945`）。
恢复时必须：

1. 按本文件重新归层，不按旧文件平铺恢复；
2. 先恢复"有真实断言"的文件（旧文件在 `7ed2aff` 处均有 `Assert` 且无恒真断言）；
3. 已删除的 6 个无价值测试（`DebugHallowedTest` 等，commit `d56b508`）**不恢复**；
4. 恢复后统一替换命名空间并补充 `using BattleSystemECS.Tests.Infrastructure;`；
5. 全量 `dotnet test` 通过后才允许进入提交门禁。

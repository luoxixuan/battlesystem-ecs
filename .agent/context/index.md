# BattleSystem-ECS - 项目上下文索引

## 项目结构
```
BattleSystem-ECS/
├── Core/                    # 核心
│   ├── IRenderer.cs       # 渲染器接口
│   ├── ConsoleLogger.cs    # 控制台日志渲染器
│   ├── FileLogger.cs       # 文件日志渲染器
│   └── DamageSystem.cs     # 伤害计算系统
├── Components/            # 所有组件
│   ├── Components.cs       # 基础组件
│   ├── SkillComponent.cs   # 技能组件
│   └── BuffDebuffComponents.cs  # Buff/Debuff 组件
├── Systems/                # 所有系统
│   ├── CombatSystem.cs     # 战斗逻辑系统
│   ├── SkillSystem.cs      # 技能系统
│   └── BuffDebuffSystem.cs  # Buff/Debuff 系统
├── EntityManager.cs        # ECS 核心
├── Program.cs              # 主程序
├── BattleSystemECS.csproj # 项目文件
├── .agent/
│   └── context/
│       └── index.md (本文件)
├── AGENTS.md               # 开发规则
└── README.md               # 项目说明
```

## ECS 组件 (Components/)

### 基础组件
- [Components.cs](../Components.cs) - 基础组件定义
  - HealthComponent - 生命值组件
  - AttackPowerComponent - 攻击力组件
  - DefensePowerComponent - 防御力组件
  - NameComponent - 名称组件
  - PlayerTagComponent - 玩家标签
  - EnemyTagComponent - 敌人标签
  - BattleStateComponent - 战斗状态组件

### 战斗组件
- [SkillComponent.cs](../Components/SkillComponent.cs) - 技能组件
  - 技能名称、伤害、冷却
  - 魔力消耗、魔力值

- [BuffDebuffComponents.cs](../Components/BuffDebuffComponents.cs) - Buff/Debuff 组件
  - BuffComponent - Buff 效果（攻击力加成、防御力加成、治疗）
  - DebuffComponent - Debuff 效果（攻击力惩罚、防御力惩罚、持续伤害）

## ECS 系统 (Systems/ & Core/)

### 逻辑系统
- [CombatSystem.cs](../Systems/CombatSystem.cs) - 战斗系统
  - 回合制战斗
  - 普通攻击、技能攻击
  - 伤害计算、暴击判定

- [SkillSystem.cs](../Systems/SkillSystem.cs) - 技能系统
  - 技能使用
  - 冷却管理
  - 魔力恢复

- [BuffDebuffSystem.cs](../Systems/BuffDebuffSystem.cs) - Buff/Debuff 系统
  - Buff 效果管理
  - Debuff 效果管理
  - 持续时间管理

- [DamageSystem.cs](../Core/DamageSystem.cs) - 伤害计算系统
  - 基础伤害计算
  - 暴击判定

### 渲染系统
- [IRenderer.cs](../Core/IRenderer.cs) - 渲染器接口
  - 定义渲染器行为契约

- [ConsoleLogger.cs](../Core/ConsoleLogger.cs) - 控制台日志渲染器
  - 实现输出到控制台

- [FileLogger.cs](../Core/FileLogger.cs) - 文件日志渲染器
  - 实现输出到文件

## 系统架构

### 数据流
```
Input (创建实体) → EntityManager
                  ↓
           添加组件
                  ↓
           System 处理
                  ↓
           更新组件
                  ↓
           Renderer 输出
                  ↓
           Console/File 日志
```

### 系统组
- SimulationSystemGroup - 战斗、技能、Buff/Debuff 系统
- PresentationSystemGroup - 日志渲染

## 依赖包
```
"Microsoft.NETCore.App" (implicit)
```

## 功能列表

### 已实现
- ✅ ECS 基础架构（Entity-Component-System）
- ✅ 战斗系统（回合制、普通攻击、暴击）
- ✅ 技能系统（技能使用、冷却、魔力管理）
- ✅ Buff/Debuff 系统（效果应用、持续时间管理）
- ✅ 渲染系统（控制台日志、文件日志）
- ✅ 逻辑核心与渲染层完全分离
- ✅ 纯 C# 实现，无渲染依赖

### 计划中
- 🔄 装备系统
- 🔄 存档系统
- 🔄 团队战斗
- 🔄 战斗 AI

## 快速开始

详见 [README.md](../README.md)

1. 编译项目：`dotnet build`
2. 运行项目：`dotnet run`
3. 选择渲染方式：控制台/文件日志
4. 观察战斗日志

## 开发规范

详见 [AGENTS.md](../AGENTS.md)

### ECS 组件
- 实现 `IComponentData` 接口
- 只包含数据，不包含逻辑
- 使用 struct 而不是 class

### ECS 系统
- 继承 `SystemBase`
- 使用 `Entities.ForEach()` 或 `IJobEntity`
- 指定 `UpdateInGroup`

### 渲染层
- 实现 `IRenderer` 接口
- 只负责输出，不包含逻辑
- 逻辑核心通过接口调用渲染

## 更新记录
- 2026-02-08: 项目初始化
- 2026-02-08: 完成 ECS 架构
- 2026-02-08: 添加技能系统
- 2026-02-08: 添加 Buff/Debuff 系统
- 2026-02-08: 逻辑核心与渲染层分离
- 2026-02-08: Git 仓库初始化
- 2026-02-08: 优化 Git（排除编译产物）
- 2026-02-08: 添加 .agent/context 目录

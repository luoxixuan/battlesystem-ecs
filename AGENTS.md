# 战斗系统 Demo - ECS 架构 - AGENTS.md

## 项目概述
使用 **ECS（Entity Component System）** 架构实现的战斗系统，纯 C# 语言，可独立编译成 exe 运行。

## 项目目标
- 严格遵循 ECS 架构设计
- Entity（实体）只作为 ID
- Component（组件）只包含数据
- System（系统）只包含逻辑
- 战斗内容用战报日志输出
- 方便排查逻辑是否正确

## 技术栈
- C# .NET 6.0
- 控制台应用程序
- ECS 架构
- 纯 C# 实现，无外部依赖

## ECS 架构规则

### 核心原则
1. **Entity（实体）**
   - 只是 ID，不包含任何数据或逻辑
   - 由 EntityManager 统一管理
   - 通过 ID 引用

2. **Component（组件）**
   - 只包含数据，不包含任何逻辑
   - 使用 class 实现
   - 必须有清晰的命名：XxxComponent

3. **System（系统）**
   - 只包含逻辑，不包含数据
   - 通过 EntityManager 操作实体和组件
   - 处理拥有特定组件的实体

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
           日志输出
```

## 开发规范

### 组件开发
- ✅ 只包含数据，不包含逻辑
- ✅ 必须以 Component 结尾
- ✅ 使用 class 实现
- ✅ 所有字段必须是 public
- ✅ 必须有构造函数

```csharp
public class HealthComponent
{
    public float Current { get; set; }
    public float Max { get; set; }

    public HealthComponent(float current, float max)
    {
        Current = current;
        Max = max;
    }
}
```

### 系统开发
- ✅ 只包含逻辑，不包含数据
- ✅ 必须以 System 结尾
- ✅ 必须接收 EntityManager 作为构造参数
- ✅ 所有逻辑都要有日志记录

```csharp
public class DamageSystem
{
    private EntityManager entityManager;

    public DamageSystem(EntityManager entityManager)
    {
        this.entityManager = entityManager;
    }

    public float CalculateDamage(int attackerId, int defenderId)
    {
        // 伤害计算逻辑
    }
}
```

### 日志规范
- ✅ 所有战斗过程都要有日志
- ✅ 使用统一的 BattleLogger
- ✅ 日志级别：[INFO], [BATTLE], [DAMAGE], [DEATH], [WIN]
- ✅ 伤害计算要记录详细信息

## 战斗系统规则

### 伤害公式
```
基础伤害 = 攻击力 - 防御力 * 0.5
实际伤害 = max(1, 基础伤害)
暴击伤害 = 实际伤害 * 1.5
```

### 暴击系统
- 暴击几率：20%
- 暴击倍率：1.5 倍
- 暴击标记：在日志中显示 [暴击!]

### 战斗流程
1. 创建玩家和敌人实体
2. 添加必要的组件
3. 玩家攻击敌人
4. 敌人攻击玩家
5. 重复步骤 3-4，直到一方死亡
6. 记录战斗结果

## 项目结构
```
BattleSystem-ECS/
├── Components.cs         # 所有组件定义
├── EntityManager.cs     # ECS 核心：实体管理器
├── BattleLogger.cs      # 日志系统
├── DamageSystem.cs      # 伤害计算系统
├── CombatSystem.cs      # 战斗逻辑系统
├── Program.cs           # 主程序
├── BattleSystemECS.csproj # 项目文件
├── README.md            # 说明文档
└── AGENTS.md           # 本文件
```

## 注意事项
- 严格遵循 ECS 架构
- Component 不包含逻辑
- System 不包含数据
- 所有逻辑都要有日志
- 纯 C# 实现，无渲染依赖

## 迭代记录
- 2026-02-08: 初始化项目，实现 ECS 架构
- 2026-02-08: 完成基础战斗功能
- 2026-02-08: 添加战报日志系统
- 2026-02-08: 完成文档编写

## 下一步扩展
- 添加技能系统
- 实现 Buff/Debuff 机制
- 支持团队战斗
- 实现存档/读档
- 添加装备系统
- 实现战斗 AI

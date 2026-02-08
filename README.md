# 战斗系统 Demo - ECS 架构 - 完整版

一个使用 **ECS（Entity Component System）** 架构实现的完整战斗系统，**逻辑核心与渲染层完全分离**，纯 C# 语言，可独立编译成 exe 运行。

## ✨ 核心特性

- 🏗️ **ECS 架构**：严格的 Entity-Component-System 设计
- 🎯 **逻辑核心与渲染分离**：战斗逻辑不涉及任何输出，完全独立
- 🎨 **可插拔渲染器**：通过 IRenderer 接口，轻松切换渲染方式
- 🔮 **完整战斗系统**：普通攻击 + 技能系统 + Buff/Debuff 系统
- 🚀 **纯 C# 实现**：无任何渲染依赖，完全独立
- 📦 **独立 exe**：可直接编译成可执行文件
- 📜 **战报日志**：详细记录战斗过程

## 🎮 系统功能

### ✅ 基础功能
- 回合制战斗
- 普通攻击
- 暴击系统（20% 几率，1.5 倍伤害）
- 伤害公式：攻击力 - 防御力 * 0.5

### ✅ 技能系统
- 技能释放
- 技能冷却
- 魔力消耗
- 魔力恢复
- 技能伤害计算

### ✅ Buff/Debuff 系统
- Buff 效果（攻击力加成、防御力加成、治疗）
- Debuff 效果（攻击力惩罚、防御力惩罚、持续伤害）
- 持续时间管理
- 效果自动应用和移除

### ✅ 渲染层
- 控制台日志渲染
- 文件日志渲染
- 易于扩展新的渲染方式

## 🏗️ ECS 架构说明

### ECS 核心概念

1. **Entity（实体）**
   - 只是 ID，不包含任何数据或逻辑
   - 由 EntityManager 管理

2. **Component（组件）**
   - 纯数据，不包含逻辑
   - 例如：HealthComponent, SkillComponent, BuffComponent

3. **System（系统）**
   - 纯逻辑，操作拥有特定组件的实体
   - 例如：DamageSystem, SkillSystem, CombatSystem

4. **Renderer（渲染器）**
   - 负责将游戏状态呈现给用户
   - 实现 IRenderer 接口
   - 例如：ConsoleLogger, FileLogger

## 📁 项目结构

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
│   ├── CombatSystem.cs     # 战斗系统
│   ├── SkillSystem.cs      # 技能系统
│   └── BuffDebuffSystem.cs  # Buff/Debuff 系统
├── EntityManager.cs        # ECS 核心
├── Program.cs              # 主程序
├── BattleSystemECS.csproj # 项目文件
├── README.md               # 本文件
└── AGENTS.md               # 开发规则
```

## 🚀 快速开始

### 前置要求
- .NET 6.0 SDK 或更高版本

### 编译项目

```bash
cd F:\AI\BattleSystem-ECS
dotnet build
```

### 运行项目

```bash
dotnet run
```

运行时会提示选择渲染方式：
- 1. 控制台日志（默认）
- 2. 文件日志（保存到 battle_log.txt）

### 生成独立 exe

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

生成的 exe 位于：
```
bin\Release\net6.0\win-x64\publish\BattleSystemECS.exe
```

## 🏗️ ECS 组件列表

### 基础组件

1. **HealthComponent** - 生命值组件
   - Current: 当前生命值
   - Max: 最大生命值

2. **AttackPowerComponent** - 攻击力组件
   - Value: 攻击力数值

3. **DefensePowerComponent** - 防御力组件
   - Value: 防御力数值

4. **NameComponent** - 名称组件
   - Value: 实体名称（用于日志输出）

5. **PlayerTagComponent** - 玩家标签
6. **EnemyTagComponent** - 敌人标签
7. **BattleStateComponent** - 战斗状态组件

### 技能组件

8. **SkillComponent** - 技能组件
   - SkillName: 技能名称
   - Damage: 技能伤害
   - Cooldown: 冷却时间
   - CurrentCooldown: 当前冷却时间
   - ManaCost: 魔力消耗
   - CurrentMana: 当前魔力
   - MaxMana: 最大魔力
   - CanUseSkill(): 是否可以使用技能
   - UseSkill(): 使用技能
   - UpdateCooldown(): 更新冷却
   - RegenerateMana(): 恢复魔力

### Buff/Debuff 组件

9. **BuffComponent** - Buff 组件
   - BuffName: Buff 名称
   - Duration: 持续时间
   - RemainingDuration: 剩余持续时间
   - AttackBonus: 攻击力加成
   - DefenseBonus: 防御力加成
   - HealAmount: 治疗量
   - IsActive(): 是否激活
   - UpdateDuration(): 更新持续时间
   - ApplyEffect(): 应用效果
   - RemoveEffect(): 移除效果

10. **DebuffComponent** - Debuff 组件
    - DebuffName: Debuff 名称
    - Duration: 持续时间
    - RemainingDuration: 剩余持续时间
    - AttackPenalty: 攻击力惩罚
    - DefensePenalty: 防御力惩罚
    - DamageOverTime: 每秒伤害
    - IsActive(): 是否激活
    - UpdateDuration(): 更新持续时间
    - ApplyEffect(): 应用效果
    - ApplyDamageOverTime(): 应用持续伤害
    - RemoveEffect(): 移除效果

## ⚙️ ECS 系统列表

### 核心系统

1. **DamageSystem** - 伤害计算系统
   - CalculateDamage(): 计算伤害
   - CheckCritical(): 检查是否暴击

2. **CombatSystem** - 战斗系统
   - StartBattle(): 开始战斗
   - ProcessBattle(): 处理战斗回合
   - ApplyDamage(): 应用伤害
   - TryUseSkill(): 尝试使用技能
   - ApplySkillDamage(): 应用技能伤害

3. **SkillSystem** - 技能系统
   - UpdateCooldowns(): 更新技能冷却
   - RegenerateMana(): 恢复魔力
   - UseSkill(): 使用技能
   - GetSkillStatus(): 获取技能状态

4. **BuffDebuffSystem** - Buff/Debuff 系统
   - UpdateDurations(): 更新持续时间和效果
   - ApplyBuffEffects(): 应用 Buff 效果
   - ApplyDebuffEffects(): 应用 Debuff 效果
   - UpdateDurations(): 更新持续时间
   - ApplyDamageOverTime(): 应用持续伤害

### 渲染系统

5. **IRenderer** - 渲染器接口
   - Log(): 输出信息
   - LogBattle(): 输出战斗信息
   - LogDamage(): 输出伤害信息
   - LogDeath(): 输出死亡信息
   - LogWin(): 输出胜利信息
   - LogBattleStart(): 输出战斗开始
   - LogTurn(): 输出回合信息

6. **ConsoleLogger** - 控制台日志渲染器
   - 实现 IRenderer 接口
   - 输出到控制台

7. **FileLogger** - 文件日志渲染器
   - 实现 IRenderer 接口
   - 输出到文件（默认：battle_log.txt）

## 📜 战斗日志示例

### 控制台日志模式

```
========================================
     战斗系统 Demo - ECS 架构
     逻辑核心与渲染层完全分离
========================================

请选择渲染方式：
1. 控制台日志（默认）
2. 文件日志（保存到 battle_log.txt）

请输入选择 (1-2): 1
[INFO] 已选择：控制台日志渲染

========================================
[BATTLE] 战斗开始：玩家 VS 敌人
========================================
[INFO] 玩家 - 攻击: 50, 防御: 20, 生命: 100/100
[INFO] 敌人 - 攻击: 40, 防御: 15, 生命: 100/100

[INFO] 战斗开始！

[INFO] [BUFF] 狂暴 效果已应用到实体 1
[BATTLE] --- 第 1 回合 ---
[INFO] [SKILL] 火球术 释放成功！造成 60.0 点伤害，消耗 20 魔力
[INFO] [SKILL DAMAGE] 玩家 使用火球术，对 敌人 造成 55.5 点技能伤害
[INFO] 敌人 剩余生命: 44.5/100.0
[DAMAGE] 玩家 攻击 敌人，造成 42.5 点伤害
[INFO] 敌人 剩余生命: 2.0/100.0
[INFO] [ERROR] 实体 2 没有名为 火球术 的技能
[DAMAGE] 敌人 攻击 玩家，造成 45.0 点伤害 [暴击!]
[INFO] 玩家 剩余生命: 55.0/100.0
[INFO] [BUFF] 狂暴 效果已应用到实体 1
[BATTLE] --- 第 2 回合 ---
[INFO] [ERROR] 技能 火球术 还在冷却中，剩余 10.0 秒
[DAMAGE] 玩家 攻击 敌人，造成 42.5 点伤害
[INFO] 敌人 剩余生命: 0.0/100.0
[DEATH] 敌人 已死亡！
[DEATH] 敌人 已死亡！
[WIN] 战斗结束，玩家 获胜！
========================================

[INFO] 战斗结束！

[INFO] 程序即将退出...
```

## 🎯 游戏机制详解

### 伤害计算

#### 普通攻击伤害
```csharp
基础伤害 = 攻击力 - 防御力 * 0.5
实际伤害 = max(1, 基础伤害)
```

#### 技能伤害
```csharp
技能伤害 = 技能伤害值 - 防御力 * 0.3
实际伤害 = max(1, 技能伤害)
```

#### 暴击系统
- 普通攻击：20% 几率，1.5 倍伤害
- 技能攻击：20% 几率，1.5 倍伤害

### 技能系统

#### 技能冷却
- 技能释放后进入冷却状态
- 冷却时间结束后可再次使用

#### 魔力系统
- 使用技能消耗魔力
- 每秒自动恢复魔力（默认：5 点/秒）
- 魔力不足时无法使用技能

### Buff/Debuff 系统

#### Buff 效果
- **攻击力加成**：增加攻击力
- **防御力加成**：增加防御力
- **治疗**：恢复生命值

#### Debuff 效果
- **攻击力惩罚**：减少攻击力
- **防御力惩罚**：减少防御力
- **持续伤害**：每秒造成固定伤害

#### 持续时间管理
- Buff/Debuff 有持续时间
- 持续时间结束后自动移除效果
- 在战斗过程中实时更新

## 🔧 自定义配置

### 修改实体属性

在 `Program.cs` 的 `CreatePlayer()` 和 `CreateEnemy()` 方法中修改：

```csharp
// 创建玩家
entityManager.AddComponent(playerId, new HealthComponent(100f, 100f));  // 生命值
entityManager.AddComponent(playerId, new AttackPowerComponent(50f));    // 攻击力
entityManager.AddComponent(playerId, new DefensePowerComponent(20f));   // 防御力
```

### 添加新技能

在 `Program.cs` 中添加新技能组件：

```csharp
// 添加技能（冰冻术）
entityManager.AddComponent(playerId, new SkillComponent(
    skillName: "冰冻术",
    damage: 40f,           // 技能伤害
    cooldown: 8f,         // 冷却时间（秒）
    manaCost: 15,         // 魔力消耗
    maxMana: 100          // 最大魔力
));
```

### 添加新 Buff/Debuff

```csharp
// 添加 Buff（增加防御力）
entityManager.AddComponent(playerId, new BuffComponent(
    buffName: "护盾",
    duration: 10f,        // 持续时间（秒）
    attackBonus: 0f,
    defenseBonus: 30f,    // 防御力加成
    healAmount: 0f
));

// 添加 Debuff（减少攻击力）
entityManager.AddComponent(enemyId, new DebuffComponent(
    debuffName: "虚弱",
    duration: 5f,         // 持续时间（秒）
    attackPenalty: 20f,    // 攻击力惩罚
    defensePenalty: 0f,
    damageOverTime: 0f
));
```

## 🎮 ECS 架构示例

### 创建实体

```csharp
// 创建实体
int playerId = entityManager.CreateEntity();

// 添加组件
entityManager.AddComponent(playerId, new NameComponent("玩家"));
entityManager.AddComponent(playerId, new HealthComponent(100f, 100f));
entityManager.AddComponent(playerId, new AttackPowerComponent(50f));
entityManager.AddComponent(playerId, new DefensePowerComponent(20f));
entityManager.AddComponent(playerId, new SkillComponent("火球术", 60f, 10f, 20, 100));
entityManager.AddComponent(playerId, new BuffComponent("狂暴", 5f, 0f, 0f, 0f));
```

### 查询实体

```csharp
// 获取拥有特定组件的所有实体
var entitiesWithSkill = entityManager.GetEntitiesWithComponent<SkillComponent>();

// 检查实体是否有组件
if (entityManager.HasComponent<NameComponent>(entityId))
{
    var name = entityManager.GetComponent<NameComponent>(entityId);
    Console.WriteLine(name.Value);
}
```

### 更新组件

```csharp
// 获取组件
var health = entityManager.GetComponent<HealthComponent>(entityId);

// 修改数据
health.Current -= 10f;

// 更新组件
entityManager.SetComponent(entityId, health);
```

## 🚀 下一步扩展

基于 ECS 架构和逻辑核心与渲染分离的设计，可以轻松扩展：

1. **装备系统**
   - 创建 EquipmentComponent
   - 创建 EquipmentSystem
   - 影响实体属性

2. **团队战斗**
   - 创建 TeamComponent
   - 修改战斗逻辑支持多对多
   - 使用 ECS 查询获取队友和敌人

3. **存档系统**
   - 实现存档/读档
   - 支持 JSON 或 XML 序列化

4. **战斗 AI**
   - 创建 AIComponent
   - 创建 AISystem
   - 实现自动战斗 AI

5. **更多技能**
   - 添加新技能组件
   - 实现不同的技能效果

6. **更多 Buff/Debuff**
   - 添加新的 Buff/Debuff 类型
   - 实现复杂的组合效果

## 📝 注意事项

- ECS 架构严格分离数据和逻辑
- Component 只包含数据
- System 只包含逻辑
- Renderer 只负责输出
- 逻辑核心不依赖渲染层

## 📞 技术优势

- **性能优秀**：数据局部性好，缓存命中率高
- **易于扩展**：添加新功能只需添加新组件或系统
- **易于测试**：逻辑核心可以独立测试
- **代码清晰**：数据和逻辑分离，易于理解和维护

## 🛠️ 故障排除

### 编译错误
- 检查是否添加了所有必要的 using 语句
- 确认项目配置正确

### 运行时错误
- 检查实体是否正确创建
- 检查组件是否正确添加
- 检查系统是否正确初始化

### 战斗不正常
- 检查 CombatSystem 是否正确配置
- 检查 EntityManager 是否正确传递
- 检查 Renderer 是否正确注入

---

**开始你的 ECS 架构开发之旅吧！** 🏗️⚡

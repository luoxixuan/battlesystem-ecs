# 塔防游戏 ECS + GAS 知识库
> 自动生成 · 2026-06-01 01:09

已分析 16 个仓库

## 塔防专项模式

### 技能系统
GAS 风格 Ability + Modifier 分离
来源：[felipeggrod/gasify](https://github.com/felipeggrod/gasify), [intrxx/Obsidian](https://github.com/intrxx/Obsidian)

### 实体管理器
ECS 风格：实体创建/销毁/查询
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics)

### 伤害计算
攻击/防御/暴击/属性缩放公式
来源：[intrxx/Obsidian](https://github.com/intrxx/Obsidian), [Pantong51/GASContent](https://github.com/Pantong51/GASContent)

### 性能优化
Burst 编译、NativeArray、JobSystem
来源：[keijiro/Voxelman](https://github.com/keijiro/Voxelman), [reeseschultz/ReeseUnityDemos](https://github.com/reeseschultz/ReeseUnityDemos)

### 攻击间隔
攻速属性、独立冷却管理
来源：[felipeggrod/gasify](https://github.com/felipeggrod/gasify)

### Unity DOTS Archetype
DOTS 模式：chunk data layout + entity query
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

### 状态机 AI
敌怪状态机：移动/攻击/死亡
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

### Unity 桥接
纯 ECS 逻辑与 GameObject 渲染层桥接方案
来源：[Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics)

### 塔升级系统
塔等级/星级/进阶，属性成长曲线配置化
来源：[rparrett/taipo](https://github.com/rparrett/taipo)

### 敌怪属性
敌怪血量/攻击/速度随波次成长
来源：[intrxx/Obsidian](https://github.com/intrxx/Obsidian)

### 行为树
行为树节点：Sequence/Selector/Condition/Action
来源：[Pantong51/GASContent](https://github.com/Pantong51/GASContent)

## 项目架构线索

### 配置数据
来源：[DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura), [DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura)

### ECS 架构
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics)

## 通用工程模式

### 状态机模式
状态转换清晰，可视化
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

### 缓存友好
数据连续布局，缓存命中优先
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

## 实践洞察

- "While other frameworks typically limit user freedom to avoid exposing flaws in the archetype-based concept, Svelto." — [sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS) (2026-05-31)
- "That's a best practice (Based on working experience), that's not only for Attributes Sets, but for any C++ class that might get reference by another object." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-06-01)
- "The reason is simple: a game should avoid rubber-banding death." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-06-01)

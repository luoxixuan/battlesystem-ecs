# 塔防游戏 ECS + GAS 知识库
> 自动生成 · 2026-05-31 01:19

已分析 8 个仓库

## 塔防专项模式

### 实体管理器
ECS 风格：实体创建/销毁/查询
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics)

### 攻击间隔
攻速属性、独立冷却管理
来源：[felipeggrod/gasify](https://github.com/felipeggrod/gasify)

### 技能系统
GAS 风格 Ability + Modifier 分离
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

## 项目架构线索

### ECS 架构
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics)

### 配置数据
来源：[DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura), [DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura)

## 通用工程模式

### 状态机模式
状态转换清晰，可视化
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

### 缓存友好
数据连续布局，缓存命中优先
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

## 实践洞察

- "While other frameworks typically limit user freedom to avoid exposing flaws in the archetype-based concept, Svelto." — [sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS) (2026-05-31)

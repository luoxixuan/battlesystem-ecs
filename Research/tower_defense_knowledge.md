# 塔防游戏 ECS + GAS 知识库
> 自动生成 · 2026-05-13 01:00

已分析 21 个仓库

## 塔防专项模式

### 实体管理器
ECS 风格：实体创建/销毁/查询
来源：[genaray/Arch](https://github.com/genaray/Arch), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### Unity DOTS Archetype
DOTS 模式：chunk data layout + entity query
来源：[genaray/Arch](https://github.com/genaray/Arch), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 技能系统
GAS 风格 Ability + Modifier 分离
来源：[imnazake/Unify](https://github.com/imnazake/Unify), [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 状态机 AI
敌怪状态机：移动/攻击/死亡
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 行为树
行为树节点：Sequence/Selector/Condition/Action
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 性能优化
Burst 编译、NativeArray、JobSystem
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 伤害计算
攻击/防御/暴击/属性缩放公式
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

## 通用工程模式

### 状态机模式
状态转换清晰，可视化
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

## 实践洞察

- "That's a best practice (Based on working experience), that's not only for Attributes Sets, but for any C++ class that might get reference by another object." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-05-12)
- "The reason is simple: a game should avoid rubber-banding death." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-05-12)

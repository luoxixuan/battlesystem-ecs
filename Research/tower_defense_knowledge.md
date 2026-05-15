# 塔防游戏 ECS + GAS 知识库
> 自动生成 · 2026-05-16 01:00

已分析 34 个仓库

## 塔防专项模式

### 技能系统
GAS 风格 Ability + Modifier 分离
来源：[imnazake/Unify](https://github.com/imnazake/Unify), [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 实体管理器
ECS 风格：实体创建/销毁/查询
来源：[genaray/Arch](https://github.com/genaray/Arch), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 状态机 AI
敌怪状态机：移动/攻击/死亡
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 塔升级系统
塔等级/星级/进阶，属性成长曲线配置化
来源：[prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D), [prabdhal/TD3D-UnityGame](https://github.com/prabdhal/TD3D-UnityGame)

### Unity DOTS Archetype
DOTS 模式：chunk data layout + entity query
来源：[genaray/Arch](https://github.com/genaray/Arch), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 行为树
行为树节点：Sequence/Selector/Condition/Action
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 攻击间隔
攻速属性、独立冷却管理
来源：[Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity), [felipeggrod/gasify](https://github.com/felipeggrod/gasify)

### 敌怪属性
敌怪血量/攻击/速度随波次成长
来源：[prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D), [prabdhal/TD3D-UnityGame](https://github.com/prabdhal/TD3D-UnityGame)

### 性能优化
Burst 编译、NativeArray、JobSystem
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### 伤害计算
攻击/防御/暴击/属性缩放公式
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 空间分区
GridSpatialHash O(1) 邻域查询，避免全量遍历
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### Unity 桥接
纯 ECS 逻辑与 GameObject 渲染层桥接方案
来源：[No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity)

### 效果系统
伤害/治疗/控制效果排队执行
来源：[h2v9696/UnityGAS](https://github.com/h2v9696/UnityGAS)

## 通用工程模式

### ScriptableObject
数据资产化，配置与代码分离
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity)

### 状态机模式
状态转换清晰，可视化
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 对象池模式
复用对象，减少 Instantiate/Destroy
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### SerializeField
Inspector 调试，保留封装
来源：[No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity)

## 实践洞察

- "That's a best practice (Based on working experience), that's not only for Attributes Sets, but for any C++ class that might get reference by another object." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-05-12)
- "The reason is simple: a game should avoid rubber-banding death." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-05-12)
- "- Increases a character's ability to avoid incoming attacks." — [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity) (2026-05-15)

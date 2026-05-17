# 塔防游戏 ECS + GAS 知识库
> 自动生成 · 2026-05-18 01:10

已分析 74 个仓库

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

### Unity DOTS Archetype
DOTS 模式：chunk data layout + entity query
来源：[genaray/Arch](https://github.com/genaray/Arch), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS)

### Unity 桥接
纯 ECS 逻辑与 GameObject 渲染层桥接方案
来源：[No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity), [SaiTingHu/HTFramework](https://github.com/SaiTingHu/HTFramework)

### 攻击间隔
攻速属性、独立冷却管理
来源：[Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity), [felipeggrod/gasify](https://github.com/felipeggrod/gasify)

### 塔升级系统
塔等级/星级/进阶，属性成长曲线配置化
来源：[prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D), [prabdhal/TD3D-UnityGame](https://github.com/prabdhal/TD3D-UnityGame)

### 寻路系统
A*/BFS/网格寻路，敌人沿路径移动
来源：[zulfajuniadi/unity-ecs-navmesh](https://github.com/zulfajuniadi/unity-ecs-navmesh), [quiver-dev/tower-defense-tutorial](https://github.com/quiver-dev/tower-defense-tutorial)

### 行为树
行为树节点：Sequence/Selector/Condition/Action
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 性能优化
Burst 编译、NativeArray、JobSystem
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [AkanshDivker/Simple-ECS](https://github.com/AkanshDivker/Simple-ECS)

### 敌怪属性
敌怪血量/攻击/速度随波次成长
来源：[prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D), [prabdhal/TD3D-UnityGame](https://github.com/prabdhal/TD3D-UnityGame)

### 系统更新
SystemBase 按组排序更新，数据逻辑分离
来源：[AkanshDivker/Simple-ECS](https://github.com/AkanshDivker/Simple-ECS), [annulusgames/MagicTween](https://github.com/annulusgames/MagicTween)

### 敌怪 AI
AI 决策：追踪/逃跑/施法/躲避
来源：[quiver-dev/tower-defense-tutorial](https://github.com/quiver-dev/tower-defense-tutorial), [quiver-dev/tower-defense-godot4](https://github.com/quiver-dev/tower-defense-godot4)

### 伤害计算
攻击/防御/暴击/属性缩放公式
来源：[Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 空间分区
GridSpatialHash O(1) 邻域查询，避免全量遍历
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 效果系统
伤害/治疗/控制效果排队执行
来源：[h2v9696/UnityGAS](https://github.com/h2v9696/UnityGAS)

## 项目架构线索

### 配置数据
来源：[strayTrain/SimpleGameplayAbilitySystem](https://github.com/strayTrain/SimpleGameplayAbilitySystem), [fpwong/FPGameplayAbilities](https://github.com/fpwong/FPGameplayAbilities)

### 性能优化
来源：[sschmid/Entitas](https://github.com/sschmid/Entitas)

### 塔系统
来源：[Brackeys/Tower-Defense-Tutorial](https://github.com/Brackeys/Tower-Defense-Tutorial)

## 通用工程模式

### 状态机模式
状态转换清晰，可视化
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### ScriptableObject
数据资产化，配置与代码分离
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity)

### 对象池模式
复用对象，减少 Instantiate/Destroy
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [SaiTingHu/HTFramework](https://github.com/SaiTingHu/HTFramework)

### SerializeField
Inspector 调试，保留封装
来源：[No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity), [sjai013/unity-gameplay-ability-system](https://github.com/sjai013/unity-gameplay-ability-system)

### 缓存友好
数据连续布局，缓存命中优先
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

### 事件总线
解耦系统通信，Publish/Subscribe
来源：[PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity)

## 实践洞察

- "That's a best practice (Based on working experience), that's not only for Attributes Sets, but for any C++ class that might get reference by another object." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-05-12)
- "The reason is simple: a game should avoid rubber-banding death." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-05-12)
- "- Increases a character's ability to avoid incoming attacks." — [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity) (2026-05-15)
- "While other frameworks typically limit user freedom to avoid exposing flaws in the archetype-based concept, Svelto." — [sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS) (2026-05-17)
- "It's recommended to use extension methods when available." — [annulusgames/MagicTween](https://github.com/annulusgames/MagicTween) (2026-05-17)
- "In most cases, the impact on performance is minimal, but it's recommended to avoid using callbacks when creating a large number of tweens." — [annulusgames/MagicTween](https://github.com/annulusgames/MagicTween) (2026-05-17)
- "This is a very powerful concept and I don't recommend to use it until you really understand what are you doing." — [PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity) (2026-05-17)

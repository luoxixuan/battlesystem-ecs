# 塔防游戏 ECS + GAS 知识库
> 自动生成 · 2026-06-10 01:03 · v3

已分析 85 个仓库

## 塔防专项模式

### 技能系统
> GAS 风格 Ability + Modifier 分离
来源：[felipeggrod/gasify](https://github.com/felipeggrod/gasify), [intrxx/Obsidian](https://github.com/intrxx/Obsidian), [Pantong51/GASContent](https://github.com/Pantong51/GASContent)

### 分裂/克隆
> 敌人死后分裂为多个小怪，或主动克隆
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [pshenok/server-survival](https://github.com/pshenok/server-survival), [ape1121/Godot-4-Tower-Defense-Template](https://github.com/ape1121/Godot-4-Tower-Defense-Template)

### 实体管理器
> ECS 风格：实体创建/销毁/查询
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics), [areilly711/unity_ecs](https://github.com/areilly711/unity_ecs)

### N击护盾/屏障
> 需N次命中击破的护盾，或伤害阈值盾
来源：[FlameskyDexive/Legends-Of-Heroes](https://github.com/FlameskyDexive/Legends-Of-Heroes), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity)

### 弧线弹道
> 迫击炮/抛射弹道，无视地形障碍
来源：[PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity), [chromealex/ecs](https://github.com/chromealex/ecs), [Antoshidza/NSprites](https://github.com/Antoshidza/NSprites)

### 锁链/连接
> 两个敌人生命/伤害共享，强制绑定
来源：[prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity)

### 处决/死亡标记
> 目标血量低于阈值自动处决，额外金币
来源：[PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity), [Antoshidza/NSprites](https://github.com/Antoshidza/NSprites), [v1vendi/minimal_ue5_GAS_demo](https://github.com/v1vendi/minimal_ue5_GAS_demo)

### 恐惧/混乱
> 敌人反向逃跑或随机移动，CC 状态
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity), [pshenok/server-survival](https://github.com/pshenok/server-survival)

### 状态机 AI
> 敌怪状态机：移动/攻击/死亡
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [killop/anything_about_game](https://github.com/killop/anything_about_game), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 范围溅射
> 命中目标后对周围敌人造成范围伤害
来源：[prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [maciej-trebacz/tower-of-time-game](https://github.com/maciej-trebacz/tower-of-time-game)

### 失衡/破防/击退
> 累积伤害触发硬直/击退/打断施法
来源：[pshenok/server-survival](https://github.com/pshenok/server-survival), [techwithtim/Tower-Defense-Game](https://github.com/techwithtim/Tower-Defense-Game), [danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### Unity DOTS Archetype
> DOTS 模式：chunk data layout + entity query
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [killop/anything_about_game](https://github.com/killop/anything_about_game), [zulfajuniadi/unity-ecs-navmesh](https://github.com/zulfajuniadi/unity-ecs-navmesh)

### 寻路系统
> A*/BFS/网格寻路，敌人沿路径移动
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game), [zulfajuniadi/unity-ecs-navmesh](https://github.com/zulfajuniadi/unity-ecs-navmesh), [chromealex/ecs](https://github.com/chromealex/ecs)

### 拉扯/吸引
> 将敌人拉向塔或特定位置
来源：[Antoshidza/NSprites](https://github.com/Antoshidza/NSprites), [ape1121/Godot-4-Tower-Defense-Template](https://github.com/ape1121/Godot-4-Tower-Defense-Template), [danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### 攻击间隔
> 攻速属性、独立冷却管理
来源：[felipeggrod/gasify](https://github.com/felipeggrod/gasify), [ape1121/Godot-4-Tower-Defense-Template](https://github.com/ape1121/Godot-4-Tower-Defense-Template), [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity)

### 踩踏/冲锋
> Boss 直线冲锋伤害路径上的单位
来源：[zulfajuniadi/unity-ecs-navmesh](https://github.com/zulfajuniadi/unity-ecs-navmesh), [pshenok/server-survival](https://github.com/pshenok/server-survival), [danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### Unity 桥接
> 纯 ECS 逻辑与 GameObject 渲染层桥接方案
来源：[Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics), [killop/anything_about_game](https://github.com/killop/anything_about_game), [No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity)

### 塔升级系统
> 塔等级/星级/进阶，属性成长曲线配置化
来源：[rparrett/taipo](https://github.com/rparrett/taipo), [prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D), [rickylai248/Bloons-Tower-Defense](https://github.com/rickylai248/Bloons-Tower-Defense)

### 伤害计算
> 攻击/防御/暴击/属性缩放公式
来源：[intrxx/Obsidian](https://github.com/intrxx/Obsidian), [Pantong51/GASContent](https://github.com/Pantong51/GASContent), [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example)

### 行为树
> 行为树节点：Sequence/Selector/Condition/Action
来源：[Pantong51/GASContent](https://github.com/Pantong51/GASContent), [killop/anything_about_game](https://github.com/killop/anything_about_game), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 性能优化
> Burst 编译、NativeArray、JobSystem
来源：[keijiro/Voxelman](https://github.com/keijiro/Voxelman), [reeseschultz/ReeseUnityDemos](https://github.com/reeseschultz/ReeseUnityDemos), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 系统更新
> SystemBase 按组排序更新，数据逻辑分离
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game), [AkanshDivker/Simple-ECS](https://github.com/AkanshDivker/Simple-ECS), [annulusgames/MagicTween](https://github.com/annulusgames/MagicTween)

### 施法可打断
> 敌人施法有前摇，可被CC打断
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [chromealex/ecs](https://github.com/chromealex/ecs), [EcsRx/ecsrx.unity](https://github.com/EcsRx/ecsrx.unity)

### 相位/幽灵敌人
> 敌人可穿越塔/障碍，免疫物理伤害
来源：[maciej-trebacz/tower-of-time-game](https://github.com/maciej-trebacz/tower-of-time-game), [v1vendi/minimal_ue5_GAS_demo](https://github.com/v1vendi/minimal_ue5_GAS_demo), [archangel4031/Myra](https://github.com/archangel4031/Myra)

### 敌怪属性
> 敌怪血量/攻击/速度随波次成长
来源：[intrxx/Obsidian](https://github.com/intrxx/Obsidian), [prabdhal/Tower-Defence-3D](https://github.com/prabdhal/Tower-Defence-3D)

### 空间分区
> GridSpatialHash O(1) 邻域查询，避免全量遍历
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 诱饵/吸引塔
> 吸引敌人偏离路径或攻击诱饵而非基地
来源：[zulfajuniadi/unity-ecs-navmesh](https://github.com/zulfajuniadi/unity-ecs-navmesh), [pshenok/server-survival](https://github.com/pshenok/server-survival)

### 塔能量/法力
> 塔消耗法力攻击，法力恢复/消耗管理
来源：[maciej-trebacz/tower-of-time-game](https://github.com/maciej-trebacz/tower-of-time-game), [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity)

### 敌怪 AI
> AI 决策：追踪/逃跑/施法/躲避
来源：[quiver-dev/tower-defense-tutorial](https://github.com/quiver-dev/tower-defense-tutorial), [quiver-dev/tower-defense-godot4](https://github.com/quiver-dev/tower-defense-godot4)

### 伤害反弹/反击
> 受到攻击时反弹百分比伤害给攻击者
来源：[danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2), [scellecs/morpeh](https://github.com/scellecs/morpeh)

### 过量伤害/溢出
> 伤害超过目标血量时溢出给周围敌人
来源：[danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2), [bartofzo/NativeTrees](https://github.com/bartofzo/NativeTrees)

### 路径点系统
> 预定义路径点序列，支持分支路径
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 渲染系统
> ECS 数据到 Unity 渲染的同步方案
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 资源加载
> Addressables 动态加载塔/敌怪/技能资源
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 塔变形/形态切换
> 塔可在多种形态间切换（对单/对群/控制）
来源：[pshenok/server-survival](https://github.com/pshenok/server-survival)

### 穿透弹道
> 子弹穿过敌人继续飞行，线性伤害
来源：[ape1121/Godot-4-Tower-Defense-Template](https://github.com/ape1121/Godot-4-Tower-Defense-Template)

### 弹跳弹道
> 子弹碰到目标后弹向下一目标
来源：[danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### 光束/激光塔
> 持续照射敌人，每帧 tick 伤害。预热/过热机制
来源：[danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### 巡逻/移动塔
> 塔可沿路径移动，动态调整防守位置
来源：[danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### 钻地/潜行
> 敌人钻入地下躲避攻击，然后冒出
来源：[danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### 子弹时间
> 全局/局部时间减速，仅敌人受影响
来源：[danderfer/Comp_Sci_Sem_2](https://github.com/danderfer/Comp_Sci_Sem_2)

### 成长曲线
> 经验/等级/敌人强度曲线配置化
来源：[CompleteUnityDeveloper/07-Glitch-Garden](https://github.com/CompleteUnityDeveloper/07-Glitch-Garden)

## 源码结构参考

### Leopotam/ecslite (521⭐)
- `src/components.cs` — `_sparseItems`, `_recycledItems`
- `src/filters.cs` — `_denseEntities`, `SparseEntities`, `GetRawEntities`
- `src/worlds.cs` — `_recycledEntities`

## 项目架构线索

### 配置数据
来源：[DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura), [DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura), [intrxx/Obsidian](https://github.com/intrxx/Obsidian)

### 塔系统
来源：[quiver-dev/tower-defense-tutorial](https://github.com/quiver-dev/tower-defense-tutorial), [techwithtim/Tower-Defense-Game](https://github.com/techwithtim/Tower-Defense-Game), [SanderMertens/tower_defense](https://github.com/SanderMertens/tower_defense)

### GAS 技能系统
来源：[v1vendi/minimal_ue5_GAS_demo](https://github.com/v1vendi/minimal_ue5_GAS_demo), [MaiKuraki/UnityGameplayAbilitySystemSample](https://github.com/MaiKuraki/UnityGameplayAbilitySystemSample), [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity)

### 敌怪系统
来源：[ape1121/Godot-4-Tower-Defense-Template](https://github.com/ape1121/Godot-4-Tower-Defense-Template), [quiver-dev/tower-defense-tutorial](https://github.com/quiver-dev/tower-defense-tutorial), [techwithtim/Tower-Defense-Game](https://github.com/techwithtim/Tower-Defense-Game)

### 技能/能力系统
来源：[LordWake/Unreal-2021-GameplayAbilitySystem](https://github.com/LordWake/Unreal-2021-GameplayAbilitySystem), [strayTrain/SimpleGameplayAbilitySystem](https://github.com/strayTrain/SimpleGameplayAbilitySystem), [strayTrain/SimpleGameplayAbilitySystem](https://github.com/strayTrain/SimpleGameplayAbilitySystem)

### AI/行为树
来源：[pshenok/server-survival](https://github.com/pshenok/server-survival), [ape1121/Godot-4-Tower-Defense-Template](https://github.com/ape1121/Godot-4-Tower-Defense-Template), [techwithtim/Tower-Defense-Game](https://github.com/techwithtim/Tower-Defense-Game)

### 系统/组件分离
来源：[maciej-trebacz/tower-of-time-game](https://github.com/maciej-trebacz/tower-of-time-game), [maciej-trebacz/tower-of-time-game](https://github.com/maciej-trebacz/tower-of-time-game), [Daivuk/tddod](https://github.com/Daivuk/tddod)

### ECS 架构
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics), [scellecs/morpeh](https://github.com/scellecs/morpeh)

### 性能优化
来源：[needle-mirror/com.unity.entities.graphics](https://github.com/needle-mirror/com.unity.entities.graphics), [scellecs/morpeh](https://github.com/scellecs/morpeh), [annulusgames/MagicTween](https://github.com/annulusgames/MagicTween)

### 弹道系统
来源：[ape1121/Godot-4-Tower-Defense-Template](https://github.com/ape1121/Godot-4-Tower-Defense-Template), [quiver-dev/tower-defense-tutorial](https://github.com/quiver-dev/tower-defense-tutorial), [quiver-dev/tower-defense-godot4](https://github.com/quiver-dev/tower-defense-godot4)

### 事件系统
来源：[Alex-Rachel/TEngine](https://github.com/Alex-Rachel/TEngine), [imnazake/Unify](https://github.com/imnazake/Unify)

### 波次系统
来源：[quiver-dev/tower-defense-tutorial](https://github.com/quiver-dev/tower-defense-tutorial)

## 通用工程模式

### ScriptableObject
> 数据资产化，配置与代码分离
来源：[sajad0131/Unity-Gameplay-Ability-System](https://github.com/sajad0131/Unity-Gameplay-Ability-System), [killop/anything_about_game](https://github.com/killop/anything_about_game), [No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity)

### 状态机模式
> 状态转换清晰，可视化
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [killop/anything_about_game](https://github.com/killop/anything_about_game), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter)

### 对象池模式
> 复用对象，减少 Instantiate/Destroy
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity), [ATHellboy/SampleProject-FightingGame](https://github.com/ATHellboy/SampleProject-FightingGame)

### 事件总线
> 解耦系统通信，Publish/Subscribe
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game), [PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity)

### 结构体优先
> 小型固定数据用 struct，避免 GC
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game), [scellecs/morpeh](https://github.com/scellecs/morpeh)

### SerializeField
> Inspector 调试，保留封装
来源：[No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity), [sjai013/unity-gameplay-ability-system](https://github.com/sjai013/unity-gameplay-ability-system)

### 缓存友好
> 数据连续布局，缓存命中优先
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

### GC 优化
> 对象池、数组复用、避免每帧 new
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

## 实践洞察

- "While other frameworks typically limit user freedom to avoid exposing flaws in the archetype-based concept, Svelto." — [sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS) (2026-05-31)
- "That's a best practice (Based on working experience), that's not only for Attributes Sets, but for any C++ class that might get reference by another object." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-06-01)
- "The reason is simple: a game should avoid rubber-banding death." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-06-01)
- "It allows you to avoid using third-party services such as Playful, PAN, or Smartfox server." — [killop/anything_about_game](https://github.com/killop/anything_about_game) (2026-06-03)
- "This is a very powerful concept and I don't recommend to use it until you really understand what are you doing." — [PixeyeHQ/actors.unity](https://github.com/PixeyeHQ/actors.unity) (2026-06-03)
- "Recommend to have experienced programming skill." — [PhysaliaStudio/Flexi](https://github.com/PhysaliaStudio/Flexi) (2026-06-04)
- "- Increases a character's ability to avoid incoming attacks." — [Rangerz132/gas-unity](https://github.com/Rangerz132/gas-unity) (2026-06-05)
- "> We recommend that in places where you are in doubt about using this attribute, you check everything for null yourself." — [scellecs/morpeh](https://github.com/scellecs/morpeh) (2026-06-05)
- "* `MORPEH_NON_SERIALIZED` Define to avoid serialization of Morpeh core parts." — [scellecs/morpeh](https://github.com/scellecs/morpeh) (2026-06-05)
- "It's recommended to use extension methods when available." — [annulusgames/MagicTween](https://github.com/annulusgames/MagicTween) (2026-06-05)
- "In most cases, the impact on performance is minimal, but it's recommended to avoid using callbacks when creating a large number of tweens." — [annulusgames/MagicTween](https://github.com/annulusgames/MagicTween) (2026-06-05)
- "It is not recommended to use Myra in production just yet." — [archangel4031/Myra](https://github.com/archangel4031/Myra) (2026-06-10)

# 塔防游戏 ECS + GAS 知识库
> 自动生成 · 2026-06-03 01:27

已分析 32 个仓库

## 塔防专项模式

### 技能系统
GAS 风格 Ability + Modifier 分离
来源：[felipeggrod/gasify](https://github.com/felipeggrod/gasify), [intrxx/Obsidian](https://github.com/intrxx/Obsidian)

### 实体管理器
ECS 风格：实体创建/销毁/查询
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics)

### Unity 桥接
纯 ECS 逻辑与 GameObject 渲染层桥接方案
来源：[Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 伤害计算
攻击/防御/暴击/属性缩放公式
来源：[intrxx/Obsidian](https://github.com/intrxx/Obsidian), [Pantong51/GASContent](https://github.com/Pantong51/GASContent)

### 性能优化
Burst 编译、NativeArray、JobSystem
来源：[keijiro/Voxelman](https://github.com/keijiro/Voxelman), [reeseschultz/ReeseUnityDemos](https://github.com/reeseschultz/ReeseUnityDemos)

### Unity DOTS Archetype
DOTS 模式：chunk data layout + entity query
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 状态机 AI
敌怪状态机：移动/攻击/死亡
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 行为树
行为树节点：Sequence/Selector/Condition/Action
来源：[Pantong51/GASContent](https://github.com/Pantong51/GASContent), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 攻击间隔
攻速属性、独立冷却管理
来源：[felipeggrod/gasify](https://github.com/felipeggrod/gasify)

### 塔升级系统
塔等级/星级/进阶，属性成长曲线配置化
来源：[rparrett/taipo](https://github.com/rparrett/taipo)

### 敌怪属性
敌怪血量/攻击/速度随波次成长
来源：[intrxx/Obsidian](https://github.com/intrxx/Obsidian)

### 系统更新
SystemBase 按组排序更新，数据逻辑分离
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 空间分区
GridSpatialHash O(1) 邻域查询，避免全量遍历
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 寻路系统
A*/BFS/网格寻路，敌人沿路径移动
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 路径点系统
预定义路径点序列，支持分支路径
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 渲染系统
ECS 数据到 Unity 渲染的同步方案
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 资源加载
Addressables 动态加载塔/敌怪/技能资源
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

## 项目架构线索

### 配置数据
来源：[DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura), [DruidMech/GameplayAbilitySystem_Aura](https://github.com/DruidMech/GameplayAbilitySystem_Aura)

### ECS 架构
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [Gornhoth/Unity-Smoothed-Particle-Hydrodynamics](https://github.com/Gornhoth/Unity-Smoothed-Particle-Hydrodynamics)

### 性能优化
来源：[needle-mirror/com.unity.entities.graphics](https://github.com/needle-mirror/com.unity.entities.graphics)

## 通用工程模式

### ScriptableObject
数据资产化，配置与代码分离
来源：[sajad0131/Unity-Gameplay-Ability-System](https://github.com/sajad0131/Unity-Gameplay-Ability-System), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 状态机模式
状态转换清晰，可视化
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 缓存友好
数据连续布局，缓存命中优先
来源：[sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS)

### 事件总线
解耦系统通信，Publish/Subscribe
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 结构体优先
小型固定数据用 struct，避免 GC
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### GC 优化
对象池、数组复用、避免每帧 new
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### SerializeField
Inspector 调试，保留封装
来源：[No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity)

## 实践洞察

- "While other frameworks typically limit user freedom to avoid exposing flaws in the archetype-based concept, Svelto." — [sebas77/Svelto.ECS](https://github.com/sebas77/Svelto.ECS) (2026-05-31)
- "That's a best practice (Based on working experience), that's not only for Attributes Sets, but for any C++ class that might get reference by another object." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-06-01)
- "The reason is simple: a game should avoid rubber-banding death." — [Narxim/Narxim-GAS-Example](https://github.com/Narxim/Narxim-GAS-Example) (2026-06-01)
- "It allows you to avoid using third-party services such as Playful, PAN, or Smartfox server." — [killop/anything_about_game](https://github.com/killop/anything_about_game) (2026-06-03)

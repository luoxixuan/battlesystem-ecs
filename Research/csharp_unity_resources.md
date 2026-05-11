# Unity ECS / DOTS / GAS 知识库

> 最后更新：2026-05-12 01:30

## 编程原则

### 依赖注入
通过外部注入依赖，而非内部创建，提高可测试性
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [DCFApixels/DragonECS](https://github.com/DCFApixels/DragonECS), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### Entity-Component 思想
数据和行为绑定到实体，组件可复用
来源：[DCFApixels/DragonECS](https://github.com/DCFApixels/DragonECS), [friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [Hoodrij/Yogurt](https://github.com/Hoodrij/Yogurt)

### 游戏循环更新模式
按帧或固定时间步更新游戏逻辑
来源：[DCFApixels/DragonECS](https://github.com/DCFApixels/DragonECS), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 最小惊异原则
行为应符合预期，不产生意外副作用
来源：[starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference)

### DRY 原则
避免重复代码，抽象公共逻辑复用
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 数据导向设计
按数据布局组织内存，提高缓存命中
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 函数式编程思想
优先使用不可变数据，减少副作用
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 低耦合原则
模块间依赖最小化，便于维护和测试
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

## 设计模式

### 命令模式
将请求封装为对象，支持撤销、重做、队列操作
来源：[marinasundstrom/raven](https://github.com/marinasundstrom/raven), [BrighterCommand/Brighter](https://github.com/BrighterCommand/Brighter), [ivanpaulovich/clean-architecture-manga](https://github.com/ivanpaulovich/clean-architecture-manga)

### 工厂模式
封装对象创建逻辑，隐藏具体实现类
来源：[marinasundstrom/raven](https://github.com/marinasundstrom/raven), [wbenny/scfw](https://github.com/wbenny/scfw), [BrighterCommand/Brighter](https://github.com/BrighterCommand/Brighter)

### 单例模式
全局唯一实例，常用于管理器类（慎用，易造成耦合）
来源：[tarydon/Nori](https://github.com/tarydon/Nori), [breakersol/ActivityFramework](https://github.com/breakersol/ActivityFramework), [enif-lee/DesignPattern.NET](https://github.com/enif-lee/DesignPattern.NET)

### ECS 架构
Entity-Component-System，数据与逻辑分离的组件模式
来源：[isadorasophia/bang](https://github.com/isadorasophia/bang), [EvotecIT/HtmlTinkerX](https://github.com/EvotecIT/HtmlTinkerX), [GopherSecurity/gopher-mcp](https://github.com/GopherSecurity/gopher-mcp)

### 策略模式
定义一系列算法，可相互替换
来源：[BrighterCommand/Brighter](https://github.com/BrighterCommand/Brighter), [OPCFoundation/UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard), [enif-lee/DesignPattern.NET](https://github.com/enif-lee/DesignPattern.NET)

### 观察者模式
一对多依赖，当对象状态变化时自动通知所有观察者
来源：[enif-lee/DesignPattern.NET](https://github.com/enif-lee/DesignPattern.NET), [hudmarc/FFO-FishNet-Floating-Origin](https://github.com/hudmarc/FFO-FishNet-Floating-Origin), [iandinwoodie/cpp-design-patterns-for-humans](https://github.com/iandinwoodie/cpp-design-patterns-for-humans)

### 对象池模式
预先创建对象并复用，避免频繁的创建销毁开销
来源：[Unity-Technologies/com.unity.multiplayer.samples.coop](https://github.com/Unity-Technologies/com.unity.multiplayer.samples.coop), [ATHellboy/SampleProject-FightingGame](https://github.com/ATHellboy/SampleProject-FightingGame), [youngwolf-project/ascs](https://github.com/youngwolf-project/ascs)

### MVC 模式
模型-视图-控制器分离，数据、界面、控制逻辑解耦
来源：[SamuelAsherRivello/rmc-mini-mvcs](https://github.com/SamuelAsherRivello/rmc-mini-mvcs), [iohao/ionet](https://github.com/iohao/ionet), [PacktPublishing/Hands-On-Design-Patterns-with-C-and-.NET-Core](https://github.com/PacktPublishing/Hands-On-Design-Patterns-with-C-and-.NET-Core)

### MVVM 模式
模型-视图-视图模型，数据绑定实现界面与逻辑分离
来源：[PacktPublishing/Hands-On-Design-Patterns-with-C-and-.NET-Core](https://github.com/PacktPublishing/Hands-On-Design-Patterns-with-C-and-.NET-Core), [uhub/awesome-cpp](https://github.com/uhub/awesome-cpp), [Cholopol/Cholopol-Tetris-Inventory-System](https://github.com/Cholopol/Cholopol-Tetris-Inventory-System)

### 原型模式
通过克隆现有对象创建新实例
来源：[MaiKuraki/UnityGameplayAbilitySystemSample](https://github.com/MaiKuraki/UnityGameplayAbilitySystemSample), [laicasaane/tower_of_encosy](https://github.com/laicasaane/tower_of_encosy)

### 中介者模式
集中管理对象间通信，避免网状依赖
来源：[DCFApixels/DragonECS](https://github.com/DCFApixels/DragonECS)

### 建造者模式
分步构建复杂对象，链式调用更可读
来源：[DCFApixels/DragonECS](https://github.com/DCFApixels/DragonECS), [starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference), [delmarle/RPG-Core](https://github.com/delmarle/RPG-Core)

### 装饰器模式
动态为对象添加职责，不改变原有类
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 门面模式
为复杂子系统提供统一简化接口
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

## 实用技巧

### 使用 [SerializeField]
私有字段在 Inspector 显示，保持封装
来源：[CompleteUnityDeveloper2/5_Realm_Rush](https://github.com/CompleteUnityDeveloper2/5_Realm_Rush), [starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference), [No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity)

### Unity DOTS 技术栈
DOTS：Entity、JobSystem、Burst 编译器三位一体
来源：[Unity-Technologies/megacity-metro](https://github.com/Unity-Technologies/megacity-metro), [PhilSA/Trove](https://github.com/PhilSA/Trove), [Dreaming381/lsss-wip](https://github.com/Dreaming381/lsss-wip)

### Start 初始化
第一次 Update 前调用，适合做业务初始化
来源：[Unity-Technologies/megacity-metro](https://github.com/Unity-Technologies/megacity-metro), [Leopotam/ecslite](https://github.com/Leopotam/ecslite), [annulusgames/LitMotion](https://github.com/annulusgames/LitMotion)

### FixedUpdate 固定更新
按固定时间步执行物理计算，与帧率解耦
来源：[Leopotam/ecslite](https://github.com/Leopotam/ecslite), [tbg10101/DOTS-Hybrid-Simulation-Worlds](https://github.com/tbg10101/DOTS-Hybrid-Simulation-Worlds), [annulusgames/LitMotion](https://github.com/annulusgames/LitMotion)

### 使用 async/await
异步编程，避免阻塞主线程
来源：[annulusgames/LitMotion](https://github.com/annulusgames/LitMotion), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [TastSong/CrazyCar](https://github.com/TastSong/CrazyCar)

### await 异步等待
配合 Task/UniTask 实现非阻塞等待
来源：[annulusgames/LitMotion](https://github.com/annulusgames/LitMotion), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [TastSong/CrazyCar](https://github.com/TastSong/CrazyCar)

### UniTask（C# 异步库）
比 Task 更适合 Unity 的异步方案，支持取消和进度报告
来源：[annulusgames/LitMotion](https://github.com/annulusgames/LitMotion), [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [TastSong/CrazyCar](https://github.com/TastSong/CrazyCar)

### LateUpdate 延迟更新
所有 Update 后执行，适合相机跟随等操作
来源：[harumas/UGizmo](https://github.com/harumas/UGizmo), [scellecs/morpeh](https://github.com/scellecs/morpeh)

### 使用缓存和对象池
减少内存分配，提高性能
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity), [DCFApixels/DragonECS](https://github.com/DCFApixels/DragonECS)

### 使用 ?. 空值检查
安全访问可能为空的成员
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [Fire-Aalt/KrasCore-Mosaic](https://github.com/Fire-Aalt/KrasCore-Mosaic), [DCFApixels/DragonECS](https://github.com/DCFApixels/DragonECS)

### ScriptableObject 数据容器
可作为数据资产或事件载体，减少 MonoBehaviour 耦合
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [No78Vino/gameplay-ability-system-for-unity](https://github.com/No78Vino/gameplay-ability-system-for-unity), [Fire-Aalt/KrasCore-Mosaic](https://github.com/Fire-Aalt/KrasCore-Mosaic)

### Addressables 资源系统
动态资源加载，支持热更新和依赖管理
来源：[MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter), [needle-mirror/com.unity.addressables](https://github.com/needle-mirror/com.unity.addressables), [CyberAgentGameEntertainment/SmartAddresser](https://github.com/CyberAgentGameEntertainment/SmartAddresser)

### 碰撞与触发器
OnCollision 用于物理碰撞，OnTrigger 用于触发区域
来源：[starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference), [Felid-Force-Studios/StaticEcs-Unity](https://github.com/Felid-Force-Studios/StaticEcs-Unity)

### Unity Job System / Burst
多线程计算，充分利用多核 CPU
来源：[svermeulen/trecs](https://github.com/svermeulen/trecs), [killop/anything_about_game](https://github.com/killop/anything_about_game)

### 避免 GC 分配
频繁分配触发 GC，影响帧率，用对象池和结构体优化
来源：[friflo/Friflo.Engine.ECS](https://github.com/friflo/Friflo.Engine.ECS), [killop/anything_about_game](https://github.com/killop/anything_about_game), [scellecs/morpeh](https://github.com/scellecs/morpeh)

### Awake/OnEnable 初始化
组件绑定后立即调用，适合做依赖获取
来源：[Felid-Force-Studios/StaticEcs-Unity](https://github.com/Felid-Force-Studios/StaticEcs-Unity), [scellecs/morpeh](https://github.com/scellecs/morpeh)

### 结构体 vs 类选择
小型固定数据用结构体，避免装箱开销
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game), [scellecs/morpeh](https://github.com/scellecs/morpeh)

### 线程安全
多线程访问共享数据时需要同步机制
来源：[killop/anything_about_game](https://github.com/killop/anything_about_game)

### 避免装箱拆箱
值类型与引用类型转换有性能开销
来源：[nilpunch/massive-ecs](https://github.com/nilpunch/massive-ecs)

## 洞察笔记

- "> If you find this project helpful, please consider giving it a star ⭐." — [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter) (2026-05-11)
- "Import only what you need, remove what you don't." — [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter) (2026-05-11)
- "> **📚 Important**: Each module has detailed documentation in its directory." — [MaiKuraki/UnityStarter](https://github.com/MaiKuraki/UnityStarter) (2026-05-11)
- ", don't hesitate to contact me in Github issues, and I will consider adding the prototype to the codebase when time permits." — [MaiKuraki/UnityGameplayAbilitySystemSample](https://github.com/MaiKuraki/UnityGameplayAbilitySystemSample) (2026-05-11)
- "Recommend to have experienced programming skill." — [PhysaliaStudio/Flexi](https://github.com/PhysaliaStudio/Flexi) (2026-05-11)
- "If you don't want to specify a version, you can also update the version by editing the hash of this library in the package-lock." — [CyberAgentGameEntertainment/SmartAddresser](https://github.com/CyberAgentGameEntertainment/SmartAddresser) (2026-05-11)
- "Use this tool to adjust the layout rules and eliminate warnings and errors." — [CyberAgentGameEntertainment/SmartAddresser](https://github.com/CyberAgentGameEntertainment/SmartAddresser) (2026-05-11)
- "Multiple scenes found with the given address in the map (`AddressNotUniqueException`)." — [starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference) (2026-05-11)
- "You can avoid it by making sure the `State` property is not `Unsafe`." — [starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference) (2026-05-11)
- "You don't have to supply both fields at once." — [starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference) (2026-05-11)
- "It is recommended to leave this at `Warning`." — [starikcetin/Eflatun.SceneReference](https://github.com/starikcetin/Eflatun.SceneReference) (2026-05-11)
- "It allows you to avoid using third-party services such as Playful, PAN, or Smartfox server." — [killop/anything_about_game](https://github.com/killop/anything_about_game) (2026-05-11)
- "Consider this list a work in progress as well as the project." — [nilpunch/massive-ecs](https://github.com/nilpunch/massive-ecs) (2026-05-11)
- "// Pass arguments to avoid boxing." — [nilpunch/massive-ecs](https://github.com/nilpunch/massive-ecs) (2026-05-11)
- "// You don't have to cache anything." — [nilpunch/massive-ecs](https://github.com/nilpunch/massive-ecs) (2026-05-11)
- "> We recommend that in places where you are in doubt about using this attribute, you check everything for null yourself." — [scellecs/morpeh](https://github.com/scellecs/morpeh) (2026-05-11)
- "Consider them as a "feature" to group the systems by their common purpose." — [scellecs/morpeh](https://github.com/scellecs/morpeh) (2026-05-11)
- "* `MORPEH_NON_SERIALIZED` Define to avoid serialization of Morpeh core parts." — [scellecs/morpeh](https://github.com/scellecs/morpeh) (2026-05-11)
- "> Don't care about attributes." — [scellecs/morpeh](https://github.com/scellecs/morpeh) (2026-05-11)
- "> It is important to understand that this disables any checks for null, so in the release build any calls to a null object will lead to a hard crash." — [scellecs/morpeh](https://github.com/scellecs/morpeh) (2026-05-11)

---

## 代码模板

### Unity 单例管理器
```csharp
public class Singleton<T> : MonoBehaviour where T : MonoBehaviour
{
    private static T _instance;
    private static readonly object _lock = new object();
    
    public static T Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance = FindObjectOfType<T>();
                    if (_instance == null)
                    {
                        var go = new GameObject($"[Singleton] {typeof(T).Name}");
                        _instance = go.AddComponent<T>();
                        DontDestroyOnLoad(go);
                    }
                }
            }
            return _instance;
        }
    }
    
    protected virtual void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }
}
```

### Unity 对象池
```csharp
public class ObjectPool
{
    private readonly Queue<GameObject> _pool = new();
    private readonly GameObject _prefab;
    private readonly Transform _parent;
    
    public ObjectPool(GameObject prefab, int prewarm = 10, Transform parent = null)
    {
        _prefab = prefab;
        _parent = parent;
        
        for (int i = 0; i < prewarm; i++)
        {
            var obj = Object.Instantiate(prefab, parent);
            obj.SetActive(false);
            _pool.Enqueue(obj);
        }
    }
    
    public GameObject Get()
    {
        if (_pool.Count > 0)
            return _pool.Dequeue();
        return Object.Instantiate(_prefab, _parent);
    }
    
    public void Return(GameObject obj)
    {
        obj.SetActive(false);
        _pool.Enqueue(obj);
    }
}
```

### C# 异步操作
```csharp
public async UniTask<T> LoadAsync<T>(string path) where T : UnityEngine.Object
{
    var op = Resources.LoadAsync<T>(path);
    await op;
    return op.asset as T;
}
```
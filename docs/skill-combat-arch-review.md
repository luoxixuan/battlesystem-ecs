# 技能 & 战斗框架架构审查

> 审查日期:2026-08-29
> 审查范围:技能(GAS/SkillSystem 及其分支)与战斗(伤害结算、系统编排、SOA 数据模型、配置层、可扩展性)
> 方法:6 个维度并行深挖 + 逐条 file:line 证据核验(grep/读码复核),每条头部结论均已独立验证
> 结论定性:CONFIRMED = 已读码/grep 核实;下文所有 A–H 主结论均为 CONFIRMED

---

## 一、总体评价

**真正扎实的部分**:并行安全模型(两阶段"并行收集 → 串行 apply",damage queue 存 raw value 累加而非 last-write-wins)与测试可隔离性(`TestWorld` 直接构造 `ComponentStore` 驱动单系统,1317 测试),理念文档("先对再快""职责收口")也落到了实处。

**贯穿性根因**:技能与战斗框架缺少一个**声明式 / 可注册的抽象层**。

- GAS 看起来像 Unreal 的 Gameplay Ability System,实际是空壳(属性从不实例化、修饰符 op 从不读取);
- 技能派发退化成它本要消灭的巨型 switch;
- 伤害结算没有单一管线,散落在约 30 个系统里各写各的;
- 系统编排、阶段归属、启用状态、跨系统引用全部是命令式表达(调用顺序、`= null`、`Set*`、注释),框架不做任何一致性校验。

结果:**每加一个内容(技能 / 塔 / 效果)要改 6–9 处代码,而框架不校验一致性,漏改即静默 bug**。本次审查已确认至少 6 个正在"发货"的战斗正确性缺陷,全部是该根因的直接产物。

---

## 二、已验证的核心问题(按 影响 × 阻碍加内容程度 排序)

### 🔴 A. 伤害结算没有单一管线 —— 框架级根因

**问题**:没有 `Damage` 结构体、没有 `IDamageSource`,伤害就是到处传的裸元组。全库 **30 处裸 `EnemyHealth[x] -=` 写点,分布在 19 个文件**;号称"规范入口"的 `store.ApplyEnemyDamage` 只有 5 处调用,**约 84% 的伤害绕过它**。而两个"规范"入口本身互不重叠:

- `ApplyEnemyDamage`(`Core/ComponentStore_Enemy.cs:2098`):处理路径地形倍率 + 血量下限 + 护盾(含元素护盾规则),**但不含护甲 / 元素抗性 / 暴击 / 死亡入队**;
- `TowerAttackSystem` 内联串行段(`Systems/TowerAttackSystem.cs:1928`):处理护甲 / 抗性 / 暴击 / 减伤 / 饱和 / 附魔 / 下限 / 死亡入队,**但完全不碰 `EnemyShield`**。

**已核验的后果**——按"是真 bug / 是有意取舍 / 是当前不可达的隐患"分级。这个分级很重要:**碎片化本身是确定的结构问题,但它导致的后果并非都是正在发货的缺陷**。

#### A-1 真 bug(应立即修)

| 缺陷 | 证据 | 后果 |
|---|---|---|
| **沙暴 DoT 击杀不入队**(同类,且**当前唯一可达**) | `WeatherSystem.cs:111` 裸写 `EnemyHealth[eid] -= dmg` 后无 `QueueEnemyDeath`(全文件 0 次)。系统确实在跑:`SystemRegistry.cs:424` 构造、`:791` 接入 PreGame;`ApplyWeatherDot` 由 `Update` 无条件调用(`:82`);`weather.json` 的 `Sandstorm` 带 `enemyDotPct: 0.005`,`TransitionWeather` 以 70% 概率在 5 种天气中随机选中 | 被沙暴 tick 打到 HP≤0 的敌人不给金币、不计击杀,且**继续行走**(`EnemyMovementSystem` 只按 `EnemyActive` 门控)、期间无法被再次击杀(各系统 `HP<=0 continue` 跳过它),走到底由 `GameManager.cs:461` **白扣一条基地命**后才入队。**Meteor 的 `GlobalSkills` 生产端 0 处写入(loader 无解析、数据无键),所以这条才是该 bug 类当前唯一真正发货的实例** |
| **`ElementalReactionSystem` 从未构造 → 元素状态永不衰减 + `_pendingShieldBreaks` 无界增长**(新发现,可达) | `new ElementalReactionSystem(` 全库 **0 次**(连测试都没有);`SystemRegistry` 无字段、11 个 `*Group.cs` 无属性。但它是**唯一**的元素计时器衰减者(`:244-265`)与**唯一**的 `PendingShieldBreaks` 消费者(`:231`),而生产端确实在写入两者:`ApplyEnemyDamage:2184-2189`(破盾)——`monster_shield.json`/`monster_enforcer.json` 带非零 `Shield`,且 `ApplyEnemyDamage` 有 5 处生产调用 | ① 敌人一旦被破盾附上元素状态,`EnemyElementStatus`/`EnemyElementTimer` **永不清除**(无其他衰减者);② `_pendingShieldBreaks` 每次破盾 `Add` 一个 int 而永不 `Clear`,随会话时长**无界增长**;③ `EnemyExposureMask`/`Timer` 只由该系统写入 → `TowerAttackSystem:1838` 与 `PlayerTowerAttackSystem:596` 读的 **+30% 元素易伤永不触发**(整个 Exposure 特性是死的);④ 冻结/眩晕 effect 施加路径(`:198,213`)同样是死的 |
| **Meteor 击杀不入队**(同类,结构性代表,当前**不可达**) | `GlobalSkillSystem.cs:184-192` 把 HP 夹到 0、`killed++`、打日志,但**全文件 `QueueEnemyDeath` 0 次**;而 `ResolveEnemiesKilledThisFrame` 只处理入队项,**无全局 `HP<=0` 兜底 sweeper**(唯一兜底 `MovementGroup.cs:182` 只覆盖它自己那条 DoT) | 陨石"击杀"的敌人 HP=0 但仍 `EnemyActive`:不给金币、不计分、不释放实体槽,且被后续所有 `EnemyHealth<=0 continue` 的系统当死人跳过 → 僵尸**并非永久占位** —— `EnemyMovementSystem` 只按 `EnemyActive` 门控,HP=0 的敌人继续走到基地,由 `GameManager.cs:461` 扣命后入队。故真实后果是**不给金币 / 不计击杀 / 白扣一条命 / 期间免疫再次击杀**。此项为该 bug 类的结构性代表,但**当前配置下不可达**(见上一行) |
| 塔伤害元素类型丢失 | 塔 damage queue 是 `(enemyId,damage,playerId,towerId)` 无 `DamageType`,上报硬编码字面量 `"Physical"`(`TowerAttackSystem.cs:1933`),尽管 `:1019` 处已知类型;对比 `PlayerTowerAttackSystem` 同位置传 `damageType.ToString()` | on-hit / 分析 / UI 消费方对**所有塔伤害**拿到错误元素归因 |
| DeathMark 层数增伤是死代码 | `GetDamageMultiplier` 生产端 **0 调用**(只在自身定义 + 测试文件) | 一整个"标记叠层增伤"机制静默失效,测试却因直接调 getter 而绿 |

#### A-2 已声明的设计取舍(不是"忘了",但表达方式该改)

| 项 | 证据 | 正确的批评角度 |
|---|---|---|
| 技能层跳过护甲 | `SkillSystem.cs:313-317` **明确注释**:护甲减免由 `PlayerTowerAttack`/`TowerAttack` 负责,技能层刻意不做以避免 per-enemy 串行开销;且技能**确实** honor `EnemyDamageResistance`(`:1191`) | 不是 bug。真正的问题是**取舍散落各处、无统一表达** —— 该由 `DamageFlags.IgnoresArmor` 显式声明,而非靠注释约定 |
| 荆棘不吃护盾/抗性 | `ThornsAuraSystem.cs:167-171` 写明"不吃护盾/抗性,future iteration 可加类型字段" | 同上 |
| 塔攻击不碰护盾 | `EnemyShield` 在 `TowerAttackSystem.cs` 中 **0 次引用**;文件内两处 `ApplyEnemyDamage` 字样是注释,明说 "bypasses"(`:1712`、`:1936`) | 机制确定存在(护盾对主力伤害源无效、元素破盾反应对塔击杀不触发)。**未核实**其是否为有意取舍——文件内无声明,故列为"待定性",修前应先确认设计意图 |

#### A-3 当前不可达的隐患(未来接线时会咬人)

| 项 | 证据 | 定性 |
|---|---|---|
| 血量下限被 DoT / 投射物 / 荆棘绕过 | **5** 条来源裸写不调 floor:`ProjectileSystem.cs:406`、`BuffSystem.cs:151`、`BleedSystem.cs:173`、`FrostbiteSystem.cs:170`、`ThornsAuraSystem.cs:174`。**天气不在其中**——`WeatherSystem.cs:109` 确实调了 `ClampDamageToHealthFloor`(原稿误列,已撤)。**但 `EnemyMinHealthFloor` 在生产代码中从未被赋非零值**(除声明/注释/读取外无任何写点,JSON 中 0 个键),仅测试里 3 处 | "Round 132 Boss 不可被秒"不变量**目前没有任何敌人启用** → 是隐患,不是正在发货的缺陷。一旦接线,这 5 条路径会立刻破坏它 |

> **已撤回的结论**:原稿称"玩家普攻不乘 `EnemyDamageResistance`,科技树全局减伤对普攻静默失效"。核验后**未能证实** —— `PlayerTowerAttackSystem.cs:473` 确实读取 `EnemyDamageResistance`(linked 分支),主路径抗性在更上游计算。此条作废。

**改进**:引入 `readonly struct DamageInstance { int target; float amount; DamageType type; ElementType element; int sourcePlayer; int sourceTower; DamageFlags flags; }` 和单一 `ComponentStore.ResolveDamage(in DamageInstance)`,按固定顺序跑完整链(免疫 → 护甲 / 抗性 / 元素 → 暴击 → 护盾 → 下限 → 应用 → 死亡入队)。把约 30 个 drain loop 改成"入队 `DamageInstance` + 串行段统一调用 resolver"。对确需真伤的少数来源用 `DamageFlags.IgnoresArmor` 显式 opt-out,而非靠遗漏。

**预期收益**(按上面的分级重述,不夸大):

1. **把散落的取舍变成显式声明** —— 这是主要收益。A-2 的三项决定目前写在三个文件的注释里,统一后由 `DamageFlags` 在调用点表达,可读、可测、可 review。
2. **修掉 A-1 的三个真 bug**,并让 A-3 的下限隐患在接线时自动被覆盖(resolver 内含下限,无需 6 处手工补 `ApplyMinHealthFloorInPlace`)。
3. **死亡入队收口** —— 沙暴 / Meteor 那类"改了 HP 却忘了入队"由 resolver 统一负责,从根上消除该类 bug(A-1 前两项的结构性解法)。
4. 元素 / 类型信息随 `DamageInstance` 全程携带,上报归因自动正确。

这仍是**最高杠杆的单项改动**,但杠杆来自"消除未来的漏改",而非"当下修掉 5 个 bug"。

---

### 🔴 B. GAS 是空壳抽象(属性 / 修饰符从未生效)

**问题**:GAS 层结构完整但功能空心。

- 全库 **0 个 `new GameplayAttribute`、0 个 `.ApplyModifier` / `.ResetToBase` 调用**(grep 核实);`GameplayAttribute` 只有 `Core/GAS/Attributes.cs:7-17` 的定义。
- `AttributeModifierOp`(Add/Multiply/Override)只在**构造 effect def** 时被写入(`SkillSystem.cs:208,215`、`BuffSystem.cs:193`、`ElementalReactionSystem.cs:192,207`),从没被 switch / 比较 / 读取过。
- 真正的 "+10% 攻击" 来自硬编码位标志 `GetPlayerAttackModifier`(`ComponentStore_Player.cs:747` 返回 `1.1f`),GAS 的对应 effect 是"死写"(`SkillSystem.cs:207-211`)。
- `AppliedEffect` / `GameplayEffectDef` **确实**被 tick(`BuffSystem.cs:100-113`),但 tick 直接用 `Magnitude * StackCount` 裸减血,`AttributeIndex`(传了 `ENEMY_HEALTH`)**从不解引用** —— effect struct 实际只是个 DoT 计时器。
- `AttributeSetDefinitions` 用 `const int` 且玩家 / 敌人索引值重叠(都从 0 起,`Attributes.cs:26-39`);敌人属性集从未接存储;`ENEMY_DAMAGE` / `ENEMY_GOLD_REWARD` 是纯死常量。
- 本该做"形状 × 效果"正交化的 `SkillModifierDef` / `Modifiers`(`GameConfig.cs:1413-1432`)在 `Systems/` 里 **0 消费者**,配置注释自己写着"运行时消费待重构接入"。

**代价**:约 160 行 struct + 存储(`ComponentStore_World.cs:361,838-866`)需要维护,却只提供 DoT 计时;新人会合理假设属性 / 修饰符能用,在沙上建塔。

**改进**:二选一。(a) 让它真跑起来 —— 玩家 / 敌人伤害走属性数组按常量索引,`BuffSystem` 按 `ModifierOp` 应用;(b) 删掉 `GameplayAttribute` / `AttributeModifierOp` / 未用常量,把 effect struct 改名 `PeriodicDamageDef` 以反映其真实职责。

**预期收益**:消除一个误导性抽象;无论哪条路,效果路径都变诚实。

---

### 🔴 C. 技能派发退化成 3 处并行 switch + 20 个 Cast 方法

**问题**:`Systems/SkillSystem.cs:324-399` 是 22 case + default 的 `switch(def.AreaShape)`,派发到 **20 个 `private Cast*` 方法**(部分 case 共用);`AreaShapeType` 有 22 个 `const int`(`GameplayAbility.cs:11-34`);`FromString`(`:37-65`)是**第三个** 22 case 的字符串→int switch —— 同一份形状枚举手工维护三处。

- 许多 case 根本不是"形状":`TimeWarp` / `Summon` / `TimeRewind` / `MassResurrect` / `HealingZone` 是无区域的效果。"AreaShape" 已沦为技能的**类型判别符**,而非几何。
- `GameplayAbilityDef` 是 **29 字段的 god-struct**(`GameplayAbility.cs:72-160`),每个技能不论类型都携带全部 29 字段;字段还按 shape 复用不同含义:`ShieldAmount` = 护盾值 / 时间缩放;`HealPercent` = 治疗% / 回溯秒数 / 治疗区 HPS;`Cooldown` = 冷却 / 治疗区持续。构造函数 22 个位置参数,另有 6 个字段构造后再赋值(`SkillSystem.cs:193-201`)。
- 加一个新机制要改 **6–9 处**:枚举常量 + `FromString` case + `GameplayAbilityDef` 字段 + `InitializePlayerSkills` 赋值 + `SkillConfig` 字段 + `ExecuteAbility` case + `Cast*` 方法。漏掉 `FromString` case → 静默 fallback 到 `Single`(`:63` 的 `_ => Single`)。

**改进**:用 `ISkillShape.CollectHits` 策略 + `IEffect.Apply` 效果列表,技能 = 形状 + 效果(纯数据)。`CollectCircleHits`(`SkillSystem.cs:541`)已证明 9 个 shape 共用一套查询,只是串行段应用的效果不同。配套:拆散 god-struct 为 per-effect payload(`DotParams` / `ShieldParams` / `CcParams`),废弃位置参数构造函数。

**预期收益**:3 处并行 switch + 20 个 `Cast*` 方法收敛到少数策略 / 效果类;多数新技能变成零代码(纯配置);消除字段复用带来的类型不安全。

---

### 🔴 D. 已确认的初始化 / 生命周期 bug

1. **空注入 —— 实为两处(已修)** ✅
   原 `Core/SystemRegistry.cs:399` 才 `new ReflectTowerSystem(...)`,但它在 `:371`(`EnemyAISystem`)和 `:396`(`SuicideBombSystem`)就已被当参数传入,存进 **`readonly` 字段**且无补注 setter → 永久为 null。这是 god-object 手工排序直接制造的 bug。
   **核查补充**:原稿只记了 `ReflectTower`,但 `TowerStealth`(原 `:402`)有**完全相同的问题** —— 同样排在 `SuicideBomb:396` 之后。两者构造函数都只要 `(store, playerId)`,无自身顺序约束,已一并前移到 `:376`。
   **影响面修正**:`EnemyAISystem` 里那个字段除构造赋值外**零使用**(死参数);真正的消费者是 `SuicideBombSystem:199,219`(reflect)与 `:104,192`(stealth)。但该系统整条链被 `CollectExplosionEvents:78` 的 `EnemyIsSuicide` 门死,而该数组生产端零写入 → 修复本身正确且必要(防止将来接线时带病),但当前**并非"已发货机制失效"**。
   **回归测试**:`SystemRegistryTests` 走完整 `CreateAll → WireDependencies` 后反射断言两个私有字段非 null(生产未暴露只读访问器)。撤销前移 → 2 个测试变红。

2. **DoT 跨实体泄漏(回收 ID 继承旧 DoT)—— 已修** ✅
   `ActiveEffectCount[]` / `AbilityCount[]` 在 `DestroyEntity` 中**从不清零**(grep 核实:`ComponentStore.cs` 内 0 次),`AddEnemy` / `CreateEntity` 也不重置。而实体 ID 走 free-list 回收(`ComponentStore.cs:551-573`),`BuffSystem.cs:95` 按 `GetEffectCount(enemyId)` tick 敌人 DoT。**注意证据路径已更正**:原稿引 `ElementalReactionSystem.cs:198,213` 作为"敌人会被加 effect"的依据,但该系统**从未被构造**(见 A-1),那条路是死的。真正可达的是 `BuffSystem.ApplyDot` → `AddEffect`(`:216,261`),生产入口为 `TowerAttackSystem:2094` 的 Firewall 分支(`Data/Towers/tower_061/086/111.json` 确有 `"Type":"Firewall"`)。→ 回收 ID 的新敌人继承前一个占用者的冻结 / 眩晕 / DoT(连 `SourceEntityId` 都指向旧攻击者)。
   **修复仅需 2 行**:`DestroyEntity` 共享段加 `AbilityCount[id]=0; ActiveEffectCount[id]=0;`(count 归零即可,槽内容从不越过 count 读取)。其中真正会触发的是 `ActiveEffectCount` 那半;`AbilityCount` 那半是防御性的 —— `AddAbility` 生产端唯一调用者传 `playerId`(`SkillSystem.cs:202`),而玩家 id 从不进 `freeEntityIds`(`nextEntityId` 从 2 起,`AddPlayer` 不走 `CreateEntity`)。**已验证安全**:全部 `DestroyEntity` 调用点只传敌人 / 陷阱 / 回声克隆 / 塔,不会误清玩家技能栏。
   **回归测试**:`GasSlotRecycleTests` 3 个用例 —— 销毁归零、回收 id 不继承 effect、以及驱动真实 `BuffSystem.Update` + `ResolveDotDamage` 断言回收敌人 HP 未被上一任的 DoT 扣减(第三个用例是关键:前两个是 getter 形状,只有它证明机制真的活着)。撤销 2 行 → 3 个全红。

3. **附带死代码(已决定:保留不删)**:`EnemyIsSuicide` 全库**只读不写**(`SuicideBombSystem.cs:78` 读,写点为 0)→ 数组恒 false,整个 `SuicideBombSystem` 不可达。
   核查补充:`EnemySuicideTriggerRange` / `DmgRadius` / `DmgAmount` 同样零写入,`Data/` 无对应键。该系统在 registry 中**确实被构造并接入 `Combat` group 每帧调用**(`:408`、`:872`),但 `CollectExplosionEvents` 的第一个 `if` 就把所有敌人跳过 → 每帧一次空的 `Parallel.For` 开销。删除本会是安全的(它是 D-1 两个依赖的唯一消费者),但**已决定保留** —— 接线缺口只有 spawn 期写入 `EnemyIsSuicide` + 三个 `EnemySuicide*` 参数,系统内部逻辑(爆炸收集 / 塔伤害 / 反伤 / 潜行判定)均已完整。保留的代价是每帧一次空 `Parallel.For`。

**实际结果**:D-1(两处前移)、D-2(2 行归零)均已落地,合计约 12 行生产代码 + 5 个回归测试。D-1 的价值是消除一类"构造顺序制造 null"的隐患而非恢复可见机制(见上文影响面修正);D-2 消除了一个真实可达的跨实体状态泄漏。D-3 已决定保留(不删),接线缺口仅剩 spawn 期写入。

---

### 🟠 E. 配置层:"配置驱动"名不副实 + 数据源与消费者字段集从未对齐

架构文档称"配置驱动,代码只做逻辑"。对**参数微调**成立,对**新机制 / 新行为不成立**。

- **规模**:`GameConfig.cs` 4273 行(**85 个 class/struct + 8 enum**,是个 god-DTO);`GameConfigLoader.cs` 3046 行(**5 个 `Extract*` 助手定义、约 200 处调用**、32 个 try/catch、46 个 `??` 默认兜底)。
  > 修正:原稿写"45 个手写 parse 助手 / 35 处",数字不实;`GameConfig` 的类型数原稿偏高约 20%。"手写解析广泛使用"这一方向成立,但应以调用点数(~200)而非助手数来衡量。
- **E-1 玩家技能栏退化成单体 / 半径 0 —— 但性质是"字段集从未对齐",不是"loader 漏读"**:
  `ParseSkillConfig`(`GameConfigLoader.cs:1428-1455`)不读 `AreaShape` / `AreaRadius` / DoT / Heal / Shield,而 `SkillSystem.cs:178-184` 恰好消费这些字段 → `FromString(null)` = `Single`、半径传 0,`CastSingleTarget` 以 `radiusSq=0` 收集,只命中同格敌人。
  **关键修正**:`game_config.json` 中 `AreaShape` / `AreaRadius` **各出现 0 次** —— 该数据源本来就只有 `AreaWidth/Height`。所以这不是"loader 漏读了配置里有的字段",而是**这一路数据源与消费者的字段集从未对齐**。
  因此修法不同:不该"给旧手写解析器补读一个不存在的键",而应二选一 —— (a) 把玩家技能栏并入 `SkillDefs` 单一数据源(推荐,同时解决 E-3);(b) 由 `AreaWidth/Height` 推导 shape 与半径(同时解决 E-2)。
- **E-2 `AreaWidth/AreaHeight` 是死字段**:`Systems/` 中无任何代码读 `sc.AreaWidth/Height`;`SetSkillAreaWidth/Height`(`ComponentStore_World.cs:757,769`)零外部调用者。设计师调的主要旋钮(网格尺寸)对技能路径无效果。
- **E-3 双解析器 / 三数据源**:同一个 `SkillConfig` 被两套字段覆盖不同、JSON 引擎不同的解析器填充 —— 玩家栏走手写子串解析(`ParseSkillConfig`),curated / static 走 `System.Text.Json` 的 `ParseSkillDefElement`(`:1808`,字段完整)。同名技能可在两个世界里字段不一致,"谁赢"取决于消费系统而非单一规则。
- **E-4 手写子串 JSON 解析脆弱且依赖 locale**:`ExtractString`(`:374-386`)抓 key 之后第一个字符串,嵌套 / 重复 key 会静默取错值;`ExtractFloat` 只 1 处 `InvariantCulture`,逗号小数点 locale 上 `float.TryParse` 失败 → `catch { return default }` 静默归零。27 处已用 `JsonDocument` 与 35 处手写解析并存。
- **E-5 无 schema 校验**:loader 内 `schema|validate` grep = 0;错配的 key / 类型 / 小数点产出"可玩但错"的构建,无任何信号。

**改进**:用**一个** `System.Text.Json` 类型化反序列化器(records + `[JsonPropertyName]`)替换两个手写技能解析器和约 200 处 `Extract*` 调用;加一遍加载后校验(必填字段、数值范围、枚举字符串对 `FromString` 成员校验),dev 构建下 fail-fast。**前置决策**:先定"玩家技能栏是否并入 `SkillDefs` 单一数据源",E-1/E-2/E-3 的修法都取决于这个选择。

**预期收益**:E-3 漂移与 E-4 locale bug 消失;`GameConfigLoader` 缩短数百行;静默配置 bug 变成构建期错误。E-1 的收益取决于上述决策 —— 并入单一数据源可让玩家技能栏获得 DoT/Heal/Shield 等完整字段(而非仅"恢复 AoE")。

---

### 🟠 F. SOA 数据模型:内存与清理成本随"功能总数"膨胀

**规模(实测)**:5 个 partial 共 **1008 个数组字段声明**,其中 **711 个是 `new T[MAX_ENTITIES]`**(每个 10 万元素)。单个 `ComponentStore` 估算在**数百 MB 量级**,而配置波次每波仅 30 敌人。

> 这是**刻意的 SOA 性能取舍**(连续内存、缓存友好),不应全盘否定。问题在于**过度分配的方向错了**——把只有 10 个玩家 / 数十座塔 / 个位数 Boss 用到的字段,也按 10 万实体铺开。

- **F-1(约 4 行改动)`AbilityInstances` 严重超配**:`ComponentStore_World.cs:361` 按 `MAX_ENTITIES × 5` = 50 万槽,但 12 处调用**全部用 `playerId`**(0–9),实际可达 50 槽 —— 利用率 0.01%。`AbilityInstance` 内嵌 `GameplayAbilityDef`(29 字段含 4 个引用)→ 约 200 万个 GC 引用槽,为一张 10 元素逻辑表永久占用。改 `MAX_PLAYERS * MAX_ABILITIES_PER_ENTITY` 即可。
  > **不要引用精确 MB 数**:原稿的 "−68.7 MB" 是按字段布局推算的,按 x64 实际布局粗估在 **90–110 MB 量级**。结论(超配 4 个数量级、4 行可修)不依赖这个数字,但数字本身请以实测 `sizeof` 为准。
- **F-2 `DestroyEntity` 一次敌人死亡触发约 227 次写,横跨约 227 个各自 400KB 跨步的数组**(`ComponentStore.cs:575-1131`;塔分支约 109;另有两个 `BOSS_PHASE_MAX` 循环对每个敌人无条件多写 28 次)。成本随**代码库功能总数**增长而非实体实际用量;一个 wave-1 小怪付全套 boss/sapper/phaser 拆解。
- **F-3 无 archetype / chunk / sparse-set / 组件位掩码**(grep 全 0);存在性靠临时的 per-feature bool 数组(每个又是 10 万数组)编码。项目自评 `docs/dots-migration-evaluation.md:33` 亦承认"无 archetype 拆分收益"。
- **F-4 清理路径不完整 —— 但塔侧的真实风险面远小于原稿所称**:
  - `Dispose`(`:1256-1478`)只 null 掉 486 个数组,过半从不 null(而 `Dispose(bool)` 本身只翻 `_disposed` 标志,这 222 行手写清单收益很低);
  - 敌人侧:337 个 `Enemy*` 数组中,**41 个既不在 `DestroyEntity` 清理也不在 `AddEnemy` 初始化**。但"未清"≠"会泄漏" —— 逐个核验后**真实风险面只有 2 个**:`EnemyElementStatus` / `EnemyElementTimer`(唯一衰减者 `ElementalReactionSystem` 从未构造,见 A-1;写入者 `ApplyEnemyDamage:2184-2187` 破盾路径可达)。其余 39 个被排除的理由:20 个任何生产系统都不写;`EnemyDisarmDurationLeft` 的唯一 setter `SetEnemyDisarm` **零生产调用者**;clone / wound / affix 系列的写入者未被构造;morph / fission 系列在 `WaveSpawningSystem:532-537` 无条件初始化;`EnemyIsFeared` 只被置 false;`EnemyMoveDirX/Y` 每帧自纠;`EnemySabotageTimer` 被零写入的 `EnemyCanSabotage` 门控;`EnemyTetherSlowFactor` 声明处即 `Enumerable.Repeat(1f, …)` 且被零写入的 `EnemyTetherMaxLength` 门控;
  - 塔侧:`RemoveTower` 清 208 个字段、`DestroyEntity` 塔分支清 90 个,差集 150 个 —— **但交叉核验 `AddTower`(238 字段)后,150 个差集中有 140 个在 `AddTower` 被初始化**,即无脏继承风险。**真实风险面只有 10 个**:`TowerIsChronoTower`、`TowerIsMobile`、`TowerMoveSpeed`、`TowerPatrolDirection/PathId/WaypointIndex/AttackSpeedPenalty`、`TowerSelected`、`TowerTimeFieldRadius`、`TowerTimeScale`。
  > **已修正的结论**:原稿称"生产走较不完整的路径"并建议 `DestroyEntity` 委托 `RemoveTower`。这个判断把 150 当成了风险面(实为 10),且该修法会为 10 个字段的问题增加 120+ 次无谓写入。正确做法:**只补这 10 个字段的清理**。

**改进**:复用 `_World.cs` 已有的**容量封顶池**惯用法(`MAX_HAZARD_ZONES=500`、`MAX_TERRAIN_ZONES=200` 等)。优先级:F-1(约 4 行)→ boss-phase 表改 `MAX_BOSSES≈64` → 各 niche 敌人 / 塔机制移入封顶侧表 + 槽索引数组。清理侧:补敌人 **2** 项 + 塔 10 项(见阶段 0 第 5/6 项的收窄依据);用构造期反射对 `T[]` 字段生成统一清理循环(含非零默认值表),取代三个 170–350 赋值的手写方法。

**预期收益**:内存可回收百 MB 量级(不动战斗语义);`DestroyEntity` 常见小怪拆解写次数可降约一半,且**每次死亡成本不再随未来功能增长**;按构造消除整类 ID-reuse bug,而非靠注释纪律(`DestroyEntity` 里约 110 条"避免 ID 复用泄漏"注释正是它反复咬人的证据)。F 系列应视为**扩展性保险**——现有 mode5 基准表明死亡当前非帧主导成本,不要期待即时 FPS 收益。

---

### 🟠 G. 系统编排:执行顺序全靠手工排列,正确性写在注释里

**规模**:`Systems/` 有 141 个 `*System.cs`;`SystemRegistry` 构造 105 个;11 个 `*Group.cs` 共 140 个可空 `public Systems.XxxSystem? Foo { get; set; }` 属性。`CombatGroup` 单个持有 40 个属性、`Execute` 里 43 个有序调用。`Round N` 注释 589 处 / 83 个轮次。

- **G-1(最严重)零编排强制**:全库 `topolog|DependsOn|UpdateBefore|UpdateAfter` grep = 0。执行顺序 = group 里 `?.Update()` 的字面文本顺序。让顺序正确的**约 90 条不变量只写在英文注释里**(12 处 "runs last"、19 处 "next frame"、33 处 "must run before/after"、24 处 "same frame / freshly-cached"),无任何强制。任意能编译、能过浅测试的重排都可能静默破坏同帧数据依赖(如 `TowerSynergy.ResolveBuffShares` 必须先于 `TowerAttack.Update`;`Mana` 必须先于 `ManaShield`)。
- **G-2 "runs last" 措辞不精确(非事实错误)**:`CombatGroup` 有 **7 处**注释各自声称"runs last"(`:65,75,89,167,182,187,194`),而尾部实为 **8 个**串行 `?.Update`。核验后澄清:这 7 个系统**确实**按注释意图排在尾部区域,所以并非"自相矛盾",而是 **7 个系统共享同一段尾部区域**、各自用了独占式措辞。问题仍在:当注释是唯一的顺序规范(G-1)时,"runs last" 这种措辞无法表达"谁在谁之后",维护者据此无法判断在尾部插入新系统是否安全。
  > 修正:原稿称"自相矛盾 / 从未与现实对账",定性过重。
- **G-3 `SystemRegistry` 是 god-object**:996 行,三个巨型线性方法(`CreateAll` ≈350 行、`WireDependencies` ≈140 行、`AssignToGroups` ≈230 行),三种耦合机制并存无一致性 —— 构造函数位置参数(如 `EnemyAISystem` 8 参,含 D-1 的 null 隐患)、`Set*/Inject*` setter(40 处)、事件订阅(24 处)。加一个功能要改 4 处(属性 + 三个方法),无编译保证同步。文档自承"从 GameManager 抽出以消灭 ~300 行 spaghetti init"—— spaghetti 是被搬走而非消除,现已长到 996 行。
- **G-4 21 个 group 槽被接线成 `= null`(为未落地功能预留的槽位)**:`AssignToGroups` 显式把 21 个槽赋 null(如 `Combat.Heat/TowerOvercharge/Demolish/Dispel/TowerSilence/EnemyProjectile`),但 `CombatGroup.Execute` 仍每帧 `Heat?.Update()`。核验:这些系统在 `SystemRegistry` 中 **`new` 次数全为 0** —— 从未被构造,而非"接线漏了"。
  > 两处修正:(1) 原稿称"读代码者以为 dispel 生效,实际静默关闭了一个能用的系统"——定性不准,更准确的说法是**为尚未落地的功能预留了槽位**;(2) 我核验时也发现,这 11 个系统的**实现文件全部存在**(`TowerDispelSystem.cs`、`FogOfWarSystem.cs`、`EnemyWoundSystem.cs` 等),所以"连实现文件都不存在"同样不成立 —— 它们是"有实现、从未构造"。
  坏味道依然成立:该删属性 / 加 `FeatureFlags`,并让"有实现但未启用"在启动日志里可见,否则 21 个死槽 + 每帧 21 次无效 null-check 会持续误导读者。
- **G-5 阶段门控是二元硬编码**:`FrameScheduler.Tick`(`:98-104`)靠单个 `Phase==BuildPhase` 分支切换整条管线;`PostDeath.Phase` 每帧手工再同步(第二个真相源);`WaveBranch.IsBranchActive` 在 `Execute` 中途 early-return —— 阶段逻辑散落在 scheduler + group 字段 + group 内早退三处。新增阶段无法不改 scheduler 与每个 group。
- **G-6 `FrameScheduler` 泄漏它声称只"编排"的战斗机制**:`ISystemGroup` 说"scheduler 只编排 group",但它内联实现了 `DecrementInvulnFramesLeft`、`TickPhaserCycle`、`TickBlinkerCycle`(≈83 行,含一次性 `SetPathfindingSystem` 注入)、`EmitPositionEvents` 四个 per-enemy 机制,绕过 group / registry 模型。

**改进**:引入**声明式系统模型** —— 每个系统声明 `Reads/Writes` 组件标签或 `RunsAfter(typeof(X))`,由 builder 在启动时拓扑排序并校验(遇环 / 违规 fail-fast)。更廉价的第一步:加一个"顺序快照"断言测试,强制重排必须过一次有意识 diff。删除 "runs last" 措辞与 21 个死槽(或用 `FeatureFlags` 显式门控 + 启动日志)。把 G-6 四个机制抽成真正的系统。

**预期收益**:把约 90 个静默腐化风险变成编译期 / 启动期失败;`AssignToGroups` 的 4 处 shotgun edit 收敛;消除 D-1 那一类"构造顺序"bug;支持 N 阶段而非 2 条硬编码路径。

---

### 🟠 H. 可扩展性:无注册缝隙,加内容 = 跨 6–9 个 god-file 手术

**问题**:技能 / 塔 / 效果**没有任何插件 / 自注册点**,`AGENTS.md` 把"加系统"记录为**手工 4 步编辑**而非注册调用。

- 塔行为是硬编码 switch(`TowerAttackSystem.cs:1304-1367` 的 `switch(towerType)`,`:2087` 另一个),`TowerType` 是 13 值封闭 enum;新塔的战斗行为必须改进这个 2923 行文件里的 switch。
- **加一个新塔类型的接触点(已核验)**:`TowerType.cs` 枚举 + `ComponentStore_Tower.cs` SOA 字段 + `DestroyEntity/BeginFrame` 清理 + `TowerAttackSystem.cs` 新 case + 新 `XxxSystem.cs` + `SystemRegistry.cs` 4 处 + 相关 `*Group.cs` 属性与 `Update` + `TowerPlacementSystem`/上限 + `Data/Towers/*.json` —— **7–9 处**。加一个技能形状类似(见 C)。
- **H-1 事件模型不承重**:`EventBus` 只有 **7 种事件类型**(`EventBus.cs:79`),17 处 publish/subscribe;而 `Systems/` 里 `Set*/Inject*` **定义** 126 处 / 79 文件(注:这是 setter 定义数,非调用点数),比例仍显著偏向硬引用。`TowerAttackSystem` 单个暴露 19 个 setter。新效果想响应"造成伤害 / 塔放置"无法订阅,只能被授予引用并在 `SystemRegistry` 手工接线,于是 `WireDependencies` 持续膨胀;击杀 / 死亡通知只能另走 `ComponentStore` 的 C# 事件(`OnEnemyKilled`)。`IBattleEventBus` 纯 logic→render。
- **H-2 无 engine / content 边界**:144 个系统平铺在一个 `Systems/`,全编进 Core 库,**零系统实现共享 `ISystem`**(grep = 0)。group 持有具体可空引用,scheduler 无法泛型迭代,每个系统一行手写 `X?.Update(dt)`。派发逻辑与内容实现在单文件里融合(`SkillSystem` 1334 行、`TowerAttackSystem` 2923 行)。
- **H-3 技能逻辑碎在 5 个系统,含 2 个纯 stub**:4 个手写冷却 ticker(CDR 数学各不同:`SkillSystem` `cdr*adr`、`GlobalSkill` `1+min(cdr,0.6)`、Hero/Tower 裸 `deltaTime`);2 条发散的敌人伤害路径(`SkillSystem` 走 resistance+invuln,`GlobalSkill.ExecuteMeteorStrike` 内联自己的 `armor/(armor+50)` 且无抗性 / 无敌检查、**且不入队死亡** —— 即 A-1 的 Meteor 僵尸 bug);`HeroSkillSystem.TriggerHeroSkill`、`TowerActiveSkillSystem.TriggerTowerActive` **只翻冷却 + `Console.WriteLine`,无任何效果派发**,却各自带冷却数组和手写 JSON 解析。唯一的好公民是 `AutoSkillSystem` —— 它委托 `SkillSystem.CastSkill` 而非重实现。
  > 注:`private Cast*` 方法实测 **20 个**(`switch` 22 case 中部分共用),原稿"23 个"偏高。

**改进**:定义 `ISystem { Update(store,dt,turn) }`,group 持 `List<ISystem>`,注册变 `group.Add(system)`;把两个大 switch 换成字典派发,技能 / 塔各在一个文件里自注册。把 `TowerPlaced/DamageDealt/EnemyKilled/HitLanded` 升为一等事件,让 sustain/aura/on-kill 效果(Bloodlust/Culling/Thorns/SoulHarvest,现全是注入 + 接线)改为订阅。`Systems/` 拆 `Engine/`(调度 / 伤害 / 空间 / 寻路)与 `Content/`(塔 / 技能 / boss / buff),content 依赖 engine 单向。抽一份 `CooldownTable` / ready-scan / `ApplyAoeDamage`(honor 抗性 + 无敌)供所有技能源共用;Hero/Tower active 把配置的 `skillId` 路由进 `SkillSystem` 管线而非 stub。

**预期收益**:加技能 / 塔从约 7–9 文件降到约 2(行为类 + JSON);`WireDependencies` 随触发类内容不再膨胀;修掉 Meteor 减伤发散;退掉 2 个 stub 系统的重复管线;engine/content 边界让"内容非法伸进引擎内部"变得显眼。

---

## 三、改进路线图

> 原则(沿用项目理念):正确性修复无门槛、先做;不以性能名义改业务;每步保持全套测试通过(当前 1317)。

### 阶段 0 —— 真 bug 修复(低风险,后果明确)

只保留"真 bug、后果确定"的项。原稿此处混入了取舍项与未证实项,已剔除。

> **状态:6/7 项已落地并验证**(2026-08-29)。每项都做了"撤销修复 → 测试变红 → 恢复 → 全绿"的
> 回退验证,确认测试真能捕获对应 bug 而非假绿。测试数 1298 → **1317**(新增 19),
> 两个 build 均 0 warning / 0 error,`git diff --check` 干净,测试规则 0 违规。
> 未做:第 7 项(删死系统)—— 属删代码而非修 bug,留给你决定。

| 优先 | 项 | 状态 | 改动 | 风险 |
|---|---|---|---|---|
| 1 | **A-1 沙暴 DoT 不入队**(唯一可达) | ✅ **已修** | `WeatherSystem.cs:111` HP 归零时补 `QueueEnemyDeath(eid, playerId)`;注意它已调 `ClampDamageToHealthFloor`(`:109`),补入队时不要重复夹血 | 极低 |
| 2 | A-1 Meteor 不入队 | ✅ **已修** | `GlobalSkillSystem.cs:187-192` 同样补入队。**当前配置不可达**(`GlobalSkills` 生产端 0 写入),但修它成本等同于零,且可防止将来接线时带病上线 | 极低 |
| 3 | D-2 DoT 跨实体泄漏 | ✅ **已修** | `DestroyEntity` 加 2 行清 `AbilityCount/ActiveEffectCount` | 极低 |
| 4 | D-1 null 注入 —— 实为**两处** | ✅ **已修** | `ReflectTower` **与 `TowerStealth`** 一并前移到 `:376`(原 `:399`/`:402`,都排在消费者 `SuicideBomb:396` 之后)。核查发现 `TowerStealth` 有同样的问题,原稿只记了一处 | 低 |
| 5 | F-4 敌人漏清 —— 实测**只有 2 个**真会泄漏 | ✅ **已修** | 补 `EnemyElementStatus` / `EnemyElementTimer`(后者 4 槽/敌人)的 reset。**不要**照原稿补 44 个:实测 337 个 `Enemy*` 数组里 41 个在 `DestroyEntity`/`AddEnemy` 都没写,但其中 20 个**任何生产系统都不写**(不可能脏)、其余 19 个或由死系统写(`EnemyCloneSystem`/`EnemyWoundSystem`/`EnemyAffixSystem` 均未构造)、或在 spawn 期无条件初始化(`WaveSpawningSystem:532-537` 的 morph/fission 字段)、或只被置 false(`EnemyIsFeared`)、或每帧自纠(`EnemyMoveDirX/Y`)、或被零写入者的开关门控(`EnemySabotageTimer`←`EnemyCanSabotage`;`EnemyTetherSlowFactor`←`EnemyTetherMaxLength`,且它声明处已 `Repeat(1f)`) | 低 |
| 6 | F-4 塔 **10** 项漏清 | ✅ **已修** | 只补这 10 个(`TowerIsChronoTower`/`TowerIsMobile`/`TowerPatrol*`/`TowerTimeScale` 等)。**不要**委托 `RemoveTower` | 低 |
| + | **`_pendingShieldBreaks` 无界增长**(核查中新发现) | ✅ **已修** | `BeginFrame` 加 `_pendingShieldBreaks.Clear()`。该 List 由 `ApplyEnemyDamage:2189` 每次破元素盾追加,唯一消费者(含 `Clear`)在从未构造的 `ElementalReactionSystem` → 整个会话只增不减。shipped 数据可达(`monster_shield`/`monster_enforcer` 带 `Shield`+`ShieldElement`) | 极低 |
| 7 | D-3 死系统 | ⏸ **决定保留** | 不删 `SuicideBombSystem` / `EnemyIsSuicide`。代价是每帧一次空的 `Parallel.For`(`CollectExplosionEvents` 第一个 `if` 跳过全部敌人),换取将来接线自爆兵时机制现成。**若要接线**,缺口只有一处:`EnemyIsSuicide` + `EnemySuicideTriggerRange/DmgRadius/DmgAmount` 的 spawn 期写入(`WaveSpawningSystem` 按 monster 配置),系统本身的爆炸/反伤/潜行逻辑均已完整 | — |

**从阶段 0 移出的项**:

- **A-1 DeathMark 增伤接入** → 移到阶段 2。它需要决定"在管线哪一步乘"(护甲/抗性之后),孤立接入会再造一处散落取舍。
- **A-3 血量下限** → 不做。`EnemyMinHealthFloor` 当前无任何非零写点,修了也不可达;等接线 Boss 配置时由阶段 2 的 resolver 统一覆盖。
- **A-2 技能跳护甲 / 荆棘不吃护盾** → 不是 bug,不修;移到阶段 2 变成显式 `DamageFlags`。
- **A-1 元素类型上报** → 移到阶段 2。原列此处并标为"1 行替换",复核后**修法不成立**：`:1933` 拿不到 `:1019` 的 `dmgType`(那是并行收集段的局部变量),damage queue 元组只有 `(enemyId, damage, playerId, towerId)`(`:69`)。串行段只能用 `towerId` 反查 `store.TowerDamageType[towerId]`,而该反查在**伤害转换路径上是错的**：`conversionRatio > 0` 时(`:1021-1050`)一次攻击拆成原类型 + `TowerConvertedDamageType` 两份却合并成单个 `finalDmg` 入队,一个上报值无法表达两种类型。正解是给队列元组加 `DamageType` 字段(转换塔按主类型上报)或拆成两条队列项(准确但扩散到 on-hit/lifesteal 消费方)—— 二者都是 A 根因"damage 缺类型字段"的一部分,应随阶段 2 一起做。
- **E-1 玩家 AoE** → 移到阶段 1,因为它需要**先做数据源决策**(见下),不是打补丁。

**已解锁**:沙暴击杀正常结算(不再白扣基地命)、回收 id 不再继承 DoT / CC / 元素状态、破盾队列不再无界增长、反伤与潜行对自爆兵路径恢复非 null、塔回收槽位不再带残留时空/巡逻态。后续重构现在有一个可信基线。

### 阶段 1 —— 决策与快赢(低风险,机械)

- **前置决策(阻塞 E 系列)**:玩家技能栏是否并入 `SkillDefs` 单一数据源?
  - 并入 → E-1/E-2/E-3 一并解决,玩家技能获得完整字段集(DoT/Heal/Shield);
  - 不并入 → 需给 `game_config.json` 补 `AreaShape/AreaRadius` 键**或**由 `AreaWidth/Height` 推导。
  - **不要**给 `ParseSkillConfig` 补读一个数据源里不存在的键。
- F-1 `AbilityInstances` 改 `MAX_PLAYERS * MAX_ABILITIES_PER_ENTITY`(约 4 行,回收百 MB 量级);boss-phase 表改 `MAX_BOSSES≈64`。先用 `sizeof` 实测确认收益数字。
- 用构造期反射对 `T[]` 字段生成统一清理循环(含非零默认值表),取代手写 `DestroyEntity`/`Dispose`/`AddEnemy` —— 这才是消除 F-4 整类漏清的结构解法(阶段 0 的补清只是止血)。
- H-2 第一步:引入 `ISystem`,group 改 `List<ISystem>`(消除逐系统 `?.Update` 行,为阶段 3 铺路)。
- G-4 清理:删掉 21 个死槽属性,或加 `FeatureFlags` + 启动日志暴露"有实现但未启用"。

### 阶段 2 —— 伤害管线统一(中风险,最高杠杆)

- A:`DamageInstance` + `ResolveDamage` 单管线,约 30 个 drain loop 逐个迁移。**每迁一个补一条"真实攻击驱动"的集成测试**,防 DeathMark 那类 getter-only 假绿。
- 迁移时把 A-2 的三处取舍变成**显式** `DamageFlags.IgnoresArmor` 等声明(这是本阶段的主要收益);把 A-1 的 DeathMark 增伤接到护甲/抗性之后;让下限、护盾、死亡入队由 resolver 统一负责(顺带覆盖 A-3 隐患与 Meteor 类漏队)。
- **先确认设计意图**:塔攻击不碰 `EnemyShield` 是有意还是遗漏?这决定 resolver 里塔路径是否走护盾分支。
- H-3:抽 `ApplyAoeDamage` / `CooldownTable`,收敛 5 个技能源的重复管线(4 套 CDR 数学归一)。

### 阶段 3 —— 声明式系统模型 + 编排(中高风险,深改)

- G:每系统声明 `RunsAfter` / `Reads/Writes`,builder 启动时拓扑排序 + 校验;删 "runs last" 注释与 21 个死槽;阶段归属改为 group 声明 `ActivePhases`(消除 G-5 双真相源);G-6 四机制抽成真系统。
- G-3:把每系统的构造 + 接线收进自注册 `ISystemInstaller`,registry 只迭代 installer(4 处 shotgun edit → 1 处)。

### 阶段 4 —— 技能 / 效果正交化 + 配置收口(深改,须在阶段 2/3 后)

- C + B:`ISkillShape` + `IEffect` 效果列表落地(激活半死的 `Modifiers` 路径),3 处并行 switch + 20 个 `Cast*` 方法收敛;GAS 属性层要么接真存储要么删(不要留在"结构完整、功能空心"状态)。
- E:单一 `System.Text.Json` 类型化反序列化 + 加载校验,退掉两个手写技能解析器和约 200 处 `Extract*` 调用。
- H:`Systems/` 拆 `Engine/` 与 `Content/`,内容自注册。

---

## 四、一句话结论

**这套技能 / 战斗框架的引擎底座(并行安全的两阶段模型、可隔离的测试)是可靠的,真正的问题是内容层缺一个声明式抽象:GAS 空转、技能靠三处并行维护的巨型 switch、伤害无单一管线、系统顺序靠手工排列且约 90 条不变量只写在注释里——因此每加一点内容都要跨 6–9 个 god-file 手术,而框架不做任何一致性校验,漏改即静默失效。** 但要区分两件事:**碎片化是确定的结构债,它导致的后果却不全是正在发货的缺陷** —— 本次核验后确认的真 bug 是:**沙暴 DoT 击杀不入队**(该类唯一可达,已修)、**`ElementalReactionSystem` 从未构造**(元素状态永不衰减 + `_pendingShieldBreaks` 无界增长 + 整个 +30% 元素易伤是死的)、DoT 跨实体泄漏(已修)、Meteor 击杀不入队(同类但当前配置不可达,已一并修)、塔伤害元素上报错误、DeathMark 增伤未接入、ReflectTower 永久 null(实际影响面为零 —— 它唯一的消费者 `SuicideBombSystem` 整条链被零写入的 `EnemyIsSuicide` 门死),以及敌人 **2** / 塔 10 个字段漏清(均已修)。**阶段 0 的 7 项修复已全部落地并通过回退验证**(撤销任一项都能让对应测试变红),测试 1298 → **1317**;仍未动的是 `ElementalReactionSystem` 接线、塔伤害元素上报与 DeathMark 增伤接入 —— 这两项都该随阶段 2 的统一伤害管线一起做,孤立修会再造散落取舍。而"技能跳护甲""荆棘不吃护盾"是**已写在代码里的取舍**,"血量下限被绕过"在当前数据下**不可达**。所以路线是:阶段 0 先修那几个真 bug 止血,再以"统一伤害管线"为最高杠杆——**它的价值在于把散落各处的取舍变成显式的 `DamageFlags` 声明、把"改了 HP 却忘了入队"这类漏改从人工纪律转为框架保证**,而不是一次修掉五个 bug。

---

## 附:本次审查的自我修正记录

以下结论在复核后被**撤回或降级**,保留在此以免后续引用错误数据:

| 原结论 | 复核结果 |
|---|---|
| "玩家普攻不乘 `EnemyDamageResistance`,科技树全局减伤失效" | **撤回**。`PlayerTowerAttackSystem.cs:473` 确实读取,主路径抗性在更上游 |
| "血量下限被 DoT 绕过,Boss 不可被秒不变量被破坏" | **降级为不可达隐患**。`EnemyMinHealthFloor` 生产代码无任何非零写点,JSON 无键 |
| "技能忘了 honor 护甲""荆棘忘了吃护盾" | **降级为已声明取舍**。两处代码均有明确注释说明理由 |
| "E-1 loader 漏读配置字段" | **改写性质**。`game_config.json` 中 `AreaShape`/`AreaRadius` 各 0 次出现 —— 是数据源与消费者字段集从未对齐,修法不同 |
| "F-1 省 68.7 MB" | **数字作废**。按 x64 布局粗估应在 90–110 MB 量级;结论不依赖精确值,但不要引用该数字 |
| "F-4 生产走较不完整的塔清理路径,应委托 `RemoveTower`" | **结论修正**。150 个差集中 140 个在 `AddTower` 已初始化,真实风险面仅 **10 个**;委托 `RemoveTower` 会为 10 个字段增加 120+ 次无谓写入 |
| "G-2 7 处 runs last 自相矛盾" | **降级为措辞不精确**。7 个系统确实排在尾部区域,是共享同一段尾部而非矛盾 |
| "G-4 静默关闭了已实现的 dispel" | **改写性质**。这些系统 `new` 次数为 0(从未构造),是为未落地功能预留槽位;但实现文件**均存在**,"文件不存在"亦不成立 |
| "元素类型上报是 1 行替换,可进阶段 0" | **修法作废**。`:1933` 无法取到 `:1019` 的 `dmgType`(并行段局部变量),queue 元组无类型字段(`:69`);按 `towerId` 反查 `TowerDamageType` 在伤害转换路径(`:1021-1050`)上是错的 —— 一次攻击拆两种类型却合并成单个 `finalDmg`。已移入阶段 2 |
| "D-1 ReflectTower 永久 null → 反伤塔对敌人 AI 和自爆兵失效" | **影响面修正为零**。`EnemyAISystem` 里 `_reflectTowerSystem` 除构造赋值外**零使用**(死参数);`SuicideBombSystem` 确实用它(`:199,219`),但整条链被 `CollectExplosionEvents:78` 的 `EnemyIsSuicide` 门死,而该数组生产端零写入 → 仍应修(2 分钟),但不是"已发货机制失效" |
| "D-1 空注入只有 `ReflectTower` 一处" | **漏报一处**。`TowerStealth`(原 `:402`)有完全相同的问题 —— 同样排在消费者 `SuicideBomb:396` 之后被当参数传入。原稿只查了 `ReflectTower`,没有把"构造顺序晚于消费者"当成一类问题去扫 `CreateAll` 的其余部分。两者已一并前移 |
| "F-4 敌人 44 个字段纯脏继承,是真实 ID 复用风险" | **收窄到 2 个**。实测 337 个 `Enemy*` 数组中 41 个在两个生命周期方法里都没写,但逐个核验后只有 `EnemyElementStatus`/`EnemyElementTimer` 真会泄漏(其唯一衰减者从未构造)。其余 39 个被排除:20 个无任何生产写入者、写入系统未构造、spawn 期无条件初始化、只写 false、每帧自纠、或被零写入的开关门控 |
| 数字微调 | 数组字段 1018→**1008**;`new T[MAX_ENTITIES]` 714→**711**;`Dispose` null 487→**486**;`GameConfig` 类型 101+15→**85+8**;`Extract*` "45 个助手"→**5 个定义 / ~200 处调用**;`Cast*` 23→**20** |

**遗漏补报**:原稿把 Meteor 问题归到"减伤发散"里带过,漏掉了其中最严重的部分 —— `GlobalSkillSystem.cs` 全文件 `QueueEnemyDeath` **0 次**,且无全局 `HP<=0` 兜底 sweeper(唯一兜底 `MovementGroup.cs:182` 只覆盖自身 DoT),导致陨石"击杀"的敌人无法正常死亡。此项已提入 A-1。**二次核查又发现同类的 `WeatherSystem.cs:111`(沙暴 DoT)同样不入队,且它是该 bug 类当前唯一真正可达的实例**(Meteor 的 `GlobalSkills` 生产端 0 写入),已列为 A-1 首位 / 阶段 0 第 1 项。同时修正:原稿把 `WeatherSystem` 列入"绕过血量下限"的来源,实为误列——它调了 `ClampDamageToHealthFloor`。

**三次核查再补报(最重要的一条)**:`ElementalReactionSystem` **从未被构造**(全库 `new ElementalReactionSystem(` 0 次,`SystemRegistry` 无字段、11 个 group 无属性、连测试都没有),而它独占三项职责:元素计时器衰减、`PendingShieldBreaks` 消费、`EnemyExposureMask/Timer` 写入。后果:破盾附加的元素状态永不清除、`_pendingShieldBreaks` 无界增长、两处攻击系统读的 +30% 元素易伤永不触发。**原稿从头到尾把这个系统当活的引用**(A 表元素破盾反应、B 节 `ModifierOp`、D-2 的"敌人会被加 effect"证据),这是本次审查最大的单点事实错误 —— 一个 395 行、被 CHANGELOG 记录为两轮功能交付的系统,实际从未接线。

---

*方法说明:A–H 主结论均经 file:line 读码 / grep 独立核验。仍未实测的项已就地标注:`AbilityInstance`/`AppliedEffect` 的内存量由字段布局推算(非 `sizeof`,含 padding 不确定度 —— 但"利用率 0.01%、4 行可修"的结论不依赖它);F-2 的 per-death 缓存成本为分析推理而非 profile,现有 mode5 基准表明死亡当前非帧主导成本 —— **F 系列应视为扩展性保险,而非即时 FPS 收益**。文档中标注"未核实"的项(如塔攻击不碰护盾是否为有意取舍)应在动手前先确认设计意图。*

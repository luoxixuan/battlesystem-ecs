using System;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Infrastructure
{
    /// <summary>
    /// 每个测试一个独立世界：持有全新的 <see cref="ComponentStore"/> + <see cref="MockRenderer"/>
    /// + <see cref="GameConfig"/>，并提供配置驱动的实体工厂（Enemy / Tower / Player）。
    /// 关键设计：每个测试实例一个 World，避免跨测试共享 store 导致的状态污染。
    /// </summary>
    public sealed class TestWorld : IDisposable
    {
        public ComponentStore Store { get; }

        public MockRenderer Renderer { get; }

        public GameConfig Config { get; }

        private TowerPlacementSystem? _placement;

        /// <summary>懒加载的塔放置系统（复用 Store + Renderer）。非塔测试不触发 JSON 加载。</summary>
        public TowerPlacementSystem Placement => _placement ??= new TowerPlacementSystem(Store, Renderer);

        public TestWorld()
        {
            Store = new ComponentStore();
            Renderer = new MockRenderer();
            Config = new GameConfig();
        }

        /// <summary>
        /// 生成一个敌人。默认"普通敌人"（原点、满血 100、无抗性），
        /// 用 lambda 覆盖任意字段，例如：
        /// <c>var boss = w.Enemy(e =&gt; { e.Health = e.MaxHealth = 1000; e.Name = "Boss"; });</c>
        /// </summary>
        public int Enemy(Action<EnemySpec>? configure = null)
        {
            var s = new EnemySpec();
            configure?.Invoke(s);
            return Store.AddEnemy(
                s.X, s.Y, s.MoveSpeed,
                s.Health, s.MaxHealth ?? s.Health, s.Damage,
                s.GoldReward, s.WaveNumber, s.Name,
                s.Armor, s.Shield, s.MagicResist,
                s.FireResist, s.IceResist, s.LightningResist, s.HolyResist);
        }

        /// <summary>
        /// 放置一座塔（通过 TowerPlacementSystem.PlaceTower，走完整放置逻辑）。
        /// 默认 Basic 攻击塔；用 <c>type</c> 改类型、用 lambda 覆盖伤害/射程/攻速/造价。
        /// </summary>
        public int Tower(int x, int y, TowerType type = TowerType.Basic, Action<TowerSpec>? configure = null)
        {
            var s = new TowerSpec { X = x, Y = y, Type = type };
            configure?.Invoke(s);
            return Placement.PlaceTower(s.X, s.Y, s.Type, s.Damage, s.Range, s.Speed, s.Cost);
        }

        /// <summary>
        /// 绕过 <see cref="Placement"/> 的裸塔工厂，供需要精确字段控制的测试用：
        /// 直接 CreateEntity + 写 PositionX/Y/PositionActive + AddTower，不走塔位规则/金币扣费等放置逻辑。
        /// 注意：<c>ComponentStore.AddTower</c> 末尾已自动注册活跃塔列表，调用方无需再调 AddActiveTowerId。
        /// </summary>
        public int RawTower(int x, int y, TowerType type = TowerType.Basic, float damage = 50f, int range = 3,
            float speed = 1f, int level = 1, float cost = 50f)
        {
            int id = Store.CreateEntity();
            Store.PositionX[id] = x;
            Store.PositionY[id] = y;
            Store.PositionActive[id] = true;
            Store.AddTower(id, type, damage, range, speed, level, cost);
            return id;
        }

        /// <summary>
        /// 生成一个玩家（默认实体 id 0），并设置坐标、生命、金币。
        /// 用 lambda 覆盖任意字段。
        /// </summary>
        public int Player(Action<PlayerSpec>? configure = null)
        {
            var s = new PlayerSpec();
            configure?.Invoke(s);
            Store.AddPlayer(s.EntityId, s.AttackRange, s.AttackSpeed, s.AttackDamage, s.Level, s.BaseLives);
            Store.AddPosition(s.EntityId, s.X, s.Y);
            Store.PlayerMaxHealth[s.EntityId] = s.Health;
            Store.PlayerCurrentHealth[s.EntityId] = s.Health;
            if (s.Gold != 0f) Store.SetPlayerGold(s.EntityId, s.Gold);
            return s.EntityId;
        }

        /// <summary>给玩家发放金币。</summary>
        public void GrantGold(int playerId, float gold) => Store.SetPlayerGold(playerId, gold);

        /// <summary>
        /// 清空指定玩家的所有 per-type 塔数量上限（0 = unlimited）。默认上限由
        /// tower_placement.json 加载，单元测试不应依赖数据里的具体 cap 值；
        /// 需要测 cap 机制的测试再自行写入显式值。默认清空玩家 0。
        /// </summary>
        public static void DisablePerTypeTowerCaps(ComponentStore store, int playerId = 0)
        {
            for (int t = 0; t < ComponentStore.MAX_TOWER_TYPES; t++)
            {
                store.PlayerTowersOfTypeCap[playerId * ComponentStore.MAX_TOWER_TYPES + t] = 0;
            }
        }

        /// <summary>实例版本：清空当前 <see cref="Store"/> 指定玩家的全部 per-type 塔数量上限。</summary>
        public void DisablePerTypeTowerCapsInstance(int playerId = 0) => DisablePerTypeTowerCaps(Store, playerId);

        /// <summary>查找位于 (x,y) 的塔实体 id，找不到返回 -1。</summary>
        public int FindTowerAt(int x, int y)
        {
            foreach (int tid in Store.ActiveTowerIds)
            {
                if ((int)Store.PositionX[tid] == x && (int)Store.PositionY[tid] == y)
                    return tid;
            }
            return -1;
        }

        public void Dispose() => Store.Dispose();
    }
}

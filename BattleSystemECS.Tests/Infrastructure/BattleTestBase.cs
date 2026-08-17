using System;
using System.Reflection;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Infrastructure
{
    /// <summary>
    /// 系统行为测试的基类。xUnit 每个测试方法都会 new 一个派生类实例，
    /// 因此 <see cref="World"/>（及其内部 store）每个测试都是全新的，无跨测试状态污染。
    ///
    /// 派生类直接使用 <c>Store</c> / <c>Renderer</c> / <c>Config</c> / <c>Enemy(...)</c> /
    /// <c>Tower(...)</c> / <c>Player(...)</c>，替代散落各处的
    /// <c>new ComponentStore()</c> + <c>new MockRenderer()</c> + <c>new GameConfig()</c> + 魔法数字工厂。
    /// </summary>
    public abstract class BattleTestBase : IDisposable
    {
        protected TestWorld World { get; }

        protected ComponentStore Store => World.Store;
        protected MockRenderer Renderer => World.Renderer;
        protected GameConfig Config => World.Config;
        protected TowerPlacementSystem Placement => World.Placement;

        protected BattleTestBase() => World = new TestWorld();

        protected int Enemy(Action<EnemySpec>? configure = null) => World.Enemy(configure);

        protected int Tower(int x, int y, TowerType type = TowerType.Basic, Action<TowerSpec>? configure = null)
            => World.Tower(x, y, type, configure);

        /// <summary>绕过 Placement 的裸塔工厂：精确控制位置/伤害/射程/攻速/等级/造价。</summary>
        protected int RawTower(int x, int y, TowerType type = TowerType.Basic, float damage = 50f, int range = 3,
            float speed = 1f, int level = 1, float cost = 50f)
            => World.RawTower(x, y, type, damage, range, speed, level, cost);

        /// <summary>清空当前 Store 指定玩家的全部 per-type 塔数量上限（0 = unlimited）。</summary>
        protected void DisableTowerCaps(int playerId = 0) => World.DisablePerTypeTowerCapsInstance(playerId);

        /// <summary>重建空间网格（驱动塔攻击/空间查询前调用）。</summary>
        protected void RebuildGrid() => Store.RebuildSpatialGrid();

        /// <summary>
        /// 读取 <see cref="WaveSpawningSystem"/> 实际注入的每帧生成批量大小。
        /// 生产未提供公开只读属性，先用反射读取私有字段；后续补公开测缝后可移除。
        /// </summary>
        protected static int ReadConfiguredWaveSpawnBatchSize(WaveSpawningSystem sys)
        {
            FieldInfo field = typeof(WaveSpawningSystem)
                .GetField("spawnConfig", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("WaveSpawningSystem.spawnConfig 字段不存在");
            var config = (WaveSpawnConfig)field.GetValue(sys)!;
            return config.SpawnBatchSize;
        }

        protected int Player(Action<PlayerSpec>? configure = null) => World.Player(configure);

        protected void GrantGold(int playerId, float gold) => World.GrantGold(playerId, gold);

        protected int FindTowerAt(int x, int y) => World.FindTowerAt(x, y);

        public void Dispose() => World.Dispose();
    }
}

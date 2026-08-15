using System;
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

        protected int Player(Action<PlayerSpec>? configure = null) => World.Player(configure);

        protected void GrantGold(int playerId, float gold) => World.GrantGold(playerId, gold);

        protected int FindTowerAt(int x, int y) => World.FindTowerAt(x, y);

        public void Dispose() => World.Dispose();
    }
}

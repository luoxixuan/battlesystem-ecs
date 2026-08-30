using System;
using System.Collections.Generic;
using System.Linq;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>使用真实 FrameScheduler.Tick 入口的战斗 golden 场景。</summary>
    public sealed class CombatGoldenReplayTests : BattleTestBase
    {
        [Fact]
        public void WaveFrame_ReplaysOnFreshStores_WithExpectedKillRewardAndEvents()
        {
            ReplaySnapshot first = RunWaveFrameOnFreshWorld();
            ReplaySnapshot second = RunWaveFrameOnFreshWorld();
            Assert.Equal(first, second);
            Assert.Equal(0, first.ActiveEnemyCount);
            Assert.Empty(first.ActiveEnemyIds);
            Assert.Equal(1, first.Kills);
            Assert.Equal(7f, first.Gold);
            Assert.Equal(new[] { "damage", "killed", "destroyed" }, first.Events);
        }

        [Fact]
        public void SameFrameFourteenHits_EmitsFourteenDamageEventsForSameTarget()
        {
            var events = new RecordingEventBus();
            int playerId = Player(p => { p.Health = 1000f; });
            int enemyId = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 1000f; e.MaxHealth = 1000f; });
            for (int i = 0; i < 14; i++)
            {
                int towerId = RawTower(0, 0, TowerType.Basic, damage: 1f, range: 2, speed: 1f);
                Store.TowerCritChance[towerId] = 0f;
                Store.TowerDamageVariance[towerId] = 0f;
            }
            var scheduler = new FrameScheduler(Store, Config, events);
            var attack = new Systems.TowerAttackSystem(Store, Renderer, null, 10, new EventBus(), events);
            scheduler.Combat.TowerAttack = attack;
            scheduler.CombatSetup.TowerAttack = attack;
            RebuildGrid();
            scheduler.Tick(1f, 0);
            scheduler.Tick(0f, 1);
            Assert.Equal(14, events.DamageTargets.Count);
            Assert.All(events.DamageTargets, id => Assert.Equal(enemyId, id));
            Assert.Equal(14, events.Events.Count);
            Assert.All(events.Events, kind => Assert.Equal("damage", kind));
            Assert.Equal(986f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.True(Store.IsPlayerAlive(playerId));
        }

        [Fact]
        public void BuildPhase_GlobalSkillIsRejected_WithNoHpOrDeathWork()
        {
            var events = new RecordingEventBus();
            (SystemRegistry registry, FrameScheduler scheduler, int enemyId) = MakeRegistryScenario(events);
            scheduler.Phase = GameState.BuildPhase;
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[0] = true;
            scheduler.Tick(1f, 0);
            Assert.Equal(10f, Store.EnemyHealth[enemyId]);
            Assert.True(Store.EnemyActive[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.Empty(events.KillEvents);
            Assert.True(Renderer.HasLogContaining("PhaseNotAllowed"));
        }

        [Fact]
        public void BuildPhase_RegistrySkillAndAutoSkillAreRejectedWithoutDamageWork()
        {
            var events = new RecordingEventBus();
            Player(p => { p.Health = 1000f; p.X = 0f; p.Y = 0f; });
            Config.Levels.Clear();
            Config.Skills[0].AutoCast = true;
            int enemyId = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 100f; e.MaxHealth = 100f; });
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, new StateMachine(), events);
            registry.WireDependencies(Store, 0);
            registry.Skill!.InitializePlayerSkills();
            var scheduler = new FrameScheduler(Store, Config, events);
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            scheduler.Tick(1f, 0);
            scheduler.Tick(1f, 1);
            scheduler.Tick(1f, 2);

            // Skill 与 AutoSkill 在 BuildGroup 中真实接线，但 BuildPhase 只允许资源/准备操作。
            Assert.Equal(100f, Store.EnemyHealth[enemyId]);
            Assert.Equal(0, Store.TotalKills);
            Assert.NotNull(registry.Skill);
            Assert.NotNull(registry.AutoSkill);
            Assert.True(Renderer.HasLogContaining("PhaseNotAllowed"));
            Assert.Equal(2, Renderer.Logs.FindAll(x => x.Contains("PhaseNotAllowed", StringComparison.Ordinal)).Count);
        }

        [Fact]
        public void WavePhase_GlobalSkillCommitsMeteorDeathAndReward()
        {
            var events = new RecordingEventBus();
            (SystemRegistry registry, FrameScheduler scheduler, int enemyId) = MakeRegistryScenario(events);
            scheduler.Phase = GameState.WavePhase;
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[0] = true;
            scheduler.Tick(1f, 0);
            Assert.False(Store.EnemyActive[enemyId]);
            Assert.Equal(1, Store.TotalKills);
            Assert.Equal(7f, Store.GetPlayerGold(0));
            Assert.Equal(new[] { "killed", "destroyed" }, events.KillEvents);
        }

        [Fact]
        public void BuildPhase_EmergencyHealRemainsAllowed_AndChangesPlayerHealth()
        {
            Config.Levels.Clear();
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Emergency Heal", SkillType = (int)GlobalSkillType.EmergencyHeal, ManaCost = 0f, Cooldown = 0f, HealPct = 0.5f });
            Player(p => { p.Health = 100f; });
            Store.PlayerCurrentHealth[0] = 40f;
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, new StateMachine(), new RecordingEventBus());
            registry.WireDependencies(Store, 0);
            var scheduler = new FrameScheduler(Store, Config, new RecordingEventBus());
            registry.AssignToGroups(scheduler);
            scheduler.Phase = GameState.BuildPhase;
            registry.GlobalSkill!.SetTurn(0);
            Store.PlayerGlobalSkillPressed[0] = true;
            scheduler.Tick(1f, 0);
            Assert.Equal(60f, Store.PlayerCurrentHealth[0]);
            Assert.True(Renderer.HasLogContaining("Emergency Heal"));
        }

        [Fact]
        public void BulletTime_PoisonUsesCombatClock_AtFullTickRate()
        {
            Player(p => { p.Health = 1000f; });
            int enemyId = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; });
            var buff = new Systems.BuffSystem(Store, 0, Renderer);
            var poison = Core.GAS.GameplayEffectDef.Periodic("Poison", Core.GAS.AttributeSetDefinitions.ENEMY_HEALTH, 10f, 2f, 1f);
            buff.ApplyDot(enemyId, poison);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.SkillBuff.Buff = buff;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            scheduler.Tick(1f, 0);
            scheduler.Tick(1f, 1);
            Assert.Equal(80f, Store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void BulletTime_TowerAttackUsesCombatClock_AndHitsNormally()
        {
            Player(p => { p.Health = 1000f; });
            int enemyId = Enemy(e => { e.X = 0f; e.Y = 0f; e.Health = 300f; e.MaxHealth = 300f; });
            int towerId = RawTower(0, 0, TowerType.Basic, damage: 100f, range: 2, speed: 1f);
            Store.TowerCritChance[towerId] = 0f;
            Store.TowerDamageVariance[towerId] = 0f;
            var scheduler = new FrameScheduler(Store, Config);
            var attack = new Systems.TowerAttackSystem(Store, Renderer);
            scheduler.Combat.TowerAttack = attack;
            scheduler.CombatSetup.TowerAttack = attack;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            RebuildGrid();
            scheduler.Tick(1f, 0);
            scheduler.Tick(0f, 1);
            Assert.Equal(200f, Store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void BulletTime_WeatherUsesEnemyClock_ScalingDamageByQuarter()
        {
            Player(p => { p.Health = 1000f; });
            Config.Levels.Clear();
            Config.Weather.Types["Sandstorm"] = new WeatherTypeConfig { EnemyDotPct = 0.005f, MinIntensity = 1f, MaxIntensity = 1f, DefaultDuration = 10f };
            int enemyId = Enemy(e => { e.Health = 100f; e.MaxHealth = 100f; });
            var weather = new Systems.WeatherSystem(Store, Config);
            weather.ForceWeather(0, WeatherConfig.Sandstorm, 1f, 10f);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.PreGame.Weather = weather;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            scheduler.Tick(1f, 0);
            Assert.Equal(99.875f, Store.EnemyHealth[enemyId], 3);
        }

        [Fact]
        public void BulletTime_WoundTransitionIsDtFree_AndDoesNotShareWeatherClock()
        {
            Player(p => { p.Health = 1000f; });
            Config.Levels.Clear();
            int enemyId = Enemy(e => { e.Health = 10f; e.MaxHealth = 100f; e.MoveSpeed = 2f; });
            Store.EnemyWoundThreshold[enemyId] = 0.5f;
            Store.EnemyWoundSlowRatio[enemyId] = 0.5f;
            var wound = new Systems.EnemyWoundSystem(Store, 0);
            var scheduler = new FrameScheduler(Store, Config);
            scheduler.Movement.Wound = wound;
            Store.PlayerBulletTimeTurnsLeft[0] = 2f;
            Store.PlayerBulletTimeScale[0] = 0.25f;
            scheduler.Tick(1f, 0);
            Assert.True(Store.EnemyIsWounded[enemyId]);
            Assert.Equal(1f, Store.EnemyMoveSpeed[enemyId]);
        }

        private ReplaySnapshot RunWaveFrameOnFreshWorld()
        {
            using var world = new TestWorld();
            var events = new RecordingEventBus();
            int playerId = world.Player(p => { p.Health = 100f; p.Gold = 0f; });
            int enemyId = world.Enemy(e => { e.X = 5f; e.Y = 1f; e.Health = 10f; e.MaxHealth = 10f; e.GoldReward = 7; });
            int towerId = world.Tower(5, 1, TowerType.Basic, t => { t.Damage = 100f; t.Range = 2; t.Speed = 1f; });
            world.Store.TowerCritChance[towerId] = 0f;
            world.Store.TowerDamageVariance[towerId] = 0f;
            var scheduler = new FrameScheduler(world.Store, world.Config, events);
            var attack = new Systems.TowerAttackSystem(world.Store, world.Renderer, null, 10, new EventBus(), events);
            scheduler.Combat.TowerAttack = attack;
            scheduler.CombatSetup.TowerAttack = attack;
            scheduler.Phase = GameState.WavePhase;
            world.Store.RebuildSpatialGrid();
            scheduler.Tick(1f, 0);
            scheduler.Tick(0f, 1);
            var active = new List<int>(world.Store.ActiveEnemyIds);
            return new ReplaySnapshot(world.Store.GetActiveEnemyCount(), active, world.Store.TotalKills, world.Store.GetPlayerGold(playerId), world.Store.EnemyHealth[enemyId], events.Events);
        }

        private (SystemRegistry registry, FrameScheduler scheduler, int enemyId) MakeRegistryScenario(IBattleEventBus events)
        {
            Player(p => { p.Health = 1000f; p.Gold = 0f; });
            Config.Levels.Clear();
            Config.GlobalSkills.Add(new GlobalSkillDef { Name = "Meteor Strike", SkillType = (int)GlobalSkillType.MeteorStrike, ManaCost = 0f, Cooldown = 0f, DamagePct = 100f, MaxDamage = 10000f });
            int enemyId = Enemy(e => { e.X = 20f; e.Y = 20f; e.Health = 10f; e.MaxHealth = 10f; e.GoldReward = 7; });
            var registry = new SystemRegistry();
            registry.CreateAll(Store, Config, Renderer, 0, new StateMachine(), events);
            registry.WireDependencies(Store, 0);
            var scheduler = new FrameScheduler(Store, Config, events);
            registry.AssignToGroups(scheduler);
            return (registry, scheduler, enemyId);
        }

        private sealed class RecordingEventBus : IBattleEventBus
        {
            public List<string> Events { get; } = new List<string>();
            public List<int> DamageTargets { get; } = new List<int>();
            public List<string> KillEvents { get; } = new List<string>();
            public void OnEntityCreated(int entityId, float x, float y, string entityType) => Events.Add("created");
            public void OnTowerCreated(int entityId, float x, float y, TowerType towerType) { }
            public void OnEntityDestroyed(int entityId) { Events.Add("destroyed"); KillEvents.Add("destroyed"); }
            public void OnPositionChanged(int entityId, float x, float y) { }
            public void OnPositionsChanged(List<(int entityId, float x, float y)> changes) { }
            public void OnDamageDealt(int targetId, float amount, string damageType, bool isCritical) { Events.Add("damage"); DamageTargets.Add(targetId); }
            public void OnEntityKilled(int entityId, int killerId) { Events.Add("killed"); KillEvents.Add("killed"); }
            public void OnProjectileFired(float fromX, float fromY, float toX, float toY, float speed) { }
            public void OnWaveStarted(int waveNumber) { }
            public void OnGameOver(bool victory) { }
        }

        private sealed class ReplaySnapshot : IEquatable<ReplaySnapshot>
        {
            public int ActiveEnemyCount { get; }
            public List<int> ActiveEnemyIds { get; }
            public int Kills { get; }
            public float Gold { get; }
            public float EnemyHealth { get; }
            public List<string> Events { get; }
            public ReplaySnapshot(int count, List<int> ids, int kills, float gold, float hp, List<string> events) { ActiveEnemyCount = count; ActiveEnemyIds = ids; Kills = kills; Gold = gold; EnemyHealth = hp; Events = new List<string>(events); }
            public bool Equals(ReplaySnapshot? other) => other != null && ActiveEnemyCount == other.ActiveEnemyCount && Kills == other.Kills && Gold == other.Gold && EnemyHealth == other.EnemyHealth && ActiveEnemyIds.SequenceEqual(other.ActiveEnemyIds) && Events.SequenceEqual(other.Events);
            public override bool Equals(object? obj) => Equals(obj as ReplaySnapshot);
            public override int GetHashCode() => HashCode.Combine(ActiveEnemyCount, Kills, Gold, EnemyHealth);
        }
    }
}

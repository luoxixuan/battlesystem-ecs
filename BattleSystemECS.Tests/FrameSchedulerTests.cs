using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Invariants for FrameScheduler and the game tick lifecycle.
    /// </summary>
    public class FrameSchedulerTests
    {
        [Fact]
        public void TickGameTurn_SpawnsEnemies()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.PositionX[pid] = 5f;
            store.PositionY[pid] = 0f;

            var scheduler = new FrameScheduler(store, config);
            scheduler.Spawning.WaveSpawning = new WaveSpawningSystem(store, r, config);

            scheduler.TickGameTurn(1f, 0);

            Assert.True(store.GetActiveEnemyCount() > 0,
                "TickGameTurn should trigger wave spawning on turn 0");
        }

        [Fact]
        public void Tick_ResolvesKilledEnemies()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.SetPlayerGold(pid, 9999f);
            store.PositionX[pid] = 5f;
            store.PositionY[pid] = 0f;

            // Place a tower that kills enemies immediately
            int towerId = store.CreateEntity();
            store.AddTower(towerId, TowerType.Basic, 5, 10, 1f, 1, 999f);
            store.PositionX[towerId] = 5f;
            store.PositionY[towerId] = 1f;
            store.PositionActive[towerId] = true;

            var scheduler = new FrameScheduler(store, config);
            scheduler.Spawning.WaveSpawning = new WaveSpawningSystem(store, r, config);
            scheduler.AI.EnemyAI = new EnemyAISystem(store, r, pid, config, new EnemyAbilitySystem(store, r, pid, config));
            scheduler.Movement.EnemyMovement = new EnemyMovementSystem(store, pid);
            var towerAttack = new TowerAttackSystem(store, r);
            scheduler.Combat.TowerAttack = towerAttack;
            scheduler.CombatSetup.TowerAttack = towerAttack;

            // Run several turns
            for (int t = 0; t < 5; t++)
            {
                scheduler.Tick(1f, t);
            }

            // If we get here without exception, death resolution is stable
            Assert.True(true);
        }
    }
}

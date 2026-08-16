using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Framework
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

            // 预生成静止敌人（AddEnemy 自动进入活跃列表）
            for (int i = 0; i < 5; i++)
            {
                store.AddEnemy(4f + i * 0.5f, 1f, 0f, 20f, 20f, 5f, 5, 1);
            }
            int aliveBefore = store.GetActiveEnemyCount();
            Assert.True(aliveBefore == 5, $"Expected 5 enemies, got {aliveBefore}");

            // 放置高伤塔并注册进活跃塔列表（AddTower 不会自动注册）
            int towerId = store.CreateEntity();
            store.AddTower(towerId, TowerType.Basic, 1000, 10, 1f, 1, 999f);
            store.PositionX[towerId] = 5f;
            store.PositionY[towerId] = 1f;
            store.PositionActive[towerId] = true;
            store.AddActiveTowerId(towerId);

            var scheduler = new FrameScheduler(store, config);
            var towerAttack = new TowerAttackSystem(store, r);
            scheduler.Combat.TowerAttack = towerAttack;
            scheduler.CombatSetup.TowerAttack = towerAttack;

            // 等价于 GameManager 每帧开头的网格重建
            store.RebuildSpatialGrid();

            scheduler.Tick(1f, 0);

            // Tick 内 TowerAttack 击杀 → 帧末 ResolveEnemiesKilledThisFrame 统一收窄活跃列表
            Assert.True(store.GetActiveEnemyCount() < aliveBefore,
                $"Expected frame-end death resolution, alive={store.GetActiveEnemyCount()}, before={aliveBefore}");
        }
    }
}

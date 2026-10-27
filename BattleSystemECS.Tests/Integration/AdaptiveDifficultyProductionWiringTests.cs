using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    /// <summary>
    /// 生产组装下 WaveSpawning ↔ AdaptiveDifficulty 双向接线与击杀信号回归（4bebc43 曾丢失
    /// WaveSpawning.SetAdaptiveDifficulty；RecordKill 曾无调用方）。
    /// 多份相同组装只在第 1 波的漏怪/击杀不同，断言相对关系而非具体配置值。
    /// </summary>
    public sealed class AdaptiveDifficultyProductionWiringTests
    {
        // 非 0 playerId（与压测一致）：WaveSpawning 曾硬编码 player 0 读写难度。
        private const int PlayerId = 1;
        private const int EnemiesPerWave = 4;

        [Fact]
        public void LeaksRaiseDifficultyAndNextWaveSpawnsScaledEnemies()
        {
            var calm = RunTwoWaves(leaks: 0, kills: 0);
            var leaky = RunTwoWaves(leaks: 5, kills: 0);

            Assert.True(leaky.LevelAfterWave1 > calm.LevelAfterWave1,
                $"leaks must raise adaptive difficulty: calm={calm.LevelAfterWave1}, leaky={leaky.LevelAfterWave1}");
            Assert.True(leaky.Wave2MaxHealth > calm.Wave2MaxHealth,
                $"wave 2 must spawn tougher enemies: calm={calm.Wave2MaxHealth}, leaky={leaky.Wave2MaxHealth}");
            Assert.Equal(leaky.LevelAfterWave1 / calm.LevelAfterWave1,
                leaky.Wave2MaxHealth / calm.Wave2MaxHealth, 3);
        }

        [Fact]
        public void KillsResolvedThroughDeathQueueLowerDifficulty()
        {
            var calm = RunTwoWaves(leaks: 0, kills: 0);
            var killed = RunTwoWaves(leaks: 0, kills: 2);

            Assert.True(killed.LevelAfterWave1 < calm.LevelAfterWave1,
                $"kills must lower adaptive difficulty: calm={calm.LevelAfterWave1}, killed={killed.LevelAfterWave1}");
            Assert.True(killed.Wave2MaxHealth < calm.Wave2MaxHealth,
                $"wave 2 must spawn weaker enemies: calm={calm.Wave2MaxHealth}, killed={killed.Wave2MaxHealth}");
        }

        [Fact]
        public void LeakedEnemiesRemovedThroughDeathQueueDoNotCountAsKills()
        {
            // 生产漏怪路径（GameManager / BenchmarkSystem）同时递增漏怪数并经死亡队列移除敌人。
            var leakOnly = RunTwoWaves(leaks: 2, kills: 0);
            var leakThenRemoved = RunTwoWaves(leaks: 2, kills: 0, leaksQueueDeath: true);

            Assert.Equal(leakOnly.LevelAfterWave1, leakThenRemoved.LevelAfterWave1, 5);
        }

        private static (float LevelAfterWave1, float Wave2MaxHealth) RunTwoWaves(int leaks, int kills, bool leaksQueueDeath = false)
        {
            using var store = new ComponentStore();
            store.Determinism.Reset(1234);
            store.AddPlayer(PlayerId, 10f, 1f, 10f, 1);
            var logger = new MockRenderer();
            var config = GameConfigLoader.LoadConfigStrict(logger);
            var level = config.Levels[0];
            string monsterType = config.MonsterTypes[0].Type;
            level.Waves = new List<WaveConfig>
            {
                SmallWave(1, monsterType),
                SmallWave(2, monsterType),
            };

            var stateMachine = new StateMachine();
            var scheduler = new FrameScheduler(store, config);
            var registry = new SystemRegistry();
            new ProductionSystemInstaller().Install(registry, store, config, logger, PlayerId, stateMachine, scheduler);
            var waves = registry.WaveSpawning!;
            waves.SetLevel(level.LevelNumber);

            bool waveCompleted = false;
            bool deathsResolved = false;
            waves.OnWaveComplete += () => waveCompleted = true;
            for (int i = 0; i < 16 && !waveCompleted; i++)
            {
                waves.Update();
                if (!deathsResolved)
                {
                    var wave1 = EnemiesOfWave(store, 1);
                    if (wave1.Count >= EnemiesPerWave)
                    {
                        int removed = kills + (leaksQueueDeath ? leaks : 0);
                        Assert.True(removed <= wave1.Count, "test removes more enemies than wave 1 spawned");
                        for (int k = 0; k < removed; k++)
                            store.QueueEnemyDeath(wave1[k], PlayerId);
                        store.ResolveEnemiesKilledThisFrame();
                        deathsResolved = true;
                    }
                }
                store.EnemiesLeakedThisWave[PlayerId] = leaks;
                waves.DispatchPendingCallbacks();
            }
            Assert.True(waveCompleted, "wave 1 never completed");
            Assert.True(deathsResolved, "wave 1 enemies never all spawned");
            float levelAfterWave1 = store.AdaptiveDifficultyLevel[PlayerId];

            waves.Update();
            var wave2 = EnemiesOfWave(store, 2);
            Assert.NotEmpty(wave2);
            return (levelAfterWave1, store.EnemyMaxHealth[wave2[0]]);
        }

        private static List<int> EnemiesOfWave(ComponentStore store, int wave)
        {
            var ids = new List<int>();
            foreach (int id in store.ActiveEnemyIds)
                if (store.EnemyActive[id] && store.EnemyWaveNumber[id] == wave) ids.Add(id);
            return ids;
        }

        private static WaveConfig SmallWave(int number, string monsterType) => new WaveConfig
        {
            WaveNumber = number,
            EnemyTypes = new List<EnemyTypeEntry> { new EnemyTypeEntry { MonsterType = monsterType, Count = EnemiesPerWave } },
        };
    }
}

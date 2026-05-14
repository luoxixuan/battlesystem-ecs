using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Integration tests: verify game_config.json loads correctly
    /// and all referenced entities (monsters, skills, towers) are consistent.
    /// </summary>
    public class GameConfigIntegrationTests
    {
        private MockRenderer _renderer = new MockRenderer();

        // ═══════════════════════════════════════════════════════════════════════════
        // Config Loading Tests
        // ═══════════════════════════════════════════════════════════════════════════

        [Fact]
        public void LoadConfig_FromRealFile_Succeeds()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);

            Assert.NotNull(config);
            Assert.True(config.MonsterTypes.Count > 0, "Should load monster types");
            Assert.True(config.Levels.Count > 0, "Should load levels");
        }

        [Fact]
        public void MonsterTypes_AllHaveRequiredFields()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);

            foreach (var monster in config.MonsterTypes)
            {
                Assert.False(string.IsNullOrEmpty(monster.Name), $"Monster missing name: {monster.Name}");
                Assert.False(string.IsNullOrEmpty(monster.Type), $"Monster {monster.Name} missing Type");
                Assert.True(monster.Health > 0, $"Monster {monster.Name} has invalid Health: {monster.Health}");
                Assert.True(monster.MaxHealth > 0, $"Monster {monster.Name} has invalid MaxHealth: {monster.MaxHealth}");
                Assert.True(monster.Damage >= 0, $"Monster {monster.Name} has invalid Damage: {monster.Damage}");
                Assert.True(monster.MoveSpeed >= 0, $"Monster {monster.Name} has invalid MoveSpeed: {monster.MoveSpeed}");
                Assert.True(monster.AttackRange >= 0, $"Monster {monster.Name} has invalid AttackRange: {monster.AttackRange}");
                Assert.True(monster.AttackInterval >= 0, $"Monster {monster.Name} has invalid AttackInterval: {monster.AttackInterval}");
                Assert.True(monster.GoldReward >= 0, $"Monster {monster.Name} has invalid GoldReward: {monster.GoldReward}");
                Assert.NotNull(monster.Skills);
            }
        }

        [Fact]
        public void TowerTypes_AllHaveRequiredFields()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);

            foreach (var tower in config.TowerTypes)
            {
                Assert.False(string.IsNullOrEmpty(tower.Name), $"Tower missing Name");
                Assert.False(string.IsNullOrEmpty(tower.Type), $"Tower {tower.Name} missing Type");
                Assert.True(tower.Damage > 0, $"Tower {tower.Name} has invalid Damage: {tower.Damage}");
                Assert.True(tower.Range >= 0, $"Tower {tower.Name} has invalid Range: {tower.Range}");
                Assert.True(tower.AttackSpeed > 0, $"Tower {tower.Name} has invalid AttackSpeed: {tower.AttackSpeed}");
                Assert.True(tower.Cost > 0, $"Tower {tower.Name} has invalid Cost: {tower.Cost}");
                Assert.True(tower.UpgradeCost > 0, $"Tower {tower.Name} has invalid UpgradeCost: {tower.UpgradeCost}");
            }
        }

        [Fact]
        public void Skills_AllHaveRequiredFields()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);

            foreach (var skill in config.Skills)
            {
                Assert.False(string.IsNullOrEmpty(skill.Name), $"Skill missing Name");
                Assert.True(skill.DamageMultiplier >= 0, $"Skill {skill.Name} has invalid DamageMultiplier: {skill.DamageMultiplier}");
                Assert.True(skill.Cooldown >= 0, $"Skill {skill.Name} has invalid Cooldown: {skill.Cooldown}");
                Assert.True(skill.AreaWidth >= 1, $"Skill {skill.Name} has invalid AreaWidth: {skill.AreaWidth}");
                Assert.True(skill.AreaHeight >= 1, $"Skill {skill.Name} has invalid AreaHeight: {skill.AreaHeight}");
                Assert.True(skill.AttackRange >= 0, $"Skill {skill.Name} has invalid AttackRange: {skill.AttackRange}");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Cross-Reference Tests (the critical ones)
        // ═══════════════════════════════════════════════════════════════════════════

        [Fact]
        public void LevelWave_MonsterType_ExistsInMonsterTypes()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            var monsterNames = config.MonsterTypes.Select(m => m.Name).ToHashSet();
            var monsterTypes = config.MonsterTypes.Select(m => m.Type).ToHashSet();

            foreach (var level in config.Levels)
            {
                Assert.NotNull(level.Waves);
                foreach (var wave in level.Waves)
                {
                    Assert.False(
                        string.IsNullOrEmpty(wave.MonsterType),
                        $"Level {level.LevelNumber} Wave {wave.WaveNumber} has empty MonsterType"
                    );

                    // Wave references MonsterType by TYPE string (e.g. "Normal", "Fast")
                    bool exists = config.MonsterTypes.Any(m => m.Type == wave.MonsterType);
                    Assert.True(exists,
                        $"Level {level.LevelNumber} Wave {wave.WaveNumber} references MonsterType '{wave.MonsterType}' which does not exist in MonsterTypes list");
                }
            }
        }

        // Note: Monster "Skills" like "Normal Attack" are basic attacks handled
        // internally by EnemyMovementSystem — they do NOT need to exist in
        // the game_config.json Skills list (which is for player/hero abilities).

        [Fact]
        public void AllMonsterNames_AreUnique()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            var names = config.MonsterTypes.Select(m => m.Name).ToList();
            var duplicates = names.GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void AllSkillNames_AreUnique()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            var names = config.Skills.Select(s => s.Name).ToList();
            var duplicates = names.GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        [Fact]
        public void AllTowerNames_AreUnique()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            var names = config.TowerTypes.Select(t => t.Name).ToList();
            var duplicates = names.GroupBy(x => x)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Runtime Simulation Tests — spawn enemies and run a few frames
        // ═══════════════════════════════════════════════════════════════════════════

        [Fact]
        public void SpawnEnemies_AllMonsterTypes_SucceedWithoutCrash()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            var store = new ComponentStore();

            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;

            store.SetPlayerGold(playerId, 9999f);

            var random = new Random(777);

            foreach (var monsterType in config.MonsterTypes)
            {
                // Spawn 5 of each type
                for (int i = 0; i < 5; i++)
                {
                    float x = random.Next(0, 10);
                    float y = random.Next(1, 20);
                    int id = store.AddEnemy(
                        x, y,
                        monsterType.MoveSpeed,
                        monsterType.Health,
                        monsterType.MaxHealth,
                        monsterType.Damage,
                        (int)monsterType.AttackRange,
                        (int)monsterType.AttackInterval
                    );
                    store.SetEntityName(id, $"{monsterType.Type}_{i}");
                    store.SetEnemyAIAction(id, "");
                }
            }

            // Verify all were spawned
            int totalEnemies = store.GetActiveEnemyCount();
            Assert.True(totalEnemies > 0, "No enemies spawned");
            Assert.Equal(config.MonsterTypes.Count * 5, totalEnemies);
        }

        [Fact]
        public void RunFrames_WithAllMonsterTypes_NoExceptions()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            var store = new ComponentStore();

            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.SetPlayerGold(playerId, 9999f);

            var random = new Random(777);

            // Spawn 3 of each monster type
            foreach (var monsterType in config.MonsterTypes)
            {
                for (int i = 0; i < 3; i++)
                {
                    float x = random.Next(0, 10);
                    float y = random.Next(1, 20);
                    int id = store.AddEnemy(
                        x, y,
                        monsterType.MoveSpeed,
                        monsterType.Health,
                        monsterType.MaxHealth,
                        monsterType.Damage,
                        (int)monsterType.AttackRange,
                        (int)monsterType.AttackInterval
                    );
                    store.SetEntityName(id, $"{monsterType.Type}_{i}");
                    store.SetEnemyAIAction(id, "");
                    store.EnemyBehaviorTree[id] = config.GetCachedBehaviorTree(monsterType.Type);
                }
            }

            // Simulate 10 frames - this exercises EnemyAISystem, EnemyMovementSystem, etc.
            var enemyAISystem = new EnemyAISystem(store, _renderer, playerId, config);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var waveSpawning = new WaveSpawningSystem(store, _renderer, config);
            var goldSystem = new GoldSystem(store, _renderer);

            for (int frame = 0; frame < 10; frame++)
            {
                int turn = frame;
                store.BeginFrame();
                enemyAISystem.SetTurn(turn); enemyAISystem.Update();
                enemyMovement.SetTurn(turn); enemyMovement.Update();
                goldSystem.Update();
                store.ResolveEnemiesKilledThisFrame();
            }

            // If we get here without exception, the test passes
            Assert.True(true);
        }

        [Fact]
        public void PlayerSkills_AllExistInSkillConfig()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            if (config.Player?.StartingSkills == null) return;

            var skillNames = config.Skills.Select(s => s.Name).ToList();
            foreach (var skillName in config.Player.StartingSkills)
            {
                // Skills can be referenced by exact name or by prefix (e.g. "Circuit Breaker" matches "Circuit Breaker #1")
                bool exists = skillNames.Any(n => n == skillName || n.StartsWith(skillName + " #"));
                Assert.True(exists,
                    $"Player StartingSkill '{skillName}' does not exist in game_config.json Skills list");
            }
        }

        [Fact]
        public void Levels_HaveValidWaveCounts()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);

            foreach (var level in config.Levels)
            {
                Assert.True(level.LevelNumber > 0, $"Level has invalid LevelNumber: {level.LevelNumber}");
                Assert.NotNull(level.Waves);
                Assert.True(level.WaveCount > 0, $"Level {level.LevelNumber} has no waves");
                Assert.Equal(level.WaveCount, level.Waves.Count);

                foreach (var wave in level.Waves)
                {
                    Assert.True(wave.WaveNumber > 0, $"Level {level.LevelNumber} wave has invalid WaveNumber");
                    Assert.True(wave.EnemyCount > 0, $"Level {level.LevelNumber} Wave {wave.WaveNumber} has no enemies");
                }
            }
        }
    }
}
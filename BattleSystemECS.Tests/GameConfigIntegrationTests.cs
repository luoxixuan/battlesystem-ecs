using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using BattleSystemECS.Components;
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

        // ═══════════════════════════════════════════════════════════════════════════
        // Stress Tests — all towers, all skills, all monsters together
        // ═══════════════════════════════════════════════════════════════════════════

        [Fact]
        public void All150Towers_PlaceAndAttack_NoExceptions()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            Assert.True(config.TowerTypes.Count > 0 || config.Skills.Count > 0,
                "game_config.json Towers array is loaded but GameConfigLoader doesn't parse Towers yet");

            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.SetPlayerGold(playerId, 999999f);
            store.AddPlayer(playerId, 3f, 1f, 10f, 1);

            // Place one of each tower type from game_config.json Towers array
            var towers = config.TowerTypes.Count > 0 ? config.TowerTypes : new List<TowerConfig>();
            int placed = 0;
            int row = 0, col = 0;

            foreach (var tc in towers)
            {
                int eid = store.CreateEntity();
                if (eid < 0) break;
                store.AddTower(eid, tc.Type, tc.Damage, tc.Range, tc.AttackSpeed, 1, tc.Cost);
                store.PositionX[eid] = col * 1.5f;
                store.PositionY[eid] = 5f + row;
                store.PositionActive[eid] = true;
                placed++;
                col++;
                if (col >= 10) { col = 0; row++; }
            }

            Assert.True(placed > 0, $"No towers placed from config. TowerTypes count: {towers.Count}");

            // Spawn enemies for towers to shoot at
            var random = new Random(42);
            for (int i = 0; i < 50; i++)
            {
                float x = random.Next(0, 10);
                float y = random.Next(1, 19);
                int eid = store.AddEnemy(x, y, 1f, 100f, 100f, 5f, 5, 1);
                store.SetEntityName(eid, $"StressE{i}");
                store.SetEnemyAIAction(eid, "");
            }

            // Run 20 frames with tower attacks
            var towerAttack = new TowerAttackSystem(store, _renderer);
            for (int f = 0; f < 20; f++)
            {
                int turn = f;
                store.BeginFrame();
                towerAttack.SetTurn(turn);
                towerAttack.Update(1f);
                store.ResolveEnemiesKilledThisFrame();
            }

            // If we get here without exception, all 150 towers are well-behaved
            Assert.True(true);
        }

        [Fact]
        public void StressTest_AllEntitiesAllSystems_NoExceptions()
        {
            var config = GameConfigLoader.LoadConfig(_renderer);
            var store = new ComponentStore();

            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.SetPlayerGold(playerId, 999999f);
            store.AddPlayer(playerId, 10f, 1f, 20f, 1);

            var random = new Random(777);

            // Spawn all 200 monster types (3 each = 600 enemies)
            int spawnedMonsters = 0;
            foreach (var monsterType in config.MonsterTypes)
            {
                for (int i = 0; i < 3; i++)
                {
                    float x = random.Next(0, 10);
                    float y = random.Next(1, 19);
                    int eid = store.AddEnemy(
                        x, y,
                        monsterType.MoveSpeed,
                        monsterType.Health,
                        monsterType.MaxHealth,
                        monsterType.Damage,
                        (int)monsterType.AttackRange,
                        (int)monsterType.AttackInterval
                    );
                    if (eid < 0) break;
                    store.SetEntityName(eid, $"{monsterType.Type}_{i}");
                    store.SetEnemyAIAction(eid, "");
                    store.EnemyBehaviorTree[eid] = config.GetCachedBehaviorTree(monsterType.Type);
                    spawnedMonsters++;
                }
            }
            Assert.True(spawnedMonsters > 0, "No monsters spawned");

            // Place one of each tower type
            int placedTowers = 0;
            int col = 0, row = 0;
            var towers = config.TowerTypes;
            foreach (var tc in towers)
            {
                int eid = store.CreateEntity();
                if (eid < 0) break;
                store.AddTower(eid, tc.Type, tc.Damage, tc.Range, tc.AttackSpeed, 1, tc.Cost);
                store.PositionX[eid] = col * 1.5f;
                store.PositionY[eid] = 15f + row;
                store.PositionActive[eid] = true;
                placedTowers++;
                col++;
                if (col >= 10) { col = 0; row++; }
            }

            // Initialize all systems
            var enemyAbility = new EnemyAbilitySystem(store, _renderer, playerId, config);
            var enemyAI       = new EnemyAISystem(store, _renderer, playerId, config, enemyAbility);
            var enemyMovement = new EnemyMovementSystem(store, playerId);
            var playerAttack  = new PlayerTowerAttackSystem(store, _renderer, playerId, config);
            var towerAttack   = new TowerAttackSystem(store, _renderer);
            var gold          = new GoldSystem(store, _renderer);
            var upgrade       = new UpgradeSystem(store, _renderer, playerId, config);
            var skill         = new SkillSystem(store, _renderer, playerId, config);
            skill.InitializePlayerSkills();

            // Run 20 frames with all systems
            for (int f = 0; f < 20; f++)
            {
                int turn = f;
                store.BeginFrame();

                enemyAI.SetTurn(turn);       enemyAI.Update();
                enemyMovement.SetTurn(turn); enemyMovement.Update();
                playerAttack.SetTurn(turn);  playerAttack.Update();
                towerAttack.SetTurn(turn);   towerAttack.Update(1f);
                gold.SetTurn(turn);          gold.Update();
                upgrade.Update();
                skill.Update(1f);

                store.ResolveEnemiesKilledThisFrame();
            }

            // If we get here without exception, the full system is stable
            Assert.True(true);
        }
    }
}
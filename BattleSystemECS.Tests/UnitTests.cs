using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Mock IRenderer for unit testing — captures logs without console output
    /// </summary>
    public class MockRenderer : IRenderer
    {
        public List<string> Logs = new List<string>();

        public void Log(string message) => Logs.Add(message);
        public void LogBattle(string message) => Logs.Add(message);
        public void LogDamage(string attacker, string defender, float damage, bool isCritical)
            => Logs.Add($"[DAMAGE] {attacker} -> {defender}: {damage}");
        public void LogDeath(string entity) => Logs.Add($"[DEATH] {entity}");
        public void LogWin(string winner) => Logs.Add($"[WIN] {winner}");
        public void LogBattleStart(string battleName) => Logs.Add($"[BATTLE] {battleName}");
        public void LogTurn(int turn) => Logs.Add($"[TURN] {turn}");

        public bool HasLogContaining(string substring)
        {
            foreach (var log in Logs)
                if (log.Contains(substring)) return true;
            return false;
        }
    }

    /// <summary>
    /// ComponentStore 单元测试
    /// </summary>
    public class ComponentStoreTests
    {
        [Fact]
        public void NewStore_HasInitialEntities()
        {
            var store = new ComponentStore();
            // ComponentStore constructor pre-creates player + tower entities
            Assert.True(store.NextEntityId >= 1, "Should have at least one pre-created entity");
        }

        [Fact]
        public void MAX_ENTITIES_IsReasonable()
        {
            Assert.True(ComponentStore.MAX_ENTITIES >= 1000);
        }

        [Fact]
        public void CreateEntity_IncrementsNextId()
        {
            var store = new ComponentStore();
            int before = store.NextEntityId;
            int id = store.CreateEntity();
            Assert.Equal(before, id);
            Assert.Equal(before + 1, store.NextEntityId);
        }

        [Fact]
        public void PlayerHealth_ArrayAccess()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 150f;

            Assert.Equal(200f, store.PlayerMaxHealth[playerId]);
            Assert.Equal(150f, store.PlayerCurrentHealth[playerId]);
        }

        [Fact]
        public void PlayerGold_ArrayAccess()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerGold[playerId] = 100;

            Assert.Equal(100, store.GetPlayerGold(playerId));
        }

        [Fact]
        public void TotalKills_StartsAtZero()
        {
            var store = new ComponentStore();
            Assert.Equal(0, store.TotalKills);
        }

        [Fact]
        public void TotalKills_CanIncrement()
        {
            var store = new ComponentStore();
            store.TotalKills++;
            store.TotalKills++;
            Assert.Equal(2, store.TotalKills);
        }

        [Fact]
        public void DestroyEntity_FreesId()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PositionActive[id] = true;
            store.EnemyActive[id] = true;

            store.DestroyEntity(id);

            Assert.False(store.PositionActive[id]);
            Assert.False(store.EnemyActive[id]);
        }

        [Fact]
        public void EnemyActive_DefaultFalse()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            Assert.False(store.EnemyActive[id]);
        }

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsOnlyActiveEnemies()
        {
            var store = new ComponentStore();
            int player = store.CreateEntity();
            store.AddPosition(player, 0, 0);
            int enemy1 = store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1);
            int enemy2 = store.AddEnemy(7, 19, 1f, 20, 20, 5, 10, 1);
            int neutral = store.CreateEntity();

            var active = store.GetAllActiveEnemyIds();

            Assert.Contains(enemy1, active);
            Assert.Contains(enemy2, active);
            Assert.DoesNotContain(player, active);
            Assert.DoesNotContain(neutral, active);
        }
    }

    /// <summary>
    /// GameConfigLoader / GameConfig 单元测试
    /// </summary>
    public class GameConfigLoaderTests
    {
        private GameConfig GetDefault() => GameConfigLoader.GetDefaultConfig();

        [Fact]
        public void DefaultConfig_HasPlayerMaxHealth()
        {
            var config = GetDefault();
            Assert.True(config.Player.MaxHealth > 0, "Player must have MaxHealth > 0");
        }

        [Fact]
        public void DefaultConfig_HasAttackInterval()
        {
            var config = GetDefault();
            Assert.True(config.Player.AttackInterval > 0, "Player must have AttackInterval > 0");
        }

        [Fact]
        public void DefaultConfig_HasSkills()
        {
            var config = GetDefault();
            Assert.NotEmpty(config.Skills);
        }

        [Fact]
        public void DefaultConfig_HasLevels()
        {
            var config = GetDefault();
            Assert.NotEmpty(config.Levels);
        }

        [Fact]
        public void DefaultConfig_HasMonsterTypes()
        {
            var config = GetDefault();
            Assert.NotEmpty(config.MonsterTypes);
        }

        [Fact]
        public void DefaultConfig_MonstersHaveHealth()
        {
            var config = GetDefault();
            foreach (var m in config.MonsterTypes)
                Assert.True(m.Health > 0, $"Monster {m.Name} should have Health > 0");
        }

        [Fact]
        public void DefaultConfig_PlayerHasStartingSkills()
        {
            var config = GetDefault();
            Assert.NotEmpty(config.Player.StartingSkills);
        }
    }

    /// <summary>
    /// WaveSpawningSystem 单元测试
    /// </summary>
    public class WaveSpawningSystemTests
    {
        private (ComponentStore store, GameConfig config) CreateTestEnv()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;

            var config = new GameConfig();

            return (store, config);
        }

        [Fact]
        public void NewSystem_StartsAtWaveOne()
        {
            var (store, config) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new WaveSpawningSystem(store, renderer, config);

            Assert.Equal(1, system.GetCurrentWave());
            Assert.Equal(1, system.GetCurrentLevel());
            Assert.Equal(0, system.GetTotalEnemiesSpawned());
        }

        [Fact]
        public void FirstUpdate_SpawnsEnemies()
        {
            var (store, config) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new WaveSpawningSystem(store, renderer, config);
            system.Update();

            Assert.True(system.GetTotalEnemiesSpawned() > 0, "Should spawn enemies on first update");
        }

        [Fact]
        public void BatchSize_IsFive()
        {
            var (store, config) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new WaveSpawningSystem(store, renderer, config);
            system.Update();

            Assert.Equal(5, system.GetTotalEnemiesSpawned());
        }
    }

    /// <summary>
    /// SkillSystem 单元测试
    /// </summary>
    public class SkillSystemTests
    {
        private (ComponentStore store, GameConfig config, int playerId) CreateTestEnv()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PlayerAttackDamage[playerId] = 10f;
            store.PlayerAttackRange[playerId] = 3f;
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;

            var config = new GameConfig();

            return (store, config, playerId);
        }

        private int CreateEnemy(ComponentStore store, float x, float y, float health = 10f, int goldReward = 10)
        {
            int enemyId = store.CreateEntity();
            store.EnemyActive[enemyId] = true;
            store.ActiveEnemyIds.Add(enemyId);  // Sync with maintained list
            store.PositionX[enemyId] = x;
            store.PositionY[enemyId] = y;
            store.SetEnemyHealth(enemyId, health);
            store.EnemyGoldReward[enemyId] = goldReward;
            store.AddToSpatialHash(enemyId);  // Register in spatial hash (required for GetEnemiesNear)
            return enemyId;
        }

        [Fact]
        public void NewSkillSystem_HasThreeSkills()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);
            system.InitializePlayerSkills();

            Assert.True(renderer.HasLogContaining("Cross Slash"));
            Assert.True(renderer.HasLogContaining("Mega Explosion"));
            Assert.True(renderer.HasLogContaining("Sniper Shot"));
        }

        [Fact]
        public void AutoCast_CrossSlash_Fires()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);
            system.InitializePlayerSkills();

            // Place enemy in Cross Slash range (player at 5,0; range=3)
            CreateEnemy(store, 5f, 3f);

            system.AutoCastBestSkill();

            Assert.True(renderer.HasLogContaining("Cross Slash cast"));
        }

        [Fact]
        public void Update_ReducesCooldown()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);
            system.InitializePlayerSkills();

            // Place an enemy, fire all 3 skills
            CreateEnemy(store, 5f, 3f);
            system.AutoCastBestSkill();
            system.AutoCastBestSkill();
            system.AutoCastBestSkill();

            // Update with 6s (Cross Slash CD is 5s, should be ready again)
            system.Update(6f);

            var logsBefore = renderer.Logs.Count;
            // Place another enemy for next cast
            CreateEnemy(store, 4f, 3f);
            system.AutoCastBestSkill();

            // Should have more logs (at least Cross Slash recast)
            Assert.True(renderer.Logs.Count > logsBefore, "Should cast a skill after cooldown expires");
        }

        [Fact]
        public void SkillCanDamageAndKill()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();

            // Enemy with only 10 HP — Cross Slash does 40 damage (10 * 4)
            // Cross Slash hits Y±1 from player (offset 0,0,-1,1), so place enemy at (5,1) where player is at (5,0)
            int enemyId = CreateEnemy(store, 5f, 1f, 10f);

            var system = new SkillSystem(store, renderer, playerId, config);
            system.InitializePlayerSkills();
            system.CastSkill("Cross Slash");

            Assert.True(renderer.HasLogContaining("Cross Slash cast"),
                "Cross Slash should cast (logs 'Cross Slash cast')");
            Assert.True(renderer.HasLogContaining("hit"),
                "Cross Slash should hit the enemy at (5,1)");
            // Enemy was at 10 HP, Cross Slash does 40 damage → killed, gold awarded
            float newGold = store.GetPlayerGold(playerId);
            Assert.True(newGold > 0, "Gold should increase after enemy killed");
        }

        [Fact]
        public void NoEnemies_NoCrash()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);

            // Should not crash with no enemies
            system.AutoCastBestSkill();
            Assert.True(true); // succeeded without exception
        }
    }

    /// <summary>
    /// End-to-end: basic game loop simulation
    /// </summary>
    public class GameSimulationTests
    {
        [Fact]
        public void GameLoop_RunsWithoutCrash()
        {
            var store = new ComponentStore();
            var renderer = new MockRenderer();
            var config = new GameConfig();

            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PlayerAttackDamage[playerId] = 10f;
            store.PlayerAttackRange[playerId] = 3f;
            store.PositionX[playerId] = 5f;
            store.PositionY[playerId] = 0f;

            var waveSystem = new WaveSpawningSystem(store, renderer, config);
            var attackSystem = new PlayerTowerAttackSystem(store, renderer, playerId, config);
            var skillSystem = new SkillSystem(store, renderer, playerId, config);

            // Simulate 10 turns
            for (int turn = 0; turn < 10; turn++)
            {
                waveSystem.Update();
                attackSystem.Update();
                skillSystem.Update(1f);
            }

            Assert.True(waveSystem.GetTotalEnemiesSpawned() > 0);
        }
    }
}

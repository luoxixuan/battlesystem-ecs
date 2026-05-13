using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
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

    // ═══════════════════════════════════════════════════════════════════════════════
    // ComponentStore 核心测试
    // ═══════════════════════════════════════════════════════════════════════════════

    public class ComponentStoreTests
    {
        [Fact]
        public void NewStore_HasInitialEntities()
        {
            var store = new ComponentStore();
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
        public void CreateEntity_ReturnsNegativeOneWhenPoolExhausted()
        {
            var store = new ComponentStore();
            // Fill up to MAX_ENTITIES - 1 (player entity takes slot 1)
            int created = 0;
            while (true)
            {
                int id = store.CreateEntity();
                if (id == -1) break;
                created++;
            }
            Assert.True(created > 0, "Should create at least some entities");
            // Next call should return -1
            Assert.Equal(-1, store.CreateEntity());
        }

        [Fact]
        public void CreateEntity_RejectsNegativeId()
        {
            var store = new ComponentStore();
            // After a DestroyEntity, the recycled ID should be non-negative
            int id = store.CreateEntity();
            store.DestroyEntity(id);
            // Pop from free list — should get the same ID back, not -1
            int recycled = store.CreateEntity();
            Assert.True(recycled >= 0, "Recycled entity ID must be >= 0");
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

        // ─── Bug#30: DestroyEntity 必须从 ActiveTowerIds 移除 ─────────────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveTowerIds()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.AddPlayer(playerId, 3f, 1f, 10f, 1);

            // Place a tower
            int towerId = store.CreateEntity();
            store.AddTower(towerId, "Arrow", 5f, 3, 1f, 1, 50f);
            store.AddPosition(towerId, 3f, 3f);

            Assert.Contains(towerId, store.ActiveTowerIds);

            store.DestroyEntity(towerId);

            Assert.DoesNotContain(towerId, store.ActiveTowerIds);
        }

        // ─── Bug#11 / DestroyEntity: ActiveEnemyIds 列表清理 ─────────────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveEnemyIds()
        {
            var store = new ComponentStore();
            int enemyId = store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);

            Assert.Contains(enemyId, store.ActiveEnemyIds);

            store.DestroyEntity(enemyId);

            Assert.DoesNotContain(enemyId, store.ActiveEnemyIds);
        }

        [Fact]
        public void DestroyEntity_ClearsActiveFlags()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PositionActive[id] = true;
            store.EnemyActive[id] = true;

            store.DestroyEntity(id);

            Assert.False(store.PositionActive[id]);
            Assert.False(store.EnemyActive[id]);
        }

        // ─── Bug#21: GetAllActiveEnemyIds 返回防御性副本 ─────────────────────────

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsDefensiveCopy()
        {
            var store = new ComponentStore();
            store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            store.AddEnemy(7f, 19f, 1f, 20f, 20f, 5f, 10, 1);

            var active = store.GetAllActiveEnemyIds();
            int originalCount = active.Count;

            active.Clear(); // Mutate the returned list

            // Original should be unaffected
            var fresh = store.GetAllActiveEnemyIds();
            Assert.Equal(originalCount, fresh.Count);
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

        // ─── AddEnemy / CreateEntity 失败路径 ─────────────────────────────────────

        [Fact]
        public void CreateEntity_ReturnsNegativeOneWhenExhausted()
        {
            var store = new ComponentStore();
            // Exhaust using CreateEntity directly
            int created = 0;
            while (store.CreateEntity() != -1) { created++; }
            Assert.True(created > 0);

            // Next call must return -1 (not throw, not return an invalid ID)
            int result = store.CreateEntity();
            Assert.Equal(-1, result);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // GameplayAbility / SkillSystem 测试
    // ═══════════════════════════════════════════════════════════════════════════════

    public class GameplayAbilityTests
    {
        // ─── Bug#37: CanActivate epsilon 行为 ─────────────────────────────────────

        [Fact]
        public void CanActivate_TrueWhenCooldownZero()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.CurrentCooldown = 0f;

            Assert.True(inst.CanActivate());
        }

        [Fact]
        public void CanActivate_TrueWhenCooldownBelowEpsilon()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.CurrentCooldown = 0.00005f; // well below EPSILON

            Assert.True(inst.CanActivate());
        }

        [Fact]
        public void CanActivate_FalseWhenCooldownAboveEpsilon()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.CurrentCooldown = 0.001f; // above EPSILON 0.0001f

            Assert.False(inst.CanActivate());
        }

        [Fact]
        public void CanActivate_TrueWhenCooldownAtOrBelowEpsilon()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);

            // Exactly at EPSILON: <= comparison means ready
            var atEpsilon = new AbilityInstance(def);
            atEpsilon.CurrentCooldown = 0.0001f;
            Assert.True(atEpsilon.CanActivate());

            // Below EPSILON: also ready
            var belowEpsilon = new AbilityInstance(def);
            belowEpsilon.CurrentCooldown = 0.00005f;
            Assert.True(belowEpsilon.CanActivate());

            // Above EPSILON: not ready
            var aboveEpsilon = new AbilityInstance(def);
            aboveEpsilon.CurrentCooldown = 0.001f;
            Assert.False(aboveEpsilon.CanActivate());
        }

        [Fact]
        public void Activate_SetsCooldownToDefinitionValue()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.Activate();

            Assert.Equal(5f, inst.CurrentCooldown);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SkillSystem 测试
    // ═══════════════════════════════════════════════════════════════════════════════

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
            store.ActiveEnemyIds.Add(enemyId);
            store.PositionX[enemyId] = x;
            store.PositionY[enemyId] = y;
            store.SetEnemyHealth(enemyId, health);
            store.EnemyGoldReward[enemyId] = goldReward;
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

        // ─── Bug#9: InitializePlayerSkills 重复调用不累计 ─────────────────────

        [Fact]
        public void InitializePlayerSkills_Idempotent()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);

            system.InitializePlayerSkills();
            int countAfterFirst = store.AbilityCount[playerId];
            Assert.Equal(3, countAfterFirst);

            system.InitializePlayerSkills(); // Call again
            int countAfterSecond = store.AbilityCount[playerId];
            Assert.Equal(3, countAfterSecond);
        }

        // ─── Bug#37 补充: AutoCastBestSkill 走 epsilon ───────────────────────────

        [Fact]
        public void AutoCastBestSkill_FiresWhenCooldownBelowEpsilon()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);
            system.InitializePlayerSkills();

            CreateEnemy(store, 5f, 3f);

            // Cast once to start cooldown
            system.AutoCastBestSkill();
            Assert.True(renderer.HasLogContaining("Cross Slash cast"));

            // Simulate residual cooldown below epsilon (tiny float from frame math)
            var slot0 = store.GetAbility(playerId, 0);
            slot0.CurrentCooldown = 0.00005f;
            store.SetAbility(playerId, 0, slot0);

            int logsBefore = renderer.Logs.Count;
            CreateEnemy(store, 4f, 3f);
            system.AutoCastBestSkill();

            Assert.True(renderer.Logs.Count > logsBefore, "AutoCast should fire with residual cooldown below epsilon");
        }

        [Fact]
        public void AbilityInstance_CooldownMutability()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);

            // Read initial cooldown
            float initial = inst.CurrentCooldown;
            Assert.Equal(0f, initial);

            // Mutate and verify
            inst.CurrentCooldown = 3.5f;
            Assert.Equal(3.5f, inst.CurrentCooldown);

            // Verify the mutation is independent of the original
            var inst2 = new AbilityInstance(def);
            Assert.Equal(0f, inst2.CurrentCooldown);
        }

        [Fact]
        public void AutoCast_CrossSlash_Fires()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);
            system.InitializePlayerSkills();

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

            CreateEnemy(store, 5f, 3f);
            system.AutoCastBestSkill();
            system.AutoCastBestSkill();
            system.AutoCastBestSkill();

            // Update with 6s (Cross Slash CD is 5s, should be ready again)
            system.Update(6f);

            var logsBefore = renderer.Logs.Count;
            CreateEnemy(store, 4f, 3f);
            system.AutoCastBestSkill();

            Assert.True(renderer.Logs.Count > logsBefore, "Should cast a skill after cooldown expires");
        }

        [Fact]
        public void SkillCanDamageAndKill()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();

            int enemyId = CreateEnemy(store, 5f, 1f, 10f);

            var system = new SkillSystem(store, renderer, playerId, config);
            system.InitializePlayerSkills();
            system.CastSkill("Cross Slash");

            Assert.True(renderer.HasLogContaining("Cross Slash cast"));
            Assert.True(renderer.HasLogContaining("hit"));
            float newGold = store.GetPlayerGold(playerId);
            Assert.True(newGold > 0, "Gold should increase after enemy killed");
        }

        [Fact]
        public void NoEnemies_NoCrash()
        {
            var (store, config, playerId) = CreateTestEnv();
            var renderer = new MockRenderer();
            var system = new SkillSystem(store, renderer, playerId, config);

            system.AutoCastBestSkill();
            Assert.True(true); // succeeded without exception
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TowerPlacementSystem 测试
    // ═══════════════════════════════════════════════════════════════════════════════

    public class TowerPlacementSystemTests
    {
        // ─── Bug#31: CreateEntity() == -1 时 PlaceTower 失败 ──────────────────────

        [Fact]
        public void PlaceTower_FailsWhenEntityPoolExhausted()
        {
            var store = new ComponentStore();
            var renderer = new MockRenderer();
            var system = new TowerPlacementSystem(store, renderer);

            // Exhaust entity pool
            while (true)
            {
                int id = store.CreateEntity();
                if (id == -1) break;
            }

            int result = system.PlaceTower(5, 5, "Arrow", 50f, 3, 1f, 50f);
            Assert.Equal(-1, result);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // GameConfigLoader / GameConfig 测试
    // ═══════════════════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════════════════
    // WaveSpawningSystem 测试
    // ═══════════════════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════════════════
    // 端到端：简单游戏循环
    // ═══════════════════════════════════════════════════════════════════════════════

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
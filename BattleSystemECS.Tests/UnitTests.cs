using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
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
            foreach (var log in Logs) if (log.Contains(substring)) return true;
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // ComponentStore
    // ═══════════════════════════════════════════════════════════════════════════════
    public class ComponentStoreTests
    {
        [Fact] public void NewStore_HasInitialEntities()
        {
            var store = new ComponentStore();
            Assert.True(store.NextEntityId >= 1);
        }

        [Fact] public void MAX_ENTITIES_IsReasonable()
        {
            Assert.True(ComponentStore.MAX_ENTITIES >= 1000);
        }

        [Fact] public void CreateEntity_IncrementsNextId()
        {
            var store = new ComponentStore();
            int before = store.NextEntityId;
            int id = store.CreateEntity();
            Assert.Equal(before, id);
            Assert.Equal(before + 1, store.NextEntityId);
        }

        // ─── Bug#30: DestroyEntity 必须从 ActiveTowerIds 移除 ─────────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveTowerIds()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.AddPlayer(playerId, 3f, 3f, 10f, 1);

            int towerId = store.CreateEntity();
            store.AddTower(towerId, "Arrow", 5f, 3, 1f, 1, 50f);
            store.AddPosition(towerId, 3f, 3f);

            Assert.Contains(towerId, store.ActiveTowerIds);
            store.DestroyEntity(towerId);
            Assert.DoesNotContain(towerId, store.ActiveTowerIds);
        }

        // ─── Bug#11: DestroyEntity 从 ActiveEnemyIds 移除 ───────────────────────

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

        // ─── Bug#21: GetAllActiveEnemyIds 返回防御性副本 ───────────────────────

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsDefensiveCopy()
        {
            var store = new ComponentStore();
            store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            store.AddEnemy(7f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            var active = store.GetAllActiveEnemyIds();
            int originalCount = active.Count;
            active.Clear();
            var fresh = store.GetAllActiveEnemyIds();
            Assert.Equal(originalCount, fresh.Count);
        }

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsOnlyActiveEnemies()
        {
            var store = new ComponentStore();
            int player = store.CreateEntity();
            store.AddPosition(player, 0, 0);
            int enemy = store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1);
            int neutral = store.CreateEntity();
            var active = store.GetAllActiveEnemyIds();
            Assert.Contains(enemy, active);
            Assert.DoesNotContain(player, active);
            Assert.DoesNotContain(neutral, active);
        }

        // ─── AddEnemy / CreateEntity 失败路径 ─────────────────────────────────

        [Fact]
        public void CreateEntity_Exhausted_ReturnsNegativeOne()
        {
            var store = new ComponentStore();
            int created = 0;
            while (store.CreateEntity() != -1) created++;
            Assert.True(created > 0);
            Assert.Equal(-1, store.CreateEntity());
        }

        [Fact]
        public void AddEnemy_FailsWhenPoolExhausted()
        {
            var store = new ComponentStore();
            while (store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1) != -1) { /* drain */ }
            int result = store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1);
            Assert.Equal(-1, result);
        }

        // ─── Bug#??: AddEnemy 不处理 entityId < 0 ──────────────────────────────

        [Fact]
        public void AddEnemy_DoesNotCrashOnNegativeEntityId()
        {
            var store = new ComponentStore();
            while (store.CreateEntity() != -1) { /* drain */ }
            // CreateEntity returns -1; AddEnemy must not access arrays with -1 index
            int result = store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            Assert.Equal(-1, result);
        }

        [Fact] public void PlayerHealth_ArrayAccess()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PlayerMaxHealth[id] = 200f;
            store.PlayerCurrentHealth[id] = 150f;
            Assert.Equal(200f, store.PlayerMaxHealth[id]);
            Assert.Equal(150f, store.PlayerCurrentHealth[id]);
        }

        [Fact] public void PlayerGold_ArrayAccess()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PlayerGold[id] = 100;
            Assert.Equal(100, store.GetPlayerGold(id));
        }

        [Fact] public void TotalKills_StartsAtZero()
        {
            var store = new ComponentStore();
            Assert.Equal(0, store.TotalKills);
        }

        [Fact] public void TotalKills_CanIncrement()
        {
            var store = new ComponentStore();
            store.TotalKills++;
            store.TotalKills++;
            Assert.Equal(2, store.TotalKills);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // GameplayAbility
    // ═══════════════════════════════════════════════════════════════════════════════
    public class GameplayAbilityTests
    {
        private AbilityInstance Make(float cooldown)
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.CurrentCooldown = cooldown;
            return inst;
        }

        // ─── Bug#37: CanActivate epsilon 边界 ──────────────────────────────────

        [Fact] public void CanActivate_TrueWhenCooldownZero()
            => Assert.True(Make(0f).CanActivate());

        [Fact] public void CanActivate_TrueWhenCooldownBelowEpsilon()
            => Assert.True(Make(0.00005f).CanActivate());

        [Fact] public void CanActivate_FalseWhenCooldownAboveEpsilon()
            => Assert.False(Make(0.001f).CanActivate());

        [Fact] public void CanActivate_TrueWhenCooldownAtOrBelowEpsilon()
            => Assert.True(Make(0.0001f).CanActivate());

        [Fact] public void Activate_SetsCooldownToDefinitionValue()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            inst.Activate();
            Assert.Equal(5f, inst.CurrentCooldown);
        }

        [Fact] public void AbilityInstance_CooldownMutability()
        {
            var def = new GameplayAbilityDef("Test", "desc", 5f, 0f, -1, 10f,
                AbilityActivation.Instant, 0, 0);
            var inst = new AbilityInstance(def);
            Assert.Equal(0f, inst.CurrentCooldown);
            inst.CurrentCooldown = 3.5f;
            Assert.Equal(3.5f, inst.CurrentCooldown);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // SkillSystem
    // ═══════════════════════════════════════════════════════════════════════════════
    public class SkillSystemTests
    {
        private (ComponentStore store, GameConfig config, int playerId) CreateEnv()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PlayerMaxHealth[id] = 200f;
            store.PlayerCurrentHealth[id] = 200f;
            store.PlayerAttackDamage[id] = 10f;
            store.PlayerAttackRange[id] = 3f;
            store.PositionX[id] = 5f;
            store.PositionY[id] = 0f;
            return (store, new GameConfig(), id);
        }

        private void MakeEnemy(ComponentStore store, int id, float x, float y, float hp = 10f, int gold = 10)
        {
            store.EnemyActive[id] = true;
            store.ActiveEnemyIds.Add(id);
            store.PositionX[id] = x;
            store.PositionY[id] = y;
            store.SetEnemyHealth(id, hp);
            store.EnemyGoldReward[id] = gold;
        }

        [Fact] public void NewSkillSystem_HasThreeSkills()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();
            Assert.True(r.HasLogContaining("Cross Slash"));
            Assert.True(r.HasLogContaining("Mega Explosion"));
            Assert.True(r.HasLogContaining("Sniper Shot"));
        }

        // ─── Bug#9: InitializePlayerSkills 不累计 AbilityCount ────────────────

        [Fact] public void InitializePlayerSkills_Idempotent_AbilityCount()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();
            int first = store.AbilityCount[pid];
            Assert.Equal(3, first);
            sys.InitializePlayerSkills();
            Assert.Equal(first, store.AbilityCount[pid]);
        }

        // ─── Bug#??: InitializePlayerSkills 不累计 ActiveEffectCount ───────────

        [Fact] public void InitializePlayerSkills_Idempotent_ActiveEffectCount()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();
            int first = store.GetEffectCount(pid);
            Assert.True(first > 0);
            sys.InitializePlayerSkills();
            Assert.Equal(first, store.GetEffectCount(pid));
        }

        // ─── Bug#37: AutoCastBestSkill 走 epsilon ──────────────────────────────

        [Fact] public void AutoCastBestSkill_FiresWhenCooldownBelowEpsilon()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();

            int eid = store.CreateEntity();
            MakeEnemy(store, eid, 5f, 3f);
            sys.AutoCastBestSkill();
            Assert.True(r.HasLogContaining("Cross Slash cast"));

            // Residual cooldown below epsilon
            var slot = store.GetAbility(pid, 0);
            slot.CurrentCooldown = 0.00005f;
            store.SetAbility(pid, 0, slot);
            int before = r.Logs.Count;

            int eid2 = store.CreateEntity();
            MakeEnemy(store, eid2, 4f, 3f);
            sys.AutoCastBestSkill();
            Assert.True(r.Logs.Count > before, "Should fire with residual cooldown below epsilon");
        }

        [Fact] public void AutoCastBestSkill_DoesNotFireWhenCooldownAboveEpsilon()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();

            int eid = store.CreateEntity();
            MakeEnemy(store, eid, 5f, 3f);
            sys.AutoCastBestSkill();

            var slot0 = store.GetAbility(pid, 0);
            var slot1 = store.GetAbility(pid, 1);
            var slot2 = store.GetAbility(pid, 2);
            slot0.CurrentCooldown = 1.0f;
            slot1.CurrentCooldown = 1.0f;
            slot2.CurrentCooldown = 1.0f;
            store.SetAbility(pid, 0, slot0);
            store.SetAbility(pid, 1, slot1);
            store.SetAbility(pid, 2, slot2);
            int before = r.Logs.Count;

            int eid2 = store.CreateEntity();
            MakeEnemy(store, eid2, 4f, 3f);
            sys.AutoCastBestSkill();
            Assert.Equal(before, r.Logs.Count);
        }

        [Fact] public void AutoCast_CrossSlash_Fires()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();
            int eid = store.CreateEntity();
            MakeEnemy(store, eid, 5f, 3f);
            sys.AutoCastBestSkill();
            Assert.True(r.HasLogContaining("Cross Slash cast"));
        }

        [Fact] public void Update_ReducesCooldown()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();
            int eid = store.CreateEntity();
            MakeEnemy(store, eid, 5f, 3f);
            sys.AutoCastBestSkill();
            sys.Update(6f);
            int before = r.Logs.Count;
            int eid2 = store.CreateEntity();
            MakeEnemy(store, eid2, 4f, 3f);
            sys.AutoCastBestSkill();
            Assert.True(r.Logs.Count > before);
        }

        [Fact] public void SkillCanDamageAndKill()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            int eid = store.CreateEntity();
            MakeEnemy(store, eid, 5f, 1f);
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();
            sys.CastSkill("Cross Slash");
            Assert.True(r.HasLogContaining("Cross Slash cast"));
            Assert.True(r.HasLogContaining("hit"));
            Assert.True(store.GetPlayerGold(pid) > 0);
        }

        [Fact] public void NoEnemies_NoCrash()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, pid, config);
            sys.AutoCastBestSkill();
            Assert.True(true);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // TowerPlacementSystem
    // ═══════════════════════════════════════════════════════════════════════════════
    public class TowerPlacementSystemTests
    {
        // ─── Bug#31: PlaceTower 在 CreateEntity()==-1 时失败 ──────────────────

        [Fact] public void PlaceTower_FailsWhenEntityPoolExhausted()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            while (store.CreateEntity() != -1) { /* exhaust */ }
            var sys = new TowerPlacementSystem(store, r);
            int result = sys.PlaceTower(5, 5, "Arrow", 50f, 3, 1f, 50f);
            Assert.Equal(-1, result);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // GameConfig
    // ═══════════════════════════════════════════════════════════════════════════════
    public class GameConfigLoaderTests
    {
        private GameConfig Config() => GameConfigLoader.GetDefaultConfig();

        [Fact] public void DefaultConfig_HasPlayerMaxHealth()
            => Assert.True(Config().Player.MaxHealth > 0);

        [Fact] public void DefaultConfig_HasAttackInterval()
            => Assert.True(Config().Player.AttackInterval > 0);

        [Fact] public void DefaultConfig_HasSkills()
            => Assert.NotEmpty(Config().Skills);

        [Fact] public void DefaultConfig_HasLevels()
            => Assert.NotEmpty(Config().Levels);

        [Fact] public void DefaultConfig_HasMonsterTypes()
            => Assert.NotEmpty(Config().MonsterTypes);

        [Fact] public void DefaultConfig_MonstersHaveHealth()
        {
            foreach (var m in Config().MonsterTypes)
                Assert.True(m.Health > 0, $"Monster {m.Name} must have Health > 0");
        }

        [Fact] public void DefaultConfig_PlayerHasStartingSkills()
            => Assert.NotEmpty(Config().Player.StartingSkills);
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // WaveSpawningSystem
    // ═══════════════════════════════════════════════════════════════════════════════
    public class WaveSpawningSystemTests
    {
        private (ComponentStore store, GameConfig config) Env()
        {
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            return (store, new GameConfig());
        }

        [Fact] public void NewSystem_StartsAtWaveOne()
        {
            var (store, config) = Env();
            var r = new MockRenderer();
            var sys = new WaveSpawningSystem(store, r, config);
            Assert.Equal(1, sys.GetCurrentWave());
            Assert.Equal(1, sys.GetCurrentLevel());
            Assert.Equal(0, sys.GetTotalEnemiesSpawned());
        }

        [Fact] public void FirstUpdate_SpawnsEnemies()
        {
            var (store, config) = Env();
            var r = new MockRenderer();
            var sys = new WaveSpawningSystem(store, r, config);
            sys.Update();
            Assert.True(sys.GetTotalEnemiesSpawned() > 0);
        }

        [Fact] public void BatchSize_IsFive()
        {
            var (store, config) = Env();
            var r = new MockRenderer();
            var sys = new WaveSpawningSystem(store, r, config);
            sys.Update();
            Assert.Equal(5, sys.GetTotalEnemiesSpawned());
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // Game simulation
    // ═══════════════════════════════════════════════════════════════════════════════
    public class GameSimulationTests
    {
        [Fact] public void GameLoop_RunsWithoutCrash()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.PlayerAttackDamage[pid] = 10f;
            store.PlayerAttackRange[pid] = 3f;
            store.PositionX[pid] = 5f;
            store.PositionY[pid] = 0f;

            var wave = new WaveSpawningSystem(store, r, config);
            var atk = new PlayerTowerAttackSystem(store, r, pid, config);
            var skill = new SkillSystem(store, r, pid, config);

            for (int turn = 0; turn < 10; turn++)
            {
                wave.Update();
                atk.Update();
                skill.Update(1f);
            }
            Assert.True(wave.GetTotalEnemiesSpawned() > 0);
        }
    }
}
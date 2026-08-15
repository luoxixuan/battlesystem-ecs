using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
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
            store.AddActiveEnemyId(id);
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
            MakeEnemy(store, eid, 5f, 0f, gold: 1);
            var sys = new SkillSystem(store, r, pid, config);
            sys.InitializePlayerSkills();
            sys.SetTurn(0); // required: populates _activeEnemyList before any Cast call
            sys.CastSkill("Cross Slash");
            sys.ResolveSkillDamage(); // serial-phase damage application
            store.ResolveEnemiesKilledThisFrame(); // frame-end death resolution
            Assert.True(r.HasLogContaining("Cross Slash cast"));
            Assert.True(r.HasLogContaining("queued damage"));
            Assert.True(store.GetPlayerGold(pid) > 0);
        }

    }
}

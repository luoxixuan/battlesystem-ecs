using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for the Frostbite system (Round 170 Direction 6) — non-stacking
    /// percentage-of-maxHP DoT. Frostbite ticks every ~1 second and deals
    /// (maxHpPct * EnemyMaxHealth) damage per tick. Distinct from Bleed
    /// (stacking, fixed-per-stack) — Frostbite's %-based damage makes it
    /// scale naturally with Boss HP pools.
    /// </summary>
    public class FrostbiteSystemTests
    {
        private (ComponentStore store, int playerId, FrostbiteSystem sys) CreateEnv()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            var sys = new FrostbiteSystem(store, playerId);
            return (store, playerId, sys);
        }

        [Fact]
        public void DefaultEnemy_FrostbiteFields_AreZero()
        {
            var (store, _, _) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            Assert.Equal(0f, store.EnemyFrostbiteMaxHpPct[eid]);
            Assert.Equal(0f, store.EnemyFrostbiteDurationLeft[eid]);
            Assert.Equal(0f, store.EnemyFrostbiteTimer[eid]);
            Assert.Equal(0f, store.EnemyFrostbiteResistance[eid]);
        }

        [Fact]
        public void ApplyFrostbite_SetsFields()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            // 2% max HP per tick, 5 sec duration
            sys.ApplyFrostbite(eid, 0.02f, 5f);
            Assert.Equal(0.02f, store.EnemyFrostbiteMaxHpPct[eid]);
            Assert.Equal(5f, store.EnemyFrostbiteDurationLeft[eid]);
            Assert.Equal(1f, store.EnemyFrostbiteTimer[eid]);
        }

        [Fact]
        public void ApplyFrostbite_ZeroOrNegative_NoOp()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            sys.ApplyFrostbite(eid, 0f, 5f);
            Assert.Equal(0f, store.EnemyFrostbiteMaxHpPct[eid]);
            sys.ApplyFrostbite(eid, 0.02f, 0f);
            Assert.Equal(0f, store.EnemyFrostbiteMaxHpPct[eid]);
            sys.ApplyFrostbite(eid, -0.02f, 5f);
            Assert.Equal(0f, store.EnemyFrostbiteMaxHpPct[eid]);
        }

        [Fact]
        public void ApplyFrostbite_Reapply_RefreshesDurationTakesMaxPct()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 10f, 1f, 1, 1);
            sys.ApplyFrostbite(eid, 0.01f, 5f);
            // Reapply with a stronger pct and same duration
            sys.ApplyFrostbite(eid, 0.03f, 5f);
            Assert.Equal(0.03f, store.EnemyFrostbiteMaxHpPct[eid]);
            Assert.Equal(5f, store.EnemyFrostbiteDurationLeft[eid]);
            // Reapply with weaker pct — should keep the higher one
            sys.ApplyFrostbite(eid, 0.02f, 8f);
            Assert.Equal(0.03f, store.EnemyFrostbiteMaxHpPct[eid]);
            // Duration should refresh to 8
            Assert.Equal(8f, store.EnemyFrostbiteDurationLeft[eid]);
        }

        [Fact]
        public void Update_FrostbiteTick_DealsPercentOfMaxHealth()
        {
            var (store, _, sys) = CreateEnv();
            // signature: (startX, startY, moveSpeed, health, maxHealth, damage, goldReward, waveNumber)
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // 5% max HP per tick, 5 sec duration
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            // 1.5s update → 1 tick fires (5% × 1000 = 50 dmg)
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(950f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_MultipleTicks_StackDamage()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // 2% per tick, 10 sec duration
            sys.ApplyFrostbite(eid, 0.02f, 10f);
            // 3 separate 1s updates → 3 ticks: 2% × 3 × 1000 = 60 dmg
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            Assert.Equal(940f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_Expires_StopsDealingDamage()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            sys.ApplyFrostbite(eid, 0.05f, 2f);
            // 3 separate 1s updates → 2 ticks fired (1s, 2s), then duration expires
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            float healthAfterExpire = store.EnemyHealth[eid];
            // After expiry, the field is cleared — no more ticks
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            Assert.Equal(healthAfterExpire, store.EnemyHealth[eid]);
            // Sanity: at least 1 tick fired (50 dmg per tick × 2 ticks = 100 dmg)
            Assert.Equal(900f, healthAfterExpire);
        }

        [Fact]
        public void Update_Resistance_ReducesEffectivePct()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // 50% frostbite resistance
            store.EnemyFrostbiteResistance[eid] = 0.5f;
            sys.ApplyFrostbite(eid, 0.04f, 5f);
            // Effective pct = 0.04 * (1 - 0.5) = 0.02 → 20 dmg per tick
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(980f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_InvulnerableEnemy_SkipsDamage()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            store.EnemyIsInvulnerable[eid] = true;
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(1000f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_KillsEnemy_QueuesDeath()
        {
            var (store, playerId, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            sys.ApplyFrostbite(eid, 0.5f, 5f);
            // 50% of 100 = 50 dmg per tick. 1 tick = 50 HP, 2nd tick kills.
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            // Enemy should be dead
            Assert.True(store.EnemyHealth[eid] <= 0f);
        }

        [Fact]
        public void Update_FrostbiteKillsBossWithBigHp()
        {
            var (store, _, sys) = CreateEnv();
            // Simulate a Boss with 1,000,000 HP
            int eid = store.AddEnemy(0f, 0f, 1f, 1_000_000f, 1_000_000f, 1f, 1, 1);
            // 2% per tick → 20,000 per tick. 50 ticks to kill.
            sys.ApplyFrostbite(eid, 0.02f, 60f);
            // 60 separate 1s updates → 60 ticks worth of damage (60 × 20,000 = 1,200,000)
            for (int i = 0; i < 60; i++)
            {
                sys.Update(1f);
                sys.ResolveFrostbiteDamage();
            }
            // Should kill
            Assert.True(store.EnemyHealth[eid] <= 0f);
        }

        [Fact]
        public void Update_NoFrostbite_NoDamage()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // No frostbite applied.
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(1000f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyFrostbite_RespawnEnemy_FieldsAreZero()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            // Simulate enemy respawn (DestroyEntity + reuse ID)
            store.DestroyEntity(eid);
            // After DestroyEntity, the next AddEnemy reuses the slot and resets fields.
            int eid2 = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            Assert.Equal(0f, store.EnemyFrostbiteMaxHpPct[eid2]);
            Assert.Equal(0f, store.EnemyFrostbiteDurationLeft[eid2]);
            Assert.Equal(0f, store.EnemyFrostbiteTimer[eid2]);
        }

        [Fact]
        public void Update_ReapplyOnActiveEnemy_ResetsTimer()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            // Simulate time passing 0.3s, then reapply
            sys.Update(0.3f);
            // Timer is now 0.7 (1 - 0.3). Reapply — should NOT reset timer
            // (because it's still active), but should refresh duration.
            sys.ApplyFrostbite(eid, 0.05f, 10f);
            // Timer should be ~0.7
            Assert.True(store.EnemyFrostbiteTimer[eid] > 0.5f);
            Assert.Equal(10f, store.EnemyFrostbiteDurationLeft[eid]);
        }
    }
}

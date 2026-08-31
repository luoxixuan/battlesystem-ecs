using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for the Frostbite system (Round 170 Direction 6) — non-stacking
    /// percentage-of-maxHP DoT. Frostbite ticks every ~1 second and deals
    /// (maxHpPct * EnemyMaxHealth) damage per tick. Distinct from Bleed
    /// (stacking, fixed-per-stack) — Frostbite's %-based damage makes it
    /// scale naturally with Boss HP pools.
    /// </summary>
    public class FrostbiteSystemTests : BattleTestBase
    {
        private (FrostbiteSystem sys, int playerId) CreateEnv()
        {
            int playerId = Store.CreateEntity();
            Store.AddPlayer(playerId, 3f, 1f, 1f, 1);
            var sys = new FrostbiteSystem(Store, playerId);
            return (sys, playerId);
        }

        [Fact]
        public void DefaultEnemy_FrostbiteFields_AreZero()
        {
            var (_, _) = CreateEnv();
            int eid = Enemy();
            Assert.Equal(0f, Store.EnemyFrostbiteMaxHpPct[eid]);
            Assert.Equal(0f, Store.EnemyFrostbiteDurationLeft[eid]);
            Assert.Equal(0f, Store.EnemyFrostbiteTimer[eid]);
            Assert.Equal(0f, Store.EnemyFrostbiteResistance[eid]);
        }

        [Fact]
        public void ApplyFrostbite_SetsFields()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy();
            // 2% max HP per tick, 5 sec duration
            sys.ApplyFrostbite(eid, 0.02f, 5f);
            Assert.Equal(0.02f, Store.EnemyFrostbiteMaxHpPct[eid]);
            Assert.Equal(5f, Store.EnemyFrostbiteDurationLeft[eid]);
            Assert.Equal(1f, Store.EnemyFrostbiteTimer[eid]);
        }

        [Fact]
        public void ApplyFrostbite_ZeroOrNegative_NoOp()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy();
            sys.ApplyFrostbite(eid, 0f, 5f);
            Assert.Equal(0f, Store.EnemyFrostbiteMaxHpPct[eid]);
            sys.ApplyFrostbite(eid, 0.02f, 0f);
            Assert.Equal(0f, Store.EnemyFrostbiteMaxHpPct[eid]);
            sys.ApplyFrostbite(eid, -0.02f, 5f);
            Assert.Equal(0f, Store.EnemyFrostbiteMaxHpPct[eid]);
        }

        [Fact]
        public void ApplyFrostbite_Reapply_RefreshesDurationTakesMaxPct()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy();
            sys.ApplyFrostbite(eid, 0.01f, 5f);
            // Reapply with a stronger pct and same duration
            sys.ApplyFrostbite(eid, 0.03f, 5f);
            Assert.Equal(0.03f, Store.EnemyFrostbiteMaxHpPct[eid]);
            Assert.Equal(5f, Store.EnemyFrostbiteDurationLeft[eid]);
            // Reapply with weaker pct — should keep the higher one
            sys.ApplyFrostbite(eid, 0.02f, 8f);
            Assert.Equal(0.03f, Store.EnemyFrostbiteMaxHpPct[eid]);
            // Duration should refresh to 8
            Assert.Equal(8f, Store.EnemyFrostbiteDurationLeft[eid]);
        }

        [Fact]
        public void Update_FrostbiteTick_DealsPercentOfMaxHealth()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            // 5% max HP per tick, 5 sec duration
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            // 1.5s update → 1 tick fires (5% × 1000 = 50 dmg)
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(950f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_MultipleTicks_StackDamage()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            // 2% per tick, 10 sec duration
            sys.ApplyFrostbite(eid, 0.02f, 10f);
            // 3 separate 1s updates → 3 ticks: 2% × 3 × 1000 = 60 dmg
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            Assert.Equal(940f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_Expires_StopsDealingDamage()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            sys.ApplyFrostbite(eid, 0.05f, 2f);
            // 3 separate 1s updates → 2 ticks fired (1s, 2s), then duration expires
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            float healthAfterExpire = Store.EnemyHealth[eid];
            // After expiry, the field is cleared — no more ticks
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            Assert.Equal(healthAfterExpire, Store.EnemyHealth[eid]);
            // Sanity: at least 1 tick fired (50 dmg per tick × 2 ticks = 100 dmg)
            Assert.Equal(900f, healthAfterExpire);
        }

        [Fact]
        public void Update_Resistance_ReducesEffectivePct()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            // 50% frostbite resistance
            Store.EnemyFrostbiteResistance[eid] = 0.5f;
            sys.ApplyFrostbite(eid, 0.04f, 5f);
            // Effective pct = 0.04 * (1 - 0.5) = 0.02 → 20 dmg per tick
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(980f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_InvulnerableEnemy_SkipsDamage()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            Store.EnemyIsInvulnerable[eid] = true;
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(1000f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void Update_KillsEnemy_QueuesDeath()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy();
            sys.ApplyFrostbite(eid, 0.5f, 5f);
            // 50% of 100 = 50 dmg per tick. 1 tick = 50 HP, 2nd tick kills.
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            sys.Update(1f); sys.ResolveFrostbiteDamage();
            // Enemy should be dead
            Assert.True(Store.EnemyHealth[eid] <= 0f);
        }

        [Fact]
        public void Update_FrostbiteKillsBossWithBigHp()
        {
            var (sys, _) = CreateEnv();
            // Simulate a Boss with 1,000,000 HP
            int eid = Enemy(e => { e.Health = 1_000_000f; });
            // 2% per tick → 20,000 per tick. 50 ticks to kill.
            sys.ApplyFrostbite(eid, 0.02f, 60f);
            // 60 separate 1s updates → 60 ticks worth of damage (60 × 20,000 = 1,200,000)
            for (int i = 0; i < 60; i++)
            {
                sys.Update(1f);
                sys.ResolveFrostbiteDamage();
            }
            // Should kill
            Assert.True(Store.EnemyHealth[eid] <= 0f);
        }

        [Fact]
        public void Update_NoFrostbite_NoDamage()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            // No frostbite applied.
            sys.Update(1.5f);
            sys.ResolveFrostbiteDamage();
            Assert.Equal(1000f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void ApplyFrostbite_RespawnEnemy_FieldsAreZero()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            // Simulate enemy respawn (DestroyEntity + reuse ID)
            Store.DestroyEntity(eid);
            // After DestroyEntity, the next AddEnemy reuses the slot and resets fields.
            int eid2 = Enemy(e => { e.Health = 1000f; });
            Assert.Equal(0f, Store.EnemyFrostbiteMaxHpPct[eid2]);
            Assert.Equal(0f, Store.EnemyFrostbiteDurationLeft[eid2]);
            Assert.Equal(0f, Store.EnemyFrostbiteTimer[eid2]);
        }

        [Fact]
        public void Update_ReapplyOnActiveEnemy_ResetsTimer()
        {
            var (sys, _) = CreateEnv();
            int eid = Enemy(e => { e.Health = 1000f; });
            sys.ApplyFrostbite(eid, 0.05f, 5f);
            // Simulate time passing 0.3s, then reapply
            sys.Update(0.3f);
            // Timer is now 0.7 (1 - 0.3). Reapply — should NOT reset timer
            // (because it's still active), but should refresh duration.
            sys.ApplyFrostbite(eid, 0.05f, 10f);
            // 活跃期间重贴不重置计时器：1 - 0.3 = 0.7 精确保留。
            Assert.Equal(0.7f, Store.EnemyFrostbiteTimer[eid], 3);
            Assert.Equal(10f, Store.EnemyFrostbiteDurationLeft[eid]);
        }
    }
}

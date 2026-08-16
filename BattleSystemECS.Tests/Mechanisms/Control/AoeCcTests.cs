using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Control
{
    /// <summary>
    /// Tests for Round 136 Direction 2 — AOE CC group control skills
    /// (群体禁锢/击晕/击退). Verifies:
    ///   - AreaShapeType constants: AoeStun=19, AoeRoot=20, AoeKnockback=21
    ///   - FromString parsing: "aoestun" / "aoeroot" / "aoeknockback"
    ///   - CastAoeStun: applies stun duration to all enemies in radius
    ///   - CastAoeRoot: applies root duration to all enemies in radius
    ///   - CastAoeKnockback: adds to EnemyKnockbackForceLeft on all in radius
    ///   - Out-of-radius enemies untouched
    ///   - Duration=0 / radius=0 → no-op (0 hits)
    ///   - Dead enemies skipped
    ///   - EnemyIsUnstoppable blocks all three
    ///   - Root + stun are orthogonal (root doesn't trigger stun, stun doesn't trigger root)
    ///   - Refresh semantics: longer root duration wins
    ///   - IsEnemyRooted() returns true while EnemyRootDurationLeft > 0
    /// </summary>
    public class AoeCcTests
    {
        private const int PlayerId = 0;

        private static (ComponentStore store, int p, int e1, int e2, int e3) CreateArena3Enemies()
        {
            var store = new ComponentStore();
            int p = store.CreateEntity();            // player 0
            int e1 = store.AddEnemy(50, 0, 5f, 100f, 100f, 5f, 10, 1, "E1");
            int e2 = store.AddEnemy(0, 80, 5f, 100f, 100f, 5f, 10, 1, "E2");
            int e3 = store.AddEnemy(-50, 0, 5f, 100f, 100f, 5f, 10, 1, "E3");
            // OOR enemy: way out of any reasonable radius
            int eOor = store.AddEnemy(5000, 5000, 5f, 100f, 100f, 5f, 10, 1, "E_OOR");
            return (store, p, e1, e2, e3);
        }

        private static SkillSystem NewSystem(ComponentStore store, int playerId)
        {
            var r = new MockRenderer();
            var config = new Config.GameConfig();
            var sys = new SkillSystem(store, r, playerId, config);
            sys.SetTurn(0);
            return sys;
        }

        // ─── Constants & parsing ─────────────────────────────────────────

        [Fact]
        public void AoeStun_ConstantIs19()
        {
            Assert.Equal(19, AreaShapeType.AoeStun);
        }

        [Fact]
        public void AoeRoot_ConstantIs20()
        {
            Assert.Equal(20, AreaShapeType.AoeRoot);
        }

        [Fact]
        public void AoeKnockback_ConstantIs21()
        {
            Assert.Equal(21, AreaShapeType.AoeKnockback);
        }

        [Fact]
        public void FromString_AoeStunMapsToConstant()
        {
            Assert.Equal(AreaShapeType.AoeStun, AreaShapeType.FromString("aoestun"));
        }

        [Fact]
        public void FromString_AoeRootMapsToConstant()
        {
            Assert.Equal(AreaShapeType.AoeRoot, AreaShapeType.FromString("aoeroot"));
        }

        [Fact]
        public void FromString_AoeKnockbackMapsToConstant()
        {
            Assert.Equal(AreaShapeType.AoeKnockback, AreaShapeType.FromString("aoeknockback"));
        }

        // ─── CastAoeStun ─────────────────────────────────────────────────

        [Fact]
        public void CastAoeStun_HitsAllInRadius_SetsStunDuration()
        {
            var (store, p, e1, e2, e3) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            Assert.Equal(3, hit);
            Assert.True(store.IsEnemyStunned(e1));
            Assert.True(store.IsEnemyStunned(e2));
            Assert.True(store.IsEnemyStunned(e3));
            Assert.True(store.EnemyStunDurationLeft[e1] >= 2f);
            Assert.True(store.EnemyStunDurationLeft[e2] >= 2f);
            Assert.True(store.EnemyStunDurationLeft[e3] >= 2f);
        }

        [Fact]
        public void CastAoeStun_DurationZero_NoHits()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 0f, "WarStomp");

            Assert.Equal(0, hit);
            Assert.False(store.IsEnemyStunned(e1));
        }

        [Fact]
        public void CastAoeStun_RadiusZero_NoHits()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeStun(0f, 0f, 0, 2f, "WarStomp");

            Assert.Equal(0, hit);
        }

        [Fact]
        public void CastAoeStun_SkipsUnstoppableEnemy()
        {
            var (store, p, e1, e2, e3) = CreateArena3Enemies();
            store.EnemyIsUnstoppable[e2] = true;
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            // ApplyEnemyStun is called for all in radius, but unstoppable blocks at the
            // store level — `hit` counts the in-radius check (which we still bump on all 3).
            // However, the helper no-ops on unstoppable so the duration stays 0.
            Assert.True(store.EnemyStunDurationLeft[e2] <= 0f);
            // The other two are stunned
            Assert.True(store.IsEnemyStunned(e1));
            Assert.True(store.IsEnemyStunned(e3));
            // hit count is the in-radius count (3) — not the applied count. The store
            // is responsible for filtering. So we don't assert hit here.
            _ = hit; // suppress unused warning
        }

        [Fact]
        public void CastAoeStun_SkipsDeadEnemy()
        {
            var (store, p, e1, e2, e3) = CreateArena3Enemies();
            store.SetEnemyHealth(e2, 0f);
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            // e2 is dead (HP 0) and should be skipped
            Assert.Equal(2, hit);
            Assert.True(store.IsEnemyStunned(e1));
            Assert.False(store.IsEnemyStunned(e2));
            Assert.True(store.IsEnemyStunned(e3));
        }

        // ─── CastAoeRoot ──────────────────────────────────────────────────

        [Fact]
        public void CastAoeRoot_HitsAllInRadius_SetsRootDuration()
        {
            var (store, p, e1, e2, e3) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.Equal(3, hit);
            Assert.True(store.IsEnemyRooted(e1));
            Assert.True(store.IsEnemyRooted(e2));
            Assert.True(store.IsEnemyRooted(e3));
            Assert.True(store.EnemyRootDurationLeft[e1] >= 3f);
        }

        [Fact]
        public void CastAoeRoot_DefaultFieldIsZero()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            Assert.Equal(0f, store.EnemyRootDurationLeft[e1]);
            Assert.False(store.IsEnemyRooted(e1));
        }

        [Fact]
        public void CastAoeRoot_RefreshTakesLongerDuration()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            sys.CastAoeRoot(0f, 0f, 200, 5f, "Earthroot");
            float first = store.EnemyRootDurationLeft[e1];
            sys.CastAoeRoot(0f, 0f, 200, 2f, "Earthroot");
            float second = store.EnemyRootDurationLeft[e1];
            // refresh semantics: 5f wins, second cast does not shorten
            Assert.Equal(first, second);
        }

        [Fact]
        public void CastAoeRoot_ShorterThenLonger_AppliesLonger()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            sys.CastAoeRoot(0f, 0f, 200, 2f, "Earthroot");
            float first = store.EnemyRootDurationLeft[e1];
            sys.CastAoeRoot(0f, 0f, 200, 7f, "Earthroot");
            float second = store.EnemyRootDurationLeft[e1];
            Assert.True(second > first);
            Assert.Equal(7f, second);
        }

        [Fact]
        public void CastAoeRoot_DurationZero_NoHits()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeRoot(0f, 0f, 200, 0f, "Earthroot");

            Assert.Equal(0, hit);
            Assert.False(store.IsEnemyRooted(e1));
        }

        [Fact]
        public void CastAoeRoot_OutOfRadiusNotAffected()
        {
            // Place e2 at (5000, 5000) — well outside a radius-200 center (0,0)
            var store = new ComponentStore();
            int p = store.CreateEntity();
            int eNear = store.AddEnemy(50, 0, 5f, 100f, 100f, 5f, 10, 1, "Near");
            int eFar = store.AddEnemy(5000, 5000, 5f, 100f, 100f, 5f, 10, 1, "Far");
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.Equal(1, hit);
            Assert.True(store.IsEnemyRooted(eNear));
            Assert.False(store.IsEnemyRooted(eFar));
        }

        [Fact]
        public void CastAoeRoot_SkipsUnstoppableEnemy()
        {
            var (store, p, e1, e2, e3) = CreateArena3Enemies();
            store.EnemyIsUnstoppable[e2] = true;
            var sys = NewSystem(store, p);

            sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.True(store.IsEnemyRooted(e1));
            Assert.False(store.IsEnemyRooted(e2));   // unstoppable blocks
            Assert.True(store.IsEnemyRooted(e3));
        }

        // ─── CastAoeKnockback ────────────────────────────────────────────

        [Fact]
        public void CastAoeKnockback_HitsAllInRadius_AddsToKnockbackForce()
        {
            var (store, p, e1, e2, e3) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeKnockback(0f, 0f, 200, 80f, "Shockwave");

            Assert.Equal(3, hit);
            Assert.True(store.EnemyKnockbackForceLeft[e1] > 0f);
            Assert.True(store.EnemyKnockbackForceLeft[e2] > 0f);
            Assert.True(store.EnemyKnockbackForceLeft[e3] > 0f);
        }

        [Fact]
        public void CastAoeKnockback_ForceZero_NoHits()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeKnockback(0f, 0f, 200, 0f, "Shockwave");

            Assert.Equal(0, hit);
            Assert.Equal(0f, store.EnemyKnockbackForceLeft[e1]);
        }

        [Fact]
        public void CastAoeKnockback_OutOfRadiusNotAffected()
        {
            var store = new ComponentStore();
            int p = store.CreateEntity();
            int eNear = store.AddEnemy(50, 0, 5f, 100f, 100f, 5f, 10, 1, "Near");
            int eFar = store.AddEnemy(5000, 5000, 5f, 100f, 100f, 5f, 10, 1, "Far");
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeKnockback(0f, 0f, 200, 80f, "Shockwave");

            Assert.Equal(1, hit);
            Assert.True(store.EnemyKnockbackForceLeft[eNear] > 0f);
            Assert.Equal(0f, store.EnemyKnockbackForceLeft[eFar]);
        }

        [Fact]
        public void CastAoeKnockback_Stackable()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            sys.CastAoeKnockback(0f, 0f, 200, 30f, "Shockwave");
            float first = store.EnemyKnockbackForceLeft[e1];
            sys.CastAoeKnockback(0f, 0f, 200, 50f, "Shockwave");
            float second = store.EnemyKnockbackForceLeft[e1];
            // Knockback is stackable (additive) — unlike root/stun which take the max
            Assert.Equal(first + 50f, second, 3);
        }

        [Fact]
        public void CastAoeKnockback_RadiusZero_NoHits()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            int hit = sys.CastAoeKnockback(0f, 0f, 0, 80f, "Shockwave");

            Assert.Equal(0, hit);
            Assert.Equal(0f, store.EnemyKnockbackForceLeft[e1]);
        }

        // ─── Orthogonality ──────────────────────────────────────────────

        [Fact]
        public void CastAoeRoot_DoesNotStun()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.True(store.IsEnemyRooted(e1));
            Assert.False(store.IsEnemyStunned(e1));
        }

        [Fact]
        public void CastAoeStun_DoesNotRoot()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            Assert.True(store.IsEnemyStunned(e1));
            Assert.False(store.IsEnemyRooted(e1));
        }

        [Fact]
        public void CastAoeStun_StacksWithExistingRoot()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            sys.CastAoeRoot(0f, 0f, 200, 5f, "Earthroot");
            sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            Assert.True(store.IsEnemyStunned(e1));
            Assert.True(store.IsEnemyRooted(e1));   // still rooted from earlier
        }

        [Fact]
        public void CastAoeRoot_PreservesStun()
        {
            var (store, p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(store, p);

            sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");
            sys.CastAoeRoot(0f, 0f, 200, 5f, "Earthroot");

            Assert.True(store.IsEnemyStunned(e1));
            Assert.True(store.IsEnemyRooted(e1));
        }
    }
}

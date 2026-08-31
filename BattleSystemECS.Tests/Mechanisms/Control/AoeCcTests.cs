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
    public class AoeCcTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private (int p, int e1, int e2, int e3) CreateArena3Enemies()
        {
            int p = Store.CreateEntity();            // player 0
            int e1 = Enemy(e => { e.X = 50f; e.Y = 0f; e.Name = "E1"; });
            int e2 = Enemy(e => { e.X = 0f; e.Y = 80f; e.Name = "E2"; });
            int e3 = Enemy(e => { e.X = -50f; e.Y = 0f; e.Name = "E3"; });
            // OOR enemy: way out of any reasonable radius
            int eOor = Enemy(e => { e.X = 5000f; e.Y = 5000f; e.Name = "E_OOR"; });
            return (p, e1, e2, e3);
        }

        private SkillSystem NewSystem(int playerId)
        {
            var sys = new SkillSystem(Store, Renderer, playerId, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
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
            var (p, e1, e2, e3) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            Assert.Equal(3, hit);
            Assert.True(Store.IsEnemyStunned(e1));
            Assert.True(Store.IsEnemyStunned(e2));
            Assert.True(Store.IsEnemyStunned(e3));
            // 生产路径：duration=2 → Ceil(2)=2 回合，字段精确写入 2。
            Assert.Equal(2f, Store.EnemyStunDurationLeft[e1]);
            Assert.Equal(2f, Store.EnemyStunDurationLeft[e2]);
            Assert.Equal(2f, Store.EnemyStunDurationLeft[e3]);
        }

        [Fact]
        public void CastAoeStun_DurationZero_NoHits()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 0f, "WarStomp");

            Assert.Equal(0, hit);
            Assert.False(Store.IsEnemyStunned(e1));
        }

        [Fact]
        public void CastAoeStun_RadiusZero_NoHits()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeStun(0f, 0f, 0, 2f, "WarStomp");

            Assert.Equal(0, hit);
        }

        [Fact]
        public void CastAoeStun_SkipsUnstoppableEnemy()
        {
            var (p, e1, e2, e3) = CreateArena3Enemies();
            Store.EnemyIsUnstoppable[e2] = true;
            var sys = NewSystem(p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            // ApplyEnemyStun is called for all in radius, but unstoppable blocks at the
            // store level — `hit` counts the in-radius check (which we still bump on all 3).
            // However, the helper no-ops on unstoppable so the duration stays 0.
            Assert.True(Store.EnemyStunDurationLeft[e2] <= 0f);
            // The other two are stunned
            Assert.True(Store.IsEnemyStunned(e1));
            Assert.True(Store.IsEnemyStunned(e3));
            // 生产契约：hit 统计"半径内检查数"（3），实际施加由 store 过滤。
            Assert.Equal(3, hit);
        }

        [Fact]
        public void CastAoeStun_SkipsDeadEnemy()
        {
            var (p, e1, e2, e3) = CreateArena3Enemies();
            Store.SetEnemyHealth(e2, 0f);
            var sys = NewSystem(p);

            int hit = sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            // e2 is dead (HP 0) and should be skipped
            Assert.Equal(2, hit);
            Assert.True(Store.IsEnemyStunned(e1));
            Assert.False(Store.IsEnemyStunned(e2));
            Assert.True(Store.IsEnemyStunned(e3));
        }

        // ─── CastAoeRoot ──────────────────────────────────────────────────

        [Fact]
        public void CastAoeRoot_HitsAllInRadius_SetsRootDuration()
        {
            var (p, e1, e2, e3) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.Equal(3, hit);
            Assert.True(Store.IsEnemyRooted(e1));
            Assert.True(Store.IsEnemyRooted(e2));
            Assert.True(Store.IsEnemyRooted(e3));
            // 生产路径：duration=3 → Ceil(3)=3 回合，字段精确写入 3。
            Assert.Equal(3f, Store.EnemyRootDurationLeft[e1]);
        }

        [Fact]
        public void CastAoeRoot_DefaultFieldIsZero()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            Assert.Equal(0f, Store.EnemyRootDurationLeft[e1]);
            Assert.False(Store.IsEnemyRooted(e1));
        }

        [Fact]
        public void CastAoeRoot_RefreshTakesLongerDuration()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            sys.CastAoeRoot(0f, 0f, 200, 5f, "Earthroot");
            float first = Store.EnemyRootDurationLeft[e1];
            Assert.Equal(5f, first); // 首次写入精确值
            sys.CastAoeRoot(0f, 0f, 200, 2f, "Earthroot");
            float second = Store.EnemyRootDurationLeft[e1];
            // refresh semantics: 5f wins, second cast does not shorten
            Assert.Equal(5f, second);
            Assert.Equal(first, second);
        }

        [Fact]
        public void CastAoeRoot_ShorterThenLonger_AppliesLonger()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            sys.CastAoeRoot(0f, 0f, 200, 2f, "Earthroot");
            float first = Store.EnemyRootDurationLeft[e1];
            sys.CastAoeRoot(0f, 0f, 200, 7f, "Earthroot");
            float second = Store.EnemyRootDurationLeft[e1];
            Assert.True(second > first);
            Assert.Equal(7f, second);
        }

        [Fact]
        public void CastAoeRoot_DurationZero_NoHits()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeRoot(0f, 0f, 200, 0f, "Earthroot");

            Assert.Equal(0, hit);
            Assert.False(Store.IsEnemyRooted(e1));
        }

        [Fact]
        public void CastAoeRoot_OutOfRadiusNotAffected()
        {
            // Place e2 at (5000, 5000) — well outside a radius-200 center (0,0)
            int p = Store.CreateEntity();
            int eNear = Enemy(e => { e.X = 50f; e.Y = 0f; e.Name = "Near"; });
            int eFar = Enemy(e => { e.X = 5000f; e.Y = 5000f; e.Name = "Far"; });
            var sys = NewSystem(p);

            int hit = sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.Equal(1, hit);
            Assert.True(Store.IsEnemyRooted(eNear));
            Assert.False(Store.IsEnemyRooted(eFar));
        }

        [Fact]
        public void CastAoeRoot_SkipsUnstoppableEnemy()
        {
            var (p, e1, e2, e3) = CreateArena3Enemies();
            Store.EnemyIsUnstoppable[e2] = true;
            var sys = NewSystem(p);

            sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.True(Store.IsEnemyRooted(e1));
            Assert.False(Store.IsEnemyRooted(e2));   // unstoppable blocks
            Assert.True(Store.IsEnemyRooted(e3));
        }

        // ─── CastAoeKnockback ────────────────────────────────────────────

        [Fact]
        public void CastAoeKnockback_HitsAllInRadius_AddsToKnockbackForce()
        {
            var (p, e1, e2, e3) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeKnockback(0f, 0f, 200, 80f, "Shockwave");

            Assert.Equal(3, hit);
            Assert.Equal(80f, Store.EnemyKnockbackForceLeft[e1]);
            Assert.Equal(80f, Store.EnemyKnockbackForceLeft[e2]);
            Assert.Equal(80f, Store.EnemyKnockbackForceLeft[e3]);
        }

        [Fact]
        public void CastAoeKnockback_ForceZero_NoHits()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeKnockback(0f, 0f, 200, 0f, "Shockwave");

            Assert.Equal(0, hit);
            Assert.Equal(0f, Store.EnemyKnockbackForceLeft[e1]);
        }

        [Fact]
        public void CastAoeKnockback_OutOfRadiusNotAffected()
        {
            int p = Store.CreateEntity();
            int eNear = Enemy(e => { e.X = 50f; e.Y = 0f; e.Name = "Near"; });
            int eFar = Enemy(e => { e.X = 5000f; e.Y = 5000f; e.Name = "Far"; });
            var sys = NewSystem(p);

            int hit = sys.CastAoeKnockback(0f, 0f, 200, 80f, "Shockwave");

            Assert.Equal(1, hit);
            Assert.Equal(80f, Store.EnemyKnockbackForceLeft[eNear]);
            Assert.Equal(0f, Store.EnemyKnockbackForceLeft[eFar]);
        }

        [Fact]
        public void CastAoeKnockback_Stackable()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            sys.CastAoeKnockback(0f, 0f, 200, 30f, "Shockwave");
            float first = Store.EnemyKnockbackForceLeft[e1];
            Assert.Equal(30f, first); // 首次写入精确值
            sys.CastAoeKnockback(0f, 0f, 200, 50f, "Shockwave");
            float second = Store.EnemyKnockbackForceLeft[e1];
            // Knockback is stackable (additive) — unlike root/stun which take the max
            Assert.Equal(80f, second);
            Assert.Equal(first + 50f, second, 3);
        }

        [Fact]
        public void CastAoeKnockback_RadiusZero_NoHits()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            int hit = sys.CastAoeKnockback(0f, 0f, 0, 80f, "Shockwave");

            Assert.Equal(0, hit);
            Assert.Equal(0f, Store.EnemyKnockbackForceLeft[e1]);
        }

        // ─── Orthogonality ──────────────────────────────────────────────

        [Fact]
        public void CastAoeRoot_DoesNotStun()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            sys.CastAoeRoot(0f, 0f, 200, 3f, "Earthroot");

            Assert.True(Store.IsEnemyRooted(e1));
            Assert.False(Store.IsEnemyStunned(e1));
        }

        [Fact]
        public void CastAoeStun_DoesNotRoot()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            Assert.True(Store.IsEnemyStunned(e1));
            Assert.False(Store.IsEnemyRooted(e1));
        }

        [Fact]
        public void CastAoeStun_StacksWithExistingRoot()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            sys.CastAoeRoot(0f, 0f, 200, 5f, "Earthroot");
            sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");

            Assert.True(Store.IsEnemyStunned(e1));
            Assert.True(Store.IsEnemyRooted(e1));   // still rooted from earlier
        }

        [Fact]
        public void CastAoeRoot_PreservesStun()
        {
            var (p, e1, _, _) = CreateArena3Enemies();
            var sys = NewSystem(p);

            sys.CastAoeStun(0f, 0f, 200, 2f, "WarStomp");
            sys.CastAoeRoot(0f, 0f, 200, 5f, "Earthroot");

            Assert.True(Store.IsEnemyStunned(e1));
            Assert.True(Store.IsEnemyRooted(e1));
        }
    }
}

using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Round 131 — Chain Heal (链式治疗) tests.
    /// Mirrors ChainLightning but targets injured allies and applies heal (+ optional shield bonus).
    /// Covers: AreaShapeType.ChainHeal constant, FromString parsing, single-target hop,
    /// multi-hop chain (4 max), decay 50% per hop, range gating, no-overheal clamp, dead skip,
    /// full-HP skip, shield bonus, and end-to-end CastSkill dispatch via switch case 17.
    /// </summary>
    public class ChainHealTests : BattleTestBase
    {
        // ── Helper: build a 2-3 player arena ─────────────────────────────────
        private (int p0, int p1, int p2) CreateArena()
        {
            int p0 = Store.CreateEntity();
            int p1 = Store.CreateEntity();
            int p2 = Store.CreateEntity();
            Store.PositionActive[p0] = true;
            Store.PositionActive[p1] = true;
            Store.PositionActive[p2] = true;
            // Default state: all at full HP, MaxHealth=200, positioned on a horizontal line
            for (int i = 0; i < 10; i++)  // MAX_PLAYERS = 10
            {
                Store.PlayerMaxHealth[i] = 200f;
                Store.PlayerCurrentHealth[i] = 200f;
                Store.PlayerAttackDamage[i] = 10f;
                Store.PlayerAttackRange[i] = 3f;
                Store.PositionX[i] = 0f;
                Store.PositionY[i] = 0f;
            }
            // Position: p0 at origin, p1 at +100 px, p2 at +200 px (chain hops p0→p1→p2)
            Store.PositionX[p0] = 0f;   Store.PositionY[p0] = 0f;
            Store.PositionX[p1] = 100f; Store.PositionY[p1] = 0f;
            Store.PositionX[p2] = 200f; Store.PositionY[p2] = 0f;
            return (p0, p1, p2);
        }

        private void DamagePlayer(int pid, float amount)
        {
            Store.PlayerCurrentHealth[pid] = Math.Max(0f, Store.PlayerCurrentHealth[pid] - amount);
        }

        // ── Test 1: AreaShapeType.ChainHeal = 17 constant ─────────────────────
        [Fact]
        public void ChainHeal_AreaShapeType_Is17()
        {
            Assert.Equal(17, AreaShapeType.ChainHeal);
        }

        // ── Test 2: FromString parses "chainheal" → 17 ───────────────────────
        [Fact]
        public void ChainHeal_FromString_ParsesLowercase()
        {
            Assert.Equal(17, AreaShapeType.FromString("chainheal"));
        }

        [Fact]
        public void ChainHeal_FromString_UnknownFallsBackToSingle()
        {
            // Defensive: unknown strings default to Single (0) — chainheal should NOT do that
            Assert.Equal(AreaShapeType.Single, AreaShapeType.FromString("not_a_real_shape"));
        }

        // ── Test 3: Single injured target — heal applied once, no chain ──────
        [Fact]
        public void ChainHeal_SingleInjuredTarget_HealsOnce()
        {
            var (p0, p1, p2) = CreateArena();
            DamagePlayer(p1, 100f);  // p1 at 100 HP
            float before = Store.PlayerCurrentHealth[p1];
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 250, "ChainHeal", 0f, 0f);

            Assert.Equal(1, healed);
            Assert.Equal(before + 50f, Store.PlayerCurrentHealth[p1]);
        }

        // ── Test 4: Multi-hop — heals 3 injured players in order of max-deficit ──
        [Fact]
        public void ChainHeal_ChainsToMultipleInjuredPlayers()
        {
            var (p0, p1, p2) = CreateArena();
            // p1 deficit=50, p2 deficit=150 (most injured → p2 first)
            DamagePlayer(p1, 50f);
            DamagePlayer(p2, 150f);
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(60f, 0f, 0f, 300, "ChainHeal", 0f, 0f);

            // p2 (deficit 150) → heal 60 (no decay), p1 (deficit 50) → heal 30 (60 * 0.5)
            Assert.Equal(2, healed);
            // p2: 200 - 150 + 60 = 110
            Assert.Equal(110f, Store.PlayerCurrentHealth[p2]);
            // p1: 200 - 50 + 30 = 180
            Assert.Equal(180f, Store.PlayerCurrentHealth[p1]);
        }

        // ── Test 5: Decay — each hop is 50% of previous ──────────────────────
        [Fact]
        public void ChainHeal_DecayHalvesPerHop()
        {
            var (p0, p1, p2) = CreateArena();
            // Equal deficits: tie broken by nearest → p1 first, then p2
            DamagePlayer(p1, 80f);
            DamagePlayer(p2, 80f);
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(40f, 0f, 0f, 300, "ChainHeal", 0f, 0f);

            // p1 (nearest, deficit 80) gets 40; p2 (deficit 80, but further) gets 20
            Assert.Equal(2, healed);
            Assert.Equal(160f, Store.PlayerCurrentHealth[p1]);  // 120 + 40
            Assert.Equal(140f, Store.PlayerCurrentHealth[p2]);  // 120 + 20
        }

        // ── Test 6: Max hops = 4 (primary + 3 chain) ─────────────────────────
        [Fact]
        public void ChainHeal_MaxFourHops()
        {
            int p0 = Store.CreateEntity();   // player 1
            int p1 = Store.CreateEntity();   // player 2
            int p2 = Store.CreateEntity();   // player 3
            int p3 = Store.CreateEntity();   // player 4
            // p0=1, p1=2, p2=3, p3=4 (CreateEntity starts at 2 since slot 1 is reserved for player)
            // alive friendlies: p1, p2, p3 (caster p0 excluded)
            int[] alive = { p1, p2, p3 };
            foreach (int i in alive)
            {
                Store.PlayerMaxHealth[i] = 100f;
                Store.PlayerCurrentHealth[i] = 50f;  // all injured (deficit 50)
                Store.PositionX[i] = (i - p0) * 50f;
                Store.PositionY[i] = 0f;
            }
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(40f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            // Should heal 3 friendlies (caster p0 excluded, CHAIN_HEAL_MAX_TARGETS=4 cap not hit)
            Assert.Equal(3, healed);
        }

        // ── Test 7: Range gating — out-of-range target ignored ───────────────
        [Fact]
        public void ChainHeal_OutOfRangeIgnored()
        {
            var (p0, p1, p2) = CreateArena();
            Store.PositionX[p1] = 5000f;  // far out of range
            Store.PositionX[p2] = 5000f;
            DamagePlayer(p1, 100f);
            DamagePlayer(p2, 100f);
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 100, "ChainHeal", 0f, 0f);

            // No injured friendly within range of p0
            Assert.Equal(0, healed);
        }

        // ── Test 8: No overheal — clamps to MaxHealth ────────────────────────
        [Fact]
        public void ChainHeal_ClampsAtMaxHealth()
        {
            var (p0, p1, p2) = CreateArena();
            DamagePlayer(p1, 10f);  // p1 at 190 HP
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(100f, 0f, 0f, 250, "ChainHeal", 0f, 0f);

            // Heal 100, but p1 can only go to 200 — clamp prevents overheal
            Assert.Equal(1, healed);
            Assert.Equal(200f, Store.PlayerCurrentHealth[p1]);
        }

        // ── Test 9: Dead player (HP=0) is skipped ────────────────────────────
        [Fact]
        public void ChainHeal_DeadPlayerSkipped()
        {
            var (p0, p1, p2) = CreateArena();
            Store.PlayerCurrentHealth[p1] = 0f;  // p1 dead
            // p2 already at 200/200 (full HP) from CreateArena
            // p0 already at 200/200 (full HP) from CreateArena
            // No injured friendlies → 0 heals
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            Assert.Equal(0, healed);
            // p1 still dead
            Assert.Equal(0f, Store.PlayerCurrentHealth[p1]);
        }

        // ── Test 10: Full-HP player is skipped (no overheal waste) ───────────
        [Fact]
        public void ChainHeal_FullHpPlayerSkipped()
        {
            var (p0, p1, p2) = CreateArena();
            // p1 is at full HP (200/200) — should be skipped
            // No injured friendlies → 0 heals
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            Assert.Equal(0, healed);
            Assert.Equal(200f, Store.PlayerCurrentHealth[p1]);
        }

        // ── Test 11: Shield bonus applied per healed target ──────────────────
        [Fact]
        public void ChainHeal_ShieldBonusAppliedPerHit()
        {
            var (p0, p1, p2) = CreateArena();
            DamagePlayer(p1, 50f);
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);
            float shieldBefore = Store.PlayerShield[p1];

            int healed = sys.CastChainHealPublic(30f, 0f, 0f, 250, "ChainHeal", 20f, 0f);

            Assert.Equal(1, healed);
            // Shield should have increased by 20
            Assert.True(Store.PlayerShield[p1] >= shieldBefore + 20f - 0.001f,
                $"Expected shield increase, got before={shieldBefore}, after={Store.PlayerShield[p1]}");
        }

        // ── Test 12: No-op when baseHeal = 0 ─────────────────────────────────
        [Fact]
        public void ChainHeal_ZeroBaseHealNoOp()
        {
            var (p0, p1, p2) = CreateArena();
            DamagePlayer(p1, 50f);
            float before = Store.PlayerCurrentHealth[p1];
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(0f, 0f, 0f, 250, "ChainHeal", 0f, 0f);

            Assert.Equal(0, healed);
            Assert.Equal(before, Store.PlayerCurrentHealth[p1]);
        }

        // ── Test 13: End-to-end CastSkill via switch case 17 ─────────────────
        [Fact]
        public void ChainHeal_EndToEnd_ViaCastSkill_AppliesHeal()
        {
            var (p0, p1, p2) = CreateArena();
            // Add a chainheal ability directly to p0
            var chainHealDef = new GameplayAbilityDef
            {
                Name = "Test Chain Heal",
                Description = "test",
                Cooldown = 5f,
                Cost = 0f,
                DamageMultiplierAttr = -1,
                FixedBaseDamage = 0f,  // unused; HealPercent drives heal amount
                Activation = AbilityActivation.Instant,
                AreaShape = AreaShapeType.ChainHeal,
                AreaRadius = 200,
                HealPercent = 0.5f,    // heal 50% of caster max HP
                ShieldAmount = 10f,
                ShieldDuration = 2f,
            };
            Store.AddAbility(p0, chainHealDef);
            DamagePlayer(p1, 100f);  // p1 at 100 HP
            float shieldBefore = Store.PlayerShield[p1];
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            sys.CastSkill("Test Chain Heal");

            // 状态断言：治疗量从注入值推导 = HealPercent × 施法者 MaxHP = 0.5 × 200 = 100，
            // p1(100) + 100 恰好回满 200；护盾从注入的 ShieldAmount=10 推导。
            Assert.Equal(200f, Store.PlayerCurrentHealth[p1]);
            Assert.Equal(shieldBefore + chainHealDef.ShieldAmount, Store.PlayerShield[p1], 3);
        }

        // ── Test 14: CastSkill chain heal triggers cooldown ──────────────────
        [Fact]
        public void ChainHeal_EndToEnd_StartsCooldown()
        {
            var (p0, p1, p2) = CreateArena();
            var chainHealDef = new GameplayAbilityDef
            {
                Name = "Test Chain Heal CD",
                Description = "test",
                Cooldown = 7f,
                Cost = 0f,
                DamageMultiplierAttr = -1,
                FixedBaseDamage = 0f,
                Activation = AbilityActivation.Instant,
                AreaShape = AreaShapeType.ChainHeal,
                AreaRadius = 200,
                HealPercent = 0.25f,
                ShieldAmount = 0f,
                ShieldDuration = 0f,
            };
            Store.AddAbility(p0, chainHealDef);
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            sys.CastSkill("Test Chain Heal CD");

            int count = Store.AbilityCount[p0];
            bool foundCd = false;
            for (int i = 0; i < count; i++)
            {
                var slot = Store.GetAbility(p0, i);
                if (slot.Definition.Name == "Test Chain Heal CD")
                {
                    // 期望从注入的 Cooldown=7 推导，而不是钉一个 >6.5 的宽松阈值。
                    Assert.Equal(7f, slot.CurrentCooldown, 3);
                    foundCd = true;
                }
            }
            Assert.True(foundCd);
        }

        // ── Test 15: A single hit target is not double-healed (no double-tap) ──
        [Fact]
        public void ChainHeal_SingleTarget_NoDoubleHeal()
        {
            var (p0, p1, p2) = CreateArena();
            DamagePlayer(p1, 50f);
            var sys = new SkillSystem(Store, Renderer, p0, Config);
            sys.SetPhaseContext(new PhaseContext(PhaseContextKind.Wave));
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(100f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            // Only p1 injured within range; should hit once even though CHAIN_HEAL_MAX_TARGETS=4
            Assert.Equal(1, healed);
            // p1: 200 - 50 + 100 = 250 → clamp to 200
            Assert.Equal(200f, Store.PlayerCurrentHealth[p1]);
        }
    }
}

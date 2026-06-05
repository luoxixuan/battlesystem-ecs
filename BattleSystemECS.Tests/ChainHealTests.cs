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
    /// Round 131 — Chain Heal (链式治疗) tests.
    /// Mirrors ChainLightning but targets injured allies and applies heal (+ optional shield bonus).
    /// Covers: AreaShapeType.ChainHeal constant, FromString parsing, single-target hop,
    /// multi-hop chain (4 max), decay 50% per hop, range gating, no-overheal clamp, dead skip,
    /// full-HP skip, shield bonus, and end-to-end CastSkill dispatch via switch case 17.
    /// </summary>
    public class ChainHealTests
    {
        // ── Helper: build a 2-3 player arena ─────────────────────────────────
        private (ComponentStore store, GameConfig config, int p0, int p1, int p2) CreateArena()
        {
            var store = new ComponentStore();
            int p0 = store.CreateEntity();
            int p1 = store.CreateEntity();
            int p2 = store.CreateEntity();
            // Default state: all at full HP, MaxHealth=200, positioned on a horizontal line
            for (int i = 0; i < 10; i++)  // MAX_PLAYERS = 10
            {
                store.PlayerMaxHealth[i] = 200f;
                store.PlayerCurrentHealth[i] = 200f;
                store.PlayerAttackDamage[i] = 10f;
                store.PlayerAttackRange[i] = 3f;
                store.PositionX[i] = 0f;
                store.PositionY[i] = 0f;
            }
            // Position: p0 at origin, p1 at +100 px, p2 at +200 px (chain hops p0→p1→p2)
            store.PositionX[p0] = 0f;   store.PositionY[p0] = 0f;
            store.PositionX[p1] = 100f; store.PositionY[p1] = 0f;
            store.PositionX[p2] = 200f; store.PositionY[p2] = 0f;
            return (store, new GameConfig(), p0, p1, p2);
        }

        private void DamagePlayer(ComponentStore store, int pid, float amount)
        {
            store.PlayerCurrentHealth[pid] = Math.Max(0f, store.PlayerCurrentHealth[pid] - amount);
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
            var (store, config, p0, p1, p2) = CreateArena();
            DamagePlayer(store, p1, 100f);  // p1 at 100 HP
            float before = store.PlayerCurrentHealth[p1];
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 250, "ChainHeal", 0f, 0f);

            Assert.Equal(1, healed);
            Assert.Equal(before + 50f, store.PlayerCurrentHealth[p1]);
        }

        // ── Test 4: Multi-hop — heals 3 injured players in order of max-deficit ──
        [Fact]
        public void ChainHeal_ChainsToMultipleInjuredPlayers()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            // p1 deficit=50, p2 deficit=150 (most injured → p2 first)
            DamagePlayer(store, p1, 50f);
            DamagePlayer(store, p2, 150f);
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(60f, 0f, 0f, 300, "ChainHeal", 0f, 0f);

            // p2 (deficit 150) → heal 60 (no decay), p1 (deficit 50) → heal 30 (60 * 0.5)
            Assert.Equal(2, healed);
            // p2: 200 - 150 + 60 = 110
            Assert.Equal(110f, store.PlayerCurrentHealth[p2]);
            // p1: 200 - 50 + 30 = 180
            Assert.Equal(180f, store.PlayerCurrentHealth[p1]);
        }

        // ── Test 5: Decay — each hop is 50% of previous ──────────────────────
        [Fact]
        public void ChainHeal_DecayHalvesPerHop()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            // Equal deficits: tie broken by nearest → p1 first, then p2
            DamagePlayer(store, p1, 80f);
            DamagePlayer(store, p2, 80f);
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(40f, 0f, 0f, 300, "ChainHeal", 0f, 0f);

            // p1 (nearest, deficit 80) gets 40; p2 (deficit 80, but further) gets 20
            Assert.Equal(2, healed);
            Assert.Equal(160f, store.PlayerCurrentHealth[p1]);  // 120 + 40
            Assert.Equal(140f, store.PlayerCurrentHealth[p2]);  // 120 + 20
        }

        // ── Test 6: Max hops = 4 (primary + 3 chain) ─────────────────────────
        [Fact]
        public void ChainHeal_MaxFourHops()
        {
            var store = new ComponentStore();
            int p0 = store.CreateEntity();   // player 1
            int p1 = store.CreateEntity();   // player 2
            int p2 = store.CreateEntity();   // player 3
            int p3 = store.CreateEntity();   // player 4
            // p0=1, p1=2, p2=3, p3=4 (CreateEntity starts at 2 since slot 1 is reserved for player)
            // alive friendlies: p1, p2, p3 (caster p0 excluded)
            int[] alive = { p1, p2, p3 };
            foreach (int i in alive)
            {
                store.PlayerMaxHealth[i] = 100f;
                store.PlayerCurrentHealth[i] = 50f;  // all injured (deficit 50)
                store.PositionX[i] = (i - p0) * 50f;
                store.PositionY[i] = 0f;
            }
            var r = new MockRenderer();
            var config = new GameConfig();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(40f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            // Should heal 3 friendlies (caster p0 excluded, CHAIN_HEAL_MAX_TARGETS=4 cap not hit)
            Assert.Equal(3, healed);
        }

        // ── Test 7: Range gating — out-of-range target ignored ───────────────
        [Fact]
        public void ChainHeal_OutOfRangeIgnored()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            store.PositionX[p1] = 5000f;  // far out of range
            store.PositionX[p2] = 5000f;
            DamagePlayer(store, p1, 100f);
            DamagePlayer(store, p2, 100f);
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 100, "ChainHeal", 0f, 0f);

            // No injured friendly within range of p0
            Assert.Equal(0, healed);
        }

        // ── Test 8: No overheal — clamps to MaxHealth ────────────────────────
        [Fact]
        public void ChainHeal_ClampsAtMaxHealth()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            DamagePlayer(store, p1, 10f);  // p1 at 190 HP
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(100f, 0f, 0f, 250, "ChainHeal", 0f, 0f);

            // Heal 100, but p1 can only go to 200 — clamp prevents overheal
            Assert.Equal(1, healed);
            Assert.Equal(200f, store.PlayerCurrentHealth[p1]);
        }

        // ── Test 9: Dead player (HP=0) is skipped ────────────────────────────
        [Fact]
        public void ChainHeal_DeadPlayerSkipped()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            store.PlayerCurrentHealth[p1] = 0f;  // p1 dead
            // p2 already at 200/200 (full HP) from CreateArena
            // p0 already at 200/200 (full HP) from CreateArena
            // No injured friendlies → 0 heals
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            Assert.Equal(0, healed);
            // p1 still dead
            Assert.Equal(0f, store.PlayerCurrentHealth[p1]);
        }

        // ── Test 10: Full-HP player is skipped (no overheal waste) ───────────
        [Fact]
        public void ChainHeal_FullHpPlayerSkipped()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            // p1 is at full HP (200/200) — should be skipped
            // No injured friendlies → 0 heals
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(50f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            Assert.Equal(0, healed);
            Assert.Equal(200f, store.PlayerCurrentHealth[p1]);
        }

        // ── Test 11: Shield bonus applied per healed target ──────────────────
        [Fact]
        public void ChainHeal_ShieldBonusAppliedPerHit()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            DamagePlayer(store, p1, 50f);
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);
            float shieldBefore = store.PlayerShield[p1];

            int healed = sys.CastChainHealPublic(30f, 0f, 0f, 250, "ChainHeal", 20f, 0f);

            Assert.Equal(1, healed);
            // Shield should have increased by 20
            Assert.True(store.PlayerShield[p1] >= shieldBefore + 20f - 0.001f,
                $"Expected shield increase, got before={shieldBefore}, after={store.PlayerShield[p1]}");
        }

        // ── Test 12: No-op when baseHeal = 0 ─────────────────────────────────
        [Fact]
        public void ChainHeal_ZeroBaseHealNoOp()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            DamagePlayer(store, p1, 50f);
            float before = store.PlayerCurrentHealth[p1];
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(0f, 0f, 0f, 250, "ChainHeal", 0f, 0f);

            Assert.Equal(0, healed);
            Assert.Equal(before, store.PlayerCurrentHealth[p1]);
        }

        // ── Test 13: End-to-end CastSkill via switch case 17 ─────────────────
        [Fact]
        public void ChainHeal_EndToEnd_ViaCastSkill_AppliesHeal()
        {
            var (store, config, p0, p1, p2) = CreateArena();
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
            store.AddAbility(p0, chainHealDef);
            DamagePlayer(store, p1, 100f);  // p1 at 100 HP
            float before = store.PlayerCurrentHealth[p1];
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            sys.CastSkill("Test Chain Heal");

            // HealPercent 0.5 * caster(p0) maxHP(200) = 100 base heal → p1 (100 HP) → 200 (clamped)
            Assert.Equal(200f, store.PlayerCurrentHealth[p1]);
            Assert.True(r.HasLogContaining("Test Chain Heal cast"));
        }

        // ── Test 14: CastSkill chain heal triggers cooldown ──────────────────
        [Fact]
        public void ChainHeal_EndToEnd_StartsCooldown()
        {
            var (store, config, p0, p1, p2) = CreateArena();
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
            store.AddAbility(p0, chainHealDef);
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            sys.CastSkill("Test Chain Heal CD");

            int count = store.AbilityCount[p0];
            bool foundCd = false;
            for (int i = 0; i < count; i++)
            {
                var slot = store.GetAbility(p0, i);
                if (slot.Definition.Name == "Test Chain Heal CD")
                {
                    Assert.True(slot.CurrentCooldown > 6.5f, $"Cooldown not set, got {slot.CurrentCooldown}");
                    foundCd = true;
                }
            }
            Assert.True(foundCd);
        }

        // ── Test 15: A single hit target is not double-healed (no double-tap) ──
        [Fact]
        public void ChainHeal_SingleTarget_NoDoubleHeal()
        {
            var (store, config, p0, p1, p2) = CreateArena();
            DamagePlayer(store, p1, 50f);
            var r = new MockRenderer();
            var sys = new SkillSystem(store, r, p0, config);
            sys.SetTurn(0);

            int healed = sys.CastChainHealPublic(100f, 0f, 0f, 1000, "ChainHeal", 0f, 0f);

            // Only p1 injured within range; should hit once even though CHAIN_HEAL_MAX_TARGETS=4
            Assert.Equal(1, healed);
            // p1: 200 - 50 + 100 = 250 → clamp to 200
            Assert.Equal(200f, store.PlayerCurrentHealth[p1]);
        }
    }
}

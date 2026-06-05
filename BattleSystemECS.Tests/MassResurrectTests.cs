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
    /// Round 133 Direction 5 — Mass Resurrect (群体复活) tests.
    /// Verifies the player-triggered AOE revival ability that scans the corpse queue
    /// and re-spawns up to N corpses in radius. Mirrors NecromancerSystem's per-corpse
    /// resurrection but with one-shot semantics and a fixed hpFraction.
    ///
    /// Coverage:
    ///   - AreaShapeType.MassResurrect = 18 constant + FromString parsing
    ///   - Empty corpse queue returns 0
    ///   - Single corpse in radius → 1 revived
    ///   - Multiple corpses in radius → all revived
    ///   - Out-of-radius corpse ignored
    ///   - Already-claimed corpse (OwnerId >= 0) ignored (necromancer priority)
    ///   - Already-reanimated corpse ignored
    ///   - Aged-out corpse ignored
    ///   - hpFraction applies to spawned minion's max HP
    ///   - Spawned minion has EnemyIsReanimated = true + EnemyOwnerId = playerId
    ///   - SkillSystem.CastSkill dispatch via case 18 with NecromancerSystem injected
    ///   - SkillSystem case 18 with NecromancerSystem NOT injected returns 0 (defensive)
    /// </summary>
    public class MassResurrectTests
    {
        // ── Helper: build a minimal arena with player + a registered MonsterConfig ──
        private (ComponentStore store, GameConfig config, int playerId) CreateArena()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            var config = new GameConfig();
            // Register a minimal monster type so SpawnReanimatedMinion can look up its stats.
            // Health=100 → after hpFraction=0.3 the reanimated minion spawns with 30 HP.
            config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "TestSkeleton",
                Name = "Test Skeleton",
                Health = 100f,
                MaxHealth = 100f,
                Damage = 5f,
                MoveSpeed = 1f,
                Armor = 0f,
                MagicResist = 0f,
                GoldReward = 3
            });
            // Position the player at origin so we can reason about distances in tiles.
            store.PositionX[playerId] = 0f;
            store.PositionY[playerId] = 0f;
            return (store, config, playerId);
        }

        // Helper: queue a corpse at (x, y) of "TestSkeleton" type, died at simTime=turn
        private int QueueCorpse(ComponentStore store, float x, float y, float hpPercent, float simTime)
        {
            return store.NecromancerQueueCorpse(-1, x, y, "TestSkeleton", hpPercent, simTime);
        }

        // ── Test 1: AreaShapeType.MassResurrect = 18 constant ──────────────────
        [Fact]
        public void MassResurrect_AreaShapeType_Is18()
        {
            Assert.Equal(18, AreaShapeType.MassResurrect);
        }

        // ── Test 2: FromString parses "massresurrect" → 18 ────────────────────
        [Fact]
        public void MassResurrect_FromString_ParsesLowercase()
        {
            Assert.Equal(18, AreaShapeType.FromString("massresurrect"));
        }

        // ── Test 3: FromString is case-insensitive (ToLowerInvariant) ──────────
        [Fact]
        public void MassResurrect_FromString_ParsesMixedCase()
        {
            Assert.Equal(18, AreaShapeType.FromString("MassResurrect"));
            Assert.Equal(18, AreaShapeType.FromString("MASSRESURRECT"));
        }

        // ── Test 4: Empty corpse queue returns 0 revived ───────────────────────
        [Fact]
        public void MassResurrect_EmptyQueue_ReturnsZero()
        {
            var (store, config, playerId) = CreateArena();
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(0, revived);
        }

        // ── Test 5: Single corpse in radius → 1 revived, minion spawned ───────
        [Fact]
        public void MassResurrect_SingleCorpseInRadius_RevivesOne()
        {
            var (store, config, playerId) = CreateArena();
            QueueCorpse(store, 2f, 0f, 1.0f, 0f); // 2 units away, 100% HP, died at t=0
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(1, revived);
        }

        // ── Test 6: Multiple corpses in radius → all revived ──────────────────
        [Fact]
        public void MassResurrect_MultipleCorpsesInRadius_RevivesAll()
        {
            var (store, config, playerId) = CreateArena();
            // 3 corpses within 4 units of origin
            QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            QueueCorpse(store, -1f, 0f, 1.0f, 0f);
            QueueCorpse(store, 0f, 2f, 1.0f, 0f);
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(3, revived);
        }

        // ── Test 7: Out-of-radius corpse is ignored ───────────────────────────
        [Fact]
        public void MassResurrect_OutOfRadiusCorpse_Ignored()
        {
            var (store, config, playerId) = CreateArena();
            QueueCorpse(store, 100f, 0f, 1.0f, 0f); // 100 units away, well outside radius 4
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(0, revived);
        }

        // ── Test 8: Already-claimed corpse (necromancer owns) is ignored ───────
        [Fact]
        public void MassResurrect_ClaimedCorpse_Ignored()
        {
            var (store, config, playerId) = CreateArena();
            int corpseId = QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            // Simulate a necromancer already claiming this corpse
            store.CorpseOwnerId[corpseId] = 42;
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(0, revived);
        }

        // ── Test 9: Already-reanimated corpse is ignored (idempotent) ─────────
        [Fact]
        public void MassResurrect_AlreadyReanimatedCorpse_Ignored()
        {
            var (store, config, playerId) = CreateArena();
            int corpseId = QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            store.CorpseReanimated[corpseId] = true;
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(0, revived);
        }

        // ── Test 10: Aged-out corpse is ignored (older than MAX_CORPSE_AGE_SEC) ──
        [Fact]
        public void MassResurrect_AgedCorpse_Ignored()
        {
            var (store, config, playerId) = CreateArena();
            // Corpse died 100s ago, MAX_CORPSE_AGE_SEC defaults to 30
            QueueCorpse(store, 1f, 0f, 1.0f, -100f);
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f); // current simTime = 0, age = 0 - (-100) = 100 > 30

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(0, revived);
        }

        // ── Test 11: hpFraction applies to spawned minion's HP ────────────────
        [Fact]
        public void MassResurrect_HpFraction_AppliesToMinionMaxHealth()
        {
            var (store, config, playerId) = CreateArena();
            // Queue a corpse with 100% HP (i.e. corpseHpPercent=1.0). After 0.5 hpFraction,
            // the reanimated minion should spawn with Health = 100 * 1.0 * 0.5 = 50.
            QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.5f);

            Assert.Equal(1, revived);
            // Find the newly spawned minion (look for any active enemy with IsReanimated=true
            // that was created after the test started). Since we know the player is at slot
            // index 0 and the next available is index 1, we check ActiveEnemyIds.
            var activeIds = store.GetCachedActiveEnemyIds();
            int minionId = -1;
            for (int i = 0; i < activeIds.Count; i++)
            {
                int eid = activeIds[i];
                if (eid != playerId && store.EnemyIsReanimated[eid])
                {
                    minionId = eid;
                    break;
                }
            }
            Assert.True(minionId >= 0, "A reanimated minion should have been spawned");
            // HP = 100 (maxHealth) * 1.0 (corpseHpPercent) * 0.5 (hpFraction) = 50
            Assert.Equal(50f, store.EnemyHealth[minionId]);
            Assert.Equal(50f, store.EnemyMaxHealth[minionId]);
        }

        // ── Test 12: Spawned minion is tagged with EnemyOwnerId = playerId ─────
        [Fact]
        public void MassResurrect_MinionTaggedWithOwnerId()
        {
            var (store, config, playerId) = CreateArena();
            QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(1, revived);
            var activeIds = store.GetCachedActiveEnemyIds();
            int minionId = -1;
            for (int i = 0; i < activeIds.Count; i++)
            {
                int eid = activeIds[i];
                if (eid != playerId && store.EnemyIsReanimated[eid])
                {
                    minionId = eid;
                    break;
                }
            }
            Assert.True(minionId >= 0, "A reanimated minion should have been spawned");
            Assert.Equal(playerId, store.EnemyOwnerId[minionId]);
        }

        // ── Test 13: CorpseReanimated flag is set to true after revive ────────
        [Fact]
        public void MassResurrect_SetsCorpseReanimatedFlag()
        {
            var (store, config, playerId) = CreateArena();
            int corpseId = QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            Assert.False(store.CorpseReanimated[corpseId], "Pre-condition: flag should be false");
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.True(store.CorpseReanimated[corpseId], "After MassResurrect the flag should be true");
            Assert.Equal(playerId, store.CorpseOwnerId[corpseId]);
        }

        // ── Test 14: Zero-radius call returns 0 (defensive) ────────────────────
        [Fact]
        public void MassResurrect_ZeroRadius_ReturnsZero()
        {
            var (store, config, playerId) = CreateArena();
            QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int revived = sys.MassResurrect(playerId, 0f, 0f, 0f, 0.3f);

            Assert.Equal(0, revived);
        }

        // ── Test 15: Negative hpFraction falls back to 0.3 default ────────────
        [Fact]
        public void MassResurrect_NegativeHpFraction_UsesDefault()
        {
            var (store, config, playerId) = CreateArena();
            QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            // hpFraction = -1 should fall back to 0.3 internally
            int revived = sys.MassResurrect(playerId, 0f, 0f, 4f, -1f);

            Assert.Equal(1, revived);
            var activeIds = store.GetCachedActiveEnemyIds();
            int minionId = -1;
            for (int i = 0; i < activeIds.Count; i++)
            {
                int eid = activeIds[i];
                if (eid != playerId && store.EnemyIsReanimated[eid])
                {
                    minionId = eid;
                    break;
                }
            }
            Assert.True(minionId >= 0, "A reanimated minion should have been spawned");
            // HP = 100 (max) * 1.0 (corpseHp) * 0.3 (default) = 30
            // (precision tolerance: float multiplication accumulates IEEE 754 rounding)
            Assert.Equal(30f, store.EnemyHealth[minionId], 3);
        }

        // ── Test 16: Second MassResurrect call is a no-op (corpses already claimed) ──
        [Fact]
        public void MassResurrect_TwiceInARow_SecondCallIsNoOp()
        {
            var (store, config, playerId) = CreateArena();
            QueueCorpse(store, 1f, 0f, 1.0f, 0f);
            QueueCorpse(store, 2f, 0f, 1.0f, 0f);
            var r = new MockRenderer();
            var sys = new NecromancerSystem(store, config, r);
            sys.SetTurn(0, 0f);

            int firstPass = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);
            int secondPass = sys.MassResurrect(playerId, 0f, 0f, 4f, 0.3f);

            Assert.Equal(2, firstPass);
            Assert.Equal(0, secondPass); // Both corpses already reanimated
        }

        // ── Test 17: End-to-end via SkillSystem.CastSkill with MassResurrect def ──
        [Fact]
        public void SkillSystem_MassResurrect_DispatchesToNecromancerSystem()
        {
            var (store, config, playerId) = CreateArena();
            // Add the MassResurrect ability to the player
            var massResDef = new GameplayAbilityDef(
                name: "Mass Resurrect",
                desc: "Test mass resurrect",
                cooldown: 60f,
                cost: 0f,
                dmgAttr: -1,
                fixedDmg: 0f,
                act: AbilityActivation.InputPressed,
                areaShape: AreaShapeType.MassResurrect,
                areaRadius: 4
            )
            {
                HealPercent = 0.3f
            };
            // AbilityInstance slot 0 — set the def and bump AbilityCount so CastSkill
            // iterates slot 0 (it gates on `slot < AbilityCount[playerId]`).
            var ability = new AbilityInstance(massResDef);
            store.SetAbility(playerId, 0, ability);
            store.AbilityCount[playerId] = 1;

            // Queue a corpse in range
            QueueCorpse(store, 1f, 0f, 1.0f, 0f);

            var r = new MockRenderer();
            var necro = new NecromancerSystem(store, config, r);
            var skillSys = new SkillSystem(store, r, playerId, config);
            skillSys.InjectNecromancerSystem(necro);
            skillSys.SetTurn(0);

            // Cast by name
            skillSys.CastSkill("Mass Resurrect");

            // The corpse should be reanimated
            // Find the minion and verify it exists
            var activeIds = store.GetCachedActiveEnemyIds();
            int minionCount = 0;
            for (int i = 0; i < activeIds.Count; i++)
            {
                int eid = activeIds[i];
                if (eid != playerId && store.EnemyIsReanimated[eid])
                {
                    minionCount++;
                }
            }
            Assert.Equal(1, minionCount);
        }

        // ── Test 18: SkillSystem case 18 without NecromancerSystem injected → 0 revived ──
        [Fact]
        public void SkillSystem_MassResurrect_NoNecromancerSystem_ReturnsZero()
        {
            var (store, config, playerId) = CreateArena();
            var massResDef = new GameplayAbilityDef(
                name: "Mass Resurrect",
                desc: "Test",
                cooldown: 60f,
                cost: 0f,
                dmgAttr: -1,
                fixedDmg: 0f,
                act: AbilityActivation.InputPressed,
                areaShape: AreaShapeType.MassResurrect,
                areaRadius: 4
            );
            var ability = new AbilityInstance(massResDef);
            store.SetAbility(playerId, 0, ability);
            store.AbilityCount[playerId] = 1;

            QueueCorpse(store, 1f, 0f, 1.0f, 0f);

            var r = new MockRenderer();
            // NOTE: deliberately NOT injecting NecromancerSystem — defensive path
            var skillSys = new SkillSystem(store, r, playerId, config);
            skillSys.SetTurn(0);

            skillSys.CastSkill("Mass Resurrect");

            // No minion should be spawned since the cast short-circuited
            var activeIds = store.GetCachedActiveEnemyIds();
            for (int i = 0; i < activeIds.Count; i++)
            {
                int eid = activeIds[i];
                if (eid != playerId)
                {
                    Assert.False(store.EnemyIsReanimated[eid], "No minion should be spawned when NecromancerSystem is missing");
                }
            }
        }
    }
}

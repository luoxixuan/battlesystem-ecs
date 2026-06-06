using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Round 168 Direction 3 — Hallowed Ground tests.
    /// Verifies that a CorpseEffect of type 6 (HallowedGround) deals
    /// per-tick damage to enemies inside its radius.
    /// </summary>
    public class CorpseEffectSystemTests
    {
        private (ComponentStore store, GameConfig config, BuffSystem buff, int playerId) CreateEnv()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 0f;
            store.PositionY[playerId] = 0f;
            var config = new GameConfig();
            var buff = new BuffSystem(store, playerId, new MockRenderer());
            return (store, config, buff, playerId);
        }

        private int AddEnemy(ComponentStore store, float x, float y, float hp)
        {
            int id = store.CreateEntity();
            store.EnemyActive[id] = true;
            store.AddActiveEnemyId(id);
            store.PositionX[id] = x;
            store.PositionY[id] = y;
            store.SetEnemyHealth(id, hp);
            store.EnemyMaxHealth[id] = hp;
            return id;
        }

        [Fact]
        public void HallowedGround_DamagesEnemyInRangePerTick()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            // Spawn HallowedGround at origin, radius 1.5, 4 dmg/tick, 1s interval, 5s duration
            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,            // HallowedGround
                radius: 1.5f,
                duration: 5f,
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);
            Assert.True(store.CorpseEffectActive[zoneId]);

            // Place an enemy in range (distance 1.0 < 1.5)
            int enemyId = AddEnemy(store, 1.0f, 0.0f, 100f);
            float hpBefore = store.EnemyHealth[enemyId];
            Assert.Equal(100f, hpBefore);

            // Frame 1: CorpseEffect ticks at t=1s, applies DoT to enemy (stored as effect)
            sys.Update(1.0f);
            // BuffSystem.Update would normally process the queued effect and add to _dotDamageQueue,
            // but for the CorpseEffectSystem test we simulate the same drain manually.
            buff.Update(1.0f);          // tick the stored effect → enqueue damage
            buff.ResolveDotDamage();
            float hpAfterTick1 = store.EnemyHealth[enemyId];
            Assert.True(hpAfterTick1 < hpBefore, $"Expected hp to drop after 1s tick, was {hpAfterTick1}");

            // Tick another 1.0s — another 4 dmg
            float hpAfterTick2_before = store.EnemyHealth[enemyId];
            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            float hpAfterTick2 = store.EnemyHealth[enemyId];
            Assert.True(hpAfterTick2 < hpAfterTick2_before,
                $"Expected second tick to also damage; before={hpAfterTick2_before} after={hpAfterTick2}");
        }

        [Fact]
        public void HallowedGround_DoesNotDamageEnemyOutOfRange()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,
                radius: 1.5f,
                duration: 5f,
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);

            // Enemy far outside radius
            int enemyId = AddEnemy(store, 10f, 10f, 100f);
            float hpBefore = store.EnemyHealth[enemyId];

            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();

            // Out-of-range enemy: no DoT applied, so no damage.
            Assert.Equal(hpBefore, store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void HallowedGround_ExpiresAfterDuration()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,
                radius: 1.5f,
                duration: 2f,           // 2 seconds
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);
            Assert.True(store.CorpseEffectActive[zoneId]);

            sys.Update(2.5f);          // exceeds duration
            buff.ResolveDotDamage();

            Assert.False(store.CorpseEffectActive[zoneId]);
        }

        // ========== Round 169 Direction 10 — Thorny Bramble (effectType=7) ==========

        [Fact]
        public void ThornyBramble_DamagesAndSlowsEnemyInRange()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            // Spawn ThornyBramble at origin: 1.2 radius, 3 dmg/tick, 0.6x slow, 1s tick, 4s dur
            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 7,            // ThornyBramble
                radius: 1.2f,
                duration: 4f,
                damagePerTick: 3f,
                slowAmount: 0.6f,         // 40% slow
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);

            // Enemy in range (distance 1.0 < 1.2)
            int enemyId = AddEnemy(store, 1.0f, 0.0f, 100f);
            store.EnemyTerrainMoveSpeedMult[enemyId] = 1.0f; // baseline
            float hpBefore = store.EnemyHealth[enemyId];

            // Frame 1: continuous slow applied this frame (no need to wait for tick)
            sys.Update(0.1f);
            Assert.Equal(0.6f, store.EnemyTerrainMoveSpeedMult[enemyId]);

            // Tick fires at t=1s: enemy takes 3 dmg
            sys.Update(0.9f);             // total dt = 1.0s
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            float hpAfter1 = store.EnemyHealth[enemyId];
            Assert.True(hpAfter1 < hpBefore, $"Expected DoT to damage; before={hpBefore} after={hpAfter1}");

            // Tick again at t=2s: another 3 dmg
            float hpAfter1Snapshot = store.EnemyHealth[enemyId];
            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            float hpAfter2 = store.EnemyHealth[enemyId];
            Assert.True(hpAfter2 < hpAfter1Snapshot,
                $"Expected second tick to damage; before={hpAfter1Snapshot} after={hpAfter2}");
        }

        [Fact]
        public void ThornyBramble_DoesNotAffectEnemyOutOfRange()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 7,
                radius: 1.2f,
                duration: 4f,
                damagePerTick: 3f,
                slowAmount: 0.6f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);

            // Enemy far outside radius
            int enemyId = AddEnemy(store, 10f, 10f, 100f);
            store.EnemyTerrainMoveSpeedMult[enemyId] = 1.0f;
            float hpBefore = store.EnemyHealth[enemyId];

            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();

            // Out-of-range: no DoT, no slow
            Assert.Equal(hpBefore, store.EnemyHealth[enemyId]);
            Assert.Equal(1.0f, store.EnemyTerrainMoveSpeedMult[enemyId]);
        }

        [Fact]
        public void ThornyBramble_StacksSlowerSlowWithExistingSlow()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            // Bramble with 0.6x slow (40% slow)
            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 7,
                radius: 1.5f,
                duration: 4f,
                damagePerTick: 3f,
                slowAmount: 0.6f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);

            int enemyId = AddEnemy(store, 1.0f, 0.0f, 100f);
            // Pre-existing stronger slow (0.3x) from another effect
            store.EnemyTerrainMoveSpeedMult[enemyId] = 0.3f;

            // Bramble slow (0.6) is weaker — must NOT override the stronger 0.3 slow
            sys.Update(0.1f);
            Assert.Equal(0.3f, store.EnemyTerrainMoveSpeedMult[enemyId]);

            // Now weaken the existing slow to 0.8; bramble's 0.6 should win
            store.EnemyTerrainMoveSpeedMult[enemyId] = 0.8f;
            sys.Update(0.1f);
            Assert.Equal(0.6f, store.EnemyTerrainMoveSpeedMult[enemyId]);
        }

        [Fact]
        public void ThornyBramble_ExpiresAfterDuration()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 7,
                radius: 1.2f,
                duration: 2f,
                damagePerTick: 3f,
                slowAmount: 0.6f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);
            Assert.True(store.CorpseEffectActive[zoneId]);

            sys.Update(2.5f);
            buff.ResolveDotDamage();

            Assert.False(store.CorpseEffectActive[zoneId]);
        }

        [Fact]
        public void ThornyBramble_LoadsFromJsonConfig()
        {
            // Verifies the JSON config entry by reading the source file directly
            // (corpse_effects.json is not CopyToOutputDirectory'd into the test bin,
            // so we can't use GameConfigLoader.LoadConfig from a test process).
            string jsonPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "Data", "Configs", "corpse_effects.json");
            string jsonContent = System.IO.File.ReadAllText(jsonPath);
            Assert.Contains("\"thorny_bramble\"", jsonContent);
            Assert.Contains("\"effectType\": 7", jsonContent);
            Assert.Contains("\"Troll\"", jsonContent);
            Assert.Contains("\"Demon\"", jsonContent);
            Assert.Contains("\"ThornedShambler\"", jsonContent);
        }

        // ========== Round 171 Direction 4 — Blighted Ground (effectType=8) ==========
        // Blighted Ground deals per-tick DoT AND applies armor+speed debuffs to enemies
        // standing in the zone. The debuffs are written to EnemyCurseArmorReduction /
        // EnemyCurseSpeedReduction (same SOA fields CurseAuraSystem writes to).

        [Fact]
        public void BlightedGround_AppliesArmorAndSpeedDebuffPerFrame()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            // Spawn BlightedGround: 1.4 radius, 2 dmg/tick, 30% armor, 20% speed, 1s tick, 5s dur
            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 8,            // BlightedGround
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,    // 30% armor debuff
                speedReduction: 0.20f     // 20% speed debuff
            );
            Assert.True(zoneId >= 0);
            Assert.True(store.CorpseEffectActive[zoneId]);

            // Enemy in range (distance 1.0 < 1.4)
            int enemyId = AddEnemy(store, 1.0f, 0.0f, 100f);

            // Frame 1 (deltaTime small so no tick fires): continuous debuffs applied
            sys.Update(0.1f);
            Assert.Equal(0.30f, store.EnemyCurseArmorReduction[enemyId]);
            Assert.Equal(0.20f, store.EnemyCurseSpeedReduction[enemyId]);
        }

        [Fact]
        public void BlightedGround_DoesNotAffectEnemyOutOfRange()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 8,
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,
                speedReduction: 0.20f
            );
            Assert.True(zoneId >= 0);

            // Enemy far outside radius
            int enemyId = AddEnemy(store, 10f, 10f, 100f);
            float hpBefore = store.EnemyHealth[enemyId];

            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();

            // Out-of-range: no DoT, no debuffs
            Assert.Equal(hpBefore, store.EnemyHealth[enemyId]);
            Assert.Equal(0f, store.EnemyCurseArmorReduction[enemyId]);
            Assert.Equal(0f, store.EnemyCurseSpeedReduction[enemyId]);
        }

        [Fact]
        public void BlightedGround_TicksDoTAlongsideDebuff()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 8,
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,
                speedReduction: 0.20f
            );
            Assert.True(zoneId >= 0);

            // Enemy in range
            int enemyId = AddEnemy(store, 1.0f, 0.0f, 100f);
            float hpBefore = store.EnemyHealth[enemyId];

            // After 1s: DoT tick + debuffs applied
            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            float hpAfter1 = store.EnemyHealth[enemyId];

            // DoT did damage
            Assert.True(hpAfter1 < hpBefore, $"Expected DoT to damage; before={hpBefore} after={hpAfter1}");
            // Debuffs also applied
            Assert.Equal(0.30f, store.EnemyCurseArmorReduction[enemyId]);
            Assert.Equal(0.20f, store.EnemyCurseSpeedReduction[enemyId]);

            // After 2s: another tick
            float hpAfter1Snap = store.EnemyHealth[enemyId];
            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            float hpAfter2 = store.EnemyHealth[enemyId];
            Assert.True(hpAfter2 < hpAfter1Snap, $"Expected 2nd DoT tick to damage; before={hpAfter1Snap} after={hpAfter2}");
        }

        [Fact]
        public void BlightedGround_AccumulatesAdditivelyWithMultipleZones()
        {
            // Two BlightedGround zones overlapping — their debuffs should stack additively
            // per frame (each zone contributes 0.30 armor, total 0.60).
            // ComponentStore.BeginFrame() (called by the frame scheduler in real gameplay)
            // resets the field to 0 at frame start, so accumulation is well-bounded.
            // In this unit test we don't call BeginFrame, so we can verify that
            // successive sys.Update() calls accumulate across zones.
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId1 = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 8,
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,
                speedReduction: 0.20f
            );
            int zoneId2 = store.AddCorpseEffect(
                x: 0.5f, y: 0f,    // overlap with zone 1
                effectType: 8,
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,
                speedReduction: 0.20f
            );
            Assert.True(zoneId1 >= 0);
            Assert.True(zoneId2 >= 0);

            int enemyId = AddEnemy(store, 0.5f, 0.0f, 100f);

            // Single frame: both zones apply; expect 0.30 + 0.30 = 0.60 additive
            sys.Update(0.1f);
            Assert.Equal(0.60f, store.EnemyCurseArmorReduction[enemyId]);
            Assert.Equal(0.40f, store.EnemyCurseSpeedReduction[enemyId]);
        }

        [Fact]
        public void BlightedGround_ExpiresAfterDuration()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 8,
                radius: 1.4f,
                duration: 2f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,
                speedReduction: 0.20f
            );
            Assert.True(zoneId >= 0);
            Assert.True(store.CorpseEffectActive[zoneId]);

            sys.Update(2.5f);
            buff.ResolveDotDamage();

            Assert.False(store.CorpseEffectActive[zoneId]);
        }

        [Fact]
        public void BlightedGround_LoadsFromJsonConfig()
        {
            // Verifies the JSON config entry by reading the source file directly
            // (corpse_effects.json is not CopyToOutputDirectory'd into the test bin,
            // so we can't use GameConfigLoader.LoadConfig from a test process).
            string jsonPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "Data", "Configs", "corpse_effects.json");
            string jsonContent = System.IO.File.ReadAllText(jsonPath);
            Assert.Contains("\"blighted_ground\"", jsonContent);
            Assert.Contains("\"effectType\": 8", jsonContent);
            Assert.Contains("\"armorReduction\": 0.3", jsonContent);
            Assert.Contains("\"speedReduction\": 0.2", jsonContent);
            Assert.Contains("\"BlightedHorror\"", jsonContent);
            Assert.Contains("\"Wraith\"", jsonContent);
            Assert.Contains("\"PlagueBearer\"", jsonContent);
        }

        [Fact]
        public void BlightedGround_OtherEffectTypesIgnoreNewDebuffFields()
        {
            // Verify that HallowedGround (effectType=6) and ThornyBramble (effectType=7)
            // are NOT affected by the new BlightedGround debuff fields. This guards
            // against regression where the new fields leak into existing effect types.
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            // HallowedGround with armor/speed fields set (they should be ignored)
            int zoneId6 = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,
                radius: 1.5f,
                duration: 5f,
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.99f,    // should be ignored
                speedReduction: 0.99f     // should be ignored
            );
            int enemyId6 = AddEnemy(store, 1.0f, 0.0f, 100f);
            sys.Update(0.1f);
            Assert.Equal(0f, store.EnemyCurseArmorReduction[enemyId6]);
            Assert.Equal(0f, store.EnemyCurseSpeedReduction[enemyId6]);

            // ThornyBramble with armor/speed fields set (they should be ignored)
            int zoneId7 = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 7,
                radius: 1.2f,
                duration: 4f,
                damagePerTick: 3f,
                slowAmount: 0.6f,
                tickInterval: 1f,
                armorReduction: 0.99f,    // should be ignored
                speedReduction: 0.99f     // should be ignored
            );
            int enemyId7 = AddEnemy(store, 1.0f, 0.0f, 100f);
            sys.Update(0.1f);
            Assert.Equal(0f, store.EnemyCurseArmorReduction[enemyId7]);
            Assert.Equal(0f, store.EnemyCurseSpeedReduction[enemyId7]);
        }
    }
}

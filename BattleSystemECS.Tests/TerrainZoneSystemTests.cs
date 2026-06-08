using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 200 / Direction 2 — Elemental Terrain Zone system.
    /// Player-spawned elemental terrain (Frozen Lake / Burning Ground / Toxic Swamp /
    /// Holy Sanctum) with per-element stacks, additive slow, per-tick DoT, and lifetime
    /// decay. Verify fast-path inert state, per-element stack growth + cap, slow clamp,
    /// zone expiry, expand-over-time radius growth, leave-zone stack decay, and the
    /// public DecayStacksOnLeave API.
    /// </summary>
    public class TerrainZoneSystemTests
    {
        private (ComponentStore store, GameConfig config, TerrainZoneSystem sys) CreateEnv()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            var config = new GameConfig();
            // Pre-load a couple of zone defs directly into config (avoids file IO).
            config.TerrainZoneDefs.Add(new GameConfig.TerrainZoneDef
            {
                Id = "frozen_lake",
                Name = "Frozen Lake",
                Element = 1, // Ice
                BaseDps = 8.0f,
                SlowPerStack = 0.12f,
                MaxStacks = 5,
                Lifetime = 10f,
                Radius = 3.5f,
                TickInterval = 1f,
                ExpandOverTime = false
            });
            config.TerrainZoneDefs.Add(new GameConfig.TerrainZoneDef
            {
                Id = "burning_ground",
                Name = "Burning Ground",
                Element = 0, // Fire
                BaseDps = 15.0f,
                SlowPerStack = 0f,
                MaxStacks = 8,
                Lifetime = 8f,
                Radius = 3.0f,
                TickInterval = 1f,
                ExpandOverTime = true
            });
            config.TerrainZoneDefs.Add(new GameConfig.TerrainZoneDef
            {
                Id = "toxic_swamp",
                Name = "Toxic Swamp",
                Element = 2, // Toxic
                BaseDps = 6.0f,
                SlowPerStack = 0.08f,
                MaxStacks = 6,
                Lifetime = 12f,
                Radius = 4.0f,
                TickInterval = 1f,
                ExpandOverTime = false
            });
            var sys = new TerrainZoneSystem(store, config, playerId);
            return (store, config, sys);
        }

        [Fact]
        public void Constructor_NullStore_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new TerrainZoneSystem(null, new GameConfig(), 0));
        }

        [Fact]
        public void Constructor_NullConfig_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new TerrainZoneSystem(new ComponentStore(), null, 0));
        }

        [Fact]
        public void Default_EnemyTerrainState_IsZero()
        {
            var (store, _, _) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            Assert.Equal(0, store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneToxicStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneHolyStacks[eid]);
            Assert.Equal(0f, store.EnemyTerrainZoneSlowTotal[eid]);
            Assert.Equal(0f, store.EnemyTerrainZoneDpsTotal[eid]);
            Assert.Equal(0, store.EnemyInTerrainZone[eid]);
        }

        [Fact]
        public void Update_NoZones_RemainsInert()
        {
            var (store, _, sys) = CreateEnv();
            int eid = store.AddEnemy(0f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            sys.Update(0.016f);
            Assert.Equal(0, store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0f, store.EnemyTerrainZoneSlowTotal[eid]);
            Assert.Equal(0f, store.EnemyTerrainZoneDpsTotal[eid]);
        }

        [Fact]
        public void SpawnTerrainZone_ByDefId_ReturnsValidId()
        {
            var (store, _, sys) = CreateEnv();
            int zoneId = sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            Assert.True(zoneId >= 0);
            Assert.True(store.TerrainZoneActive[zoneId]);
            Assert.Equal(1, store.TerrainZoneElement[zoneId]);
            Assert.Equal(8.0f, store.TerrainZoneBaseDps[zoneId]);
            Assert.Equal(0.12f, store.TerrainZoneSlowPerStack[zoneId]);
            Assert.Equal(5, store.TerrainZoneMaxStacks[zoneId]);
            Assert.Equal(10f, store.TerrainZoneLifetime[zoneId]);
            Assert.Equal(3.5f, store.TerrainZoneRadius[zoneId]);
        }

        [Fact]
        public void SpawnTerrainZone_UnknownId_ReturnsMinusOne()
        {
            var (store, _, sys) = CreateEnv();
            int zoneId = sys.SpawnTerrainZone("nonexistent_zone", 0f, 0f);
            Assert.Equal(-1, zoneId);
        }

        [Fact]
        public void Update_FrozenLake_InsideRadius_AddsIceStack()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            int eid = store.AddEnemy(1f, 0f, 1f, 100f, 100f, 1f, 1, 1);
            // Tick once (deltaTime=1s, interval=1s → one tick fires)
            sys.Update(1.0f);
            Assert.Equal(1, store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(1, store.EnemyInTerrainZone[eid]);
            Assert.Equal(0.12f, store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_FrozenLake_OutsideRadius_StacksStayZero()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            // Enemy far away (radius is 3.5)
            int eid = store.AddEnemy(100f, 100f, 1f, 100f, 100f, 1f, 1, 1);
            sys.Update(1.0f);
            Assert.Equal(0, store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0f, store.EnemyTerrainZoneSlowTotal[eid]);
        }

        [Fact]
        public void Update_StackingAtMaxCap_DoesNotExceed()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f); // MaxStacks=5
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // Tick 8 times to overshoot cap.
            for (int i = 0; i < 8; i++) sys.Update(1.0f);
            Assert.Equal(5, store.EnemyTerrainZoneIceStacks[eid]);
            // Slow = 0.12 * 5 = 0.6 (within 0.9 clamp)
            Assert.Equal(0.6f, store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_SlowClampsAtPointNine()
        {
            var (store, _, sys) = CreateEnv();
            // Spawn a zone with HUGE slow to verify clamp.
            store.AddTerrainZone(0f, 0f, 5f, element: 1, baseDps: 0f, slowPerStack: 0.5f, maxStacks: 10,
                lifetime: 100f, tickInterval: 1f, expandOverTime: false, ownerPlayerId: 0, id: "bigslow");
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            // Stops at 0.9 even though 0.5*5 = 2.5 would otherwise apply.
            Assert.Equal(0.9f, store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_ZoneExpiresAfterLifetime()
        {
            var (store, _, sys) = CreateEnv();
            int zoneId = sys.SpawnTerrainZone("frozen_lake", 0f, 0f); // Lifetime=10s
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // Tick 11 times → exceeds 10s lifetime. 5 inside (5 stacks), 6 outside decay.
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            Assert.Equal(5, store.EnemyTerrainZoneIceStacks[eid]);
            // Move out, then 6 more ticks. Stack decays 1/frame; 5+5=10s used, lifetime 0.
            // 11th frame triggers expiry and 5 decayed → 0 stacks.
            store.PositionX[eid] = 1000f;
            store.PositionY[eid] = 1000f;
            for (int i = 0; i < 6; i++) sys.Update(1.0f);
            Assert.False(store.TerrainZoneActive[zoneId]);
            Assert.Equal(0, store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void Update_ExpandOverTime_GrowsRadius()
        {
            var (store, _, sys) = CreateEnv();
            // Spawn a fresh expand-over-time zone with a long lifetime so cap test isn't truncated.
            int zoneId = store.AddTerrainZone(0f, 0f, 3f, element: 0, baseDps: 1f,
                slowPerStack: 0f, maxStacks: 1, lifetime: 1000f, tickInterval: 1f,
                expandOverTime: true, ownerPlayerId: 0, id: "expand_test");
            float initialR = store.TerrainZoneRadius[zoneId]; // 3.0
            // Tick 2 seconds → radius += 2 * 0.5 = 1.0 → 4.0
            sys.Update(1.0f);
            sys.Update(1.0f);
            Assert.Equal(4.0f, store.TerrainZoneRadius[zoneId], 2);
            // Cap at 1.5x initial (maxR = 4.5) — tick more to hit cap.
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            Assert.Equal(4.5f, store.TerrainZoneRadius[zoneId], 2);
        }

        [Fact]
        public void Update_BurningGround_AppliesFireStacks()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("burning_ground", 0f, 0f);
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            sys.Update(1.0f);
            // Fire element = 0
            Assert.Equal(1, store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void Update_ToxicSwamp_AppliesToxicStacks()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("toxic_swamp", 0f, 0f);
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            sys.Update(1.0f);
            Assert.Equal(1, store.EnemyTerrainZoneToxicStacks[eid]);
            // 0.08 * 1 = 0.08 slow
            Assert.Equal(0.08f, store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_MultipleZones_StackDifferentElements()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);    // Ice
            sys.SpawnTerrainZone("burning_ground", 0f, 0f); // Fire (expand=true)
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            sys.Update(1.0f);
            Assert.Equal(1, store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(1, store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneToxicStacks[eid]);
        }

        [Fact]
        public void Update_LeaveZone_DecaysStacksGradually()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // 5 ticks inside → 5 ice stacks
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            Assert.Equal(5, store.EnemyTerrainZoneIceStacks[eid]);
            // Move enemy far away, then tick (zone still active but enemy is out).
            store.PositionX[eid] = 1000f;
            store.PositionY[eid] = 1000f;
            // First out-of-zone frame skips Decay (InTerrainZone still 1 from last in-zone frame).
            // Then 5 decayed frames: 5→4→3→2→1→0.
            for (int i = 0; i < 6; i++) sys.Update(1.0f);
            Assert.Equal(0, store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void DecayStacksOnLeave_ResetsAllState()
        {
            var (store, _, sys) = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            for (int i = 0; i < 3; i++) sys.Update(1.0f);
            Assert.Equal(3, store.EnemyTerrainZoneIceStacks[eid]);
            sys.DecayStacksOnLeave(eid);
            Assert.Equal(0, store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneToxicStacks[eid]);
            Assert.Equal(0, store.EnemyTerrainZoneHolyStacks[eid]);
            Assert.Equal(0f, store.EnemyTerrainZoneSlowTotal[eid]);
            Assert.Equal(0f, store.EnemyTerrainZoneDpsTotal[eid]);
            Assert.Equal(0, store.EnemyInTerrainZone[eid]);
        }

        [Fact]
        public void DecayStacksOnLeave_NegativeId_NoThrow()
        {
            var (store, _, sys) = CreateEnv();
            sys.DecayStacksOnLeave(-1); // Should not throw
        }

        [Fact]
        public void GetTerrainZoneDef_ReturnsCorrect()
        {
            var (_, config, _) = CreateEnv();
            var def = config.GetTerrainZoneDef("frozen_lake");
            Assert.NotNull(def);
            Assert.Equal("Frozen Lake", def.Name);
            Assert.Equal(1, def.Element);
            Assert.Equal(5, def.MaxStacks);
        }

        [Fact]
        public void GetTerrainZoneDef_Unknown_ReturnsNull()
        {
            var (_, config, _) = CreateEnv();
            Assert.Null(config.GetTerrainZoneDef("missing"));
        }

        [Fact]
        public void GetTerrainZoneDef_EmptyId_ReturnsNull()
        {
            var (_, config, _) = CreateEnv();
            Assert.Null(config.GetTerrainZoneDef(""));
        }

        [Fact]
        public void Update_LongFrameTickTimer_Handled()
        {
            var (store, _, sys) = CreateEnv();
            // Long-lifetime zone so 8-tick safety cap kicks in (not lifetime expiry).
            int zoneId = store.AddTerrainZone(0f, 0f, 5f, element: 1, baseDps: 0f,
                slowPerStack: 0.1f, maxStacks: 5, lifetime: 1000f, tickInterval: 1f,
                expandOverTime: false, ownerPlayerId: 0, id: "longtick");
            int eid = store.AddEnemy(1f, 0f, 1f, 1000f, 1000f, 1f, 1, 1);
            // Huge single-frame delta — internal safety cap should prevent runaway.
            sys.Update(100f);
            // Stacks should clamp at MaxStacks=5 even if 100 ticks would have fired.
            Assert.Equal(5, store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void AddTerrainZone_FullPool_ReturnsMinusOne()
        {
            var (store, config, _) = CreateEnv();
            // Fill the pool.
            int filled = 0;
            for (int i = 0; i < 200; i++)
            {
                int zid = store.AddTerrainZone(0f, 0f, 3f, 0, 1f, 0f, 1, 10f, 1f, false, 0, "x");
                if (zid >= 0) filled++;
                else break;
            }
            Assert.Equal(200, filled);
            int next = store.AddTerrainZone(0f, 0f, 3f, 0, 1f, 0f, 1, 10f, 1f, false, 0, "y");
            Assert.Equal(-1, next);
        }

        [Fact]
        public void RemoveTerrainZone_ClearsAllFields()
        {
            var (store, _, sys) = CreateEnv();
            int zid = sys.SpawnTerrainZone("frozen_lake", 5f, 5f);
            store.RemoveTerrainZone(zid);
            Assert.False(store.TerrainZoneActive[zid]);
            Assert.Equal(0f, store.TerrainZoneX[zid]);
            Assert.Equal(0f, store.TerrainZoneY[zid]);
            Assert.Equal(0f, store.TerrainZoneRadius[zid]);
            Assert.Equal(0, store.TerrainZoneElement[zid]);
            Assert.Equal(0f, store.TerrainZoneBaseDps[zid]);
            Assert.Null(store.TerrainZoneId[zid]);
        }
    }
}

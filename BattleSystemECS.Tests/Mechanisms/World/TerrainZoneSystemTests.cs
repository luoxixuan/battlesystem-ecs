using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Config;

namespace BattleSystemECS.Tests.Mechanisms.World
{
    /// <summary>
    /// Tests for Round 200 / Direction 2 — Elemental Terrain Zone system.
    /// Player-spawned elemental terrain (Frozen Lake / Burning Ground / Toxic Swamp /
    /// Holy Sanctum) with per-element stacks, additive slow, per-tick DoT, and lifetime
    /// decay. Verify fast-path inert state, per-element stack growth + cap, slow clamp,
    /// zone expiry, expand-over-time radius growth, leave-zone stack decay, and the
    /// public DecayStacksOnLeave API.
    /// </summary>
    public class TerrainZoneSystemTests : BattleTestBase
    {
        private TerrainZoneSystem CreateEnv()
        {
            int playerId = Store.CreateEntity();
            // Pre-load a couple of zone defs directly into Config (avoids file IO).
            Config.TerrainZoneDefs.Add(new GameConfig.TerrainZoneDef
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
            Config.TerrainZoneDefs.Add(new GameConfig.TerrainZoneDef
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
            Config.TerrainZoneDefs.Add(new GameConfig.TerrainZoneDef
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
            return new TerrainZoneSystem(Store, Config, playerId);
        }

        private int SpawnEnemy(float x, float y, float health = 100f)
            => Enemy(e =>
            {
                e.X = x;
                e.Y = y;
                e.MoveSpeed = 1f;
                e.Health = health;
                e.MaxHealth = health;
                e.Damage = 1f;
                e.GoldReward = 1;
            });

        [Fact]
        public void Constructor_NullStore_Throws()
        {
            // 保留独立 config：构造器空参守卫需要非基类实例作为非空参数。
            Assert.Throws<ArgumentNullException>(() => new TerrainZoneSystem(null, new GameConfig(), 0));
        }

        [Fact]
        public void Constructor_NullConfig_Throws()
        {
            // 保留独立 store：构造器空参守卫需要非基类实例作为非空参数。
            Assert.Throws<ArgumentNullException>(() => new TerrainZoneSystem(new ComponentStore(), null, 0));
        }

        [Fact]
        public void Default_EnemyTerrainState_IsZero()
        {
            _ = CreateEnv();
            int eid = SpawnEnemy(0f, 0f);
            Assert.Equal(0, Store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneToxicStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneHolyStacks[eid]);
            Assert.Equal(0f, Store.EnemyTerrainZoneSlowTotal[eid]);
            Assert.Equal(0f, Store.EnemyTerrainZoneDpsTotal[eid]);
            Assert.Equal(0, Store.EnemyInTerrainZone[eid]);
        }

        [Fact]
        public void Update_NoZones_RemainsInert()
        {
            var sys = CreateEnv();
            int eid = SpawnEnemy(0f, 0f);
            sys.Update(0.016f);
            Assert.Equal(0, Store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0f, Store.EnemyTerrainZoneSlowTotal[eid]);
            Assert.Equal(0f, Store.EnemyTerrainZoneDpsTotal[eid]);
        }

        [Fact]
        public void SpawnTerrainZone_ByDefId_ReturnsValidId()
        {
            var sys = CreateEnv();
            int zoneId = sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            Assert.True(zoneId >= 0);
            Assert.True(Store.TerrainZoneActive[zoneId]);
            Assert.Equal(1, Store.TerrainZoneElement[zoneId]);
            Assert.Equal(8.0f, Store.TerrainZoneBaseDps[zoneId]);
            Assert.Equal(0.12f, Store.TerrainZoneSlowPerStack[zoneId]);
            Assert.Equal(5, Store.TerrainZoneMaxStacks[zoneId]);
            Assert.Equal(10f, Store.TerrainZoneLifetime[zoneId]);
            Assert.Equal(3.5f, Store.TerrainZoneRadius[zoneId]);
        }

        [Fact]
        public void SpawnTerrainZone_UnknownId_ReturnsMinusOne()
        {
            var sys = CreateEnv();
            int zoneId = sys.SpawnTerrainZone("nonexistent_zone", 0f, 0f);
            Assert.Equal(-1, zoneId);
        }

        [Fact]
        public void Update_FrozenLake_InsideRadius_AddsIceStack()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            int eid = SpawnEnemy(1f, 0f);
            // Tick once (deltaTime=1s, interval=1s → one tick fires)
            sys.Update(1.0f);
            Assert.Equal(1, Store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(1, Store.EnemyInTerrainZone[eid]);
            Assert.Equal(0.12f, Store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_FrozenLake_OutsideRadius_StacksStayZero()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            // Enemy far away (radius is 3.5)
            int eid = SpawnEnemy(100f, 100f);
            sys.Update(1.0f);
            Assert.Equal(0, Store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0f, Store.EnemyTerrainZoneSlowTotal[eid]);
        }

        [Fact]
        public void Update_StackingAtMaxCap_DoesNotExceed()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f); // MaxStacks=5
            int eid = SpawnEnemy(1f, 0f, 1000f);
            // Tick 8 times to overshoot cap.
            for (int i = 0; i < 8; i++) sys.Update(1.0f);
            Assert.Equal(5, Store.EnemyTerrainZoneIceStacks[eid]);
            // Slow = 0.12 * 5 = 0.6 (within 0.9 clamp)
            Assert.Equal(0.6f, Store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_SlowClampsAtPointNine()
        {
            var sys = CreateEnv();
            // Spawn a zone with HUGE slow to verify clamp.
            Store.AddTerrainZone(0f, 0f, 5f, element: 1, baseDps: 0f, slowPerStack: 0.5f, maxStacks: 10,
                lifetime: 100f, tickInterval: 1f, expandOverTime: false, ownerPlayerId: 0, id: "bigslow");
            int eid = SpawnEnemy(1f, 0f, 1000f);
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            // Stops at 0.9 even though 0.5*5 = 2.5 would otherwise apply.
            Assert.Equal(0.9f, Store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_ZoneExpiresAfterLifetime()
        {
            var sys = CreateEnv();
            int zoneId = sys.SpawnTerrainZone("frozen_lake", 0f, 0f); // Lifetime=10s
            int eid = SpawnEnemy(1f, 0f, 1000f);
            // Tick 11 times → exceeds 10s lifetime. 5 inside (5 stacks), 6 outside decay.
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            Assert.Equal(5, Store.EnemyTerrainZoneIceStacks[eid]);
            // Move out, then 6 more ticks. Stack decays 1/frame; 5+5=10s used, lifetime 0.
            // 11th frame triggers expiry and 5 decayed → 0 stacks.
            Store.PositionX[eid] = 1000f;
            Store.PositionY[eid] = 1000f;
            for (int i = 0; i < 6; i++) sys.Update(1.0f);
            Assert.False(Store.TerrainZoneActive[zoneId]);
            Assert.Equal(0, Store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void Update_ExpandOverTime_GrowsRadius()
        {
            var sys = CreateEnv();
            // Spawn a fresh expand-over-time zone with a long lifetime so cap test isn't truncated.
            int zoneId = Store.AddTerrainZone(0f, 0f, 3f, element: 0, baseDps: 1f,
                slowPerStack: 0f, maxStacks: 1, lifetime: 1000f, tickInterval: 1f,
                expandOverTime: true, ownerPlayerId: 0, id: "expand_test");
            float initialR = Store.TerrainZoneRadius[zoneId]; // 3.0
            // Tick 2 seconds → radius += 2 * 0.5 = 1.0 → 4.0
            sys.Update(1.0f);
            sys.Update(1.0f);
            Assert.Equal(4.0f, Store.TerrainZoneRadius[zoneId], 2);
            // Cap at 1.5x initial (maxR = 4.5) — tick more to hit cap.
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            Assert.Equal(4.5f, Store.TerrainZoneRadius[zoneId], 2);
        }

        [Fact]
        public void Update_BurningGround_AppliesFireStacks()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("burning_ground", 0f, 0f);
            int eid = SpawnEnemy(1f, 0f, 1000f);
            sys.Update(1.0f);
            // Fire element = 0
            Assert.Equal(1, Store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void Update_ToxicSwamp_AppliesToxicStacks()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("toxic_swamp", 0f, 0f);
            int eid = SpawnEnemy(1f, 0f, 1000f);
            sys.Update(1.0f);
            Assert.Equal(1, Store.EnemyTerrainZoneToxicStacks[eid]);
            // 0.08 * 1 = 0.08 slow
            Assert.Equal(0.08f, Store.EnemyTerrainZoneSlowTotal[eid], 3);
        }

        [Fact]
        public void Update_MultipleZones_StackDifferentElements()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);    // Ice
            sys.SpawnTerrainZone("burning_ground", 0f, 0f); // Fire (expand=true)
            int eid = SpawnEnemy(1f, 0f, 1000f);
            sys.Update(1.0f);
            Assert.Equal(1, Store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(1, Store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneToxicStacks[eid]);
        }

        [Fact]
        public void Update_LeaveZone_DecaysStacksGradually()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            int eid = SpawnEnemy(1f, 0f, 1000f);
            // 5 ticks inside → 5 ice stacks
            for (int i = 0; i < 5; i++) sys.Update(1.0f);
            Assert.Equal(5, Store.EnemyTerrainZoneIceStacks[eid]);
            // Move enemy far away, then tick (zone still active but enemy is out).
            Store.PositionX[eid] = 1000f;
            Store.PositionY[eid] = 1000f;
            // First out-of-zone frame skips Decay (InTerrainZone still 1 from last in-zone frame).
            // Then 5 decayed frames: 5→4→3→2→1→0.
            for (int i = 0; i < 6; i++) sys.Update(1.0f);
            Assert.Equal(0, Store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void DecayStacksOnLeave_ResetsAllState()
        {
            var sys = CreateEnv();
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            int eid = SpawnEnemy(1f, 0f, 1000f);
            for (int i = 0; i < 3; i++) sys.Update(1.0f);
            Assert.Equal(3, Store.EnemyTerrainZoneIceStacks[eid]);
            sys.DecayStacksOnLeave(eid);
            Assert.Equal(0, Store.EnemyTerrainZoneFireStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneToxicStacks[eid]);
            Assert.Equal(0, Store.EnemyTerrainZoneHolyStacks[eid]);
            Assert.Equal(0f, Store.EnemyTerrainZoneSlowTotal[eid]);
            Assert.Equal(0f, Store.EnemyTerrainZoneDpsTotal[eid]);
            Assert.Equal(0, Store.EnemyInTerrainZone[eid]);
        }

        [Fact]
        public void DecayStacksOnLeave_NegativeId_DoesNotTouchValidSlot()
        {
            var sys = CreateEnv();
            // 先建立合法的 zone 状态：3 层冰霜 + 累计减速。
            sys.SpawnTerrainZone("frozen_lake", 0f, 0f);
            int eid = SpawnEnemy(1f, 0f, 1000f);
            for (int i = 0; i < 3; i++) sys.Update(1.0f);
            Assert.Equal(3, Store.EnemyTerrainZoneIceStacks[eid]);
            float slowBefore = Store.EnemyTerrainZoneSlowTotal[eid];

            // 负 id 必须 no-op，合法槽位的聚合状态不得被破坏。
            sys.DecayStacksOnLeave(-1);

            Assert.Equal(3, Store.EnemyTerrainZoneIceStacks[eid]);
            Assert.Equal(slowBefore, Store.EnemyTerrainZoneSlowTotal[eid]);
            Assert.Equal(1, Store.EnemyInTerrainZone[eid]);
        }

        [Fact]
        public void GetTerrainZoneDef_ReturnsCorrect()
        {
            _ = CreateEnv();
            var def = Config.GetTerrainZoneDef("frozen_lake");
            Assert.NotNull(def);
            Assert.Equal("Frozen Lake", def.Name);
            Assert.Equal(1, def.Element);
            Assert.Equal(5, def.MaxStacks);
        }

        [Fact]
        public void GetTerrainZoneDef_Unknown_ReturnsNull()
        {
            _ = CreateEnv();
            Assert.Null(Config.GetTerrainZoneDef("missing"));
        }

        [Fact]
        public void GetTerrainZoneDef_EmptyId_ReturnsNull()
        {
            _ = CreateEnv();
            Assert.Null(Config.GetTerrainZoneDef(""));
        }

        [Fact]
        public void Update_LongFrameTickTimer_Handled()
        {
            var sys = CreateEnv();
            // Long-lifetime zone so 8-tick safety cap kicks in (not lifetime expiry).
            int zoneId = Store.AddTerrainZone(0f, 0f, 5f, element: 1, baseDps: 0f,
                slowPerStack: 0.1f, maxStacks: 5, lifetime: 1000f, tickInterval: 1f,
                expandOverTime: false, ownerPlayerId: 0, id: "longtick");
            int eid = SpawnEnemy(1f, 0f, 1000f);
            // Huge single-frame delta — internal safety cap should prevent runaway.
            sys.Update(100f);
            // Stacks should clamp at MaxStacks=5 even if 100 ticks would have fired.
            Assert.Equal(5, Store.EnemyTerrainZoneIceStacks[eid]);
        }

        [Fact]
        public void AddTerrainZone_FullPool_ReturnsMinusOne()
        {
            _ = CreateEnv();
            // Fill the pool.
            int filled = 0;
            for (int i = 0; i < 200; i++)
            {
                int zid = Store.AddTerrainZone(0f, 0f, 3f, 0, 1f, 0f, 1, 10f, 1f, false, 0, "x");
                if (zid >= 0) filled++;
                else break;
            }
            Assert.Equal(200, filled);
            int next = Store.AddTerrainZone(0f, 0f, 3f, 0, 1f, 0f, 1, 10f, 1f, false, 0, "y");
            Assert.Equal(-1, next);
        }

        [Fact]
        public void RemoveTerrainZone_ClearsAllFields()
        {
            var sys = CreateEnv();
            int zid = sys.SpawnTerrainZone("frozen_lake", 5f, 5f);
            Store.RemoveTerrainZone(zid);
            Assert.False(Store.TerrainZoneActive[zid]);
            Assert.Equal(0f, Store.TerrainZoneX[zid]);
            Assert.Equal(0f, Store.TerrainZoneY[zid]);
            Assert.Equal(0f, Store.TerrainZoneRadius[zid]);
            Assert.Equal(0, Store.TerrainZoneElement[zid]);
            Assert.Equal(0f, Store.TerrainZoneBaseDps[zid]);
            Assert.Null(Store.TerrainZoneId[zid]);
        }
    }
}

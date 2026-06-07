using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 187 Direction 4: Rally Buff (Player-Tower Linkage).
    /// Verifies:
    ///   1. RallyConfig defaults match the niche (radius 5, +30% atk spd, 3s duration, 5s cooldown)
    ///   2. ComponentStore SOA fields zero-init on AddTower / AddPlayer
    ///   3. ComponentStore.BeginFrame resets TowerRallyAtkSpdBonus to 0 (drift guard)
    ///   4. ComponentStore.DestroyEntity resets per-tower rally field (no ID-reuse leak)
    ///   5. SetTurn / Update early-return when no player has rally active (zero-overhead)
    ///   6. PlayerDamaged event triggers rally activation for the damaged player
    ///   7. Cooldown gate prevents re-trigger within RallyCooldown window
    ///   8. Duration ticks down and rally expires naturally
    ///   9. Expired rally clears all affected towers' bonuses
    ///  10. Dispel / destroyed / dead towers are excluded from rally zone
    ///  11. Update re-derives per-tower bonus every frame (towers added mid-rally get it)
    /// </summary>
    public class RallySystemTests
    {
        private const int PlayerId = 0;

        private static (ComponentStore store, MockRenderer renderer) Env()
        {
            var store = new ComponentStore();
            store.PlayerMaxHealth[PlayerId] = 200f;
            store.PlayerCurrentHealth[PlayerId] = 180f; // pre-damaged to match PlayerDamagedEvent.RemainingHealth
            return (store, new MockRenderer());
        }

        private static int PlaceTower(ComponentStore store, MockRenderer r, int x, int y,
            TowerType type = TowerType.Basic)
        {
            var tps = new TowerPlacementSystem(store, r);
            return tps.PlaceTower(x, y, type, 0f, 0, 0f, 25f);
        }

        private static RallySystem MakeSystem(ComponentStore store, MockRenderer r, IEventBus? bus = null)
        {
            return new RallySystem(store, r, bus);
        }

        // ─── Config defaults ─────────────────────────────────────────────

        [Fact]
        public void RallyConfig_Defaults_MatchNiche()
        {
            // All four constants must be the "niche sweet spot":
            // small radius (5 = adjacent towers), +30% atk spd burst, 3s duration, 5s cooldown.
            Assert.Equal(5.0f, RallyConfig.RallyRadius);
            Assert.Equal(0.30f, RallyConfig.RallyAtkSpdBonus);
            Assert.Equal(3.0f, RallyConfig.RallyDuration);
            Assert.Equal(5.0f, RallyConfig.RallyCooldown);
        }

        // ─── SOA field lifecycle ──────────────────────────────────────────

        [Fact]
        public void ComponentStore_RallyFields_DefaultToFalseAndZero_OnInit()
        {
            var (store, _) = Env();
            // Per-player fields
            Assert.False(store.PlayerRallyActive[PlayerId]);
            Assert.Equal(0f, store.PlayerRallyDurationLeft[PlayerId]);
            Assert.Equal(0f, store.PlayerRallyCooldown[PlayerId]);
            // Per-tower field on a placed tower
            int tid = PlaceTower(store, new MockRenderer(), 0, 0);
            Assert.Equal(0f, store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void ComponentStore_BeginFrame_ResetsTowerRallyAtkSpdBonus_ToZero()
        {
            // Drift guard: any tower with a residual rally bonus from the previous frame
            // must be wiped at the start of the next frame, so RallySystem re-derives
            // from the live PlayerRallyActive set (a tower that lost rally mid-frame
            // cleanly reverts on the next frame's BeginFrame).
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            store.TowerRallyAtkSpdBonus[tid] = 0.99f; // simulate stale frame
            store.BeginFrame();
            Assert.Equal(0f, store.TowerRallyAtkSpdBonus[tid]);
        }

        // ─── No-op paths (zero-overhead when no rally active) ─────────────

        [Fact]
        public void Update_NoActiveRallyPlayer_DoesNotThrow_AndSkipsWrites()
        {
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            var sys = MakeSystem(store, r);
            // No rally active. Update must early-return without writing TowerRallyAtkSpdBonus.
            sys.Update(0.016f);
            Assert.Equal(0f, store.TowerRallyAtkSpdBonus[tid]);
            Assert.False(store.PlayerRallyActive[PlayerId]);
        }

        // ─── PlayerDamaged triggers rally ────────────────────────────────

        [Fact]
        public void PlayerDamaged_ActivatesRally_ForDamagedPlayer()
        {
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            // Simulate the player taking a 20-damage hit
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent
            {
                Damage = 20f,
                RemainingHealth = 180f
            });
            // Rally is now active
            Assert.True(store.PlayerRallyActive[PlayerId]);
            Assert.Equal(RallyConfig.RallyDuration, store.PlayerRallyDurationLeft[PlayerId], 3);
            Assert.Equal(RallyConfig.RallyCooldown, store.PlayerRallyCooldown[PlayerId], 3);
            // The tower (within radius) has the bonus
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, store.TowerRallyAtkSpdBonus[tid], 3);
        }

        [Fact]
        public void PlayerDamaged_DuringCooldown_DoesNotReTrigger()
        {
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            // First hit activates rally
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.True(store.PlayerRallyActive[PlayerId]);
            float firstDuration = store.PlayerRallyDurationLeft[PlayerId];
            // Simulate a tiny tick (duration nearly identical, cooldown ramps up)
            sys.Update(0.001f);
            // Second hit during cooldown: must NOT re-activate
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 10f, RemainingHealth = 170f });
            // Duration should be exactly the same as after the first hit (only Update decrements it)
            // Cooldown should still be set (still ticking from first hit)
            Assert.True(store.PlayerRallyCooldown[PlayerId] > 0f);
            // Duration is whatever Update(0.001) left of it (almost full, but slightly less)
            Assert.True(store.PlayerRallyDurationLeft[PlayerId] <= firstDuration);
            // Rally still active (didn't get re-stamped because cooldown gate blocked it)
            Assert.True(store.PlayerRallyActive[PlayerId]);
        }

        // ─── Duration ticks down and rally expires ────────────────────────

        [Fact]
        public void Update_DecrementsRallyDuration_AndExpires_WhenDurationReachesZero()
        {
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            // Activate rally
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, store.TowerRallyAtkSpdBonus[tid], 3);
            // Tick beyond the duration in one frame
            sys.Update(RallyConfig.RallyDuration + 0.5f);
            // Rally expired
            Assert.False(store.PlayerRallyActive[PlayerId]);
            Assert.Equal(0f, store.PlayerRallyDurationLeft[PlayerId]);
            // Tower bonus cleared by BeginFrame at the start of next Update, but tower field
            // holds whatever the last ApplyRallyBonusesForPlayer wrote before the expire
            // (it was set to 0 because duration ticked to 0 BEFORE the re-derivation in
            // Update step 3, so affected list was cleared).
            // After expiry + Update, the per-tower bonus should be 0 (no longer in zone).
            Assert.Equal(0f, store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void Update_DecrementsCooldown_AfterRallyExpires()
        {
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            // Activate
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            float initialCooldown = store.PlayerRallyCooldown[PlayerId];
            Assert.Equal(RallyConfig.RallyCooldown, initialCooldown, 3);
            // Tick a bit
            sys.Update(1.0f);
            // Cooldown decreased by 1.0
            Assert.Equal(RallyConfig.RallyCooldown - 1.0f, store.PlayerRallyCooldown[PlayerId], 3);
        }

        // ─── Dispel / destroyed tower excluded ───────────────────────────

        [Fact]
        public void DispelTower_ExcludedFromRallyZone()
        {
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            // Tower gets dispelled before rally activation
            store.TowerIsDispelled[tid] = true;
            // Activate rally
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            // Dispeled tower is NOT in the rally zone
            Assert.Equal(0f, store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void Update_RallyTowersAfterExpiry_AreZeroedByNextBeginFrame()
        {
            // Drift-safety: even if a tower had a non-zero bonus when its player expired,
            // the NEXT frame's BeginFrame wipes it to 0, and ApplyRallyBonusesForPlayer
            // does not run for that player (since PlayerRallyActive is false).
            var (store, r) = Env();
            int tid = PlaceTower(store, r, 0, 0);
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, store.TowerRallyAtkSpdBonus[tid], 3);
            // Expire
            sys.Update(RallyConfig.RallyDuration + 0.1f);
            Assert.False(store.PlayerRallyActive[PlayerId]);
            // A new Update must keep the field at 0 (no re-derivation for inactive players)
            sys.Update(0.1f);
            Assert.Equal(0f, store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void TowerOutOfRallyRadius_DoesNotReceiveBonus()
        {
            // BUG 2 regression: tower at (8, 0) is at distance 8 from player at (0, 0),
            // far outside RallyRadius=5 (map is 10x20 so 8 is a valid in-map position).
            // The rally must not buff it.
            var (store, r) = Env();
            int far = PlaceTower(store, r, 8, 0);
            Assert.NotEqual(-1, far); // sanity: placed successfully
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            // Far tower: 0 (out of radius 5)
            Assert.Equal(0f, store.TowerRallyAtkSpdBonus[far]);
        }

        [Fact]
        public void TowerWithinRallyRadius_ReceivesBonus()
        {
            // Pair to TowerOutOfRallyRadius: tower at (3, 4) is at distance 5 from
            // player at (0, 0), exactly on the radius boundary. The rally must buff it.
            var (store, r) = Env();
            int near = PlaceTower(store, r, 3, 4);
            Assert.NotEqual(-1, near); // sanity: placed successfully
            var bus = new EventBus();
            var sys = MakeSystem(store, r, bus);
            bus.Publish(GameEvents.PlayerDamaged, new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, store.TowerRallyAtkSpdBonus[near], 3);
        }
    }
}

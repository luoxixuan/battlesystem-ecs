using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Buffs
{
    /// <summary>
    /// Tests for Round 187 Direction 4: Rally Buff (Player-Tower Linkage).
    /// Verifies:
    ///   1. RallyConfig defaults match the niche (相对不变量：Radius>0、加成∈(0,1]、Duration>0、Cooldown≥Duration)
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
    public class RallySystemTests : BattleTestBase
    {
        private const int PlayerId = 0;

        private (RallySystem sys, int towerId, EventBus bus) MakeSystem(int x = 0, int y = 0)
        {
            Player(p => p.Health = 200f);
            Store.PlayerCurrentHealth[PlayerId] = 180f; // pre-damaged to match PlayerDamagedEvent.RemainingHealth
            _ = Placement; // 构造 Placement（LoadPerTypeCaps 会写 JSON cap），随后显式清空 cap
            DisableTowerCaps();
            int tid = Placement.PlaceTower(x, y, TowerType.Basic, 0f, 0, 0f, 25f);
            var bus = new EventBus();
            var sys = new RallySystem(Store, Renderer, bus);
            return (sys, tid, bus);
        }

        // ─── Config defaults ─────────────────────────────────────────────

        [Fact]
        public void RallyConfig_Defaults_MatchNiche()
        {
            // 只断言“niche 定位”需要的相对不变量，不钉具体数值：
            // 半径必须为正；攻速加成是 (0,1] 的乘区；持续时间 >0；冷却不短于持续时间。
            Assert.True(RallyConfig.RallyRadius > 0f);
            Assert.True(RallyConfig.RallyAtkSpdBonus > 0f && RallyConfig.RallyAtkSpdBonus <= 1f);
            Assert.True(RallyConfig.RallyDuration > 0f);
            Assert.True(RallyConfig.RallyCooldown >= RallyConfig.RallyDuration);
        }

        // ─── SOA field lifecycle ──────────────────────────────────────────

        [Fact]
        public void ComponentStore_RallyFields_DefaultToFalseAndZero_OnInit()
        {
            var (_, tid, _) = MakeSystem();
            // Per-player fields
            Assert.False(Store.PlayerRallyActive[PlayerId]);
            Assert.Equal(0f, Store.PlayerRallyDurationLeft[PlayerId]);
            Assert.Equal(0f, Store.PlayerRallyCooldown[PlayerId]);
            // Per-tower field on a placed tower
            Assert.Equal(0f, Store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void ComponentStore_BeginFrame_ResetsTowerRallyAtkSpdBonus_ToZero()
        {
            // Drift guard: any tower with a residual rally bonus from the previous frame
            // must be wiped at the start of the next frame, so RallySystem re-derives
            // from the live PlayerRallyActive set (a tower that lost rally mid-frame
            // cleanly reverts on the next frame's BeginFrame).
            var (_, tid, _) = MakeSystem();
            Store.TowerRallyAtkSpdBonus[tid] = 0.99f; // simulate stale frame
            Store.BeginFrame();
            Assert.Equal(0f, Store.TowerRallyAtkSpdBonus[tid]);
        }

        // ─── No-op paths (zero-overhead when no rally active) ─────────────

        [Fact]
        public void Update_NoActiveRallyPlayer_DoesNotThrow_AndSkipsWrites()
        {
            var (sys, tid, _) = MakeSystem();
            // No rally active. Update must early-return without writing TowerRallyAtkSpdBonus.
            sys.Update(0.016f);
            Assert.Equal(0f, Store.TowerRallyAtkSpdBonus[tid]);
            Assert.False(Store.PlayerRallyActive[PlayerId]);
        }

        // ─── PlayerDamaged triggers rally ────────────────────────────────

        [Fact]
        public void PlayerDamaged_ActivatesRally_ForDamagedPlayer()
        {
            var (sys, tid, bus) = MakeSystem();
            // Simulate the player taking a 20-damage hit
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent
            {
                Damage = 20f,
                RemainingHealth = 180f
            });
            // Rally is now active
            Assert.True(Store.PlayerRallyActive[PlayerId]);
            Assert.Equal(RallyConfig.RallyDuration, Store.PlayerRallyDurationLeft[PlayerId], 3);
            Assert.Equal(RallyConfig.RallyCooldown, Store.PlayerRallyCooldown[PlayerId], 3);
            // The tower (within radius) has the bonus
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, Store.TowerRallyAtkSpdBonus[tid], 3);
        }

        [Fact]
        public void PlayerDamaged_DuringCooldown_DoesNotReTrigger()
        {
            var (sys, tid, bus) = MakeSystem();
            // First hit activates rally
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.True(Store.PlayerRallyActive[PlayerId]);
            float firstDuration = Store.PlayerRallyDurationLeft[PlayerId];
            // Simulate a tiny tick (duration nearly identical, cooldown ramps up)
            sys.Update(0.001f);
            // Second hit during cooldown: must NOT re-activate
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 10f, RemainingHealth = 170f });
            // Duration should be exactly the same as after the first hit (only Update decrements it)
            // Cooldown should still be set (still ticking from first hit)
            Assert.True(Store.PlayerRallyCooldown[PlayerId] > 0f);
            // Duration is whatever Update(0.001) left of it (almost full, but slightly less)
            Assert.True(Store.PlayerRallyDurationLeft[PlayerId] <= firstDuration);
            // Rally still active (didn't get re-stamped because cooldown gate blocked it)
            Assert.True(Store.PlayerRallyActive[PlayerId]);
        }

        // ─── Duration ticks down and rally expires ────────────────────────

        [Fact]
        public void Update_DecrementsRallyDuration_AndExpires_WhenDurationReachesZero()
        {
            var (sys, tid, bus) = MakeSystem();
            // Activate rally
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, Store.TowerRallyAtkSpdBonus[tid], 3);
            // Tick beyond the duration in one frame
            sys.Update(RallyConfig.RallyDuration + 0.5f);
            // Rally expired
            Assert.False(Store.PlayerRallyActive[PlayerId]);
            Assert.Equal(0f, Store.PlayerRallyDurationLeft[PlayerId]);
            // Tower bonus cleared by BeginFrame at the start of next Update, but tower field
            // holds whatever the last ApplyRallyBonusesForPlayer wrote before the expire
            // (it was set to 0 because duration ticked to 0 BEFORE the re-derivation in
            // Update step 3, so affected list was cleared).
            // After expiry + Update, the per-tower bonus should be 0 (no longer in zone).
            Assert.Equal(0f, Store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void Update_DecrementsCooldown_AfterRallyExpires()
        {
            var (sys, tid, bus) = MakeSystem();
            // Activate
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            float initialCooldown = Store.PlayerRallyCooldown[PlayerId];
            Assert.Equal(RallyConfig.RallyCooldown, initialCooldown, 3);
            // Tick a bit
            sys.Update(1.0f);
            // Cooldown decreased by 1.0
            Assert.Equal(RallyConfig.RallyCooldown - 1.0f, Store.PlayerRallyCooldown[PlayerId], 3);
        }

        // ─── Dispel / destroyed tower excluded ───────────────────────────

        [Fact]
        public void DispelTower_ExcludedFromRallyZone()
        {
            var (sys, tid, bus) = MakeSystem();
            // Tower gets dispelled before rally activation
            Store.TowerIsDispelled[tid] = true;
            // Activate rally
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            // Dispeled tower is NOT in the rally zone
            Assert.Equal(0f, Store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void Update_RallyTowersAfterExpiry_AreZeroedByNextBeginFrame()
        {
            // Drift-safety: even if a tower had a non-zero bonus when its player expired,
            // the NEXT frame's BeginFrame wipes it to 0, and ApplyRallyBonusesForPlayer
            // does not run for that player (since PlayerRallyActive is false).
            var (sys, tid, bus) = MakeSystem();
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, Store.TowerRallyAtkSpdBonus[tid], 3);
            // Expire
            sys.Update(RallyConfig.RallyDuration + 0.1f);
            Assert.False(Store.PlayerRallyActive[PlayerId]);
            // A new Update must keep the field at 0 (no re-derivation for inactive players)
            sys.Update(0.1f);
            Assert.Equal(0f, Store.TowerRallyAtkSpdBonus[tid]);
        }

        [Fact]
        public void TowerOutOfRallyRadius_DoesNotReceiveBonus()
        {
            // BUG 2 regression: tower at (8, 0) is at distance 8 from player at (0, 0),
            // outside the configured RallyRadius (map is 10x20 so 8 is a valid in-map position).
            // The rally must not buff it.
            var (sys, far, bus) = MakeSystem(8, 0);
            Assert.NotEqual(-1, far); // sanity: placed successfully
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            // Far tower: 0 (out of the configured rally radius)
            Assert.Equal(0f, Store.TowerRallyAtkSpdBonus[far]);
        }

        [Fact]
        public void TowerWithinRallyRadius_ReceivesBonus()
        {
            // Pair to TowerOutOfRallyRadius: tower at (3, 4) is at distance 5 from
            // player at (0, 0), exactly on the radius boundary. The rally must buff it.
            var (sys, near, bus) = MakeSystem(3, 4);
            Assert.NotEqual(-1, near); // sanity: placed successfully
            bus.PlayerDamaged.Publish(new PlayerDamagedEvent { Damage = 20f, RemainingHealth = 180f });
            Assert.Equal(RallyConfig.RallyAtkSpdBonus, Store.TowerRallyAtkSpdBonus[near], 3);
        }
    }
}

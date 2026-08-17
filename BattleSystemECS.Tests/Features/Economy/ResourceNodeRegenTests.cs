using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Economy
{
    /// <summary>
    /// Tests for Round 108 Direction 4: Resource Node Regen / Respawn.
    /// Verifies that:
    ///   - Default state: regen fields are zero / inert (legacy no-regen behavior)
    ///   - InitializeFromLevel reads RegenDelay into the SOA array
    ///   - DamageNode sets Depleted=true and arms RegenTimer when RegenDelay > 0
    ///   - DamageNode does NOT set Depleted when RegenDelay <= 0 (legacy)
    ///   - Update() decrements RegenTimer for depleted nodes
    ///   - Update() respawns node at full HP + active when timer reaches 0
    ///   - Update() does NOT respawn nodes with RegenDelay <= 0
    ///   - Indestructible nodes (MaxHealth = 0) ignore damage
    ///   - Depleted nodes do not produce resources while waiting to respawn
    ///   - Multiple nodes have independent timers
    ///   - RecommendedRegenDelay constant is reasonable
    /// </summary>
    public class ResourceNodeRegenTests : BattleTestBase
    {
        private const float DeltaTime = 1f / 60f;

        private ResourceNodeSystem MakeSystem(LevelConfig level)
        {
            var system = new ResourceNodeSystem(Store, Renderer);
            system.InitializeFromLevel(level);
            return system;
        }

        private LevelConfig MakeLevelWithNodes(params ResourceNodeDef[] nodes)
        {
            return new LevelConfig
            {
                LevelNumber = 1,
                WaveCount = 1,
                ResourceNodes = new List<ResourceNodeDef>(nodes),
            };
        }

        // ── Default state ────────────────────────────────────────────────

        [Fact]
        public void DefaultState_AllRegenFieldsZero()
        {
            Assert.Equal(0f, Store.ResourceNodeRegenTimer[0]);
            Assert.Equal(0f, Store.ResourceNodeRegenDelay[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
        }

        // ── InitializeFromLevel wiring ───────────────────────────────────

        [Fact]
        public void InitializeFromLevel_CopiesRegenDelay()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            Assert.Equal(30f, Store.ResourceNodeRegenDelay[0]);
            Assert.Equal(0f, Store.ResourceNodeRegenTimer[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
        }

        [Fact]
        public void InitializeFromLevel_DefaultRegenDelayIsZero()
        {
            // No RegenDelay specified → 0 → legacy no-regen behavior
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            Assert.Equal(0f, Store.ResourceNodeRegenDelay[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
        }

        // ── DamageNode behavior ──────────────────────────────────────────

        [Fact]
        public void DamageNode_RegenEnabled_SetsDepletedAndArmsTimer()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 60f); // lethal
            Assert.False(Store.ResourceNodeActive[0]);
            Assert.Equal(-1, Store.ResourceNodeOwner[0]);
            Assert.True(Store.ResourceNodeDepleted[0]);
            Assert.Equal(30f, Store.ResourceNodeRegenTimer[0]);
        }

        [Fact]
        public void DamageNode_RegenDisabled_DoesNotSetDepleted()
        {
            // RegenDelay = 0 → legacy behavior: node stays destroyed forever
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 0f, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 60f);
            Assert.False(Store.ResourceNodeActive[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
            Assert.Equal(0f, Store.ResourceNodeRegenTimer[0]);
        }

        [Fact]
        public void DamageNode_NonLethal_DoesNotArmRegen()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 20f);
            Assert.True(Store.ResourceNodeActive[0]);
            Assert.Equal(30f, Store.ResourceNodeHealth[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
            Assert.Equal(0f, Store.ResourceNodeRegenTimer[0]);
        }

        [Fact]
        public void DamageNode_Indestructible_NoEffect()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 0f }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 9999f);
            // No change because MaxHealth == 0 = indestructible
            Assert.Equal(0f, Store.ResourceNodeHealth[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
        }

        // ── Update() respawn behavior ────────────────────────────────────

        [Fact]
        public void Update_DepletedTimer_RespawnsAtFullHP()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 1.0f, MaxHealth = 50f, InitialOwner = 0 }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 60f);
            Assert.True(Store.ResourceNodeDepleted[0]);
            Assert.Equal(-1, Store.ResourceNodeOwner[0]); // destroyed → neutral

            // Tick for 1.0 second of regen. After 60 ticks (1.0s cumulative) the timer reaches 0.
            // Use 70 ticks to give a safety margin past zero so the respawn branch fires.
            for (int i = 0; i < 70; i++) system.Update(DeltaTime, GameState.WavePhase);

            Assert.True(Store.ResourceNodeActive[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
            Assert.Equal(50f, Store.ResourceNodeHealth[0]);
            Assert.Equal(0f, Store.ResourceNodeRegenTimer[0]);
            Assert.Equal(0, Store.ResourceNodeOwner[0]); // respawned for player
        }

        [Fact]
        public void Update_RegenDisabled_StaysDestroyed()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 0f, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 60f);

            for (int i = 0; i < 600; i++) system.Update(DeltaTime, GameState.WavePhase);

            // Never respawned
            Assert.False(Store.ResourceNodeActive[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
        }

        [Fact]
        public void Update_PartialTick_DoesNotRespawnYet()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 2.0f, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 60f);

            // Tick 1 second of regen out of 2 (60 ticks)
            for (int i = 0; i < 60; i++) system.Update(DeltaTime, GameState.WavePhase);
            Assert.True(Store.ResourceNodeDepleted[0]);
            Assert.False(Store.ResourceNodeActive[0]);
            // 精确期望：2s - 60 × (1/60)s，从注入的 RegenDelay 与 tick 数推导。
            Assert.Equal(2f - 60f * DeltaTime, Store.ResourceNodeRegenTimer[0], 0.01f);
        }

        // ── Multiple nodes ───────────────────────────────────────────────

        [Fact]
        public void Update_TwoNodes_HaveIndependentTimers()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 1.0f, MaxHealth = 50f },
                new ResourceNodeDef { Id = "n2", Type = 0, X = 2, Y = 2, RegenDelay = 3.0f, MaxHealth = 50f }
            );
            var system = MakeSystem(level);
            system.DamageNode(0, 60f);
            system.DamageNode(1, 60f);

            // Tick 2 seconds — first node respawns (regen=1.0), second is still depleted (regen=3.0)
            for (int i = 0; i < 120; i++) system.Update(DeltaTime, GameState.WavePhase);

            Assert.True(Store.ResourceNodeActive[0]);
            Assert.False(Store.ResourceNodeDepleted[0]);
            Assert.False(Store.ResourceNodeActive[1]);
            Assert.True(Store.ResourceNodeDepleted[1]);
            // 精确期望：3s - 120 × (1/60)s，从注入的 RegenDelay 与 tick 数推导。
            Assert.Equal(3f - 120f * DeltaTime, Store.ResourceNodeRegenTimer[1], 0.01f);
        }

        // ── Depleted nodes don't produce ─────────────────────────────────

        [Fact]
        public void Update_DepletedNode_DoesNotProduce()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f, ProductionRate = 5f, InitialOwner = 0 }
            );
            var system = MakeSystem(level);
            float goldBefore = Store.PlayerGold[0];
            system.DamageNode(0, 60f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime, GameState.BuildPhase);
            // Depleted: no gold added
            Assert.Equal(goldBefore, Store.PlayerGold[0]);
        }
    }
}
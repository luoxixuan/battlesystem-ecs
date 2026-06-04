using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
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
    public class ResourceNodeRegenTests
    {
        private const float DeltaTime = 1f / 60f;

        private static (ResourceNodeSystem system, ComponentStore store) MakeSystem(LevelConfig level)
        {
            var store = new ComponentStore();
            var logger = new MockRenderer();
            var system = new ResourceNodeSystem(store, logger);
            system.InitializeFromLevel(level);
            return (system, store);
        }

        private static LevelConfig MakeLevelWithNodes(params ResourceNodeDef[] nodes)
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
            var store = new ComponentStore();
            Assert.Equal(0f, store.ResourceNodeRegenTimer[0]);
            Assert.Equal(0f, store.ResourceNodeRegenDelay[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
        }

        [Fact]
        public void RegenConfig_ExposesRecommendedDelay()
        {
            // Default is 0 (legacy no-regen); Recommended is the design baseline for new nodes.
            Assert.Equal(0f, ResourceNodeRegenConfig.DefaultRegenDelay);
            Assert.True(ResourceNodeRegenConfig.RecommendedRegenDelay >= 15f);
        }

        // ── InitializeFromLevel wiring ───────────────────────────────────

        [Fact]
        public void InitializeFromLevel_CopiesRegenDelay()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            Assert.Equal(30f, store.ResourceNodeRegenDelay[0]);
            Assert.Equal(0f, store.ResourceNodeRegenTimer[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
        }

        [Fact]
        public void InitializeFromLevel_DefaultRegenDelayIsZero()
        {
            // No RegenDelay specified → 0 → legacy no-regen behavior
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            Assert.Equal(0f, store.ResourceNodeRegenDelay[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
        }

        // ── DamageNode behavior ──────────────────────────────────────────

        [Fact]
        public void DamageNode_RegenEnabled_SetsDepletedAndArmsTimer()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 60f); // lethal
            Assert.False(store.ResourceNodeActive[0]);
            Assert.Equal(-1, store.ResourceNodeOwner[0]);
            Assert.True(store.ResourceNodeDepleted[0]);
            Assert.Equal(30f, store.ResourceNodeRegenTimer[0]);
        }

        [Fact]
        public void DamageNode_RegenDisabled_DoesNotSetDepleted()
        {
            // RegenDelay = 0 → legacy behavior: node stays destroyed forever
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 0f, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 60f);
            Assert.False(store.ResourceNodeActive[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
            Assert.Equal(0f, store.ResourceNodeRegenTimer[0]);
        }

        [Fact]
        public void DamageNode_NonLethal_DoesNotArmRegen()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 20f);
            Assert.True(store.ResourceNodeActive[0]);
            Assert.Equal(30f, store.ResourceNodeHealth[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
            Assert.Equal(0f, store.ResourceNodeRegenTimer[0]);
        }

        [Fact]
        public void DamageNode_Indestructible_NoEffect()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 0f }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 9999f);
            // No change because MaxHealth == 0 = indestructible
            Assert.Equal(0f, store.ResourceNodeHealth[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
        }

        // ── Update() respawn behavior ────────────────────────────────────

        [Fact]
        public void Update_DepletedTimer_RespawnsAtFullHP()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 1.0f, MaxHealth = 50f, InitialOwner = 0 }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 60f);
            Assert.True(store.ResourceNodeDepleted[0]);
            Assert.Equal(-1, store.ResourceNodeOwner[0]); // destroyed → neutral

            // Tick for 1.0 second of regen. After 60 ticks (1.0s cumulative) the timer reaches 0.
            // Use 70 ticks to give a safety margin past zero so the respawn branch fires.
            for (int i = 0; i < 70; i++) system.Update(DeltaTime, GameState.WavePhase);

            Assert.True(store.ResourceNodeActive[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
            Assert.Equal(50f, store.ResourceNodeHealth[0]);
            Assert.Equal(0f, store.ResourceNodeRegenTimer[0]);
            Assert.Equal(0, store.ResourceNodeOwner[0]); // respawned for player
        }

        [Fact]
        public void Update_RegenDisabled_StaysDestroyed()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 0f, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 60f);

            for (int i = 0; i < 600; i++) system.Update(DeltaTime, GameState.WavePhase);

            // Never respawned
            Assert.False(store.ResourceNodeActive[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
        }

        [Fact]
        public void Update_PartialTick_DoesNotRespawnYet()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 2.0f, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 60f);

            // Tick 1 second of regen out of 2 (60 ticks)
            for (int i = 0; i < 60; i++) system.Update(DeltaTime, GameState.WavePhase);
            Assert.True(store.ResourceNodeDepleted[0]);
            Assert.False(store.ResourceNodeActive[0]);
            Assert.True(store.ResourceNodeRegenTimer[0] > 0.5f);
            Assert.True(store.ResourceNodeRegenTimer[0] < 1.5f);
        }

        // ── Multiple nodes ───────────────────────────────────────────────

        [Fact]
        public void Update_TwoNodes_HaveIndependentTimers()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 1.0f, MaxHealth = 50f },
                new ResourceNodeDef { Id = "n2", Type = 0, X = 2, Y = 2, RegenDelay = 3.0f, MaxHealth = 50f }
            );
            var (system, store) = MakeSystem(level);
            system.DamageNode(0, 60f);
            system.DamageNode(1, 60f);

            // Tick 2 seconds — first node respawns (regen=1.0), second is still depleted (regen=3.0)
            for (int i = 0; i < 120; i++) system.Update(DeltaTime, GameState.WavePhase);

            Assert.True(store.ResourceNodeActive[0]);
            Assert.False(store.ResourceNodeDepleted[0]);
            Assert.False(store.ResourceNodeActive[1]);
            Assert.True(store.ResourceNodeDepleted[1]);
            Assert.True(store.ResourceNodeRegenTimer[1] > 0.5f);
            Assert.True(store.ResourceNodeRegenTimer[1] < 1.5f);
        }

        // ── Depleted nodes don't produce ─────────────────────────────────

        [Fact]
        public void Update_DepletedNode_DoesNotProduce()
        {
            var level = MakeLevelWithNodes(
                new ResourceNodeDef { Id = "n1", Type = 0, X = 1, Y = 1, RegenDelay = 30f, MaxHealth = 50f, ProductionRate = 5f, InitialOwner = 0 }
            );
            var (system, store) = MakeSystem(level);
            float goldBefore = store.PlayerGold[0];
            system.DamageNode(0, 60f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime, GameState.BuildPhase);
            // Depleted: no gold added
            Assert.Equal(goldBefore, store.PlayerGold[0]);
        }
    }

    /// <summary>
    /// Constants for Round 108 Direction 4 Resource Node Regen.
    /// </summary>
    public static class ResourceNodeRegenConfig
    {
        public const float DefaultRegenDelay = 0f;
        public const float RecommendedRegenDelay = 30f;
        public const float MinRegenDelay = 1f;
    }
}

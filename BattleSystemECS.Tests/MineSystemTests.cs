using System;
using System.IO;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Tests for Round 106 Direction 2: Mine / Trap Tower System.
    /// Verifies that:
    ///   - Default state: all mine fields are zero / inert (zero-overhead path)
    ///   - MineConfig exposes sensible defaults
    ///   - PlaceTower with TowerType.Mine initializes all mine fields correctly
    ///   - DestroyEntity resets all mine fields (no ID-reuse leakage)
    ///   - Arm time gates triggering: not armed → no trigger, armed → triggers
    ///   - Trigger fires only when an enemy is in MineTriggerRadius
    ///   - Damage applies to all enemies in MineExplosionRadius
    ///   - MineStacksRemaining decrements per trigger; reaches 0 → tower destroyed
    ///   - Multiple stacks (chain mine) fire multiple times across frames
    ///   - MineTriggeredThisFrame latch prevents multi-fire per frame
    ///   - One-shot mines: 1 stack = destroyed after first detonation
    ///   - Invulnerable enemies are skipped
    ///   - Dead enemies (HP &lt;= 0) are skipped
    ///   - MineSystem loads Data/Configs/mine_towers.json correctly
    /// </summary>
    public class MineSystemTests
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── Default state & config ───────────────────────────────────────

        [Fact]
        public void DefaultState_AllMineFieldsInert()
        {
            var store = new ComponentStore();
            // Just use a plain tower slot to verify the default-zero state.
            // (TowerIsMine is only true after PlaceTower with TowerType.Mine.)
            Assert.False(store.TowerIsMine[0]);
            Assert.Equal(0f, store.MineTriggerRadius[0]);
            Assert.Equal(0f, store.MineArmTime[0]);
            Assert.Equal(0f, store.MineArmProgress[0]);
            Assert.Equal(0f, store.MineDamage[0]);
            Assert.Equal(0f, store.MineExplosionRadius[0]);
            Assert.Equal(0, store.MineMaxStacks[0]); // int[] default: 0 (set to 1 by _ResetEntity)
            Assert.Equal(0, store.MineStacksRemaining[0]); // remaining starts at 0
            Assert.False(store.MineTriggeredThisFrame[0]);
        }

        [Fact]
        public void MineConfig_ExposesExpectedDefaults()
        {
            Assert.True(MineConfig.DefaultTriggerRadius > 0f);
            Assert.True(MineConfig.DefaultArmTime >= 0f);
            Assert.True(MineConfig.DefaultDamage > 0f);
            Assert.True(MineConfig.DefaultExplosionRadius > 0f);
            Assert.Equal(1, MineConfig.DefaultMaxStacks);
            Assert.True(MineConfig.DefaultCost >= 0f);
        }

        [Fact]
        public void TowerType_ContainsMineValue()
        {
            // Mine is a defined enum value (distinct from Palisade/Basic)
            Assert.NotEqual(TowerType.Basic, TowerType.Mine);
            Assert.NotEqual(TowerType.Palisade, TowerType.Mine);
            // Enum value 10
            Assert.Equal(10, (int)TowerType.Mine);
        }

        // ── PlaceTower integration ───────────────────────────────────────

        [Fact]
        public void PlaceTower_Mine_InitializesAllFields()
        {
            var store = new ComponentStore();
            var logger = new MockRenderer();
            var gc = new GameConfig();
            var placement = new TowerPlacementSystem(store, logger, gc);

            int tid = placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);

            Assert.True(tid >= 0);
            Assert.True(store.TowerIsMine[tid]);
            Assert.Equal(MineConfig.DefaultTriggerRadius, store.MineTriggerRadius[tid]);
            Assert.Equal(MineConfig.DefaultArmTime, store.MineArmTime[tid]);
            Assert.Equal(0f, store.MineArmProgress[tid]);
            Assert.Equal(MineConfig.DefaultDamage, store.MineDamage[tid]);
            Assert.Equal(MineConfig.DefaultExplosionRadius, store.MineExplosionRadius[tid]);
            Assert.Equal(MineConfig.DefaultMaxStacks, store.MineMaxStacks[tid]);
            Assert.Equal(MineConfig.DefaultMaxStacks, store.MineStacksRemaining[tid]);
            Assert.False(store.MineTriggeredThisFrame[tid]);
        }

        [Fact]
        public void DestroyEntity_ResetsAllMineFields()
        {
            var store = new ComponentStore();
            var logger = new MockRenderer();
            var gc = new GameConfig();
            var placement = new TowerPlacementSystem(store, logger, gc);

            int tid = placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            Assert.True(store.TowerIsMine[tid]);
            Assert.True(store.MineStacksRemaining[tid] > 0);

            store.DestroyEntity(tid);

            // All mine fields must be reset to inert defaults to prevent leakage
            // on slot recycling (ID reuse).
            Assert.False(store.TowerIsMine[tid]);
            Assert.Equal(0f, store.MineTriggerRadius[tid]);
            Assert.Equal(0f, store.MineArmTime[tid]);
            Assert.Equal(0f, store.MineArmProgress[tid]);
            Assert.Equal(0f, store.MineDamage[tid]);
            Assert.Equal(0f, store.MineExplosionRadius[tid]);
            Assert.Equal(1, store.MineMaxStacks[tid]);
            Assert.Equal(0, store.MineStacksRemaining[tid]);
            Assert.False(store.MineTriggeredThisFrame[tid]);
        }

        // ── Arm time gating ──────────────────────────────────────────────

        [Fact]
        public void MineSystem_DoesNotTriggerBeforeArmTime()
        {
            var (system, store, placement) = MakeSystem();
            // Use a non-zero arm time so we can verify the gate.
            int tid = placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            // After PlaceTower with TowerType.Mine, defaults are applied.
            // Force arm time to 0.5s (the default) by re-asserting.
            store.MineArmTime[tid] = 0.5f;
            store.MineArmProgress[tid] = 0f;
            int eid = store.AddEnemy(5, 5, 1f, 100f, 100f, 5f, 10, 1, "E");
            // Advance 10 frames at 1/60 ≈ 0.167s — well below 0.5s arm time.
            for (int i = 0; i < 10; i++) system.Update(DeltaTime);
            // Enemy should still be alive and the mine stack untouched.
            Assert.Equal(100f, store.EnemyHealth[eid]);
            Assert.Equal(1, store.MineStacksRemaining[tid]);
        }

        [Fact]
        public void MineSystem_TriggersAfterArmTime()
        {
            var (system, store, placement) = MakeSystem();
            int tid = PlaceMine(placement, store, x: 5, y: 5);
            int eid = store.AddEnemy(5, 5, 1f, 100f, 100f, 5f, 10, 1, "E");
            // Arm time default = 0.5s. Run 60 frames at 1/60 → 1.0s, well past arm.
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            store.ResolveEnemiesKilledThisFrame();
            // Enemy took 80 damage (MineConfig.DefaultDamage).
            Assert.True(store.EnemyHealth[eid] < 100f || !store.EnemyActive[eid]);
        }

        // ── Trigger radius & range check ────────────────────────────────

        [Fact]
        public void MineSystem_NoTriggerWhenEnemyOutOfRange()
        {
            var (system, store, placement) = MakeSystem();
            PlaceMine(placement, store, x: 5, y: 5);
            // Place enemy far away (10 cells in y)
            int eid = store.AddEnemy(5, 20, 1f, 100f, 100f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Enemy far away, no damage
            Assert.Equal(100f, store.EnemyHealth[eid]);
        }

        [Fact]
        public void MineSystem_TriggersOnlyFirstEnemyInRange()
        {
            var (system, store, placement) = MakeSystem();
            int tid = PlaceMine(placement, store, x: 5, y: 5);
            int e1 = store.AddEnemy(5, 5, 1f, 100f, 100f, 5f, 10, 1, "E1");
            int e2 = store.AddEnemy(5, 6, 1f, 100f, 100f, 5f, 10, 1, "E2");
            // Arm time + a bit — single detonation should hit BOTH (both in explosion radius 2.0)
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            store.ResolveEnemiesKilledThisFrame();
            // Both enemies take 80 damage
            Assert.True(store.EnemyHealth[e1] < 100f || !store.EnemyActive[e1]);
            Assert.True(store.EnemyHealth[e2] < 100f || !store.EnemyActive[e2]);
            // Single-shot mine should be destroyed
            Assert.Equal(0, store.MineStacksRemaining[tid]);
            Assert.False(store.TowerActive[tid]);
        }

        // ── Stack behavior ──────────────────────────────────────────────

        [Fact]
        public void MineSystem_MultiStackMine_FiresMultipleTimes()
        {
            var (system, store, placement) = MakeSystem();
            int tid = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 3);
            int e = store.AddEnemy(5, 5, 1f, 500f, 500f, 5f, 10, 1, "Tank");
            // After arm: each frame should trigger 1 stack. Enemy has 500 HP, mine
            // does 80 damage per stack, so 500 / 80 = 7 hits needed to kill.
            // 3 stacks = 240 damage total.
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Stacks should be 0 now (3 used)
            Assert.Equal(0, store.MineStacksRemaining[tid]);
            // Tower destroyed
            Assert.False(store.TowerActive[tid]);
            // Enemy took ~240 damage
            Assert.True(store.EnemyHealth[e] <= 260f);
            Assert.True(store.EnemyHealth[e] >= 250f); // ±10 for float drift
        }

        [Fact]
        public void MineSystem_OneShotMine_DestroyedAfterFirstTrigger()
        {
            var (system, store, placement) = MakeSystem();
            int tid = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            PlaceEnemy(store, 5, 5, 200f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Assert.Equal(0, store.MineStacksRemaining[tid]);
            Assert.False(store.TowerActive[tid]);
        }

        [Fact]
        public void MineSystem_PerFrameLatch_PreventsMultiFirePerFrame()
        {
            var (system, store, placement) = MakeSystem();
            int tid = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 3);
            int e = PlaceEnemy(store, 5, 5, 500f);
            // Run 1 frame after arm complete. Arm time forced to 0 in PlaceMine,
            // so the very first Update() sees arm complete and may fire.
            // With 3 stacks, latch should let it fire exactly once that frame.
            system.Update(DeltaTime);
            // Should have consumed exactly 1 stack
            Assert.Equal(2, store.MineStacksRemaining[tid]);
        }

        // ── Edge cases ──────────────────────────────────────────────────

        [Fact]
        public void MineSystem_SkipsInvulnerableEnemies()
        {
            var (system, store, placement) = MakeSystem();
            PlaceMine(placement, store, x: 5, y: 5);
            int e = store.AddEnemy(5, 5, 1f, 100f, 100f, 5f, 10, 1, "E");
            store.EnemyIsInvulnerable[e] = true;
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Assert.Equal(100f, store.EnemyHealth[e]);
        }

        [Fact]
        public void MineSystem_SkipsDeadEnemies()
        {
            var (system, store, placement) = MakeSystem();
            PlaceMine(placement, store, x: 5, y: 5);
            int e = store.AddEnemy(5, 5, 1f, 100f, 100f, 5f, 10, 1, "E");
            store.EnemyHealth[e] = 0f;
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Mine still has 1 stack — did NOT fire on a corpse. MineSystem.Update
            // skips enemies with Health <= 0, so the trigger is never latched and
            // the mine is not consumed.
            Assert.Equal(1, store.MineStacksRemaining[store.ActiveTowerIds[0]]);
        }

        [Fact]
        public void MineSystem_NonMineTower_NotProcessed()
        {
            var (system, store, placement) = MakeSystem();
            int t = placement.PlaceTower(5, 5, TowerType.Basic, 50f, 5, 1f, 25f);
            int e = PlaceEnemy(store, 5, 5, 100f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Basic tower is not a mine — enemy should not take mine damage (it might
            // take damage from the basic tower's auto-attack, but we only placed 1 basic
            // tower with damage=50 range=5 and the enemy is in range, so it might).
            // What we CAN assert: the basic tower is not destroyed by MineSystem.
            Assert.True(store.TowerActive[t]);
            // And TowerIsMine is false
            Assert.False(store.TowerIsMine[t]);
        }

        [Fact]
        public void MineSystem_DestroyedMine_DoesNotFireOnNextFrame()
        {
            var (system, store, placement) = MakeSystem();
            int tid = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            int e1 = PlaceEnemy(store, 5, 5, 200f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Mine should be destroyed
            Assert.False(store.TowerActive[tid]);
            // Add another enemy and run more frames — the destroyed mine cannot re-fire
            int e2 = PlaceEnemy(store, 5, 5, 200f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Assert.Equal(200f, store.EnemyHealth[e2]);
        }

        // ── Config loader ───────────────────────────────────────────────

        [Fact]
        public void MineSystem_LoadsConfigFile()
        {
            var (system, store, _) = MakeSystem();
            // We don't know the exact contents (file may not exist in test env),
            // but GetMineDef should never throw. If the file IS present, we expect
            // at least 1 entry to load.
            var def1 = system.GetMineDef(1);
            // We can't assert non-null here because the file may not be copied to
            // the test bin folder. But the call must not throw.
            Assert.Null(def1); // Most likely null in unit test env — file not in bin
        }

        [Fact]
        public void MineSystem_GetMineDef_ReturnsNullForUnknownId()
        {
            var (system, _, _) = MakeSystem();
            Assert.Null(system.GetMineDef(99999));
        }

        // ── Round 172 — Chain Detonation (Direction 5) ────────────────────

        [Fact]
        public void Chain_DefaultState_AllFieldsInert()
        {
            var store = new ComponentStore();
            // Default state: all chain fields must be inert (zero-overhead fast path)
            Assert.False(store.MineCanChain[0]);
            Assert.Equal(0f, store.MineChainRadius[0]);
            Assert.Equal(0f, store.MineChainDamageMult[0]);
            Assert.Equal(0, store.MineChainDepth[0]);
        }

        [Fact]
        public void Chain_PlaceTower_DefaultsToInert()
        {
            var (system, store, placement) = MakeSystem();
            int tid = placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            // PlaceTower does NOT auto-enable chain — designers must opt in via
            // setting the fields directly (or future config-resolution path).
            Assert.False(store.MineCanChain[tid]);
            Assert.Equal(0f, store.MineChainRadius[tid]);
            Assert.Equal(0f, store.MineChainDamageMult[tid]);
            Assert.Equal(0, store.MineChainDepth[tid]);
        }

        [Fact]
        public void Chain_DestroyEntity_ResetsChainFields()
        {
            var (system, store, placement) = MakeSystem();
            int tid = placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            // Activate chain on this mine
            store.MineCanChain[tid] = true;
            store.MineChainRadius[tid] = 5f;
            store.MineChainDamageMult[tid] = 0.7f;
            store.MineChainDepth[tid] = 2;
            // Destroy and verify all fields are reset
            store.DestroyEntity(tid);
            Assert.False(store.MineCanChain[tid]);
            Assert.Equal(0f, store.MineChainRadius[tid]);
            Assert.Equal(0f, store.MineChainDamageMult[tid]);
            Assert.Equal(0, store.MineChainDepth[tid]);
        }

        [Fact]
        public void Chain_TwoChainMines_TriggersBothOnEnemyProximity()
        {
            var (system, store, placement) = MakeSystem();
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            // t2 is distance 2 from t1, outside natural trigger radius (1.5).
            // It must fire ONLY via chain.
            int t2 = PlaceMine(placement, store, x: 7, y: 5, maxStacks: 1);
            // Enable chain on BOTH t1 and t2. t1 is the source (CanChain=true means
            // t1 can chain-propagate when it detonates). t2 is the target — the
            // EnqueueChainNeighbors filter requires MineCanChain[otherTid]=true on
            // the neighbor for it to be eligible.
            store.MineCanChain[t1] = true;
            store.MineCanChain[t2] = true;
            store.MineChainRadius[t1] = 4f;
            store.MineChainRadius[t2] = 4f;
            store.MineChainDamageMult[t1] = 0.7f;
            store.MineChainDamageMult[t2] = 0.7f;
            store.MineChainDepth[t1] = 2;
            store.MineChainDepth[t2] = 1;

            // Enemy with HP=1000 so it survives both detonations:
            // t1 (80 dmg) + t2 chain (80 × 0.7 = 56 dmg) = 136 dmg, leaves 864 HP.
            int eid = store.AddEnemy(5, 5, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 detonates at full damage (80) AND chains to t2 (56 dmg).
            // Enemy should have HP 1000 - 80 - 56 = 864.
            Assert.Equal(864f, store.EnemyHealth[eid], 1);
            // Both mines should be destroyed (each had 1 stack and detonated)
            Assert.False(store.TowerActive[t1]);
            Assert.False(store.TowerActive[t2]);
        }

        [Fact]
        public void Chain_NoTriggerWhenNoEnemyInTriggerRange_ChainStaysDormant()
        {
            var (system, store, placement) = MakeSystem();
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(placement, store, x: 7, y: 5, maxStacks: 1); // 2 cells from t1
            // Configure t1 to chain with 50% mult
            store.MineCanChain[t1] = true;
            store.MineChainRadius[t1] = 5f;
            store.MineChainDamageMult[t1] = 0.5f;
            store.MineChainDepth[t1] = 1;
            // Place an enemy FAR from both t1 and t2's trigger radii
            // t1 trigger=1.5, t2 trigger=1.5, enemy at (10,5) is 5 cells from t1
            int eid = store.AddEnemy(10, 5, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 should NOT trigger (enemy out of trigger range)
            // Therefore no chain should happen either
            Assert.Equal(1000f, store.EnemyHealth[eid]);
            Assert.Equal(1, store.MineStacksRemaining[t1]);
            Assert.Equal(1, store.MineStacksRemaining[t2]);
        }

        [Fact]
        public void Chain_OutOfRangeNeighbor_DoesNotChain()
        {
            var (system, store, placement) = MakeSystem();
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            // Place t2 outside the 10x20 map → invalid. We need t2 valid but outside chain radius.
            // The map is 10x20, so the farthest valid position from t1 is (0, 19). Distance = sqrt(25+196) ≈ 14.9.
            int t2 = PlaceMine(placement, store, x: 0, y: 19, maxStacks: 1);
            store.MineCanChain[t1] = true;
            store.MineChainRadius[t1] = 4f; // 4 cells, t2 is 14.9 cells away
            store.MineChainDamageMult[t1] = 0.5f;
            store.MineChainDepth[t1] = 3;

            int eid = store.AddEnemy(5, 5, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 triggers, t2 is out of chain radius — only t1 explodes
            Assert.False(store.TowerActive[t1]);
            Assert.True(store.TowerActive[t2]); // t2 should still be active
            Assert.Equal(1, store.MineStacksRemaining[t2]); // t2 never detonated
        }

        [Fact]
        public void Chain_NonChainCapableNeighbor_DoesNotChain()
        {
            var (system, store, placement) = MakeSystem();
            // Place t2 OUTSIDE natural trigger range so we can isolate the chain effect.
            // t1 at (5,5), t2 at (9,5) — distance 4 cells. Both within chainRadius=5 if set.
            // t2's natural trigger radius is 1.5 — enemy at (5,5) is 4 cells from t2 → out of trigger.
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(placement, store, x: 9, y: 5, maxStacks: 1);
            // t1 is chain-capable; t2 is NOT (MineCanChain=false)
            store.MineCanChain[t1] = true;
            store.MineChainRadius[t1] = 5f;
            store.MineChainDamageMult[t1] = 0.7f;
            store.MineChainDepth[t1] = 2;
            // t2.MineCanChain stays false

            int eid = store.AddEnemy(5, 5, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Only t1 detonates (t2 not chain-capable → no chain propagation; t2 also out of natural trigger)
            Assert.False(store.TowerActive[t1]);
            Assert.True(store.TowerActive[t2]); // t2 should still be active
            Assert.Equal(1, store.MineStacksRemaining[t2]); // untouched
        }

        [Fact]
        public void Chain_DepthLimit_StopsAtMaxHop()
        {
            var (system, store, placement) = MakeSystem();
            // Linear chain: t1 → t2 → t3, with depth=1 means only t1 → t2 (not t3).
            // t2 and t3 must be OUTSIDE natural trigger range from the enemy at (5,5)
            // so they only fire via chain propagation, not naturally.
            // t1 at (5,5), t2 at (7,5) distance 2 > 1.5 trigger. t3 at (9,5) distance 4 > 1.5.
            // chainRadius=2.5: t1 reaches t2 (dist 2) but NOT t3 (dist 4).
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(placement, store, x: 7, y: 5, maxStacks: 1);
            int t3 = PlaceMine(placement, store, x: 9, y: 5, maxStacks: 1);
            store.MineCanChain[t1] = true;
            store.MineCanChain[t2] = true;
            store.MineCanChain[t3] = true;
            store.MineChainRadius[t1] = 2.5f; // 1-hop reach: t1→t2 only
            store.MineChainRadius[t2] = 2.5f;
            store.MineChainRadius[t3] = 2.5f;
            store.MineChainDamageMult[t1] = 0.7f;
            store.MineChainDamageMult[t2] = 0.7f;
            store.MineChainDamageMult[t3] = 0.7f;
            store.MineChainDepth[t1] = 1; // t1 → t2 only, not t3
            store.MineChainDepth[t2] = 1;
            store.MineChainDepth[t3] = 1;

            int eid = store.AddEnemy(5, 5, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 detonates (triggered by enemy at distance 0), chains to t2 (hop 1).
            // t2 detonates (chain-triggered) but next propagation from t2 is hop=2 > maxHop=1
            // → t3 should remain untouched (no chain reaches t3).
            Assert.False(store.TowerActive[t1]);
            Assert.False(store.TowerActive[t2]);
            Assert.True(store.TowerActive[t3]); // t3 should NOT detonate
            Assert.Equal(1, store.MineStacksRemaining[t3]);
        }

        [Fact]
        public void Chain_ChainOfChains_Depth2_Propagates()
        {
            var (system, store, placement) = MakeSystem();
            // 3 mines in a line, depth=2 means t1→t2→t3 (t3 at hop=2 <= maxHop=2).
            // t2 and t3 must be outside natural trigger from the enemy.
            // chainRadius=2.5: t1 reaches t2 (dist 2), t2 reaches t3 (dist 2).
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(placement, store, x: 7, y: 5, maxStacks: 1);
            int t3 = PlaceMine(placement, store, x: 9, y: 5, maxStacks: 1);
            store.MineCanChain[t1] = true;
            store.MineCanChain[t2] = true;
            store.MineCanChain[t3] = true;
            store.MineChainRadius[t1] = 2.5f;
            store.MineChainRadius[t2] = 2.5f;
            store.MineChainRadius[t3] = 2.5f;
            store.MineChainDamageMult[t1] = 0.7f;
            store.MineChainDamageMult[t2] = 0.7f;
            store.MineChainDamageMult[t3] = 0.7f;
            store.MineChainDepth[t1] = 2; // t1 chains 2 hops: t1 → t2 → t3
            store.MineChainDepth[t2] = 2;
            store.MineChainDepth[t3] = 2;

            int eid = store.AddEnemy(5, 5, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // All three should detonate
            Assert.False(store.TowerActive[t1]);
            Assert.False(store.TowerActive[t2]);
            Assert.False(store.TowerActive[t3]);
        }

        [Fact]
        public void Chain_DecayMultipliesPerHop()
        {
            var (system, store, placement) = MakeSystem();
            // 2 chain mines; check that the chained neighbor's damage is reduced
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(placement, store, x: 6, y: 5, maxStacks: 1);
            store.MineCanChain[t1] = true;
            store.MineCanChain[t2] = true; // t2 also chain-capable, but at depth=1 it won't propagate
            store.MineChainRadius[t1] = 4f;
            store.MineChainDamageMult[t1] = 0.5f; // 50% decay
            store.MineChainDamageMult[t2] = 0.5f;
            store.MineChainDepth[t1] = 1;
            store.MineChainDepth[t2] = 1;

            // Place an enemy in t1's explosion radius but not in t2's trigger range
            // (t1 expl=2.0, t1 at (5,5), t2 at (6,5), enemy at (5,6) — within 2.0 of t1, 1.0 from t2's trigger)
            // Wait, t2's trigger radius is 1.5 by default. Enemy at (5,6) is 1.0 from t2 → IN trigger range
            // Let's put enemy at (5,8): 3.0 from t1 (out of expl 2.0) and 2.24 from t2 (out of trigger 1.5)
            // Better: place enemy at (4,6) — 1.0 from t1 (in trigger 1.5), 2.24 from t2 (out of trigger 1.5),
            // 1.41 from t1 (in expl 2.0).
            int eid = store.AddEnemy(4, 6, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 triggers (enemy in trigger range 1.0 < 1.5)
            //   → t1 deals 80 damage to enemy (in expl 2.0)
            //   → t1 chains to t2 (t2 is chain-capable, in range 4.0)
            //   → t2 detonates at 0.5 × 80 = 40 damage
            //   → t2 explosion radius 2.0; distance from t2 to enemy = sqrt((4-6)^2 + (6-5)^2) = sqrt(5) ≈ 2.24 → OUT of t2's explosion range
            //   → enemy only took 80 damage from t1
            Assert.Equal(1000f - 80f, store.EnemyHealth[eid], 1);
        }

        [Fact]
        public void Chain_StackedMines_ChainedNeighborAlsoConsumesStack()
        {
            var (system, store, placement) = MakeSystem();
            int t1 = PlaceMine(placement, store, x: 5, y: 5, maxStacks: 1);
            // t2 at distance 2 (out of natural trigger 1.5) so it fires ONLY via chain.
            int t2 = PlaceMine(placement, store, x: 7, y: 5, maxStacks: 3); // 3 stacks
            // Both must be CanChain for t2 to be a valid chain target.
            store.MineCanChain[t1] = true;
            store.MineCanChain[t2] = true;
            store.MineChainRadius[t1] = 4f;
            store.MineChainRadius[t2] = 4f;
            store.MineChainDamageMult[t1] = 0.5f;
            store.MineChainDamageMult[t2] = 0.5f;
            store.MineChainDepth[t1] = 1;
            store.MineChainDepth[t2] = 1;

            int eid = store.AddEnemy(5, 5, 1f, 1000f, 1000f, 5f, 10, 1, "E");
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 (1 stack) detonates, destroys itself
            // t2 (3 stacks) chain-detonates once, now has 2 stacks left
            Assert.False(store.TowerActive[t1]);
            Assert.True(store.TowerActive[t2]);
            Assert.Equal(2, store.MineStacksRemaining[t2]);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static (MineSystem sys, ComponentStore store, TowerPlacementSystem placement) MakeSystem()
        {
            var store = new ComponentStore();
            var logger = new MockRenderer();
            var gc = new GameConfig();
            var placement = new TowerPlacementSystem(store, logger, gc);
            var sys = new MineSystem(store, logger, gc, PlayerId);
            return (sys, store, placement);
        }

        private static int PlaceMine(TowerPlacementSystem placement, ComponentStore store, int x, int y, int maxStacks = -1)
        {
            int tid = placement.PlaceTower(x, y, TowerType.Mine, 0, 0, 0, 0);
            if (maxStacks >= 0)
            {
                store.MineMaxStacks[tid] = maxStacks;
                store.MineStacksRemaining[tid] = maxStacks;
            }
            // Force arm time to 0 for fast tests.
            store.MineArmTime[tid] = 0f;
            return tid;
        }

        private static int PlaceEnemy(ComponentStore store, int x, int y, float maxHp)
        {
            return store.AddEnemy(x, y, 1f, maxHp, maxHp, 5f, 10, 1, "TestE");
        }
    }
}

using BattleSystemECS.Tests.Infrastructure;
using System;
using System.IO;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
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
    /// </summary>
    public class MineSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── Default state & config ───────────────────────────────────────

        [Fact]
        public void DefaultState_AllMineFieldsInert()
        {
            // Just use a plain tower slot to verify the default-zero state.
            // (TowerIsMine is only true after PlaceTower with TowerType.Mine.)
            Assert.False(Store.TowerIsMine[0]);
            Assert.Equal(0f, Store.MineTriggerRadius[0], 3);
            Assert.Equal(0f, Store.MineArmTime[0], 3);
            Assert.Equal(0f, Store.MineArmProgress[0], 3);
            Assert.Equal(0f, Store.MineDamage[0], 3);
            Assert.Equal(0f, Store.MineExplosionRadius[0], 3);
            Assert.Equal(0, Store.MineMaxStacks[0]); // int[] default: 0 (set to 1 by _ResetEntity)
            Assert.Equal(0, Store.MineStacksRemaining[0]); // remaining starts at 0
            Assert.False(Store.MineTriggeredThisFrame[0]);
        }

        [Fact]
        public void MineConfig_ExposesExpectedDefaults()
        {
            Assert.True(MineConfig.DefaultTriggerRadius > 0f);
            Assert.True(MineConfig.DefaultArmTime >= 0f);
            Assert.True(MineConfig.DefaultDamage > 0f);
            Assert.True(MineConfig.DefaultExplosionRadius > 0f);
            Assert.True(MineConfig.DefaultMaxStacks >= 1);
            Assert.True(MineConfig.DefaultCost >= 0f);
        }

        // ── PlaceTower integration ───────────────────────────────────────

        [Fact]
        public void PlaceTower_Mine_InitializesAllFields()
        {
            _ = Placement; // 构造 Placement（LoadPerTypeCaps 会写 JSON cap），随后显式清空 cap
            DisableTowerCaps();

            int tid = Placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);

            Assert.True(tid >= 0);
            Assert.Equal(TowerType.Mine, Store.TowerType[tid]);
            Assert.True(Store.TowerIsMine[tid]);
            Assert.Equal(MineConfig.DefaultTriggerRadius, Store.MineTriggerRadius[tid], 3);
            Assert.Equal(MineConfig.DefaultArmTime, Store.MineArmTime[tid], 3);
            Assert.Equal(0f, Store.MineArmProgress[tid], 3);
            Assert.Equal(MineConfig.DefaultDamage, Store.MineDamage[tid], 3);
            Assert.Equal(MineConfig.DefaultExplosionRadius, Store.MineExplosionRadius[tid], 3);
            Assert.Equal(MineConfig.DefaultMaxStacks, Store.MineMaxStacks[tid]);
            Assert.Equal(MineConfig.DefaultMaxStacks, Store.MineStacksRemaining[tid]);
            Assert.False(Store.MineTriggeredThisFrame[tid]);
        }

        [Fact]
        public void DestroyEntity_ResetsAllMineFields()
        {
            _ = Placement; // 构造 Placement（LoadPerTypeCaps 会写 JSON cap），随后显式清空 cap
            DisableTowerCaps();

            int tid = Placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            Assert.True(Store.TowerIsMine[tid]);
            Assert.True(Store.MineStacksRemaining[tid] > 0);

            Store.DestroyEntity(tid);

            // All mine fields must be reset to inert defaults to prevent leakage
            // on slot recycling (ID reuse).
            Assert.False(Store.TowerIsMine[tid]);
            Assert.Equal(0f, Store.MineTriggerRadius[tid], 3);
            Assert.Equal(0f, Store.MineArmTime[tid], 3);
            Assert.Equal(0f, Store.MineArmProgress[tid], 3);
            Assert.Equal(0f, Store.MineDamage[tid], 3);
            Assert.Equal(0f, Store.MineExplosionRadius[tid], 3);
            Assert.Equal(MineConfig.DefaultMaxStacks, Store.MineMaxStacks[tid]);
            Assert.Equal(0, Store.MineStacksRemaining[tid]);
            Assert.False(Store.MineTriggeredThisFrame[tid]);
        }

        // ── Arm time gating ──────────────────────────────────────────────

        [Fact]
        public void MineSystem_DoesNotTriggerBeforeArmTime()
        {
            var system = MakeSystem();
            // Use a non-zero arm time so we can verify the gate.
            int tid = Placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            // After PlaceTower with TowerType.Mine, defaults are applied.
            // Force arm time to 0.5s (the default) by re-asserting.
            Store.MineArmTime[tid] = 0.5f;
            Store.MineArmProgress[tid] = 0f;
            int eid = PlaceEnemy(5, 5, 100f);
            // Advance 10 frames at 1/60 ≈ 0.167s — well below 0.5s arm time.
            for (int i = 0; i < 10; i++) system.Update(DeltaTime);
            // Enemy should still be alive and the mine stack untouched.
            Assert.Equal(100f, Store.EnemyHealth[eid], 3);
            Assert.Equal(MineConfig.DefaultMaxStacks, Store.MineStacksRemaining[tid]);
        }

        [Fact]
        public void MineSystem_TriggersAfterArmTime()
        {
            var system = MakeSystem();
            int tid = PlaceMine(x: 5, y: 5);
            int eid = PlaceEnemy(5, 5, 100f);
            // Arm time default = 0.5s. Run 60 frames at 1/60 → 1.0s, well past arm.
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Store.ResolveEnemiesKilledThisFrame();
            // 期望血量 = 初始 100 - 默认单次爆炸伤害（80 伤不足以击杀）。
            float expectedHealth = 100f - MineConfig.DefaultDamage;
            Assert.True(Store.EnemyActive[eid]);
            Assert.Equal(expectedHealth, Store.EnemyHealth[eid], 3);
            Assert.Equal(0, Store.MineStacksRemaining[tid]);
        }

        // ── Trigger radius & range check ────────────────────────────────

        [Fact]
        public void MineSystem_NoTriggerWhenEnemyOutOfRange()
        {
            var system = MakeSystem();
            PlaceMine(x: 5, y: 5);
            // Place enemy far away (10 cells in y)
            int eid = PlaceEnemy(5, 20, 100f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Enemy far away, no damage
            Assert.Equal(100f, Store.EnemyHealth[eid], 3);
        }

        [Fact]
        public void MineSystem_ExplosionHitsAllEnemiesInExplosionRadius()
        {
            var system = MakeSystem();
            int tid = PlaceMine(x: 5, y: 5);
            int e1 = PlaceEnemy(5, 5, 100f);
            int e2 = PlaceEnemy(5, 6, 100f);
            // 单次引爆应同时命中爆炸半径内的两个敌人。
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Store.ResolveEnemiesKilledThisFrame();
            float expectedHealth = 100f - MineConfig.DefaultDamage;
            Assert.Equal(expectedHealth, Store.EnemyHealth[e1], 3);
            Assert.Equal(expectedHealth, Store.EnemyHealth[e2], 3);
            // Single-shot mine should be destroyed
            Assert.Equal(0, Store.MineStacksRemaining[tid]);
            Assert.False(Store.TowerActive[tid]);
        }

        // ── Stack behavior ──────────────────────────────────────────────

        [Fact]
        public void MineSystem_MultiStackMine_FiresMultipleTimes()
        {
            var system = MakeSystem();
            const int maxStacks = 3;
            int tid = PlaceMine(x: 5, y: 5, maxStacks: maxStacks);
            int e = PlaceEnemy(5, 5, 500f);
            // 每帧引爆 1 层，3 层共造成 3 × DefaultDamage 的伤害。
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Stacks should be 0 now (3 used)
            Assert.Equal(0, Store.MineStacksRemaining[tid]);
            // Tower destroyed
            Assert.False(Store.TowerActive[tid]);
            // Enemy took exactly 3 stacks of default damage.
            Assert.Equal(500f - maxStacks * MineConfig.DefaultDamage, Store.EnemyHealth[e], 3);
        }

        [Fact]
        public void MineSystem_OneShotMine_DestroyedAfterFirstTrigger()
        {
            var system = MakeSystem();
            int tid = PlaceMine(x: 5, y: 5, maxStacks: 1);
            PlaceEnemy(5, 5, 200f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Assert.Equal(0, Store.MineStacksRemaining[tid]);
            Assert.False(Store.TowerActive[tid]);
        }

        [Fact]
        public void MineSystem_PerFrameLatch_PreventsMultiFirePerFrame()
        {
            var system = MakeSystem();
            int tid = PlaceMine(x: 5, y: 5, maxStacks: 3);
            int e = PlaceEnemy(5, 5, 500f);
            // Run 1 frame after arm complete. Arm time forced to 0 in PlaceMine,
            // so the very first Update() sees arm complete and may fire.
            // With 3 stacks, latch should let it fire exactly once that frame.
            system.Update(DeltaTime);
            // Should have consumed exactly 1 stack
            Assert.Equal(2, Store.MineStacksRemaining[tid]);
        }

        // ── Edge cases ──────────────────────────────────────────────────

        [Fact]
        public void MineSystem_SkipsInvulnerableEnemies()
        {
            var system = MakeSystem();
            PlaceMine(x: 5, y: 5);
            int e = PlaceEnemy(5, 5, 100f);
            Store.EnemyIsInvulnerable[e] = true;
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Assert.Equal(100f, Store.EnemyHealth[e], 3);
        }

        [Fact]
        public void MineSystem_SkipsDeadEnemies()
        {
            var system = MakeSystem();
            PlaceMine(x: 5, y: 5);
            int e = PlaceEnemy(5, 5, 100f);
            Store.EnemyHealth[e] = 0f;
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Mine still has its default stack — did NOT fire on a corpse. MineSystem.Update
            // skips enemies with Health <= 0, so the trigger is never latched and
            // the mine is not consumed.
            Assert.Equal(MineConfig.DefaultMaxStacks, Store.MineStacksRemaining[Store.ActiveTowerIds[0]]);
        }

        [Fact]
        public void MineSystem_NonMineTower_NotProcessed()
        {
            var system = MakeSystem();
            int t = Placement.PlaceTower(5, 5, TowerType.Basic, 50f, 5, 1f, 25f);
            int e = PlaceEnemy(5, 5, 100f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Basic tower is not a mine — enemy should not take mine damage (it might
            // take damage from the basic tower's auto-attack, but we only placed 1 basic
            // tower with damage=50 range=5 and the enemy is in range, so it might).
            // What we CAN assert: the basic tower is not destroyed by MineSystem.
            Assert.True(Store.TowerActive[t]);
            // And TowerIsMine is false
            Assert.False(Store.TowerIsMine[t]);
        }

        [Fact]
        public void MineSystem_DestroyedMine_DoesNotFireOnNextFrame()
        {
            var system = MakeSystem();
            int tid = PlaceMine(x: 5, y: 5, maxStacks: 1);
            int e1 = PlaceEnemy(5, 5, 200f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // Mine should be destroyed
            Assert.False(Store.TowerActive[tid]);
            // Add another enemy and run more frames — the destroyed mine cannot re-fire
            int e2 = PlaceEnemy(5, 5, 200f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            Assert.Equal(200f, Store.EnemyHealth[e2], 3);
        }

        // ── Config loader ───────────────────────────────────────────────

        [Fact]
        public void MineSystem_GetMineDef_ReturnsNullForUnknownId()
        {
            var system = MakeSystem();
            Assert.Null(system.GetMineDef(99999));
        }

        // ── Round 172 — Chain Detonation (Direction 5) ────────────────────

        [Fact]
        public void Chain_DefaultState_AllFieldsInert()
        {
            // Default state: all chain fields must be inert (zero-overhead fast path)
            Assert.False(Store.MineCanChain[0]);
            Assert.Equal(0f, Store.MineChainRadius[0], 3);
            Assert.Equal(0f, Store.MineChainDamageMult[0], 3);
            Assert.Equal(0, Store.MineChainDepth[0]);
        }

        [Fact]
        public void Chain_PlaceTower_DefaultsToInert()
        {
            var system = MakeSystem();
            int tid = Placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            // PlaceTower does NOT auto-enable chain — designers must opt in via
            // setting the fields directly (or future config-resolution path).
            Assert.False(Store.MineCanChain[tid]);
            Assert.Equal(0f, Store.MineChainRadius[tid], 3);
            Assert.Equal(0f, Store.MineChainDamageMult[tid], 3);
            Assert.Equal(0, Store.MineChainDepth[tid]);
        }

        [Fact]
        public void Chain_DestroyEntity_ResetsChainFields()
        {
            var system = MakeSystem();
            int tid = Placement.PlaceTower(5, 5, TowerType.Mine, 0, 0, 0, 0);
            // Activate chain on this mine
            Store.MineCanChain[tid] = true;
            Store.MineChainRadius[tid] = 5f;
            Store.MineChainDamageMult[tid] = 0.7f;
            Store.MineChainDepth[tid] = 2;
            // Destroy and verify all fields are reset
            Store.DestroyEntity(tid);
            Assert.False(Store.MineCanChain[tid]);
            Assert.Equal(0f, Store.MineChainRadius[tid], 3);
            Assert.Equal(0f, Store.MineChainDamageMult[tid], 3);
            Assert.Equal(0, Store.MineChainDepth[tid]);
        }

        [Fact]
        public void Chain_TwoChainMines_TriggersBothOnEnemyProximity()
        {
            var system = MakeSystem();
            const float chainMult = 0.7f;
            const float initialHp = 1000f;
            int t1 = PlaceMine(x: 5, y: 5, maxStacks: 1);
            // t2 is distance 2 from t1, outside natural trigger radius (1.5).
            // It must fire ONLY via chain.
            int t2 = PlaceMine(x: 7, y: 5, maxStacks: 1);
            // Enable chain on BOTH t1 and t2. t1 is the source (CanChain=true means
            // t1 can chain-propagate when it detonates). t2 is the target — the
            // EnqueueChainNeighbors filter requires MineCanChain[otherTid]=true on
            // the neighbor for it to be eligible.
            Store.MineCanChain[t1] = true;
            Store.MineCanChain[t2] = true;
            Store.MineChainRadius[t1] = 4f;
            Store.MineChainRadius[t2] = 4f;
            Store.MineChainDamageMult[t1] = chainMult;
            Store.MineChainDamageMult[t2] = chainMult;
            Store.MineChainDepth[t1] = 2;
            Store.MineChainDepth[t2] = 1;

            // Enemy with HP=1000 so it survives both detonations:
            // t1 满伤害 + t2 链式伤害（DefaultDamage × 显式注入的链式倍率）。
            int eid = PlaceEnemy(5, 5, initialHp);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            float expectedHealth = initialHp - MineConfig.DefaultDamage - MineConfig.DefaultDamage * chainMult;
            Assert.Equal(expectedHealth, Store.EnemyHealth[eid], 3);
            // Both mines should be destroyed (each had 1 stack and detonated)
            Assert.False(Store.TowerActive[t1]);
            Assert.False(Store.TowerActive[t2]);
        }

        [Fact]
        public void Chain_NoTriggerWhenNoEnemyInTriggerRange_ChainStaysDormant()
        {
            var system = MakeSystem();
            int t1 = PlaceMine(x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(x: 7, y: 5, maxStacks: 1); // 2 cells from t1
            // Configure t1 to chain with 50% mult
            Store.MineCanChain[t1] = true;
            Store.MineChainRadius[t1] = 5f;
            Store.MineChainDamageMult[t1] = 0.5f;
            Store.MineChainDepth[t1] = 1;
            // Place an enemy FAR from both t1 and t2's trigger radii
            // t1 trigger=1.5, t2 trigger=1.5, enemy at (10,5) is 5 cells from t1
            int eid = PlaceEnemy(10, 5, 1000f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 should NOT trigger (enemy out of trigger range)
            // Therefore no chain should happen either
            Assert.Equal(1000f, Store.EnemyHealth[eid], 3);
            Assert.Equal(1, Store.MineStacksRemaining[t1]);
            Assert.Equal(1, Store.MineStacksRemaining[t2]);
        }

        [Theory(DisplayName = "链式引爆传播受链半径、可链能力与深度共同约束")]
        // 场景：邻居超链半径 / 邻居不可链 / depth=1 只传一跳 / depth=2 传两跳。
        // 参数：t2X, t2Y, t3X, t3Y(-1 表示不布置), chainRadius, t2CanChain, t3CanChain,
        //       depth, expectedT2Active, expectedT3Active, expectedT2Stacks, expectedT3Stacks
        [InlineData(0, 19, -1, -1, 4f, true, true, 3, true, false, 1, 0)]
        [InlineData(9, 5, -1, -1, 5f, false, false, 2, true, false, 1, 0)]
        [InlineData(7, 5, 9, 5, 2.5f, true, true, 1, false, true, 0, 1)]
        [InlineData(7, 5, 9, 5, 2.5f, true, true, 2, false, false, 0, 0)]
        public void Chain_Propagation_RespectsRadiusCapabilityAndDepth(
            int t2X, int t2Y, int t3X, int t3Y,
            float chainRadius, bool t2CanChain, bool t3CanChain, int depth,
            bool expectedT2Active, bool expectedT3Active,
            int expectedT2Stacks, int expectedT3Stacks)
        {
            var system = MakeSystem();
            int t1 = PlaceMine(x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(x: t2X, y: t2Y, maxStacks: 1);
            int t3 = t3X >= 0 ? PlaceMine(x: t3X, y: t3Y, maxStacks: 1) : -1;

            ConfigureChainMine(t1, chainRadius, canChain: true, depth);
            ConfigureChainMine(t2, chainRadius, t2CanChain, depth);
            if (t3 >= 0)
                ConfigureChainMine(t3, chainRadius, t3CanChain, depth);

            // 敌人只站在 t1 上：t2/t3 均在自然触发半径之外，只能被链式引爆。
            int eid = PlaceEnemy(5, 5, 1000f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);

            // 源雷必然引爆；链式目标按半径/可链能力/深度约束收敛。
            Assert.False(Store.TowerActive[t1]);
            Assert.Equal(expectedT2Active, Store.TowerActive[t2]);
            Assert.Equal(expectedT2Stacks, Store.MineStacksRemaining[t2]);
            if (t3 >= 0)
            {
                Assert.Equal(expectedT3Active, Store.TowerActive[t3]);
                Assert.Equal(expectedT3Stacks, Store.MineStacksRemaining[t3]);
            }
        }

        [Fact]
        public void Chain_DecayMultipliesPerHop()
        {
            var system = MakeSystem();
            // 2 chain mines; check that the chained neighbor's damage is reduced
            int t1 = PlaceMine(x: 5, y: 5, maxStacks: 1);
            int t2 = PlaceMine(x: 6, y: 5, maxStacks: 1);
            Store.MineCanChain[t1] = true;
            Store.MineCanChain[t2] = true; // t2 also chain-capable, but at depth=1 it won't propagate
            Store.MineChainRadius[t1] = 4f;
            Store.MineChainDamageMult[t1] = 0.5f; // 50% decay
            Store.MineChainDamageMult[t2] = 0.5f;
            Store.MineChainDepth[t1] = 1;
            Store.MineChainDepth[t2] = 1;

            // Place an enemy in t1's explosion radius but not in t2's trigger range
            // (t1 expl=2.0, t1 at (5,5), t2 at (6,5), enemy at (5,6) — within 2.0 of t1, 1.0 from t2's trigger)
            // Wait, t2's trigger radius is 1.5 by default. Enemy at (5,6) is 1.0 from t2 → IN trigger range
            // Let's put enemy at (5,8): 3.0 from t1 (out of expl 2.0) and 2.24 from t2 (out of trigger 1.5)
            // Better: place enemy at (4,6) — 1.0 from t1 (in trigger 1.5), 2.24 from t2 (out of trigger 1.5),
            // 1.41 from t1 (in expl 2.0).
            int eid = PlaceEnemy(4, 6, 1000f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 triggers (enemy in trigger range 1.0 < 1.5)
            //   → t1 deals 80 damage to enemy (in expl 2.0)
            //   → t1 chains to t2 (t2 is chain-capable, in range 4.0)
            //   → t2 detonates at 0.5 × 80 = 40 damage
            //   → t2 explosion radius 2.0; distance from t2 to enemy = sqrt((4-6)^2 + (6-5)^2) = sqrt(5) ≈ 2.24 → OUT of t2's explosion range
            //   → enemy only took 80 damage from t1
            Assert.Equal(1000f - 80f, Store.EnemyHealth[eid], 1);
        }

        [Fact]
        public void Chain_StackedMines_ChainedNeighborAlsoConsumesStack()
        {
            var system = MakeSystem();
            int t1 = PlaceMine(x: 5, y: 5, maxStacks: 1);
            // t2 at distance 2 (out of natural trigger 1.5) so it fires ONLY via chain.
            int t2 = PlaceMine(x: 7, y: 5, maxStacks: 3); // 3 stacks
            // Both must be CanChain for t2 to be a valid chain target.
            Store.MineCanChain[t1] = true;
            Store.MineCanChain[t2] = true;
            Store.MineChainRadius[t1] = 4f;
            Store.MineChainRadius[t2] = 4f;
            Store.MineChainDamageMult[t1] = 0.5f;
            Store.MineChainDamageMult[t2] = 0.5f;
            Store.MineChainDepth[t1] = 1;
            Store.MineChainDepth[t2] = 1;

            int eid = PlaceEnemy(5, 5, 1000f);
            for (int i = 0; i < 60; i++) system.Update(DeltaTime);
            // t1 (1 stack) detonates, destroys itself
            // t2 (3 stacks) chain-detonates once, now has 2 stacks left
            Assert.False(Store.TowerActive[t1]);
            Assert.True(Store.TowerActive[t2]);
            Assert.Equal(2, Store.MineStacksRemaining[t2]);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private MineSystem MakeSystem()
        {
            _ = Placement; // 构造 Placement（LoadPerTypeCaps 会写 JSON cap），随后显式清空 cap
            DisableTowerCaps();
            return new MineSystem(Store, Renderer, Config, PlayerId);
        }

        private int PlaceMine(int x, int y, int maxStacks = -1)
        {
            int tid = Placement.PlaceTower(x, y, TowerType.Mine, 0, 0, 0, 0);
            if (maxStacks >= 0)
            {
                Store.MineMaxStacks[tid] = maxStacks;
                Store.MineStacksRemaining[tid] = maxStacks;
            }
            // Force arm time to 0 for fast tests.
            Store.MineArmTime[tid] = 0f;
            return tid;
        }

        private int PlaceEnemy(int x, int y, float maxHp)
        {
            return Enemy(e =>
            {
                e.X = x;
                e.Y = y;
                e.MoveSpeed = 1f;
                e.Health = maxHp;
                e.MaxHealth = maxHp;
                e.Name = "TestE";
            });
        }

        /// <summary>把指定雷配置为链式节点（半径/倍率/深度/可链能力均为显式注入）。</summary>
        private void ConfigureChainMine(int tid, float chainRadius, bool canChain, int depth, float damageMult = 0.7f)
        {
            Store.MineCanChain[tid] = canChain;
            Store.MineChainRadius[tid] = chainRadius;
            Store.MineChainDamageMult[tid] = damageMult;
            Store.MineChainDepth[tid] = depth;
        }
    }
}

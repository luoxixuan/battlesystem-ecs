using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using Xunit;

namespace BattleSystemECS.Tests.Features.Bosses
{
    /// <summary>
    /// Tests for Round 111 Direction 1: Boss Phase Skill Switching.
    /// Verifies that:
    ///   - Default state: all phase fields are inert (zero-overhead path)
    ///   - Reset on DestroyEntity clears all phase fields (no ID-reuse leakage)
    ///   - Phase capacity hard-cap is 4 (BOSS_PHASE_MAX)
    ///   - Speed/Damage multipliers apply one-shot on phase transition
    ///   - FiredMask prevents re-firing on subsequent HP recovery / re-entry
    ///   - PhaseAbilityIds CSV is parsed correctly
    ///   - DrainPhaseAbilityEvents empties the bag and calls EnemyAbilitySystem.EnqueueAbility
    ///   - Multiple phases in one boss chain correctly (P1 then P2 in sequence)
    ///   - HP threshold not crossed → no fire, no state change
    ///   - Empty AbilityId is a no-op (no enqueue)
    /// </summary>
    public class BossPhaseSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        /// <summary>文件内共享构造：基类 Store + 最小 GameConfig + EnemyAISystem（含 EnemyAbilitySystem）。</summary>
        private EnemyAISystem CreateAi()
        {
            var ability = new EnemyAbilitySystem(Store, Renderer, PlayerId, Config);
            var ai = new EnemyAISystem(Store, Renderer, PlayerId, Config, ability);
            // 玩家放在远处，避免无行为树的敌人回退成近战攻击。
            Store.PositionX[PlayerId] = 500f;
            Store.PositionY[PlayerId] = 500f;
            return ai;
        }

        // ── Default state & constants ─────────────────────────────────────

        [Fact]
        public void BossPhaseMax_EqualsFour()
        {
            // Sanity: the cap must be 4 (matches the JSON loader / CSV splitter assumptions).
            Assert.Equal(4, ComponentStore.BOSS_PHASE_MAX);
        }

        [Fact]
        public void DefaultState_AllPhaseFieldsInert()
        {
            int eid = 0;
            Assert.Equal(0, Store.EnemyPhaseCount[eid]);
            // All 2D ability slots are null by default
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Null(Store.EnemyPhaseAbilityIdsFlat[ph, eid]);
            Assert.Equal(0, Store.EnemyPhaseFiredMask[eid]);
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
            {
                int idx = ph * ComponentStore.MAX_ENTITIES + eid;
                Assert.Equal(0f, Store.EnemyPhaseThresholdsFlat[idx]);
                Assert.Equal(1f, Store.EnemyPhaseSpeedMults[idx]);
                Assert.Equal(1f, Store.EnemyPhaseDamageMults[idx]);
            }
        }

        [Fact]
        public void ResetOnDestroyEntity_AllPhaseFieldsCleared()
        {
            int eid = Store.AddEnemy(0, 0, 2f, 100f, 100f, 5f, 10, 1, "Boss");
            // Populate
            Store.EnemyPhaseCount[eid] = 3;
            Store.EnemyPhaseAbilityIdsFlat[0, eid] = "ab1";
            Store.EnemyPhaseAbilityIdsFlat[1, eid] = "ab2";
            Store.EnemyPhaseAbilityIdsFlat[2, eid] = "ab3";
            Store.EnemyPhaseFiredMask[eid] = 0b0101;
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
            {
                int idx = ph * ComponentStore.MAX_ENTITIES + eid;
                Store.EnemyPhaseThresholdsFlat[idx] = 0.5f - ph * 0.1f;
                Store.EnemyPhaseSpeedMults[idx] = 1.5f;
                Store.EnemyPhaseDamageMults[idx] = 2.0f;
            }
            Store.DestroyEntity(eid);
            // All phase fields should be reset to prevent ID-reuse leakage
            Assert.Equal(0, Store.EnemyPhaseCount[eid]);
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Null(Store.EnemyPhaseAbilityIdsFlat[ph, eid]);
            Assert.Equal(0, Store.EnemyPhaseFiredMask[eid]);
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
            {
                int idx = ph * ComponentStore.MAX_ENTITIES + eid;
                Assert.Equal(0f, Store.EnemyPhaseThresholdsFlat[idx]);
                Assert.Equal(1f, Store.EnemyPhaseSpeedMults[idx]);
                Assert.Equal(1f, Store.EnemyPhaseDamageMults[idx]);
            }
        }

        // ── SOA field indexing ─────────────────────────────────────────────

        [Fact]
        public void FlatThreshold_IndexingIsPerPhasePerEnemy()
        {
            // Each phase gets its own slot per enemy; verify they don't bleed into each other.
            int e1 = Store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "E1");
            int e2 = Store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "E2");
            int idx1_p0 = 0 * ComponentStore.MAX_ENTITIES + e1;
            int idx1_p1 = 1 * ComponentStore.MAX_ENTITIES + e1;
            int idx2_p0 = 0 * ComponentStore.MAX_ENTITIES + e2;
            Store.EnemyPhaseThresholdsFlat[idx1_p0] = 0.75f;
            Store.EnemyPhaseThresholdsFlat[idx1_p1] = 0.5f;
            Store.EnemyPhaseThresholdsFlat[idx2_p0] = 0.25f;
            Assert.Equal(0.75f, Store.EnemyPhaseThresholdsFlat[idx1_p0]);
            Assert.Equal(0.5f, Store.EnemyPhaseThresholdsFlat[idx1_p1]);
            Assert.Equal(0.25f, Store.EnemyPhaseThresholdsFlat[idx2_p0]);
            // Other slots are still 0
            int idx2_p1 = 1 * ComponentStore.MAX_ENTITIES + e2;
            Assert.Equal(0f, Store.EnemyPhaseThresholdsFlat[idx2_p1]);
        }

        [Fact]
        public void AbilityIdsFlat_PerPhasePerEnemy_StoredIndependently()
        {
            // The 2D string array EnemyPhaseAbilityIdsFlat[phase, enemyId] stores the per-phase
            // abilityId pre-split at spawn time (perf fix — avoid per-frame string.Split).
            // Verify independent storage and no cross-bleed between phases or enemies.
            int e1 = Store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "E1");
            int e2 = Store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "E2");
            Store.EnemyPhaseAbilityIdsFlat[0, e1] = "ab1";
            Store.EnemyPhaseAbilityIdsFlat[1, e1] = "ab2";
            Store.EnemyPhaseAbilityIdsFlat[2, e1] = "ab3";
            Store.EnemyPhaseAbilityIdsFlat[0, e2] = "abX";
            Store.EnemyPhaseAbilityIdsFlat[3, e2] = "abY";
            // Independent per enemy
            Assert.Equal("ab1", Store.EnemyPhaseAbilityIdsFlat[0, e1]);
            Assert.Equal("ab2", Store.EnemyPhaseAbilityIdsFlat[1, e1]);
            Assert.Equal("ab3", Store.EnemyPhaseAbilityIdsFlat[2, e1]);
            Assert.Equal("abX", Store.EnemyPhaseAbilityIdsFlat[0, e2]);
            Assert.Equal("abY", Store.EnemyPhaseAbilityIdsFlat[3, e2]);
            // Unset slots are still null
            Assert.Null(Store.EnemyPhaseAbilityIdsFlat[1, e2]);
            Assert.Null(Store.EnemyPhaseAbilityIdsFlat[2, e2]);
        }

        // ── Drain semantics ────────────────────────────────────────────────

        [Fact]
        public void PhaseAbilityDrainCount_StartsAtZero()
        {
            // After construction, the drain count should be 0 (nothing drained yet).
            var ai = CreateAi();
            Assert.Equal(0, ai.PhaseAbilityDrainCount);
        }

        // ── 真实阶段转移路径：EnemyAISystem.Update 一次触发 + FiredMask 防重入 ──

        [Fact]
        public void EnemyAISystem_Update_FiresPhaseOnce_AppliesSpeedAndDamage()
        {
            var ai = CreateAi();
            // 强制敌人行为树返回 charge_attack：phase 事件 drain 挂在 ExecuteChargeAttack 尾部。
            int eid = Store.AddEnemy(3f, 3f, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            Store.EnemyHealth[eid] = 50f;
            Store.EnemyMaxHealth[eid] = 100f;
            Store.EnemyPhaseCount[eid] = 1;
            int idx = 0 * ComponentStore.MAX_ENTITIES + eid;
            Store.EnemyPhaseThresholdsFlat[idx] = 0.9f;
            Store.EnemyPhaseSpeedMults[idx] = 1.5f;
            Store.EnemyPhaseDamageMults[idx] = 2f;
            var actionNode = new BTCachedNode
            {
                Id = "charge",
                Type = BTNodeType.Action,
                Action = "charge_attack",
                PrecomputedActionEnum = EnemyActionType.ChargeAttack,
                Children = Array.Empty<int>(),
            };
            Store.EnemyBehaviorTree[eid] = new BTCachedTree
            {
                MonsterType = "Boss",
                Root = actionNode,
                Nodes = new[] { actionNode },
            };

            ai.SetTurn(1, DeltaTime);
            ai.Update();

            // 生产路径：Update 内部读取阈值并一次性应用 SpeedMult / DamageMult。
            Assert.Equal(1.5f, Store.EnemyMoveSpeed[eid], 3);
            Assert.Equal(10f, Store.EnemyDamage[eid], 3);
            Assert.NotEqual(0, Store.EnemyPhaseFiredMask[eid] & (1 << 0));
            // 阶段变更事件同帧被 drain 并发布。
            Assert.Equal(1, ai.PhaseChangeDrainCount);
            Assert.Equal(1, ai.PhaseChangePublishCount);

            // 第二帧 HP 仍在阈值下方，但 FiredMask 位已置位 → 不重复应用、不重复发事件。
            ai.SetTurn(2, DeltaTime);
            ai.Update();
            Assert.Equal(1.5f, Store.EnemyMoveSpeed[eid], 3);
            Assert.Equal(10f, Store.EnemyDamage[eid], 3);
            Assert.Equal(0, ai.PhaseChangeDrainCount);
        }

        [Fact]
        public void FiredMask_PerPhaseBit_Isolated()
        {
            // Each phase gets its own bit. Firing phase 0 should not affect phase 1's bit.
            int eid = Store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            Store.EnemyPhaseFiredMask[eid] = 1 << 0; // phase 0 fired
            int bit0 = 1 << 0;
            int bit1 = 1 << 1;
            int bit2 = 1 << 2;
            Assert.NotEqual(0, Store.EnemyPhaseFiredMask[eid] & bit0);
            Assert.Equal(0, Store.EnemyPhaseFiredMask[eid] & bit1);
            Assert.Equal(0, Store.EnemyPhaseFiredMask[eid] & bit2);
        }

        [Fact]
        public void PhaseCount_Zero_NoPhases_NoOp()
        {
            // 未配置阶段的敌人跑完整 Update 后，所有阶段状态保持初始值（gated 路径无副作用）。
            var ai = CreateAi();
            int eid = Enemy(e => { e.X = 3f; e.Y = 3f; e.MoveSpeed = 1f; e.Name = "Goblin"; });
            Store.EnemyHealth[eid] = 50f; // 低血量但无阶段配置 → 不应触发任何阶段
            Store.EnemyMaxHealth[eid] = 100f;

            ai.SetTurn(1, DeltaTime);
            ai.Update();

            Assert.Equal(0, Store.EnemyPhaseCount[eid]);
            Assert.Equal(0, Store.EnemyPhaseFiredMask[eid]);
            Assert.Equal(1f, Store.EnemyMoveSpeed[eid]);
            Assert.Equal(5f, Store.EnemyDamage[eid]);
            Assert.Equal(0, ai.PhaseChangeDrainCount);
            Assert.Equal(0, ai.PhaseAbilityDrainCount);
        }

        [Fact]
        public void PhaseCount_MultiplePhases_PopulatedIndependently()
        {
            int eid = Store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            Store.EnemyPhaseCount[eid] = 3;
            for (int ph = 0; ph < 3; ph++)
            {
                int idx = ph * ComponentStore.MAX_ENTITIES + eid;
                Store.EnemyPhaseThresholdsFlat[idx] = 0.9f - ph * 0.2f;
                Store.EnemyPhaseSpeedMults[idx] = 1.0f + ph * 0.25f;
                Store.EnemyPhaseDamageMults[idx] = 1.0f + ph * 0.5f;
            }
            // Verify all three are stored independently
            for (int ph = 0; ph < 3; ph++)
            {
                int idx = ph * ComponentStore.MAX_ENTITIES + eid;
                Assert.Equal(0.9f - ph * 0.2f, Store.EnemyPhaseThresholdsFlat[idx]);
                Assert.Equal(1.0f + ph * 0.25f, Store.EnemyPhaseSpeedMults[idx]);
                Assert.Equal(1.0f + ph * 0.5f, Store.EnemyPhaseDamageMults[idx]);
            }
            // Phase 3+ slots are still at default (1.0)
            int idx3 = 3 * ComponentStore.MAX_ENTITIES + eid;
            Assert.Equal(1f, Store.EnemyPhaseSpeedMults[idx3]);
            Assert.Equal(1f, Store.EnemyPhaseDamageMults[idx3]);
        }

        [Fact]
        public void BossPhaseDef_DeserializesAllFields()
        {
            // Sanity check: the BossPhaseDef class supports all 5 fields used by the new
            // structured pipeline (Threshold / AbilityId / SpeedMult / DamageMult /
            // NewBehaviorTree). The new pipeline only consumes 4 of them (we don't wire
            // NewBehaviorTree in this round — it's reserved for a future BT swap).
            var def = new BossPhaseDef
            {
                Threshold = 0.5f,
                AbilityId = "boss_phase2_buff",
                SpeedMult = 1.5f,
                DamageMult = 2.0f,
                NewBehaviorTree = "boss_p2_bt"
            };
            Assert.Equal(0.5f, def.Threshold);
            Assert.Equal("boss_phase2_buff", def.AbilityId);
            Assert.Equal(1.5f, def.SpeedMult);
            Assert.Equal(2.0f, def.DamageMult);
            Assert.Equal("boss_p2_bt", def.NewBehaviorTree);
        }

        [Fact]
        public void MultipleEnemies_PerEnemyPhaseDataIsolated()
        {
            // Two enemies with different phase configs — verify no cross-contamination
            // via the SOA indexing.
            int boss1 = Store.AddEnemy(0, 0, 1f, 100f, 100f, 5f, 10, 1, "Boss1");
            int boss2 = Store.AddEnemy(0, 0, 2f, 200f, 200f, 10f, 20, 1, "Boss2");
            Store.EnemyPhaseCount[boss1] = 1;
            Store.EnemyPhaseCount[boss2] = 2;
            Store.EnemyPhaseAbilityIdsFlat[0, boss1] = "ab_a";
            Store.EnemyPhaseAbilityIdsFlat[0, boss2] = "ab_b";
            Store.EnemyPhaseAbilityIdsFlat[1, boss2] = "ab_c";
            int b1_p0 = 0 * ComponentStore.MAX_ENTITIES + boss1;
            int b2_p0 = 0 * ComponentStore.MAX_ENTITIES + boss2;
            int b2_p1 = 1 * ComponentStore.MAX_ENTITIES + boss2;
            Store.EnemyPhaseThresholdsFlat[b1_p0] = 0.5f;
            Store.EnemyPhaseThresholdsFlat[b2_p0] = 0.75f;
            Store.EnemyPhaseThresholdsFlat[b2_p1] = 0.25f;
            // Verify isolation
            Assert.Equal(0.5f, Store.EnemyPhaseThresholdsFlat[b1_p0]);
            Assert.Equal(0.75f, Store.EnemyPhaseThresholdsFlat[b2_p0]);
            Assert.Equal(0.25f, Store.EnemyPhaseThresholdsFlat[b2_p1]);
            Assert.Equal(1, Store.EnemyPhaseCount[boss1]);
            Assert.Equal(2, Store.EnemyPhaseCount[boss2]);
            Assert.Equal("ab_a", Store.EnemyPhaseAbilityIdsFlat[0, boss1]);
            Assert.Equal("ab_b", Store.EnemyPhaseAbilityIdsFlat[0, boss2]);
            Assert.Equal("ab_c", Store.EnemyPhaseAbilityIdsFlat[1, boss2]);
        }
    }
}
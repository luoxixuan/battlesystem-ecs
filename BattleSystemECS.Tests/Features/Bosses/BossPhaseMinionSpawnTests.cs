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
    /// Tests for Round 119 Direction 3: Boss Phase Minion Summon.
    /// Verifies that:
    ///   - Default state: all minion fields are inert (no spawn when not configured)
    ///   - Reset on DestroyEntity clears minion fields (no ID-reuse leakage)
    ///   - SetEnemyPhaseMinion clamps count to [0, BOSS_PHASE_SUMMON_CAP] and typeId to [-1, +inf)
    ///   - Boundary-safe accessors return -1/0 for invalid (enemyId, phase)
    ///   - BossPhaseDef.MinionTypeId / MinionCount defaults are 0 (opt-out)
    ///   - GameConfig.GetMonsterConfigByTypeId returns null for out-of-range typeId
    ///   - WaveSpawningSystem.SpawnMinionNearPosition with count=0 returns 0 without spawning
    ///   - WaveSpawningSystem.SpawnMinionNearPosition with invalid typeId returns 0 without spawning
    ///   - WaveSpawningSystem.SpawnMinionNearPosition spawns N enemies at ring positions
    ///   - Minion cap BOSS_PHASE_SUMMON_CAP = 8 (sanity)
    ///   - Phase firing with MinionTypeId=-1 and MinionCount=0 does NOT push a minion event
    ///   - EnemyAISystem.PhaseMinionDrainCount is 0 when no phases fire
    ///   - EnemyAISystem drain works even when _waveSpawningSystem is null (count tracked, no spawn)
    /// </summary>
    public class BossPhaseMinionSpawnTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        /// <summary>文件内共享构造：基类 Store + 最小 GameConfig + EnemyAISystem（含 EnemyAbilitySystem）。</summary>
        private EnemyAISystem CreateAi()
        {
            var ability = new EnemyAbilitySystem(Store, Renderer, PlayerId, Config);
            var ai = new EnemyAISystem(Store, Renderer, PlayerId, Config, ability);
            // 玩家放在远处，让无行为树的敌人走 move 回退分支，避免意外攻击事件。
            Store.PositionX[PlayerId] = 500f;
            Store.PositionY[PlayerId] = 500f;
            return ai;
        }

        // ── Default state & constants ─────────────────────────────────────

        [Fact]
        public void BossPhaseSummonCap_EqualsEight()
        {
            // Cap must be 8 (matches the design note in Direction 3).
            Assert.Equal(8, ComponentStore.BOSS_PHASE_SUMMON_CAP);
        }

        [Fact]
        public void DefaultState_AllMinionFieldsInert()
        {
            int eid = 0;
            // All 4 phase slots for the minion typeId should default to -1
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Equal(-1, Store.EnemyPhaseMinionTypeIdFlat[ph * ComponentStore.MAX_ENTITIES + eid]);
            // All 4 phase slots for the minion count should default to 0
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Assert.Equal(0, Store.EnemyPhaseMinionCountsFlat[ph * ComponentStore.MAX_ENTITIES + eid]);
            // Phase count itself should be 0
            Assert.Equal(0, Store.EnemyPhaseCount[eid]);
        }

        [Fact]
        public void BossPhaseDef_DefaultMinionFields_AreZero()
        {
            // BossPhaseDef must opt-out by default (0 typeId + 0 count = no summon)
            var def = new BossPhaseDef();
            Assert.Equal(0, def.MinionTypeId);
            Assert.Equal(0, def.MinionCount);
        }

        // ── Setter clamping ────────────────────────────────────────────────

        [Fact]
        public void SetEnemyPhaseMinion_ClampsCountToCap()
        {
            int eid = 0;
            // Try to set 100 — must clamp to BOSS_PHASE_SUMMON_CAP (8)
            Store.SetEnemyPhaseMinion(eid, 0, /*typeId*/ 5, /*count*/ 100);
            Assert.Equal(5, Store.GetEnemyPhaseMinionTypeId(eid, 0));
            Assert.Equal(ComponentStore.BOSS_PHASE_SUMMON_CAP, Store.GetEnemyPhaseMinionCount(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseMinion_NegativeTypeId_BecomesMinusOne()
        {
            int eid = 0;
            // Negative typeId (e.g. -5) is normalised to -1 (the "no minion" sentinel)
            Store.SetEnemyPhaseMinion(eid, 0, -5, 3);
            Assert.Equal(-1, Store.GetEnemyPhaseMinionTypeId(eid, 0));
            Assert.Equal(3, Store.GetEnemyPhaseMinionCount(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseMinion_ZeroCount_StaysZero()
        {
            int eid = 0;
            // Zero count is "no summon" — should stay 0, not get clamped to a negative
            Store.SetEnemyPhaseMinion(eid, 0, 5, 0);
            Assert.Equal(0, Store.GetEnemyPhaseMinionCount(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseMinion_NegativeCount_BecomesZero()
        {
            int eid = 0;
            // Negative count (e.g. -3) is normalised to 0
            Store.SetEnemyPhaseMinion(eid, 0, 5, -3);
            Assert.Equal(0, Store.GetEnemyPhaseMinionCount(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseMinion_InvalidEnemyId_IsNoOp()
        {
            int eid = 0;
            // 先写入合法配置作为对照，再对无效 id 调用，断言合法槽位保持不变。
            Store.SetEnemyPhaseMinion(eid, 0, 5, 3);
            Assert.Equal(5, Store.GetEnemyPhaseMinionTypeId(eid, 0));
            Assert.Equal(3, Store.GetEnemyPhaseMinionCount(eid, 0));

            Store.SetEnemyPhaseMinion(-1, 0, 7, 6);
            Store.SetEnemyPhaseMinion(ComponentStore.MAX_ENTITIES, 0, 7, 6);

            Assert.Equal(5, Store.GetEnemyPhaseMinionTypeId(eid, 0));
            Assert.Equal(3, Store.GetEnemyPhaseMinionCount(eid, 0));
        }

        [Fact]
        public void SetEnemyPhaseMinion_InvalidPhase_IsNoOp()
        {
            int eid = 0;
            // Out-of-range phase (-1 or BOSS_PHASE_MAX) is silently ignored
            Store.SetEnemyPhaseMinion(eid, -1, 5, 3);
            Store.SetEnemyPhaseMinion(eid, ComponentStore.BOSS_PHASE_MAX, 5, 3);
            // After the no-op calls, the field should still be at its default
            Assert.Equal(-1, Store.GetEnemyPhaseMinionTypeId(eid, 0));
            Assert.Equal(0, Store.GetEnemyPhaseMinionCount(eid, 0));
        }

        // ── Getter boundary safety ─────────────────────────────────────────

        [Fact]
        public void GetEnemyPhaseMinionTypeId_InvalidEnemyId_ReturnsMinusOne()
        {
            Assert.Equal(-1, Store.GetEnemyPhaseMinionTypeId(-1, 0));
            Assert.Equal(-1, Store.GetEnemyPhaseMinionTypeId(ComponentStore.MAX_ENTITIES, 0));
        }

        [Fact]
        public void GetEnemyPhaseMinionTypeId_InvalidPhase_ReturnsMinusOne()
        {
            int eid = 0;
            Assert.Equal(-1, Store.GetEnemyPhaseMinionTypeId(eid, -1));
            Assert.Equal(-1, Store.GetEnemyPhaseMinionTypeId(eid, ComponentStore.BOSS_PHASE_MAX));
        }

        [Fact]
        public void GetEnemyPhaseMinionCount_InvalidEnemyId_ReturnsZero()
        {
            Assert.Equal(0, Store.GetEnemyPhaseMinionCount(-1, 0));
            Assert.Equal(0, Store.GetEnemyPhaseMinionCount(ComponentStore.MAX_ENTITIES, 0));
        }

        [Fact]
        public void GetEnemyPhaseMinionCount_InvalidPhase_ReturnsZero()
        {
            int eid = 0;
            Assert.Equal(0, Store.GetEnemyPhaseMinionCount(eid, -1));
            Assert.Equal(0, Store.GetEnemyPhaseMinionCount(eid, ComponentStore.BOSS_PHASE_MAX));
        }

        // ── Reset on DestroyEntity (ID-reuse safety) ───────────────────────

        [Fact]
        public void DestroyEntity_ResetsMinionFields()
        {
            int eid = Store.CreateEntity();
            // Mark as active enemy so DestroyEntity takes the wasEnemy reset path
            // (minion fields are boss-specific and live under the wasEnemy branch).
            Store.AddActiveEnemyId(eid);
            Store.EnemyActive[eid] = true;
            // Set minion config on all 4 phases
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
                Store.SetEnemyPhaseMinion(eid, ph, 3 + ph, 4 + ph);
            // Sanity: values are set
            Assert.Equal(3, Store.GetEnemyPhaseMinionTypeId(eid, 0));
            Assert.Equal(4, Store.GetEnemyPhaseMinionCount(eid, 0));

            Store.DestroyEntity(eid);
            // After destroy, all 4 phase slots should be reset to (-1, 0)
            for (int ph = 0; ph < ComponentStore.BOSS_PHASE_MAX; ph++)
            {
                Assert.Equal(-1, Store.GetEnemyPhaseMinionTypeId(eid, ph));
                Assert.Equal(0, Store.GetEnemyPhaseMinionCount(eid, ph));
            }
        }

        // ── GameConfig.GetMonsterConfigByTypeId ───────────────────────────

        [Fact]
        public void GetMonsterConfigByTypeId_OutOfRange_ReturnsNull()
        {
            int count = Config.MonsterTypes.Count;
            // Negative and beyond-end indices return null; any in-range index returns a Config.
            Assert.Null(Config.GetMonsterConfigByTypeId(-1));
            Assert.Null(Config.GetMonsterConfigByTypeId(count));
            Assert.Null(Config.GetMonsterConfigByTypeId(count + 999));
        }

        [Fact]
        public void GetMonsterConfigByTypeId_ValidId_ReturnsConfig()
        {
            // GameConfig ctor seeds default MonsterTypes — append a Test type so we can
            // assert by exact Type name.
            Config.MonsterTypes.Add(new MonsterConfig { Type = "TestMinionType", Health = 100, MaxHealth = 100, Damage = 5, MoveSpeed = 1f });
            int newId = Config.MonsterTypes.Count - 1;
            var found = Config.GetMonsterConfigByTypeId(newId);
            Assert.NotNull(found);
            Assert.Equal("TestMinionType", found.Type);
            Assert.Equal(100f, found.Health);
        }

        // ── WaveSpawningSystem.SpawnMinionNearPosition ────────────────────

        [Fact]
        public void SpawnMinionNearPosition_ZeroCount_ReturnsZero()
        {
            Config.MonsterTypes.Add(new MonsterConfig { Type = "Test", Health = 50, MaxHealth = 50, Damage = 1, MoveSpeed = 1f });
            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            int spawned = wave.SpawnMinionNearPosition(0, 0, 5f, 5f);
            Assert.Equal(0, spawned);
        }

        [Fact]
        public void SpawnMinionNearPosition_InvalidTypeId_ReturnsZero()
        {
            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            // -1 typeId and 999 typeId are both out of range
            Assert.Equal(0, wave.SpawnMinionNearPosition(-1, 5, 5f, 5f));
            Assert.Equal(0, wave.SpawnMinionNearPosition(999, 5, 5f, 5f));
        }

        [Fact]
        public void SpawnMinionNearPosition_ValidConfig_SpawnsEnemies()
        {
            Config.MonsterTypes.Add(new MonsterConfig { Type = "Minion", Health = 30, MaxHealth = 30, Damage = 1, MoveSpeed = 1f });
            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            wave.SetLevel(1);
            int before = Store.GetCachedActiveEnemyIds().Count;
            int spawned = wave.SpawnMinionNearPosition(0, 4, 5f, 10f);
            int after = Store.GetCachedActiveEnemyIds().Count;
            Assert.Equal(4, spawned);
            Assert.Equal(before + 4, after);
        }

        [Fact]
        public void SpawnMinionNearPosition_PlacesMinionsAtRing()
        {
            Config.MonsterTypes.Add(new MonsterConfig { Type = "Minion", Health = 30, MaxHealth = 30, Damage = 1, MoveSpeed = 1f });
            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            wave.SetLevel(1);

            const float centerX = 5f;
            const float centerY = 10f;
            int spawned = wave.SpawnMinionNearPosition(0, 4, centerX, centerY);
            Assert.Equal(4, spawned);

            // All 4 spawned enemies must be at radius 1.5 (SummonRingRadius) from the center.
            const float SummonRingRadius = 1.5f;
            const float tolerance = 0.01f;
            var activeIds = Store.GetCachedActiveEnemyIds();
            int matched = 0;
            for (int i = 0; i < activeIds.Count; i++)
            {
                int id = activeIds[i];
                float dx = Store.PositionX[id] - centerX;
                float dy = Store.PositionY[id] - centerY;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (Math.Abs(dist - SummonRingRadius) < tolerance)
                    matched++;
            }
            // 4 个生成物都必须落在半径 1.5 的圆环上（同一中心、无历史敌人干扰）。
            Assert.Equal(4, matched);
        }

        // ── EnemyAISystem drain semantics ─────────────────────────────────

        [Fact]
        public void EnemyAISystem_PhaseMinionDrainCount_DefaultsToZero()
        {
            // Drain count must be 0 before any Update() call
            var ai = CreateAi();
            Assert.Equal(0, ai.PhaseMinionDrainCount);
            Assert.Equal(0, ai.PhaseMinionSpawnedCount);
        }

        [Fact]
        public void EnemyAISystem_NoPhaseFire_NoMinionDrain()
        {
            // 未配置阶段时跑一整帧 Update，minion drain 仍为 0，HP 也不变。
            var ai = CreateAi();
            int eid = Enemy(e => { e.X = 3f; e.Y = 3f; e.MoveSpeed = 1f; e.Name = "Goblin"; });
            Store.EnemyHealth[eid] = 50f;

            ai.SetTurn(1, DeltaTime);
            ai.Update();

            Assert.Equal(0, ai.PhaseMinionDrainCount);
            Assert.Equal(0, ai.PhaseMinionSpawnedCount);
            Assert.Equal(50f, Store.EnemyHealth[eid]);
        }

        [Fact]
        public void EnemyAISystem_NullWaveSpawningSystem_DrainCountStillTracked()
        {
            // 未注入 WaveSpawningSystem：阶段触发后 bag 仍被排空计数，但不生成敌人。
            var ai = CreateAi();
            // 配置阶段 0（阈值 0.9 + minion 召唤 2）并强制行为树返回 charge_attack。
            int boss = Store.AddEnemy(3f, 3f, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            Store.EnemyHealth[boss] = 50f;   // 50/100 = 0.5 < 0.9，满足阶段触发条件
            Store.EnemyMaxHealth[boss] = 100f;
            Store.EnemyPhaseCount[boss] = 1;
            int bossIdx = 0 * ComponentStore.MAX_ENTITIES + boss;
            Store.EnemyPhaseThresholdsFlat[bossIdx] = 0.9f;
            Store.EnemyPhaseSpeedMults[bossIdx] = 1.5f;
            Store.EnemyPhaseDamageMults[bossIdx] = 2f;
            Store.SetEnemyPhaseMinion(boss, 0, 0, 2);
            var actionNode = new BTCachedNode
            {
                Id = "charge",
                Type = BTNodeType.Action,
                Action = "charge_attack",
                PrecomputedActionEnum = EnemyActionType.ChargeAttack,
                Children = Array.Empty<int>(),
            };
            Store.EnemyBehaviorTree[boss] = new BTCachedTree
            {
                MonsterType = "Boss",
                Root = actionNode,
                Nodes = new[] { actionNode },
            };
            int activeBefore = Store.GetCachedActiveEnemyIds().Count;

            ai.SetTurn(1, DeltaTime);
            ai.Update();

            Assert.Equal(1, ai.PhaseMinionDrainCount);
            Assert.Equal(0, ai.PhaseMinionSpawnedCount);
            Assert.Equal(activeBefore, Store.GetCachedActiveEnemyIds().Count);
            Assert.NotEqual(0, Store.EnemyPhaseFiredMask[boss] & 1);
        }

        [Fact]
        public void SetWaveSpawningSystem_WiredReference_SpawnsPhaseMinionsOnDrain()
        {
            // 注入引用后必须产生真实可观察行为：阶段触发 → drain 调用 wave → 生成 minions。
            Config.MonsterTypes.Add(new MonsterConfig
            {
                Type = "PhaseMinion",
                Health = 30,
                MaxHealth = 30,
                Damage = 1,
                MoveSpeed = 1f,
            });
            int minionTypeId = Config.MonsterTypes.Count - 1;
            var ai = CreateAi();
            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            wave.SetLevel(1);

            ai.SetWaveSpawningSystem(wave);
            ai.SetWaveSpawningSystem(wave); // 重复注入应幂等

            // 配置阶段 0（阈值 0.9 + minion 召唤 3）并强制行为树返回 charge_attack。
            int boss = Store.AddEnemy(3f, 3f, 1f, 100f, 100f, 5f, 10, 1, "Boss");
            Store.EnemyHealth[boss] = 50f;   // 50/100 = 0.5 < 0.9，满足阶段触发条件
            Store.EnemyMaxHealth[boss] = 100f;
            Store.EnemyPhaseCount[boss] = 1;
            int bossIdx = 0 * ComponentStore.MAX_ENTITIES + boss;
            Store.EnemyPhaseThresholdsFlat[bossIdx] = 0.9f;
            Store.EnemyPhaseSpeedMults[bossIdx] = 1.5f;
            Store.EnemyPhaseDamageMults[bossIdx] = 2f;
            Store.SetEnemyPhaseMinion(boss, 0, minionTypeId, 3);
            var actionNode = new BTCachedNode
            {
                Id = "charge",
                Type = BTNodeType.Action,
                Action = "charge_attack",
                PrecomputedActionEnum = EnemyActionType.ChargeAttack,
                Children = Array.Empty<int>(),
            };
            Store.EnemyBehaviorTree[boss] = new BTCachedTree
            {
                MonsterType = "Boss",
                Root = actionNode,
                Nodes = new[] { actionNode },
            };
            int activeBefore = Store.GetCachedActiveEnemyIds().Count;

            ai.SetTurn(1, DeltaTime);
            ai.Update();

            Assert.Equal(1, ai.PhaseMinionDrainCount);
            Assert.Equal(3, ai.PhaseMinionSpawnedCount);
            Assert.Equal(activeBefore + 3, Store.GetCachedActiveEnemyIds().Count);
            Assert.NotEqual(0, Store.EnemyPhaseFiredMask[boss] & 1);
        }
    }
}
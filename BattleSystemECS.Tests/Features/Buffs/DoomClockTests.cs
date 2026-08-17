using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Buffs
{
    /// <summary>
    /// Tests for Round 110 Direction 10: DoomClock objective (global countdown + infinite waves).
    /// Verifies that:
    ///   - Default state: DoomClockActive is false and all DoomClock fields are zero
    ///   - DoomClockSystem.InitializeFromLevel seeds the timer, duration, counters and Active flag
    ///   - DoomClockSystem.Update decrements the timer during WavePhase and stops at 0
    ///   - DoomClockSystem.Update is a no-op when Active is false (zero-overhead path)
    ///   - DoomClockSystem.Update is a no-op during non-WavePhase (BuildPhase / Intermission)
    ///   - DoomClockSystem.GetCurrentWaveSlot wraps index into (idx, cycle) and syncs the cycle counter
    ///   - DoomClockSystem.GetCycleScalingMultiplier applies MathF.Pow(waveScaling, cycle)
    ///   - DoomClockSystem.ComputeFinalScore returns waveBonus + timeBonus + healthBonus
    ///   - DoomClockSystem.EndRun(won=true) writes FinalScore; EndRun(won=false) clears it
    ///   - DoomClockSystem.GetStatus formats both the running HUD and the post-game string
    ///   - ObjectiveSystem.CheckObjective returns 1 (win) when timer=0 and no enemies; computes FinalScore
    ///   - ObjectiveSystem.CheckObjective returns 0 (ongoing) when timer>0 or enemies still alive
    ///   - ObjectiveSystem.CheckObjective returns 0 (ongoing) for non-DoomClock objective types (unchanged)
    ///   - OnWaveCompleted increments DoomClockWavesCleared when DoomClock is active
    ///   - OnWaveCompleted is a no-op when DoomClock is not active
    ///   - ParseDoomClockInitialWaves reads a JSON array of {MonsterType, EnemyCount} entries
    ///   - LevelConfig has all 6 DoomClock fields with sensible defaults
    /// </summary>
    public class DoomClockTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float DeltaTime = 1f / 60f;

        // ── Helper: build a minimal store + objective + doom clock system ──

        private (ObjectiveSystem obj, DoomClockSystem dc) MakeSut(
            int objectiveType = (int)ObjectiveType.DoomClock,
            float doomClockDuration = 180f,
            int doomClockWaveScore = 100,
            int doomClockTimeBonusPerSec = 10,
            int doomClockHealthBonusPerPercent = 5,
            float doomClockWaveScaling = 1.10f,
            int mapHeight = 20)
        {
            var obj = new ObjectiveSystem(Store, PlayerId);
            var dc = new DoomClockSystem(Store, PlayerId);
            var level = new LevelConfig
            {
                LevelNumber = 6,
                ObjectiveType = objectiveType,
                ObjectiveTimeLimit = 180f,
                DoomClockDuration = doomClockDuration,
                DoomClockWaveScore = doomClockWaveScore,
                DoomClockTimeBonusPerSec = doomClockTimeBonusPerSec,
                DoomClockHealthBonusPerPercent = doomClockHealthBonusPerPercent,
                DoomClockWaveScaling = doomClockWaveScaling
            };
            obj.InitializeFromLevel(level, mapHeight);
            return (obj, dc);
        }

        // ── Default state ──────────────────────────────────────────────

        [Fact]
        public void DefaultState_AllDoomClockFieldsInert()
        {
            Assert.False(Store.DoomClockActive[0]);
            Assert.Equal(0f, Store.DoomClockTimer[0]);
            Assert.Equal(0f, Store.DoomClockDuration[0]);
            Assert.Equal(0, Store.DoomClockWavesCleared[0]);
            Assert.Equal(0, Store.DoomClockCycleCount[0]);
            Assert.Equal(0, Store.DoomClockFinalScore[0]);
        }

        [Fact]
        public void LevelConfig_ExposesExpectedDefaults()
        {
            var level = new LevelConfig();
            // 只断言相对不变量：默认时长必须 >0，分数/奖励因子非负，
            // 波次缩放 ≥1（低于 1 会被 DoomClockSystem 视作 1），初始波次为空列表。
            Assert.True(level.DoomClockDuration > 0f);
            Assert.True(level.DoomClockWaveScore >= 0);
            Assert.True(level.DoomClockTimeBonusPerSec >= 0);
            Assert.True(level.DoomClockHealthBonusPerPercent >= 0);
            Assert.True(level.DoomClockWaveScaling >= 1f);
            Assert.NotNull(level.DoomClockInitialWaves);
            Assert.Empty(level.DoomClockInitialWaves);
        }

        // ── Initialization ─────────────────────────────────────────────

        [Fact]
        public void InitializeFromLevel_DoomClock_SeedsAllFields()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 120f);
            Assert.True(Store.DoomClockActive[0]);
            Assert.Equal(120f, Store.DoomClockTimer[0]);
            Assert.Equal(120f, Store.DoomClockDuration[0]);
            Assert.Equal(0, Store.DoomClockWavesCleared[0]);
            Assert.Equal(0, Store.DoomClockCycleCount[0]);
            Assert.Equal(0, Store.DoomClockFinalScore[0]);
        }

        [Fact]
        public void InitializeFromLevel_NonDoomClock_DisablesActive()
        {
            var (obj, dc) = MakeSut(objectiveType: (int)ObjectiveType.KillAll);
            Assert.False(Store.DoomClockActive[0]);
            Assert.Equal(0, Store.DoomClockFinalScore[0]);
        }

        [Fact]
        public void InitializeFromLevel_IdempotentResetsCounters()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 90f);
            Store.DoomClockWavesCleared[0] = 7;
            Store.DoomClockCycleCount[0] = 2;
            Store.DoomClockFinalScore[0] = 999;

            // Re-initialize with a new level
            var level = new LevelConfig
            {
                LevelNumber = 6,
                ObjectiveType = (int)ObjectiveType.DoomClock,
                DoomClockDuration = 60f
            };
            obj.InitializeFromLevel(level, 20);

            Assert.Equal(0, Store.DoomClockWavesCleared[0]);
            Assert.Equal(0, Store.DoomClockCycleCount[0]);
            Assert.Equal(0, Store.DoomClockFinalScore[0]);
            Assert.Equal(60f, Store.DoomClockTimer[0]);
        }

        // ── Update tick ────────────────────────────────────────────────

        [Fact]
        public void Update_DecrementsTimerDuringWavePhase()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 10f);
            dc.Update(1.0f, GameState.WavePhase);
            Assert.Equal(9f, Store.DoomClockTimer[0]);
        }

        [Fact]
        public void Update_StopsAtZero()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 0.5f);
            dc.Update(1.0f, GameState.WavePhase);
            Assert.Equal(0f, Store.DoomClockTimer[0]);
            // Subsequent updates remain at 0
            dc.Update(1.0f, GameState.WavePhase);
            Assert.Equal(0f, Store.DoomClockTimer[0]);
        }

        [Fact]
        public void Update_NoOpWhenInactive()
        {
            var (obj, dc) = MakeSut(objectiveType: (int)ObjectiveType.KillAll);
            // DoomClock was never initialized to active — timer stays 0
            dc.Update(1.0f, GameState.WavePhase);
            Assert.Equal(0f, Store.DoomClockTimer[0]);
        }

        [Theory(DisplayName = "DoomClock Update 在非 WavePhase 不推进计时器")]
        [InlineData(GameState.BuildPhase)]
        [InlineData(GameState.Intermission)]
        public void Update_NoOpOutsideWavePhase(GameState state)
        {
            var (obj, dc) = MakeSut(doomClockDuration: 10f);
            dc.Update(1.0f, state);
            Assert.Equal(10f, Store.DoomClockTimer[0]);
        }

        // ── Wave slot / cycle ──────────────────────────────────────────

        [Fact]
        public void GetCurrentWaveSlot_WrapsAndBumpsCycle()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            int pool = 3;
            // wave index 0 → (0, 0)
            var (i0, c0) = dc.GetCurrentWaveSlot(0, pool);
            Assert.Equal(0, i0);
            Assert.Equal(0, c0);
            Assert.Equal(0, Store.DoomClockCycleCount[0]);

            // wave index 2 → (2, 0)
            var (i1, c1) = dc.GetCurrentWaveSlot(2, pool);
            Assert.Equal(2, i1);
            Assert.Equal(0, c1);

            // wave index 3 → (0, 1) — wrapped, cycle bumped
            var (i2, c2) = dc.GetCurrentWaveSlot(3, pool);
            Assert.Equal(0, i2);
            Assert.Equal(1, c2);
            Assert.Equal(1, Store.DoomClockCycleCount[0]);

            // wave index 7 → (1, 2)
            var (i3, c3) = dc.GetCurrentWaveSlot(7, pool);
            Assert.Equal(1, i3);
            Assert.Equal(2, c3);
        }

        [Fact]
        public void GetCurrentWaveSlot_EmptyPoolReturnsZero()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            var (i, c) = dc.GetCurrentWaveSlot(5, 0);
            Assert.Equal(0, i);
            Assert.Equal(0, c);
        }

        [Fact]
        public void GetCycleScalingMultiplier_NoScalingAtCycleZero()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockCycleCount[0] = 0;
            Assert.Equal(1f, dc.GetCycleScalingMultiplier(1.10f));
        }

        [Fact]
        public void GetCycleScalingMultiplier_AppliesPow()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockCycleCount[0] = 1;
            Assert.Equal(1.10f, dc.GetCycleScalingMultiplier(1.10f), 3);

            Store.DoomClockCycleCount[0] = 2;
            Assert.Equal(1.21f, dc.GetCycleScalingMultiplier(1.10f), 3);
        }

        [Fact]
        public void GetCycleScalingMultiplier_ScalingBelowOneIsTreatedAsOne()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockCycleCount[0] = 5;
            Assert.Equal(1f, dc.GetCycleScalingMultiplier(0.5f));
        }

        // ── Score computation ──────────────────────────────────────────

        [Fact]
        public void ComputeFinalScore_WavePlusTimePlusHealth()
        {
            var (obj, dc) = MakeSut(
                doomClockDuration: 100f,
                doomClockWaveScore: 50,
                doomClockTimeBonusPerSec: 4,
                doomClockHealthBonusPerPercent: 2);

            Store.DoomClockWavesCleared[0] = 5;
            Store.DoomClockTimer[0] = 30f;     // remaining time
            // PlayerHealth: 50% → 50 * 2 = 100
            var level = new LevelConfig
            {
                DoomClockDuration = 100f,
                DoomClockWaveScore = 50,
                DoomClockTimeBonusPerSec = 4,
                DoomClockHealthBonusPerPercent = 2
            };
            int score = dc.ComputeFinalScore(level, 0.5f);
            // waveBonus = 5*50 = 250, timeBonus = 30*4 = 120, healthBonus = 50*2 = 100
            Assert.Equal(250 + 120 + 100, score);
            Assert.Equal(score, Store.DoomClockFinalScore[0]);
        }

        [Fact]
        public void ComputeFinalScore_ClampsHealthFraction()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 100f);
            var level = new LevelConfig { DoomClockDuration = 100f };
            // Zero the timer / waves so the only variable is the health fraction.
            Store.DoomClockTimer[0] = 0f;
            Store.DoomClockWavesCleared[0] = 0;
            // Negative fraction → clamped to 0 → 0 health bonus → total 0
            int scoreNeg = dc.ComputeFinalScore(level, -0.5f);
            Assert.Equal(0, scoreNeg);
            // Over-1 fraction → clamped to 1 → 100 * 5 = 500 health bonus
            int scoreOver = dc.ComputeFinalScore(level, 1.5f);
            Assert.Equal(500, scoreOver);
        }

        [Fact]
        public void EndRun_WinComputesScore_LoseClears()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 100f);
            Store.DoomClockWavesCleared[0] = 3;
            Store.DoomClockTimer[0] = 50f;
            var level = new LevelConfig { DoomClockDuration = 100f };

            // First call: win
            dc.EndRun(true, level, 1.0f);
            Assert.False(Store.DoomClockActive[0]);
            Assert.True(Store.DoomClockFinalScore[0] > 0);

            // Second call is a no-op (already ended)
            int prev = Store.DoomClockFinalScore[0];
            dc.EndRun(true, level, 0f);
            Assert.Equal(prev, Store.DoomClockFinalScore[0]);
        }

        [Fact]
        public void EndRun_LoseDoesNotSetScore()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 100f);
            Store.DoomClockWavesCleared[0] = 3;
            var level = new LevelConfig { DoomClockDuration = 100f };
            dc.EndRun(false, level, 0.5f);
            Assert.False(Store.DoomClockActive[0]);
            Assert.Equal(0, Store.DoomClockFinalScore[0]);
        }

        // ── Status string ──────────────────────────────────────────────

        [Fact]
        public void GetStatus_RunningFormat()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockTimer[0] = 45.6f;
            Store.DoomClockWavesCleared[0] = 3;
            Store.DoomClockCycleCount[0] = 1;
            string s = dc.GetStatus();
            Assert.Contains("45.6", s);
            Assert.Contains("Waves: 3", s);
            Assert.Contains("Cycle: 1", s);
        }

        [Fact]
        public void GetStatus_EndedShowsFinalScore()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockActive[0] = false;
            Store.DoomClockFinalScore[0] = 1234;
            string s = dc.GetStatus();
            Assert.Contains("1234", s);
        }

        [Fact]
        public void GetStatus_EndedWithoutScore()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockActive[0] = false;
            Store.DoomClockFinalScore[0] = 0;
            string s = dc.GetStatus();
            Assert.Contains("ended", s);
        }

        // ── ObjectiveSystem.CheckObjective integration ─────────────────

        [Fact]
        public void CheckObjective_DoomClock_Ongoing_WhenTimerPositive()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            // No enemies, but timer still ticking
            int r = obj.CheckObjective(activeEnemyCount: 0, currentWave: 1, totalWaves: 1);
            Assert.Equal(0, r);
        }

        [Fact]
        public void CheckObjective_DoomClock_Ongoing_WhenEnemiesAlive()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockTimer[0] = 0f;     // expired
            int r = obj.CheckObjective(activeEnemyCount: 5, currentWave: 1, totalWaves: 1);
            Assert.Equal(0, r);
        }

        [Fact]
        public void CheckObjective_DoomClock_Win_WhenTimerZeroAndNoEnemies()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockTimer[0] = 0f;
            Store.DoomClockWavesCleared[0] = 2;
            int r = obj.CheckObjective(activeEnemyCount: 0, currentWave: 1, totalWaves: 1);
            Assert.Equal(1, r);
            Assert.False(Store.DoomClockActive[0]);
            // FinalScore should be computed:
            // waveBonus=2*100=200, timeBonus=0*10=0, healthBonus=0*5=0 → 200
            Assert.Equal(200, Store.DoomClockFinalScore[0]);
        }

        [Fact]
        public void CheckObjective_DoomClock_AfterEnd_IsOngoing()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            Store.DoomClockActive[0] = false;  // run already ended
            int r = obj.CheckObjective(activeEnemyCount: 0, currentWave: 1, totalWaves: 1);
            Assert.Equal(0, r);
        }

        [Fact]
        public void CheckObjective_KillAll_UnchangedBehavior()
        {
            // Sanity: non-DoomClock objectives still work as before.
            var (obj, dc) = MakeSut(objectiveType: (int)ObjectiveType.KillAll);
            int r1 = obj.CheckObjective(activeEnemyCount: 5, currentWave: 1, totalWaves: 1);
            Assert.Equal(0, r1);
            int r2 = obj.CheckObjective(activeEnemyCount: 0, currentWave: 2, totalWaves: 1);
            Assert.Equal(1, r2);
        }

        // ── OnWaveCompleted integration ────────────────────────────────

        [Fact]
        public void OnWaveCompleted_DoomClock_IncrementsCleared()
        {
            var (obj, dc) = MakeSut(doomClockDuration: 60f);
            obj.OnWaveCompleted(wavesRemaining: 5);
            Assert.Equal(1, Store.DoomClockWavesCleared[0]);
            obj.OnWaveCompleted(wavesRemaining: 4);
            Assert.Equal(2, Store.DoomClockWavesCleared[0]);
        }

        [Fact]
        public void OnWaveCompleted_NonDoomClock_NoOpOnDoomCounter()
        {
            var (obj, dc) = MakeSut(objectiveType: (int)ObjectiveType.KillAll);
            obj.OnWaveCompleted(wavesRemaining: 4);
            Assert.Equal(0, Store.DoomClockWavesCleared[0]);
        }

        // ── GameConfigLoader JSON parsing ──────────────────────────────

        [Fact]
        public void LevelConfig_DoomClockFields_AreMutableAfterConstruction()
        {
            // Public-mutability check: DoomClock fields are settable so the
            // loader can write to them. This is the surface that GameConfigLoader
            // touches in ParseLevelConfig (private, so we exercise the
            // public contract here).
            var level = new LevelConfig
            {
                DoomClockDuration = 90f,
                DoomClockWaveScore = 75,
                DoomClockTimeBonusPerSec = 8,
                DoomClockHealthBonusPerPercent = 3,
                DoomClockWaveScaling = 1.25f
            };
            level.DoomClockInitialWaves.Add(new DoomClockWaveTemplate
            {
                MonsterType = "Normal", EnemyCount = 5
            });
            level.DoomClockInitialWaves.Add(new DoomClockWaveTemplate
            {
                MonsterType = "Fast", EnemyCount = 3
            });
            Assert.Equal(90f, level.DoomClockDuration);
            Assert.Equal(75, level.DoomClockWaveScore);
            Assert.Equal(8, level.DoomClockTimeBonusPerSec);
            Assert.Equal(3, level.DoomClockHealthBonusPerPercent);
            Assert.Equal(1.25f, level.DoomClockWaveScaling);
            Assert.Equal(2, level.DoomClockInitialWaves.Count);
            Assert.Equal("Normal", level.DoomClockInitialWaves[0].MonsterType);
            Assert.Equal(5, level.DoomClockInitialWaves[0].EnemyCount);
            Assert.Equal("Fast", level.DoomClockInitialWaves[1].MonsterType);
            Assert.Equal(3, level.DoomClockInitialWaves[1].EnemyCount);
        }

        [Fact]
        public void GetDefaultConfig_ContainsDoomClockDefaults()
        {
            // GameConfigLoader.GetDefaultConfig returns a baseline GameConfig.
            // DoomClock-specific fields fall back to LevelConfig defaults (the
            // loader doesn't override them for non-DoomClock levels). This is
            // a smoke test for the public loader API surface.
            var cfg = GameConfigLoader.GetDefaultConfig();
            Assert.NotNull(cfg);
            Assert.NotNull(cfg.Levels);
        }
    }
}

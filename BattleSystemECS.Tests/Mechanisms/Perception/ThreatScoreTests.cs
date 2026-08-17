using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Perception
{
    /// <summary>
    /// Invariants for the Threat Score / Dynamic Difficulty Scaling system (Round 99 Direction 5).
    /// 威胁公式不再在测试内复刻：EMA 衰减走 FrameScheduler 真实 Tick，
    /// 生成缩放走 WaveSpawningSystem 真实生成路径，期望倍率由读取到的
    /// ThreatScoreConfig 常量与显式注入的 DPS 推导。
    /// </summary>
    public class ThreatScoreTests : BattleTestBase
    {
        // ─── Field initialization tests ──────────────────────────────────

        [Fact]
        public void PlayerRecentDPS_DefaultsToZero()
        {
            for (int p = 0; p < 10; p++)
            {
                Assert.Equal(0f, Store.PlayerRecentDPS[p]);
                Assert.Equal(0f, Store.PlayerDPSAccumulator[p]);
            }
        }

        // ─── Config constants invariants ─────────────────────────────────

        [Fact]
        public void ThreatScoreConfig_HasExpectedDefaults()
        {
            // Defaults must be safe: rate 0 means no scaling, max > 1 to allow growth.
            Assert.True(ThreatScoreConfig.ThreatScalingRate > 0f,
                "ThreatScalingRate must be positive to allow scaling");
            Assert.True(ThreatScoreConfig.MaxThreatMultiplier > 1.0f,
                "MaxThreatMultiplier must be > 1.0 to allow growth");
            Assert.True(ThreatScoreConfig.MinThreatMultiplier <= 1.0f,
                "MinThreatMultiplier must be <= 1.0 (system only makes harder)");
            Assert.Equal(1.0f, ThreatScoreConfig.MinThreatMultiplier);
            Assert.True(ThreatScoreConfig.DPSWindowSec > 0f,
                "DPSWindowSec must be positive");
        }

        // ─── EMA 衰减：走 FrameScheduler 真实 Tick ───────────────────────

        [Fact]
        public void FrameScheduler_ZeroDt_KeepsRecentDPS_AndResetsAccumulator()
        {
            Store.PlayerRecentDPS[0] = 123f;
            Store.PlayerDPSAccumulator[0] = 45f;
            var scheduler = new FrameScheduler(Store, Config);

            scheduler.Tick(0f, 0); // dt=0：跳过 blending，保留上一帧值

            Assert.Equal(123f, Store.PlayerRecentDPS[0]);
            Assert.Equal(0f, Store.PlayerDPSAccumulator[0]);
        }

        [Fact]
        public void FrameScheduler_PositiveDt_DecaysRecentDPS_AndResetsAccumulator()
        {
            Store.PlayerRecentDPS[0] = 1000f;
            Store.PlayerDPSAccumulator[0] = 60f;
            var scheduler = new FrameScheduler(Store, Config);

            scheduler.Tick(0.5f, 0);

            // 真实 EMA 衰减路径：结果必须严格介于 0 与初值之间，且累加器归零。
            Assert.True(Store.PlayerRecentDPS[0] > 0f && Store.PlayerRecentDPS[0] < 1000f,
                $"EMA decay must reduce RecentDPS (now {Store.PlayerRecentDPS[0]})");
            Assert.Equal(0f, Store.PlayerDPSAccumulator[0]);
        }

        // ─── 生成路径：PlayerRecentDPS 驱动敌人 HP 缩放 ──────────────────

        private float FirstSpawnedEnemyHealth(float recentDps)
        {
            // 保留独立 store：同一测试需两个独立世界比较 baseline 与 scaled 血量。
            var store = new ComponentStore();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.PlayerRecentDPS[0] = recentDps;
            var sys = new WaveSpawningSystem(store, Renderer, Config);
            sys.Update();
            Assert.NotEmpty(store.ActiveEnemyIds);
            return store.EnemyHealth[store.ActiveEnemyIds[0]];
        }

        [Fact]
        public void WaveSpawning_SpawnedHealth_ScalesWithInjectedDPS()
        {
            const float injectedDps = 10000f;
            float baselineHealth = FirstSpawnedEnemyHealth(0f);
            float scaledHealth = FirstSpawnedEnemyHealth(injectedDps);

            // 期望倍率由读取到的 ThreatScalingRate 与注入 DPS 推导。
            float expectedMult = 1.0f + injectedDps * ThreatScoreConfig.ThreatScalingRate;
            Assert.Equal(baselineHealth * expectedMult, scaledHealth, 3);
            Assert.True(scaledHealth > baselineHealth);
        }

        [Fact]
        public void WaveSpawning_ExtremeDPS_CappedAtMaxThreatMultiplier()
        {
            float baselineHealth = FirstSpawnedEnemyHealth(0f);
            float scaledHealth = FirstSpawnedEnemyHealth(100_000_000f);

            // 极端 DPS 的倍率被生产路径钳制到读取到的 MaxThreatMultiplier。
            Assert.Equal(baselineHealth * ThreatScoreConfig.MaxThreatMultiplier, scaledHealth, 3);
        }
    }
}

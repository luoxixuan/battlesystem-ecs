using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    public class UpgradeSystemTests
    {
        private (ComponentStore store, GameConfig config, int playerId) CreateEnv()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PlayerMaxHealth[id] = 200f;
            store.PlayerCurrentHealth[id] = 200f;
            store.PlayerGold[id] = 0f;
            store.PlayerUpgradeThreshold[id] = 100f;
            store.PositionX[id] = 5f;
            store.PositionY[id] = 0f;
            return (store, new GameConfig(), id);
        }

        // ─── Bug#31: GetUpgradeBuffs 有默认值，不为空 ───────────────────────────

        [Fact] public void UpgradeBuffs_HasDefaultValues()
        {
            var config = new GameConfig();
            var buffs = config.UpgradeBuffs;
            Assert.NotNull(buffs);
            Assert.True(buffs.Count > 0, "UpgradeBuffs should have at least one default buff");
        }

        [Fact] public void UpgradeBuffs_ContainsExpectedBuffs()
        {
            var config = new GameConfig();
            var buffs = config.UpgradeBuffs;
            // 默认值包含 Attack、Speed、Crit
            Assert.Contains("Attack+10%", buffs);
            Assert.Contains("Crit Rate+5%", buffs);
            Assert.Contains("Defense+10%", buffs);
        }

        // ─── Bug#31: Update 触发升级后玩家获得 buff ──────────────────────────────

        [Fact] public void Update_GainsBuffFromConfig()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();

            // 确保 config 有可用的 buff
            Assert.True(config.UpgradeBuffs.Count > 0);

            var sys = new UpgradeSystem(store, r, pid, config);

            // 给足够的金币触发升级（threshold 是 100）
            store.PlayerGold[pid] = 200f;

            sys.Update();

            Assert.True(store.GetPlayerBuffs(pid).Count > 0,
                "Player should gain at least one buff after Update (upgrade triggered)");
        }

        [Fact] public void Update_DoesNotGrantSameBuffTwice()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new UpgradeSystem(store, r, pid, config);

            // 足够的金币 + 足够的 threshold 差，确保两次 Upgrade 都触发
            store.PlayerGold[pid] = 1000f;
            store.PlayerUpgradeThreshold[pid] = 100f;

            sys.Update(); // 第一次升级
            var buffsAfterFirst = store.GetPlayerBuffs(pid);
            Assert.True(buffsAfterFirst.Count > 0);
            string firstBuff = buffsAfterFirst[0];

            // Reset gold 以便第二次升级能再次触发（threshold 已涨到 150）
            store.PlayerGold[pid] = 1000f; // gold >= threshold again
            sys.Update(); // 第二次升级

            // 同一个 buff 不会被加两次
            int countOfFirstBuff = 0;
            foreach (var b in store.GetPlayerBuffs(pid))
                if (b == firstBuff) countOfFirstBuff++;
            Assert.Equal(1, countOfFirstBuff);
        }

        [Fact] public void Update_FailsWhenGoldBelowThreshold()
        {
            var (store, config, pid) = CreateEnv();
            var r = new MockRenderer();
            var sys = new UpgradeSystem(store, r, pid, config);

            store.PlayerGold[pid] = 10f; // 远低于 threshold 100

            sys.Update();

            // 金币不足时 Update 后无 buff
            Assert.Empty(store.GetPlayerBuffs(pid));
        }
    }
}

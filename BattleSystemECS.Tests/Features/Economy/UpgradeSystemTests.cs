using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Economy
{
    public class UpgradeSystemTests : BattleTestBase
    {
        private (GameConfig config, int playerId) CreateEnv()
        {
            int id = Store.CreateEntity();
            Store.PlayerMaxHealth[id] = 200f;
            Store.PlayerCurrentHealth[id] = 200f;
            Store.PlayerGold[id] = 0f;
            Store.PlayerUpgradeThreshold[id] = 100f;
            Store.PositionX[id] = 5f;
            Store.PositionY[id] = 0f;
            return (Config, id);
        }

        // ─── Bug#31: GetUpgradeBuffs 有默认值，不为空 ───────────────────────────

        [Fact] public void UpgradeBuffs_HasDefaultValues()
        {
            var config = Config;
            var buffs = config.UpgradeBuffs;
            Assert.NotNull(buffs);
            Assert.True(buffs.Count > 0, "UpgradeBuffs should have at least one default buff");
        }

        [Fact] public void UpgradeBuffs_ContainsExpectedBuffs()
        {
            var config = Config;
            var buffs = config.UpgradeBuffs;
            // 结构自洽断言：不钉具体配置字符串。
            Assert.NotEmpty(buffs);
            Assert.Equal(buffs.Count, buffs.Distinct().Count()); // 无重复
            Assert.All(buffs, buff =>
            {
                Assert.False(string.IsNullOrWhiteSpace(buff), "buff name must be non-empty");
                Assert.Contains('+', buff);   // 默认 buff 约定为 "名称+数值%" 格式
                Assert.EndsWith("%", buff);
            });
        }

        // ─── Bug#31: Update 触发升级后玩家获得 buff ──────────────────────────────

        [Fact] public void Update_GainsBuffFromConfig()
        {
            var (config, pid) = CreateEnv();

            // 确保 config 有可用的 buff
            Assert.True(config.UpgradeBuffs.Count > 0);

            var sys = new UpgradeSystem(Store, Renderer, pid, config);

            // 给足够的金币触发升级（threshold 是 100）
            Store.PlayerGold[pid] = 200f;

            sys.Update();

            Assert.True(Store.GetPlayerBuffs(pid).Count > 0,
                "Player should gain at least one buff after Update (upgrade triggered)");
        }

        [Fact] public void Update_DoesNotGrantSameBuffTwice()
        {
            var (config, pid) = CreateEnv();
            var sys = new UpgradeSystem(Store, Renderer, pid, config);

            // 足够的金币 + 足够的 threshold 差，确保两次 Upgrade 都触发
            Store.PlayerGold[pid] = 1000f;
            Store.PlayerUpgradeThreshold[pid] = 100f;

            sys.Update(); // 第一次升级
            var buffsAfterFirst = Store.GetPlayerBuffs(pid);
            Assert.True(buffsAfterFirst.Count > 0);
            string firstBuff = buffsAfterFirst[0];

            // Reset gold 以便第二次升级能再次触发（threshold 已涨到 150）
            Store.PlayerGold[pid] = 1000f; // gold >= threshold again
            sys.Update(); // 第二次升级

            // 同一个 buff 不会被加两次
            int countOfFirstBuff = 0;
            foreach (var b in Store.GetPlayerBuffs(pid))
                if (b == firstBuff) countOfFirstBuff++;
            Assert.Equal(1, countOfFirstBuff);
        }

        [Fact] public void Update_FailsWhenGoldBelowThreshold()
        {
            var (config, pid) = CreateEnv();
            var sys = new UpgradeSystem(Store, Renderer, pid, config);

            Store.PlayerGold[pid] = 10f; // 远低于 threshold 100

            sys.Update();

            // 金币不足时 Update 后无 buff
            Assert.Empty(Store.GetPlayerBuffs(pid));
        }
    }
}
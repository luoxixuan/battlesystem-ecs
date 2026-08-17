using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Integration
{
    public class GameSimulationTests : BattleTestBase
    {
        [Fact]
        public void GameLoop_SpawnsConfiguredEnemiesAndAdvancesSkillCooldowns()
        {
            // 玩家伤害/射程显式置 0：本测试只验证波次生成与技能冷却推进，
            // 避免依赖 WaveSpawningSystem 内部未播种随机数产生的出生点。
            int pid = Player(p =>
            {
                p.X = 5f;
                p.Y = 0f;
                p.Health = 200f;
                p.AttackDamage = 0f;
                p.AttackRange = 0f;
            });

            const int updateCount = 10;

            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            int spawnBatchSize = ReadConfiguredWaveSpawnBatchSize(wave);

            var waveConfig = Config.Levels[0].Waves[0];
            waveConfig.EnemyCount = updateCount * spawnBatchSize; // 用真实批量大小注入波次总数
            int expectedSpawned = waveConfig.GetTotalEnemyCount();

            var atk = new PlayerTowerAttackSystem(Store, Renderer, pid, Config);
            var skill = new SkillSystem(Store, Renderer, pid, Config);
            skill.InitializePlayerSkills();

            // 显式注入 3s 冷却：10 次 Update(1f) 后必须精确归零（无 CDR / Adrenaline 加成）。
            const float injectedCooldown = 3f;
            var slot = Store.GetAbility(pid, 0);
            slot.CurrentCooldown = injectedCooldown;
            Store.SetAbility(pid, 0, slot);

            for (int turn = 0; turn < updateCount; turn++)
            {
                wave.Update();
                atk.SetTurn(turn);
                atk.Update();
                skill.Update(1f);
            }

            // 精确期望：注入总数 = 10 帧 × 真实配置的每帧批量大小，且尚未触发波次完成。
            Assert.Equal(expectedSpawned, wave.GetTotalEnemiesSpawned());
            Assert.Equal(expectedSpawned, Store.GetActiveEnemyCount());
            Assert.Equal(1, wave.GetCurrentWave());

            // 攻击系统的 SetTurn 缓存应看到全部生成敌人；技能冷却 10 次 Update 后精确归零。
            Assert.Equal(expectedSpawned, atk.GetCachedEnemyCount());
            Assert.Equal(0f, Store.GetAbility(pid, 0).CurrentCooldown, 5);
        }

        // ─── Bug 回归：SetTurn 每回合必须刷新 _activeEnemyList 缓存 ───

        [Fact]
        public void SetTurn_RefreshesActiveEnemyList_EachTurn()
        {
            Player(p => { p.X = 5f; p.Y = 0f; p.Health = 200f; });

            var wave = new WaveSpawningSystem(Store, Renderer, Config);
            var atk = new PlayerTowerAttackSystem(Store, Renderer, 0, Config);

            // Turn 0：生成第一批后，缓存必须精确等于波次系统的累计生成数。
            wave.Update();
            atk.SetTurn(0);
            int spawnedTurn0 = wave.GetTotalEnemiesSpawned();
            Assert.Equal(spawnedTurn0, atk.GetCachedEnemyCount());

            // Turn 1：再生成一批后，缓存必须刷新为新的累计值，而不是沿用旧列表。
            wave.Update();
            atk.SetTurn(1);
            int spawnedTurn1 = wave.GetTotalEnemiesSpawned();
            Assert.Equal(spawnedTurn1, atk.GetCachedEnemyCount());
            Assert.True(spawnedTurn1 > spawnedTurn0, "第二批敌人生成后累计生成数必须增加");
        }
    }
}

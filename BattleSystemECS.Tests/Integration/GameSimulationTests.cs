using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Integration
{
    public class GameSimulationTests
    {
        [Fact] public void GameLoop_SpawnsEnemiesAndAdvancesTurns()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.PlayerAttackDamage[pid] = 10f;
            store.PlayerAttackRange[pid] = 3f;
            store.PositionX[pid] = 5f;
            store.PositionY[pid] = 0f;

            var wave = new WaveSpawningSystem(store, r, config);
            var atk = new PlayerTowerAttackSystem(store, r, pid, config);
            var skill = new SkillSystem(store, r, pid, config);

            for (int turn = 0; turn < 10; turn++)
            {
                wave.Update();
                atk.SetTurn(turn);
                atk.Update();
                skill.Update(1f);
            }
            Assert.True(wave.GetTotalEnemiesSpawned() > 0);
        }

        // ─── Bug 回归：SetTurn 每回合必须刷新 _activeEnemyList 缓存 ───

        [Fact]
        public void SetTurn_RefreshesActiveEnemyList_EachTurn()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.PlayerAttackDamage[pid] = 10f;
            store.PlayerAttackRange[pid] = 3f;
            store.PositionX[pid] = 5f;
            store.PositionY[pid] = 0f;

            var wave = new WaveSpawningSystem(store, r, config);
            var atk = new PlayerTowerAttackSystem(store, r, pid, config);

            // Turn 0: spawn initial batch
            wave.Update();
            atk.SetTurn(0);
            int countTurn0 = atk.GetCachedEnemyCount();

            // Turn 1: spawn more enemies, then refresh cache
            wave.Update();
            atk.SetTurn(1);
            int countTurn1 = atk.GetCachedEnemyCount();

            Assert.True(countTurn1 > countTurn0,
                $"After SetTurn(1) new enemies should be in list: turn0={countTurn0}, turn1={countTurn1}");
        }
    }
}

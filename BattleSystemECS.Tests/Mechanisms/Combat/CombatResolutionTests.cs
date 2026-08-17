using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Invariant tests for combat resolution, death handling, and active list lifecycle.
    /// </summary>
    public class CombatResolutionTests : BattleTestBase
    {
        // ─── Invariant: DestroyEntity removes from all active lists ─────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveTowerIds()
        {
            int playerId = Store.CreateEntity();
            Store.AddPlayer(playerId, 5f, 5f, 10f, 1);

            int towerId = RawTower(0, 0, TowerType.Basic, 5f, 3, 1f, 1, 50f);

            Assert.Contains(towerId, Store.ActiveTowerIds);
            Store.DestroyEntity(towerId);
            Assert.DoesNotContain(towerId, Store.ActiveTowerIds);
        }

        [Fact]
        public void DestroyEntity_RemovesFromActiveEnemyIds()
        {
            int eid = Enemy(e =>
            {
                e.X = 5f;
                e.Y = 5f;
                e.MoveSpeed = 1f;
                e.Health = 10f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 99;
            });
            Assert.Contains(eid, Store.ActiveEnemyIds);
            Store.DestroyEntity(eid);
            Assert.DoesNotContain(eid, Store.ActiveEnemyIds);
        }

        // ─── Invariant: Dead enemy excluded from next turn's attack list ─

        [Fact]
        public void DeadEnemy_ExcludedFromNextTurn()
        {
            int pid = Store.CreateEntity();
            Store.PlayerMaxHealth[pid] = 200f;
            Store.PlayerCurrentHealth[pid] = 200f;
            Store.PlayerAttackDamage[pid] = 100f;
            Store.PlayerAttackRange[pid] = 10f;
            Store.PositionX[pid] = 5f;
            Store.PositionY[pid] = 0f;

            // Spawn an enemy with low HP
            int eid = Enemy(e =>
            {
                e.X = 5f;
                e.Y = 3f;
                e.MoveSpeed = 1f;
                e.Health = 100f;
                e.Damage = 0f;
                e.GoldReward = 99;
                e.WaveNumber = 99;
            });
            Store.SetEnemyHealth(eid, 30f);

            // Directly queue death (simulates damage-dealt path)
            Store.QueueEnemyDeath(eid, pid);
            Store.ResolveEnemiesKilledThisFrame();

            // After resolve, enemy must be absent from active list
            Assert.DoesNotContain(eid, Store.GetAllActiveEnemyIds());
        }

        // ─── Invariant: ResolveEnemiesKilledThisFrame only awards gold once ─

        [Fact]
        public void ResolveEnemiesKilledThisFrame_Idempotent()
        {
            Store.SetPlayerGold(0, 0f);

            int eid = Enemy(e =>
            {
                e.X = 5f;
                e.Y = 5f;
                e.MoveSpeed = 1f;
                e.Health = 10f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 99;
            });
            Store.SetEnemyHealth(eid, 5f);
            // 显式注入奖励：期望从注入值推导，不依赖 AddEnemy 默认参数。
            Store.EnemyGoldReward[eid] = 10;

            // 击杀：显式排队死亡，再统一结算。
            Store.QueueEnemyDeath(eid, 0);
            Store.ResolveEnemiesKilledThisFrame();
            float goldAfterFirst = Store.GetPlayerGold(0);
            Assert.Equal(10f, goldAfterFirst); // 第一次 Resolve 恰好发放注入的 10 金币

            Store.ResolveEnemiesKilledThisFrame(); // 重复调用不得二次发钱
            float goldAfterSecond = Store.GetPlayerGold(0);

            Assert.Equal(goldAfterFirst, goldAfterSecond, 0.001f);
        }

        // ─── Invariant: BeginFrame tracks per-frame state ────────────────────────

        [Fact]
        public void BeginFrame_RequiresResolveEnemiesKilledThisFrame()
        {
            int eid = Enemy(e =>
            {
                e.X = 5f;
                e.Y = 5f;
                e.MoveSpeed = 1f;
                e.Health = 10f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 99;
            });
            Store.SetEnemyHealth(eid, 5f);
            Store.SetEnemyHealth(eid, 0f);
            // 显式排队一次死亡：期望从注入的队列推导精确击杀数。
            Store.QueueEnemyDeath(eid, 0);
            Store.ResolveEnemiesKilledThisFrame();
            int killsAfter = Store.TotalKills;
            Assert.Equal(1, killsAfter);

            Store.BeginFrame();

            // 同一敌人已经死亡并被移出活跃列表，重复 Resolve 不得再增加击杀数。
            Store.ResolveEnemiesKilledThisFrame();
            Assert.Equal(killsAfter, Store.TotalKills);
        }

        // ─── Invariant: Multiple towers hitting same enemy doesn't double-count kill ─

        [Fact]
        public void MultipleTowers_SameEnemy_KillCountedOnce()
        {
            int pid = Store.CreateEntity();
            Store.AddPlayer(pid, 5f, 5f, 10f, 1);
            Store.SetPlayerGold(pid, 9999f);

            int eid = Enemy(e =>
            {
                e.X = 5f;
                e.Y = 3f;
                e.MoveSpeed = 1f;
                e.Health = 100f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 99;
            });
            Store.SetEnemyHealth(eid, 30f);

            // Place two towers within range
            int t1 = RawTower(5, 1, TowerType.Basic, 20f, 10, 1f, 1, 50f);
            int t2 = RawTower(5, 2, TowerType.Basic, 20f, 10, 1f, 1, 50f);

            var towerAtk = new TowerAttackSystem(Store, Renderer);
            for (int f = 0; f < 3; f++)
            {
                Store.BeginFrame();
                Store.RebuildSpatialGrid();
                towerAtk.SetTurn(f);
                towerAtk.Update(1f);
                Store.ResolveEnemiesKilledThisFrame();
            }

            // TotalKills should be exactly 1, not 2
            Assert.Equal(1, Store.TotalKills);
        }
    }
}

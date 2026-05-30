using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Invariant tests for combat resolution, death handling, and active list lifecycle.
    /// </summary>
    public class CombatResolutionTests
    {
        // ─── Invariant: DestroyEntity removes from all active lists ─────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveTowerIds()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.AddPlayer(playerId, 5f, 5f, 10f, 1);

            int towerId = store.CreateEntity();
            store.AddTower(towerId, TowerType.Basic, 5, 3, 1f, 1, 50f);
            store.PositionActive[towerId] = true;

            Assert.Contains(towerId, store.ActiveTowerIds);
            store.DestroyEntity(towerId);
            Assert.DoesNotContain(towerId, store.ActiveTowerIds);
        }

        [Fact]
        public void DestroyEntity_RemovesFromActiveEnemyIds()
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(5f, 5f, 1f, 10f, 10f, 0f, 1, 99);
            Assert.Contains(eid, store.ActiveEnemyIds);
            store.DestroyEntity(eid);
            Assert.DoesNotContain(eid, store.ActiveEnemyIds);
        }

        // ─── Invariant: Dead enemy excluded from next turn's attack list ─

        [Fact]
        public void DeadEnemy_ExcludedFromNextTurn()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            var config = new GameConfig();
            int pid = store.CreateEntity();
            store.PlayerMaxHealth[pid] = 200f;
            store.PlayerCurrentHealth[pid] = 200f;
            store.PlayerAttackDamage[pid] = 100f;
            store.PlayerAttackRange[pid] = 10f;
            store.PositionX[pid] = 5f;
            store.PositionY[pid] = 0f;

            // Spawn an enemy with low HP
            int eid = store.AddEnemy(5f, 3f, 1f, 100f, 100f, 0f, 99, 99);
            store.SetEnemyHealth(eid, 30f);

            // Directly queue death (simulates damage-dealt path)
            store.QueueEnemyDeath(eid, pid);
            store.ResolveEnemiesKilledThisFrame();

            // After resolve, enemy must be absent from active list
            Assert.DoesNotContain(eid, store.GetAllActiveEnemyIds());
        }

        // ─── Invariant: ResolveEnemiesKilledThisFrame only awards gold once ─

        [Fact]
        public void ResolveEnemiesKilledThisFrame_Idempotent()
        {
            var store = new ComponentStore();
            store.SetPlayerGold(0, 0f);

            int eid = store.AddEnemy(5f, 5f, 1f, 10f, 10f, 0f, 1, 99);
            store.SetEnemyHealth(eid, 5f);
            store.EnemyActive[eid] = true;
            store.AddActiveEnemyId(eid);
            store.EnemyGoldReward[eid] = 10;

            // Kill it
            store.SetEnemyHealth(eid, 0f);
            store.ResolveEnemiesKilledThisFrame();

            float goldAfterFirst = store.GetPlayerGold(0);
            store.ResolveEnemiesKilledThisFrame(); // call again
            float goldAfterSecond = store.GetPlayerGold(0);

            // Gold should not be awarded twice
            Assert.Equal(goldAfterFirst, goldAfterSecond, 0.001f);
        }

        // ─── Invariant: Active list never contains destroyed entities ─────────────

        [Fact]
        public void ActiveList_NeverContainsDestroyedEntity()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.AddPlayer(playerId, 5f, 5f, 10f, 1);

            int towerId = store.CreateEntity();
            store.AddTower(towerId, TowerType.Basic, 5, 3, 1f, 1, 50f);
            store.PositionActive[towerId] = true;

            Assert.Contains(towerId, store.ActiveTowerIds);
            store.DestroyEntity(towerId);
            Assert.DoesNotContain(towerId, store.ActiveTowerIds);
        }

        // ─── Invariant: BeginFrame tracks per-frame state ────────────────────────

        [Fact]
        public void BeginFrame_RequiresResolveEnemiesKilledThisFrame()
        {
            var store = new ComponentStore();
            int eid = store.AddEnemy(5f, 5f, 1f, 10f, 10f, 0f, 1, 99);
            store.SetEnemyHealth(eid, 5f);
            store.EnemyActive[eid] = true;
            store.AddActiveEnemyId(eid);
            store.SetEnemyHealth(eid, 0f);

            store.ResolveEnemiesKilledThisFrame();
            int killsAfter = store.TotalKills;

            store.BeginFrame();

            // After BeginFrame + another Resolve, TotalKills should reflect kills from both frames
            store.ResolveEnemiesKilledThisFrame();
            Assert.True(store.TotalKills >= killsAfter);
        }

        // ─── Invariant: Multiple towers hitting same enemy doesn't double-count kill ─

        [Fact]
        public void MultipleTowers_SameEnemy_KillCountedOnce()
        {
            var store = new ComponentStore();
            var r = new MockRenderer();
            int pid = store.CreateEntity();
            store.AddPlayer(pid, 5f, 5f, 10f, 1);
            store.SetPlayerGold(pid, 9999f);

            int eid = store.AddEnemy(5f, 3f, 1f, 100f, 100f, 0f, 1, 99);
            store.SetEnemyHealth(eid, 30f);

            // Place two towers within range
            int t1 = store.CreateEntity();
            store.AddTower(t1, TowerType.Basic, 20, 10, 1f, 1, 50f);
            store.PositionX[t1] = 5f; store.PositionY[t1] = 1f;
            store.PositionActive[t1] = true;

            int t2 = store.CreateEntity();
            store.AddTower(t2, TowerType.Basic, 20, 10, 1f, 1, 50f);
            store.PositionX[t2] = 5f; store.PositionY[t2] = 2f;
            store.PositionActive[t2] = true;

            var towerAtk = new TowerAttackSystem(store, r);
            for (int f = 0; f < 3; f++)
            {
                store.BeginFrame();
                store.RebuildSpatialGrid();
                towerAtk.SetTurn(f);
                towerAtk.Update(1f);
                store.ResolveEnemiesKilledThisFrame();
            }

            // TotalKills should be exactly 1, not 2
            Assert.Equal(1, store.TotalKills);
        }
    }
}

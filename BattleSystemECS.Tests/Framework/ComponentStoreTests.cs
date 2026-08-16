using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Framework
{
    public class ComponentStoreTests
    {
        [Fact] public void NewStore_HasInitialEntities()
        {
            var store = new ComponentStore();
            Assert.True(store.NextEntityId >= 1);
        }

        [Fact] public void MAX_ENTITIES_IsReasonable()
        {
            Assert.True(ComponentStore.MAX_ENTITIES >= 1000);
        }

        [Fact] public void CreateEntity_IncrementsNextId()
        {
            var store = new ComponentStore();
            int before = store.NextEntityId;
            int id = store.CreateEntity();
            Assert.Equal(before, id);
            Assert.Equal(before + 1, store.NextEntityId);
        }

        // ─── Bug#30: DestroyEntity 必须从 ActiveTowerIds 移除 ─────────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveTowerIds()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.AddPlayer(playerId, 3f, 3f, 10f, 1);

            int towerId = store.CreateEntity();
            store.AddTower(towerId, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            store.AddPosition(towerId, 3f, 3f);

            Assert.Contains(towerId, store.ActiveTowerIds);
            store.DestroyEntity(towerId);
            Assert.DoesNotContain(towerId, store.ActiveTowerIds);
        }

        // ─── Bug#11: DestroyEntity 从 ActiveEnemyIds 移除 ───────────────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveEnemyIds()
        {
            var store = new ComponentStore();
            int enemyId = store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            Assert.Contains(enemyId, store.ActiveEnemyIds);
            store.DestroyEntity(enemyId);
            Assert.DoesNotContain(enemyId, store.ActiveEnemyIds);
        }

        [Fact]
        public void DestroyEntity_ClearsActiveFlags()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PositionActive[id] = true;
            store.EnemyActive[id] = true;
            store.DestroyEntity(id);
            Assert.False(store.PositionActive[id]);
            Assert.False(store.EnemyActive[id]);
        }

        // ─── Bug#21: GetAllActiveEnemyIds 返回防御性副本 ───────────────────────

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsDefensiveCopy()
        {
            var store = new ComponentStore();
            store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            store.AddEnemy(7f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            var active = store.GetAllActiveEnemyIds();
            int originalCount = active.Count;
            active.Clear();
            var fresh = store.GetAllActiveEnemyIds();
            Assert.Equal(originalCount, fresh.Count);
        }

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsOnlyActiveEnemies()
        {
            var store = new ComponentStore();
            int player = store.CreateEntity();
            store.AddPosition(player, 0, 0);
            int enemy = store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1);
            int neutral = store.CreateEntity();
            var active = store.GetAllActiveEnemyIds();
            Assert.Contains(enemy, active);
            Assert.DoesNotContain(player, active);
            Assert.DoesNotContain(neutral, active);
        }

        // ─── AddEnemy / CreateEntity 失败路径 ─────────────────────────────────

        [Fact]
        public void CreateEntity_Exhausted_ReturnsNegativeOne()
        {
            var store = new ComponentStore();
            int created = 0;
            while (store.CreateEntity() != -1) created++;
            Assert.True(created > 0);
            Assert.Equal(-1, store.CreateEntity());
        }

        [Fact]
        public void AddEnemy_FailsWhenPoolExhausted()
        {
            var store = new ComponentStore();
            while (store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1) != -1) { /* drain */ }
            int result = store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1);
            Assert.Equal(-1, result);
        }

        // ─── Bug#??: AddEnemy 不处理 entityId < 0 ──────────────────────────────

        [Fact]
        public void AddEnemy_DoesNotCrashOnNegativeEntityId()
        {
            var store = new ComponentStore();
            while (store.CreateEntity() != -1) { /* drain */ }
            // CreateEntity returns -1; AddEnemy must not access arrays with -1 index
            int result = store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            Assert.Equal(-1, result);
        }

        [Fact] public void PlayerHealth_ArrayAccess()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PlayerMaxHealth[id] = 200f;
            store.PlayerCurrentHealth[id] = 150f;
            Assert.Equal(200f, store.PlayerMaxHealth[id]);
            Assert.Equal(150f, store.PlayerCurrentHealth[id]);
        }

        [Fact] public void PlayerGold_ArrayAccess()
        {
            var store = new ComponentStore();
            int id = store.CreateEntity();
            store.PlayerGold[id] = 100;
            Assert.Equal(100, store.GetPlayerGold(id));
        }

        [Fact] public void TotalKills_StartsAtZero()
        {
            var store = new ComponentStore();
            Assert.Equal(0, store.TotalKills);
        }

        [Fact] public void TotalKills_CanIncrement()
        {
            var store = new ComponentStore();
            store.TotalKills++;
            store.TotalKills++;
            Assert.Equal(2, store.TotalKills);
        }
    }
}

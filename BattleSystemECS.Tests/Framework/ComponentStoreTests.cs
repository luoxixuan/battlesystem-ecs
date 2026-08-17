using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Components;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Framework
{
    public class ComponentStoreTests : BattleTestBase
    {
        [Fact] public void NewStore_HasInitialEntities()
        {
            Assert.True(Store.NextEntityId >= 1);
        }

        [Fact] public void MAX_ENTITIES_IsReasonable()
        {
            Assert.True(ComponentStore.MAX_ENTITIES >= 1000);
        }

        [Fact] public void CreateEntity_IncrementsNextId()
        {
            int before = Store.NextEntityId;
            int id = Store.CreateEntity();
            Assert.Equal(before, id);
            Assert.Equal(before + 1, Store.NextEntityId);
        }

        // ─── Bug#30: DestroyEntity 必须从 ActiveTowerIds 移除 ─────────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveTowerIds()
        {
            int playerId = Store.CreateEntity();
            Store.AddPlayer(playerId, 3f, 3f, 10f, 1);

            int towerId = Store.CreateEntity();
            Store.AddTower(towerId, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Store.AddPosition(towerId, 3f, 3f);

            Assert.Contains(towerId, Store.ActiveTowerIds);
            Store.DestroyEntity(towerId);
            Assert.DoesNotContain(towerId, Store.ActiveTowerIds);
        }

        // ─── Bug#11: DestroyEntity 从 ActiveEnemyIds 移除 ───────────────────────

        [Fact]
        public void DestroyEntity_RemovesFromActiveEnemyIds()
        {
            int enemyId = Store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            Assert.Contains(enemyId, Store.ActiveEnemyIds);
            Store.DestroyEntity(enemyId);
            Assert.DoesNotContain(enemyId, Store.ActiveEnemyIds);
        }

        [Fact]
        public void DestroyEntity_ClearsActiveFlags()
        {
            int id = Store.CreateEntity();
            Store.PositionActive[id] = true;
            Store.EnemyActive[id] = true;
            Store.DestroyEntity(id);
            Assert.False(Store.PositionActive[id]);
            Assert.False(Store.EnemyActive[id]);
        }

        // ─── Bug#21: GetAllActiveEnemyIds 返回防御性副本 ───────────────────────

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsDefensiveCopy()
        {
            Store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            Store.AddEnemy(7f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            var active = Store.GetAllActiveEnemyIds();
            int originalCount = active.Count;
            active.Clear();
            var fresh = Store.GetAllActiveEnemyIds();
            Assert.Equal(originalCount, fresh.Count);
        }

        [Fact]
        public void GetAllActiveEnemyIds_ReturnsOnlyActiveEnemies()
        {
            int player = Store.CreateEntity();
            Store.AddPosition(player, 0, 0);
            int enemy = Store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1);
            int neutral = Store.CreateEntity();
            var active = Store.GetAllActiveEnemyIds();
            Assert.Contains(enemy, active);
            Assert.DoesNotContain(player, active);
            Assert.DoesNotContain(neutral, active);
        }

        // ─── AddEnemy / CreateEntity 失败路径 ─────────────────────────────────

        [Fact]
        public void CreateEntity_Exhausted_ReturnsNegativeOne()
        {
            int created = 0;
            while (Store.CreateEntity() != -1) created++;
            Assert.True(created > 0);
            Assert.Equal(-1, Store.CreateEntity());
        }

        // ─── 回归：实体池耗尽后 AddEnemy 必须返回 -1，且不得用 -1 索引访问任何数组 ──
        // 与 CreateEntity 耗尽用例走同一生产路径（AddEnemy 内部先调 CreateEntity），
        // 因此删除原 AddEnemy_DoesNotCrashOnNegativeEntityId 冗余用例，只保留一个有真实断言的版本。
        [Fact]
        public void AddEnemy_FailsWhenPoolExhausted()
        {
            while (Store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1) != -1) { /* drain */ }
            int result = Store.AddEnemy(5, 19, 1f, 20, 20, 5, 10, 1);
            Assert.Equal(-1, result);
        }

        [Fact] public void PlayerHealth_ArrayAccess()
        {
            int id = Store.CreateEntity();
            Store.PlayerMaxHealth[id] = 200f;
            Store.PlayerCurrentHealth[id] = 150f;
            Assert.Equal(200f, Store.PlayerMaxHealth[id]);
            Assert.Equal(150f, Store.PlayerCurrentHealth[id]);
        }

        [Fact] public void PlayerGold_ArrayAccess()
        {
            int id = Store.CreateEntity();
            Store.PlayerGold[id] = 100;
            Assert.Equal(100, Store.GetPlayerGold(id));
        }

        [Fact] public void TotalKills_StartsAtZero()
        {
            Assert.Equal(0, Store.TotalKills);
        }

        [Fact] public void TotalKills_CanIncrement()
        {
            Store.TotalKills++;
            Store.TotalKills++;
            Assert.Equal(2, Store.TotalKills);
        }
    }
}

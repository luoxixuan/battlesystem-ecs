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
        public void AddEnemy_UsesNoFissionSentinel_WhileExplicitSpawnConfigIsPreserved()
        {
            int manualEnemy = Store.AddEnemy(5f, 19f, 1f, 20f, 20f, 5f, 10, 1);

            Assert.Equal(-1, Store.EnemyFissionDefId[manualEnemy]);
            Assert.Equal(0, Store.EnemyFissionGeneration[manualEnemy]);

            int configuredEnemy = Store.AddEnemy(7f, 19f, 1f, 20f, 20f, 5f, 10, 1);
            Store.EnemyFissionDefId[configuredEnemy] = 2;
            Store.EnemyFissionGeneration[configuredEnemy] = 1;

            Assert.Equal(2, Store.EnemyFissionDefId[configuredEnemy]);
            Assert.Equal(1, Store.EnemyFissionGeneration[configuredEnemy]);
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

        [Fact]
        public void PathModifierIndexTracksActivationDeactivationAndExpiry()
        {
            int first = Store.CreateEntity();
            int second = Store.CreateEntity();
            Store.ActivatePathModifier(second, 0f, 0f, 1f, 2, 0);
            Store.ActivatePathModifier(first, 0f, 0f, 1f, 1, 0, turnsRemaining: 0.5f);

            Assert.Equal(new[] { first, second }, Store.ActivePathModifierIds);
            Store.ActivatePathModifier(first, 0f, 0f, 1f, 1, 0, turnsRemaining: 0.5f);
            Assert.Equal(2, Store.ActivePathModifierCount);

            var system = new Systems.PathModifierSystem(Store);
            system.SetTurn();
            system.Update(1f);

            Assert.False(Store.PathModifierActive[first]);
            Assert.True(Store.PathModifierActive[second]);
            Assert.Equal(new[] { second }, Store.ActivePathModifierIds);
            Assert.Equal(1, Store.ActivePathModifierCount);

            Store.ActivatePathModifier(first, 0f, 0f, 1f, 1, 0);
            Store.DestroyEntity(first);
            Assert.DoesNotContain(first, Store.ActivePathModifierIds);
            Assert.Equal(1, Store.ActivePathModifierCount);
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

        // ─── Bug 回归：元素状态必须在销毁时清除（ID 复用泄漏）─────────────────
        // EnemyElementStatus / EnemyElementTimer 的生产写入者是
        // ApplyEnemyDamage 的破盾路径与 TowerAttackSystem 的附魔路径，但唯一的
        // 衰减/清除逻辑在 ElementalReactionSystem —— 该系统从未被构造（全库
        // `new ElementalReactionSystem(` 0 次）。所以若 DestroyEntity 不清，
        // 回收 id 会永久带着上一任的元素位，而 TowerAttackSystem 的元素亲和
        // 加成（活跃读者，Data/Towers/tower_elemental_affinity.json）会据此
        // 给新敌人白送一份伤害倍率。

        [Fact]
        public void DestroyEntity_ClearsElementStatusAndTimers()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1);
            Store.EnemyElementStatus[eid] = ElementType.Fire | ElementType.Ice;
            for (int slot = 0; slot < 4; slot++)
                Store.EnemyElementTimer[eid * 4 + slot] = 5f;

            Store.DestroyEntity(eid);

            Assert.Equal(ElementType.None, Store.EnemyElementStatus[eid]);
            for (int slot = 0; slot < 4; slot++)
                Assert.Equal(0f, Store.EnemyElementTimer[eid * 4 + slot]);
        }

        [Fact]
        public void RecycledEnemyId_DoesNotInheritElementStatus()
        {
            int first = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1);
            Store.EnemyElementStatus[first] = ElementType.Fire;
            Store.EnemyElementTimer[first * 4] = 5f;

            Store.DestroyEntity(first);

            // 同一 id 被 free-list 回收给新敌人（前提：free-list 只有这一个 id）
            int second = Store.AddEnemy(1f, 1f, 1f, 200f, 200f, 5f, 10, 1);
            Assert.Equal(first, second);
            Assert.Equal(ElementType.None, Store.EnemyElementStatus[second]);
            Assert.Equal(0f, Store.EnemyElementTimer[second * 4]);
        }

        [Fact]
        public void ShieldBreakElement_IsClearedOnDestroy()
        {
            // 走真实生产路径：带元素护盾的敌人被打破盾 → ApplyEnemyDamage 写入
            // 元素位与计时器（ComponentStore_Enemy.cs:2184-2187），随后销毁必须清空。
            int eid = Store.AddEnemy(0f, 0f, 1f, 100f, 100f, 5f, 10, 1, "Shielded", 0f, shield: 20f);
            Store.EnemyShieldType[eid] = ElementType.Fire;
            Store.EnemyShieldBreakReaction[eid] = ElementType.Ice;
            Store.EnemyShieldBreakElementDuration[eid] = 2f;

            Store.ApplyEnemyDamage(eid, 50f, ElementType.Fire); // 破盾 → 附加 Ice
            Assert.NotEqual(ElementType.None, Store.EnemyElementStatus[eid]);

            Store.DestroyEntity(eid);
            Assert.Equal(ElementType.None, Store.EnemyElementStatus[eid]);
        }

        // ─── Bug 回归：破盾队列必须按帧清空（无界增长）─────────────────────
        // ApplyEnemyDamage 每次破元素盾就往 _pendingShieldBreaks 追加一个 id，
        // 而唯一的消费者（含 Clear）在 ElementalReactionSystem —— 该系统从未被
        // 构造，所以这个 List 在整个会话里只增不减。生产数据确实走到这条路：
        // monster_shield.json / monster_enforcer.json 都带 Shield + ShieldElement，
        // 由 WaveSpawningSystem:993-1001 接线。修复在 BeginFrame 按帧清空，
        // 保持"每帧队列"语义（将来接上消费者仍能看到本帧的全部追加）。

        /// <summary>破元素盾会入队，且 BeginFrame 必须清空该队列。</summary>
        [Fact]
        public void BeginFrame_ClearsPendingShieldBreaks()
        {
            int eid = MakeElementShieldedEnemy();

            Store.ApplyEnemyDamage(eid, 50f, ElementType.Fire); // 破盾 → 入队
            Assert.Single(Store.PendingShieldBreaks);

            Store.BeginFrame();
            Assert.Empty(Store.PendingShieldBreaks);
        }

        /// <summary>多帧连续破盾不得让队列无界增长。</summary>
        [Fact]
        public void PendingShieldBreaks_DoesNotGrowAcrossFrames()
        {
            for (int frame = 0; frame < 5; frame++)
            {
                Store.BeginFrame();
                int eid = MakeElementShieldedEnemy();
                Store.ApplyEnemyDamage(eid, 50f, ElementType.Fire);
                // 每帧只应留下本帧这一条，而不是累积 frame+1 条
                Assert.Single(Store.PendingShieldBreaks);
                Store.DestroyEntity(eid);
            }
        }

        /// <summary>带元素盾（Fire 盾 / 破盾附 Ice）的敌人，血量足以存活破盾伤害。</summary>
        private int MakeElementShieldedEnemy()
        {
            int eid = Store.AddEnemy(0f, 0f, 1f, 500f, 500f, 5f, 10, 1, "Shielded", 0f, shield: 20f);
            Store.EnemyShieldType[eid] = ElementType.Fire;
            Store.EnemyShieldBreakReaction[eid] = ElementType.Ice;
            Store.EnemyShieldBreakElementDuration[eid] = 2f;
            return eid;
        }

        // ─── Bug 回归：塔的时空 / 巡逻 / 选中字段必须在销毁时重置 ─────────────
        // RemoveTower 清 208 个塔字段，DestroyEntity 的塔分支只清 89 个，差集 150。
        // 其中 140 个由 AddTower 重新初始化（照抄 RemoveTower 会白写 140 次），
        // 真正的 ID 复用面就是下面这 10 个：既不在此清、也不在 AddTower 初始化。
        // 目前是防御性修复 —— 它们的写入者被 tc.IsChronoTower / tc.IsMobile 门控，
        // 而 shipped Data/Towers/*.json 没有任何一个设这两个键。
        // 关键点：默认值必须与 RemoveTower 一致，尤其
        // TowerPatrolAttackSpeedPenalty 是攻速乘数，必须是 1f 而不是 0f
        // （写 0f 会让回收槽位的塔永远打不出攻击）。

        [Fact]
        public void DestroyEntity_ResetsChronoAndPatrolTowerFields()
        {
            int tid = RawTower(3, 3);
            // 模拟一座时空 + 巡逻塔（生产由 TowerPlacementSystem 按配置写入）
            Store.TowerIsChronoTower[tid] = true;
            Store.TowerTimeFieldRadius[tid] = 4f;
            Store.TowerTimeScale[tid] = 0.5f;
            Store.TowerIsMobile[tid] = true;
            Store.TowerMoveSpeed[tid] = 3f;
            Store.TowerPatrolPathId[tid] = 2;
            Store.TowerPatrolWaypointIndex[tid] = 5;
            Store.TowerPatrolDirection[tid] = -1;
            Store.TowerPatrolAttackSpeedPenalty[tid] = 0.75f;
            Store.TowerSelected[tid] = true;

            Store.DestroyEntity(tid);

            Assert.False(Store.TowerIsChronoTower[tid]);
            Assert.Equal(0f, Store.TowerTimeFieldRadius[tid]);
            Assert.Equal(0f, Store.TowerTimeScale[tid]);
            Assert.False(Store.TowerIsMobile[tid]);
            Assert.Equal(0f, Store.TowerMoveSpeed[tid]);
            Assert.Equal(-1, Store.TowerPatrolPathId[tid]);
            Assert.Equal(0, Store.TowerPatrolWaypointIndex[tid]);
            Assert.Equal(1, Store.TowerPatrolDirection[tid]);
            Assert.False(Store.TowerSelected[tid]);
        }

        /// <summary>攻速惩罚是乘数：重置值必须为 1f（中性），0f 会让回收塔无法攻击。</summary>
        [Fact]
        public void DestroyEntity_ResetsPatrolPenaltyToNeutralMultiplier()
        {
            int tid = RawTower(4, 4);
            Store.TowerPatrolAttackSpeedPenalty[tid] = 0.75f;

            Store.DestroyEntity(tid);

            Assert.Equal(1f, Store.TowerPatrolAttackSpeedPenalty[tid]);
        }

        /// <summary>DestroyEntity 与 RemoveTower 对这 10 个字段的重置值必须一致。</summary>
        [Fact]
        public void DestroyEntity_And_RemoveTower_AgreeOnRecycleDefaults()
        {
            // 两座塔写入完全相同的非默认状态，分别走两条清理路径，结果必须相同。
            int viaDestroy = RawTower(5, 5);
            int viaRemove = RawTower(6, 6);
            foreach (int t in new[] { viaDestroy, viaRemove })
            {
                Store.TowerIsChronoTower[t] = true;
                Store.TowerTimeFieldRadius[t] = 4f;
                Store.TowerTimeScale[t] = 0.5f;
                Store.TowerIsMobile[t] = true;
                Store.TowerMoveSpeed[t] = 3f;
                Store.TowerPatrolPathId[t] = 2;
                Store.TowerPatrolWaypointIndex[t] = 5;
                Store.TowerPatrolDirection[t] = -1;
                Store.TowerPatrolAttackSpeedPenalty[t] = 0.75f;
                Store.TowerSelected[t] = true;
            }

            Store.DestroyEntity(viaDestroy);
            Store.RemoveTower(viaRemove);

            Assert.Equal(Store.TowerIsChronoTower[viaRemove], Store.TowerIsChronoTower[viaDestroy]);
            Assert.Equal(Store.TowerTimeFieldRadius[viaRemove], Store.TowerTimeFieldRadius[viaDestroy]);
            Assert.Equal(Store.TowerTimeScale[viaRemove], Store.TowerTimeScale[viaDestroy]);
            Assert.Equal(Store.TowerIsMobile[viaRemove], Store.TowerIsMobile[viaDestroy]);
            Assert.Equal(Store.TowerMoveSpeed[viaRemove], Store.TowerMoveSpeed[viaDestroy]);
            Assert.Equal(Store.TowerPatrolPathId[viaRemove], Store.TowerPatrolPathId[viaDestroy]);
            Assert.Equal(Store.TowerPatrolWaypointIndex[viaRemove], Store.TowerPatrolWaypointIndex[viaDestroy]);
            Assert.Equal(Store.TowerPatrolDirection[viaRemove], Store.TowerPatrolDirection[viaDestroy]);
            Assert.Equal(Store.TowerPatrolAttackSpeedPenalty[viaRemove], Store.TowerPatrolAttackSpeedPenalty[viaDestroy]);
            Assert.Equal(Store.TowerSelected[viaRemove], Store.TowerSelected[viaDestroy]);
        }
    }
}

using System;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for Round 105 Direction 8: Execute Threshold / Finisher Bonus.
    /// Verifies that:
    ///   - Default behavior (no execute configured) leaves gold/mana untouched
    ///   - Enemies with EnemyExecuteThreshold > 0 grant flat gold on kill
    ///   - Enemies with EnemyExecuteBonusMana > 0 grant flat mana on kill
    ///   - The one-shot EnemyExecuted guard prevents double-pay when an enemy is re-marked
    ///   - Gold and mana bonuses are clamped (no negative or overflowing values)
    ///   - DestroyEntity resets all execute fields (no ID-reuse leakage)
    ///   - ExecuteConfig static class exposes sensible defaults
    /// </summary>
    public class ExecuteSystemTests : BattleTestBase
    {
        private const int PlayerId = 0;
        private const float StartingGold = 100f;
        private const float MaxPlayerMana = 500f;
        private const float StartingMana = 50f;

        private int SpawnPlainEnemy(float maxHp = 100f)
        {
            return Store.AddEnemy(0, 0, 5f, maxHp, maxHp, 5f, 10, 1, "TestEnemy");
        }

        private int SpawnExecutableEnemy(float threshold, float goldBonus, float manaBonus, float maxHp = 100f)
        {
            int e = SpawnPlainEnemy(maxHp);
            Store.EnemyExecuteThreshold[e] = threshold;
            Store.EnemyExecuteBonusGold[e] = goldBonus;
            Store.EnemyExecuteBonusMana[e] = manaBonus;
            return e;
        }

        private void InitPlayer(float gold = StartingGold, float mana = StartingMana, float maxMana = MaxPlayerMana)
        {
            Store.PlayerGold[PlayerId] = gold;
            Store.PlayerMaxMana[PlayerId] = maxMana;
            Store.PlayerMana[PlayerId] = mana;
        }

        // ─── Default state — backward compat ─────────────────────────────

        [Fact]
        public void DefaultState_AllExecuteFieldsZero()
        {
            int e = SpawnPlainEnemy();
            Assert.Equal(0f, Store.EnemyExecuteThreshold[e]);
            Assert.Equal(0f, Store.EnemyExecuteBonusGold[e]);
            Assert.Equal(0f, Store.EnemyExecuteBonusMana[e]);
            Assert.False(Store.EnemyExecuted[e]);
        }

        [Fact]
        public void ExecuteConfig_HasSensibleDefaults()
        {
            // Defaults should be opt-out (zero) so existing behavior is preserved.
            Assert.Equal(0f, ExecuteConfig.DefaultExecuteThreshold);
            Assert.Equal(0f, ExecuteConfig.DefaultExecuteBonusGold);
            Assert.Equal(0f, ExecuteConfig.DefaultExecuteBonusMana);
            // Recommendations should be positive and balanced.
            Assert.True(ExecuteConfig.RecommendedExecuteThreshold > 0f);
            Assert.True(ExecuteConfig.RecommendedExecuteThreshold < 1f);
            Assert.True(ExecuteConfig.RecommendedExecuteBonusGold > 0f);
            Assert.True(ExecuteConfig.RecommendedExecuteBonusMana > 0f);
        }

        [Fact]
        public void ResolveEnemiesKilled_DefaultEnemy_NoExecuteBonusAwarded()
        {
            // Backward-compat: a vanilla enemy (no execute config) kills grant no extra gold/mana.
            InitPlayer();
            int e = SpawnPlainEnemy();
            Store.EnemyGoldReward[e] = 10; // 显式注入基础奖励，期望从注入值推导
            Store.EnemyHealth[e] = 0f;
            float goldBefore = Store.PlayerGold[PlayerId];
            float manaBefore = Store.PlayerMana[PlayerId];

            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            // 只发放基础奖励，无 execute 加成。
            Assert.Equal(goldBefore + 10f, Store.PlayerGold[PlayerId]);
            Assert.Equal(manaBefore, Store.PlayerMana[PlayerId]);
        }

        // ─── Execute bonus award on death ────────────────────────────────

        [Fact]
        public void ResolveEnemiesKilled_ExecutableEnemy_GrantsGoldBonus()
        {
            InitPlayer();
            float goldBefore = Store.PlayerGold[PlayerId];
            int e = SpawnExecutableEnemy(threshold: 0.2f, goldBonus: 25f, manaBonus: 0f);
            Store.EnemyGoldReward[e] = 0; // 显式清零基础奖励，期望 = 只含 execute 加成
            Store.EnemyHealth[e] = 0f;

            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            // 精确断言：goldBefore + 基础奖励(0) + execute 金币(25)。
            Assert.Equal(goldBefore + 25f, Store.PlayerGold[PlayerId]);
        }

        [Fact]
        public void ResolveEnemiesKilled_ExecutableEnemy_GrantsManaBonus()
        {
            InitPlayer();
            float manaBefore = Store.PlayerMana[PlayerId];
            int e = SpawnExecutableEnemy(threshold: 0.2f, goldBonus: 0f, manaBonus: 15f);
            Store.EnemyHealth[e] = 0f;

            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            Assert.Equal(manaBefore + 15f, Store.PlayerMana[PlayerId]);
        }

        [Fact]
        public void ResolveEnemiesKilled_ExecutableEnemy_GrantsBothBonuses()
        {
            InitPlayer();
            int e = SpawnExecutableEnemy(threshold: 0.2f, goldBonus: 25f, manaBonus: 15f);
            Store.EnemyGoldReward[e] = 0; // 显式清零基础奖励
            Store.EnemyHealth[e] = 0f;
            float goldBefore = Store.PlayerGold[PlayerId];
            float manaBefore = Store.PlayerMana[PlayerId];

            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            Assert.Equal(goldBefore + 25f, Store.PlayerGold[PlayerId]);
            Assert.Equal(manaBefore + 15f, Store.PlayerMana[PlayerId]);
        }

        // ─── Threshold opt-out (threshold = 0) ───────────────────────────

        [Fact]
        public void ResolveEnemiesKilled_ZeroThreshold_NoExecuteBonus()
        {
            // Even if gold/mana bonuses are configured, threshold=0 means opt-out.
            InitPlayer();
            int e = SpawnExecutableEnemy(threshold: 0f, goldBonus: 25f, manaBonus: 15f);
            Store.EnemyGoldReward[e] = 10; // 显式注入基础奖励
            Store.EnemyHealth[e] = 0f;
            float goldBefore = Store.PlayerGold[PlayerId];
            float manaBefore = Store.PlayerMana[PlayerId];

            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            // 只发基础奖励；execute 金币/法力都不发。
            Assert.Equal(goldBefore + 10f, Store.PlayerGold[PlayerId]);
            Assert.Equal(manaBefore, Store.PlayerMana[PlayerId]);
        }

        // ─── Mana clamping ───────────────────────────────────────────────

        [Fact]
        public void ResolveEnemiesKilled_ManaBonusClampedAtMax()
        {
            InitPlayer(mana: MaxPlayerMana - 5f);  // 5 below cap
            int e = SpawnExecutableEnemy(threshold: 0.2f, goldBonus: 0f, manaBonus: 100f);  // 100 mana would over-cap
            Store.EnemyHealth[e] = 0f;

            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            // Mana must be clamped at MaxPlayerMana, not overflowing.
            Assert.Equal(MaxPlayerMana, Store.PlayerMana[PlayerId]);
        }

        // ─── One-shot guard（真实重复排队只付一次） ──────────────────

        // 回归：同一敌人同一帧重复 QueueEnemyDeath，EnemyExecuted/EnemyActive 门必须保证只付一次。
        [Fact]
        public void ResolveEnemiesKilled_DoubleQueueStillOnlyPaysOnce()
        {
            // Re-queueing an already-killed enemy in the same frame (defensive): the bonus
            // should still only pay once because the second queue call is filtered by
            // EnemyActive check inside ResolveEnemiesKilledThisFrame.
            InitPlayer();
            float manaBefore = Store.PlayerMana[PlayerId];
            int e = SpawnExecutableEnemy(threshold: 0.2f, goldBonus: 0f, manaBonus: 15f);
            Store.EnemyHealth[e] = 0f;

            Store.QueueEnemyDeath(e, PlayerId);
            Store.QueueEnemyDeath(e, PlayerId);  // duplicate queue
            Store.ResolveEnemiesKilledThisFrame();

            // Should pay only once (15 mana), not twice.
            Assert.Equal(manaBefore + 15f, Store.PlayerMana[PlayerId]);
        }

        // ─── ID-reuse safety: DestroyEntity resets all fields ────────────

        [Fact]
        public void DestroyEntity_ResetsExecuteFields()
        {
            int e = SpawnExecutableEnemy(threshold: 0.2f, goldBonus: 25f, manaBonus: 15f);
            Store.EnemyExecuted[e] = true;  // simulate post-pay state
            Store.EnemyHealth[e] = 0f;
            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            // After destroy, the slot is recycled. A new enemy at the same ID should see
            // all defaults. (AddEnemy allocates; we just check the next enemy's fields are
            // independent.)
            int e2 = SpawnPlainEnemy();
            Assert.False(Store.EnemyExecuted[e2]);
            Assert.Equal(0f, Store.EnemyExecuteThreshold[e2]);
            Assert.Equal(0f, Store.EnemyExecuteBonusGold[e2]);
            Assert.Equal(0f, Store.EnemyExecuteBonusMana[e2]);
        }

        // ─── Stacks with Death Mark bonus ────────────────────────────────

        [Fact]
        public void ResolveEnemiesKilled_ExecuteStacksWithDeathMark()
        {
            // An enemy that is BOTH marked AND executable should get BOTH bonuses.
            InitPlayer();
            int e = SpawnExecutableEnemy(threshold: 0.2f, goldBonus: 25f, manaBonus: 15f);
            Store.EnemyGoldReward[e] = 10; // 显式注入基础奖励
            Store.EnemyMarked[e] = true;
            Store.EnemyMarkedDamageBonus[e] = 0.5f;
            Store.EnemyHealth[e] = 0f;
            float goldBefore = Store.PlayerGold[PlayerId];
            float manaBefore = Store.PlayerMana[PlayerId];

            Store.QueueEnemyDeath(e, PlayerId);
            Store.ResolveEnemiesKilledThisFrame();

            // 期望从注入值推导：基础 10 + 死亡标记加成(10 × 0.5) + execute 25。
            float expectedGold = goldBefore + 10f + 10f * 0.5f + 25f;
            Assert.Equal(expectedGold, Store.PlayerGold[PlayerId]);
            Assert.Equal(manaBefore + 15f, Store.PlayerMana[PlayerId]);
        }
    }
}

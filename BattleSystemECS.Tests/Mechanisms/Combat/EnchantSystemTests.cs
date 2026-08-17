using BattleSystemECS.Tests.Infrastructure;
using System;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Mechanisms.Combat
{
    /// <summary>
    /// Tests for the Tower Enchantment system (Round 116 Direction 3) — the "imbue" mechanic
    /// that lets a spell or upgrade change which element a tower applies on hit.
    /// 状态读写测试直接测 ComponentStore 的真实 set/get/过期路径；
    /// 伤害倍率 / 元素状态 / 计时器刷新则通过真实 TowerAttackSystem 攻击链路验证，
    /// 不再在测试内复刻 `finalDmg *= 1f + bonus` 等生产公式。
    /// </summary>
    public class EnchantSystemTests : BattleTestBase
    {
        private void CreateEnv()
        {
            Store.CreateEntity();
        }

        // ── Defaults / guards ──

        [Fact]
        public void AddTower_DefaultEnchantment_IsInactive()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Assert.Equal(0, Store.TowerEnchantedElement[tid]);
            Assert.Equal(0f, Store.TowerEnchantBonus[tid]);
            Assert.Equal(0f, Store.TowerEnchantDuration[tid]);
            Assert.Equal(-1, Store.TowerEnchantExpiresAtTurn[tid]);
            Assert.Equal(0, Store.GetTowerEnchantedElement(tid));
            Assert.Equal(0f, Store.GetTowerEnchantBonus(tid));
            Assert.Equal(0f, Store.GetTowerEnchantDuration(tid));
        }

        [Fact]
        public void GetTowerEnchantedElement_InvalidId_ReturnsZero()
        {
            CreateEnv();
            Assert.Equal(0, Store.GetTowerEnchantedElement(-1));
            Assert.Equal(0, Store.GetTowerEnchantedElement(ComponentStore.MAX_ENTITIES));
        }

        // ── Set / Get / Clear ──

        [Fact]
        public void SetTowerEnchantment_Fire_StoresValues()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Store.SetTowerEnchantment(tid, 1 /*Fire*/, 0.30f, 3.0f, -1 /*permanent*/);
            Assert.Equal(1, Store.GetTowerEnchantedElement(tid));
            Assert.Equal(0.30f, Store.GetTowerEnchantBonus(tid));
            Assert.Equal(3.0f, Store.GetTowerEnchantDuration(tid));
            Assert.Equal(-1, Store.TowerEnchantExpiresAtTurn[tid]);
        }

        [Theory]
        [InlineData(1)] // Fire
        [InlineData(2)] // Ice
        [InlineData(3)] // Lightning
        [InlineData(4)] // Poison
        public void SetTowerEnchantment_AllElements_Readable(int element)
        {
            // 四种元素只是入参不同，读回路径同构，合并为理论驱动。
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 1f, 1, 1f, 1, 1f);
            Store.SetTowerEnchantment(tid, element, 0f, 1f, -1);
            Assert.Equal(element, Store.GetTowerEnchantedElement(tid));
        }

        [Fact]
        public void ClearTowerEnchantment_ResetsAllFields()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 5f, 3, 1f, 1, 50f);
            Store.SetTowerEnchantment(tid, 2, 0.5f, 4.0f, 100);
            Store.ClearTowerEnchantment(tid);
            Assert.Equal(0, Store.TowerEnchantedElement[tid]);
            Assert.Equal(0f, Store.TowerEnchantBonus[tid]);
            Assert.Equal(0f, Store.TowerEnchantDuration[tid]);
            Assert.Equal(-1, Store.TowerEnchantExpiresAtTurn[tid]);
            Assert.Equal(0, Store.GetTowerEnchantedElement(tid));
        }

        // ── Defensive clamping ──

        [Fact]
        public void SetTowerEnchantment_ClampsOutOfRangeElement()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 1f, 1, 1f, 1, 1f);
            // 99 is invalid → clamped to 4 (Poison)
            Store.SetTowerEnchantment(tid, 99, 0f, 1f, -1);
            Assert.Equal(4, Store.TowerEnchantedElement[tid]);
            // -5 → clamped to 0 (no element)
            Store.SetTowerEnchantment(tid, -5, 0f, 1f, -1);
            Assert.Equal(0, Store.TowerEnchantedElement[tid]);
        }

        [Fact]
        public void SetTowerEnchantment_ClampsBonusAndDuration()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 1f, 1, 1f, 1, 1f);
            // Negative bonus → 0
            Store.SetTowerEnchantment(tid, 1, -1.0f, 1.0f, -1);
            Assert.Equal(0f, Store.TowerEnchantBonus[tid]);
            // Huge bonus → 10
            Store.SetTowerEnchantment(tid, 1, 999f, 1.0f, -1);
            Assert.Equal(10f, Store.TowerEnchantBonus[tid]);
            // Negative duration → 0
            Store.SetTowerEnchantment(tid, 1, 0.1f, -5f, -1);
            Assert.Equal(0f, Store.TowerEnchantDuration[tid]);
            // Huge duration → 60 cap
            Store.SetTowerEnchantment(tid, 1, 0.1f, 9999f, -1);
            Assert.Equal(60f, Store.TowerEnchantDuration[tid]);
        }

        [Fact]
        public void SetTowerEnchantment_ClampsExpiresAtTurn()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 1f, 1, 1f, 1, 1f);
            // expiresAtTurn < -1 → -1 (permanent)
            Store.SetTowerEnchantment(tid, 1, 0f, 1f, -999);
            Assert.Equal(-1, Store.TowerEnchantExpiresAtTurn[tid]);
        }

        // ── Auto-expiry ──

        [Fact]
        public void GetTowerEnchantedElement_AfterExpiry_ReturnsZero()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 1f, 1, 1f, 1, 1f);
            // Expire at turn 5
            Store.SetTowerEnchantment(tid, 1, 0.5f, 2.0f, 5);
            // Before expiry (CurrentFrame is 0 initially)
            Assert.Equal(1, Store.GetTowerEnchantedElement(tid));
            Assert.Equal(0.5f, Store.GetTowerEnchantBonus(tid));
            Assert.Equal(2.0f, Store.GetTowerEnchantDuration(tid));
            // Advance past expiry (BeginFrame() bumps CurrentFrame)
            for (int i = 0; i < 10; i++) Store.BeginFrame();
            // Now expired
            Assert.Equal(0, Store.GetTowerEnchantedElement(tid));
            Assert.Equal(0f, Store.GetTowerEnchantBonus(tid));
            Assert.Equal(0f, Store.GetTowerEnchantDuration(tid));
        }

        [Fact]
        public void GetTowerEnchantedElement_Permanent_NeverExpires()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 1f, 1, 1f, 1, 1f);
            Store.SetTowerEnchantment(tid, 3 /*Lightning*/, 0.4f, 1.0f, -1 /*permanent*/);
            for (int i = 0; i < 50; i++) Store.BeginFrame();
            Assert.Equal(3, Store.GetTowerEnchantedElement(tid));
            Assert.Equal(0.4f, Store.GetTowerEnchantBonus(tid));
        }

        // ── Recycle path resets ──

        [Fact]
        public void DestroyEntity_ResetsEnchantmentFields()
        {
            CreateEnv();
            int tid = Store.CreateEntity();
            Store.AddTower(tid, TowerType.Basic, 1f, 1, 1f, 1, 1f);
            Store.SetTowerEnchantment(tid, 1, 0.5f, 2f, 50);
            Store.DestroyEntity(tid);
            Assert.Equal(0, Store.TowerEnchantedElement[tid]);
            Assert.Equal(0f, Store.TowerEnchantBonus[tid]);
            Assert.Equal(0f, Store.TowerEnchantDuration[tid]);
            Assert.Equal(-1, Store.TowerEnchantExpiresAtTurn[tid]);
        }

        // ── 真实攻击路径：附魔伤害倍率 / 元素状态 / 计时器 ──────────────

        /// <summary>
        /// 搭一座附魔塔 + 一个正下方敌人，走真实 TowerAttackSystem 攻击链路。
        /// AddTower 已自动注册 ActiveTowerIds；再 AddActiveTowerId 会造成重复开火。
        /// </summary>
        private (int towerId, int enemyId) MakeEnchantedAttackEnv(
            int element, float bonus, float duration, int expiresAtTurn, float towerDamage = 50f)
        {
            int towerId = RawTower(0, 0, TowerType.Basic, towerDamage, range: 10, speed: 10f, level: 1, cost: 50f);
            Store.SetTowerEnchantment(towerId, element, bonus, duration, expiresAtTurn);

            int enemyId = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 1f;
                e.MoveSpeed = 1f;
                e.Health = 1000f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 1;
                e.Name = "EnchantTarget";
            });
            RebuildGrid();

            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetTurn(0);
            attack.Update(1f);
            return (towerId, enemyId);
        }

        [Fact]
        public void EnchantedAttack_AppliesBonusDamageAndElementStatus()
        {
            // Fire 附魔 +25%：50 × 1.25 = 62.5，同时 OR 上 Fire 状态并把计时器刷新为 3s。
            var (_, eid) = MakeEnchantedAttackEnv(
                element: 1 /*Fire*/, bonus: 0.25f, duration: 3f, expiresAtTurn: -1);

            Assert.Equal(1000f - 62.5f, Store.EnemyHealth[eid], 3);
            Assert.Equal(ElementType.Fire, Store.EnemyElementStatus[eid] & ElementType.Fire);
            Assert.Equal(3f, Store.EnemyElementTimer[eid * 4 + 0], 3);
        }

        [Fact]
        public void EnchantedAttack_ZeroBonus_AppliesElementWithoutDamageBoost()
        {
            // bonus=0 时只打基础伤害 50，元素状态仍要施加（附魔的身份价值）。
            var (_, eid) = MakeEnchantedAttackEnv(
                element: 2 /*Ice*/, bonus: 0f, duration: 2f, expiresAtTurn: -1);

            Assert.Equal(1000f - 50f, Store.EnemyHealth[eid], 3);
            Assert.Equal(ElementType.Ice, Store.EnemyElementStatus[eid] & ElementType.Ice);
            Assert.Equal(2f, Store.EnemyElementTimer[eid * 4 + 1], 3);
        }

        [Fact]
        public void EnchantedAttack_DoesNotShortenLongerExistingTimer()
        {
            // 真实攻击两次：敌人已有 10s Fire 计时器，2s 的 Fire 附魔不得把它缩短（max 语义）。
            int towerId = RawTower(0, 0, TowerType.Basic, 50f, 10, 10f, 1, 50f);
            Store.SetTowerEnchantment(towerId, 1 /*Fire*/, 0f, 2f, -1);

            int eid = Enemy(e =>
            {
                e.X = 0f;
                e.Y = 1f;
                e.MoveSpeed = 1f;
                e.Health = 1000f;
                e.Damage = 0f;
                e.GoldReward = 1;
                e.WaveNumber = 1;
                e.Name = "TimerTarget";
            });
            Store.EnemyElementStatus[eid] = ElementType.Fire;
            Store.EnemyElementTimer[eid * 4 + 0] = 10f; // 已有更长计时器
            RebuildGrid();

            var attack = new TowerAttackSystem(Store, Renderer);
            attack.SetTurn(0);
            attack.Update(1f); // 第一击
            attack.Update(1f); // 第二击（LastAttackTime 每次开火后归零，1s 足够再开火）

            Assert.Equal(10f, Store.EnemyElementTimer[eid * 4 + 0], 3);
            Assert.Equal(ElementType.Fire, Store.EnemyElementStatus[eid] & ElementType.Fire);
        }
    }
}

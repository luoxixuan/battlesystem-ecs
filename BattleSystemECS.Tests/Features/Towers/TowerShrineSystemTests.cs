using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 173 Direction 1: Shrine Tower (pure-buff aura, no attack).
    /// Verifies:
    ///   1. SOA fields default to 0 / false (zero-overhead on hot path)
    ///   2. AddTower of a Shrine populates the fields correctly
    ///   3. DestroyEntity resets all Shrine fields (ID-reuse safety)
    ///   4. BeginFrame resets the per-frame cache arrays to 0
    ///   5. SetTurn/Resolve with no shrine on field stay in the O(1) sentinel path
    ///   6. Each aura type writes exactly its own cache (Gold/Mana/Damage/AttackSpeed)
    ///   7. Shrine does NOT buff itself
    ///   8. Target outside radius is not buffed
    ///   9. Two shrines in range stack additively
    ///  10. Dispelled / inactive targets are skipped
    ///  11. Unknown aura type leaves caches at default 0
    /// </summary>
    public class TowerShrineSystemTests : BattleTestBase
    {
        private void Env()
        {
            Player(p =>
            {
                p.X = 5f;
                p.Y = 0f;
                p.Gold = 9999f;
            });
            _ = Placement; // 构造 Placement（LoadPerTypeCaps 会写 JSON cap），随后显式清空 cap
            DisableTowerCaps();
        }

        /// <summary>布置一个显式写入 auraType/radius/potency 的 Shrine（测试注入值，不依赖放置默认值）。</summary>
        private int PlaceShrine(int x, int y, int auraType, float radius, float potency)
        {
            int id = Placement.PlaceTower(x, y, TowerType.Shrine, 0f, 0, 0f, 50f);
            Store.TowerShrineAuraType[id] = auraType;
            Store.TowerShrineRadius[id] = radius;
            Store.TowerShrinePotency[id] = potency;
            return id;
        }

        /// <summary>复用基类 Placement，不再散落 new TowerPlacementSystem。</summary>
        private int PlaceAttackTower(int x, int y, TowerType type)
        {
            return Placement.PlaceTower(x, y, type, 10f, 3, 1f, 25f);
        }

        /// <summary>SetTurn + Resolve 的共享骨架，返回可查询缓存结果的系统实例。</summary>
        private TowerShrineSystem ResolveShrineBuffs()
        {
            var shrineSys = new TowerShrineSystem(Store);
            shrineSys.SetTurn();
            shrineSys.ResolveShrineBuffs();
            return shrineSys;
        }

        // ─── Field defaults ────────────────────────────────────────────────

        [Fact]
        public void ComponentStore_ShrineFields_DefaultToZero_OnAddTower()
        {
            // All config fields default to 0 / false → no aura (zero-overhead on hot path).
            Env();
            int id = PlaceAttackTower(0, 0, TowerType.Basic);
            Assert.False(Store.TowerIsShrine[id]);
            Assert.Equal(0, Store.TowerShrineAuraType[id]);
            Assert.Equal(0f, Store.TowerShrineRadius[id], 3);
            Assert.Equal(0f, Store.TowerShrinePotency[id], 3);
            // Per-frame caches also default to 0
            Assert.Equal(0f, Store.TowerShrineCachedGoldBonus[id], 3);
            Assert.Equal(0f, Store.TowerShrineCachedManaRegen[id], 3);
            Assert.Equal(0f, Store.TowerShrineCachedDmgBonus[id], 3);
            Assert.Equal(0f, Store.TowerShrineCachedAtkSpdBonus[id], 3);
        }

        // ─── PlaceTower post-init ──────────────────────────────────────────

        [Fact]
        public void PlaceTower_Shrine_SetsNonInertShrineFields()
        {
            Env();
            int id = Placement.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            Assert.True(Store.TowerIsShrine[id]);
            // 放置路径必须给出“真正在广播”的非惰性配置；具体数值不属于测试契约。
            Assert.True(Store.TowerShrineAuraType[id] > 0);
            Assert.True(Store.TowerShrineRadius[id] > 0f);
            Assert.True(Store.TowerShrinePotency[id] > 0f);
        }

        // ─── DestroyEntity reset ───────────────────────────────────────────

        [Fact]
        public void ComponentStore_ShrineFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a shrine and placing a fresh
            // tower in the recycled slot, the new tower must NOT inherit shrine state.
            Env();
            int shrineId = Placement.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            Assert.True(Store.TowerIsShrine[shrineId]);
            Store.DestroyEntity(shrineId);
            int newId = Placement.PlaceTower(6, 6, TowerType.Basic, 10f, 3, 1f, 25f);
            // The new tower should be a regular Basic tower, NOT inherit shrine state
            Assert.False(Store.TowerIsShrine[newId]);
            Assert.Equal(0, Store.TowerShrineAuraType[newId]);
            Assert.Equal(0f, Store.TowerShrineRadius[newId], 3);
            Assert.Equal(0f, Store.TowerShrinePotency[newId], 3);
            Assert.Equal(0f, Store.TowerShrineCachedGoldBonus[newId], 3);
        }

        // ─── BeginFrame per-frame reset ────────────────────────────────────

        [Fact]
        public void BeginFrame_ResetsShrinePerFrameCaches()
        {
            // BeginFrame() must wipe the per-frame cache arrays so the next frame's
            // ResolveShrineBuffs() starts from a clean slate (no accumulation drift).
            Env();
            int shrineId = Placement.PlaceTower(5, 5, TowerType.Shrine, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            float configuredPotency = Store.TowerShrinePotency[shrineId];
            // Simulate one frame's worth of shrine resolution
            Store.TowerShrineCachedGoldBonus[targetId] = 0.99f;
            Store.TowerShrineCachedDmgBonus[targetId] = 0.42f;
            Store.BeginFrame();
            // Caches should be wiped
            Assert.Equal(0f, Store.TowerShrineCachedGoldBonus[targetId], 3);
            Assert.Equal(0f, Store.TowerShrineCachedDmgBonus[targetId], 3);
            // The shrine's config fields are NOT touched by BeginFrame (those are persistent)
            Assert.True(Store.TowerIsShrine[shrineId]);
            Assert.Equal(configuredPotency, Store.TowerShrinePotency[shrineId]);
        }

        // ─── No-shrine fast paths ──────────────────────────────────────────

        [Fact]
        public void SetTurn_NoShrineOnField_ReportsNone()
        {
            Env();
            PlaceAttackTower(0, 0, TowerType.Basic);
            PlaceAttackTower(5, 5, TowerType.Sniper);
            var shrineSys = new TowerShrineSystem(Store);
            shrineSys.SetTurn();
            Assert.False(shrineSys.AnyShrineOnField);
        }

        [Fact]
        public void ResolveShrineBuffs_NoShrineOnField_LeavesCachesZero()
        {
            Env();
            int targetId = PlaceAttackTower(0, 0, TowerType.Basic);
            var shrineSys = ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
        }

        // ─── Aura-type matrix ──────────────────────────────────────────────

        [Theory(DisplayName = "Shrine 只把显式注入的 potency 写入其自身 aura 对应的缓存")]
        // auraType: 0=None / 1=Gold / 2=Mana / 3=Damage / 4=AttackSpeed
        // 参数：auraType, potency, expectedGold, expectedMana, expectedDmg, expectedAtkSpd
        [InlineData(0, 0.10f, 0f, 0f, 0f, 0f)]
        [InlineData(1, 0.10f, 0.10f, 0f, 0f, 0f)]
        [InlineData(1, 0f, 0f, 0f, 0f, 0f)]
        [InlineData(2, 0.50f, 0f, 0.50f, 0f, 0f)]
        [InlineData(3, 0.15f, 0f, 0f, 0.15f, 0f)]
        [InlineData(4, 0.20f, 0f, 0f, 0f, 0.20f)]
        public void ShrineAura_WritesOnlyConfiguredCache(
            int auraType, float potency,
            float expectedGold, float expectedMana, float expectedDmg, float expectedAtkSpd)
        {
            const float radius = 3f;
            Env();
            PlaceShrine(5, 5, auraType, radius, potency);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            var shrineSys = ResolveShrineBuffs();
            Assert.Equal(expectedGold, shrineSys.GetCachedGoldBonus(targetId), 3);
            Assert.Equal(expectedMana, shrineSys.GetCachedManaRegen(targetId), 3);
            Assert.Equal(expectedDmg, shrineSys.GetCachedDamageBonus(targetId), 3);
            Assert.Equal(expectedAtkSpd, shrineSys.GetCachedAttackSpeedBonus(targetId), 3);
        }

        // ─── Self-buff skip ────────────────────────────────────────────────

        [Fact]
        public void Shrine_DoesNotBuffItself()
        {
            const float radius = 3f;
            const float potency = 0.15f;
            Env();
            int shrineId = PlaceShrine(5, 5, 1, radius, potency);
            var shrineSys = ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(shrineId));
        }

        // ─── Range gate ────────────────────────────────────────────────────

        [Fact]
        public void Target_OutsideRadius_NotBuffed()
        {
            const float radius = 3f;
            const float potency = 0.15f;
            Env();
            PlaceShrine(5, 5, 1, radius, potency);
            // Place a tower 10 cells away (beyond the injected radius 3)
            int farTargetId = PlaceAttackTower(15, 15, TowerType.Basic);
            var shrineSys = ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(farTargetId));
        }

        // ─── Stacking ──────────────────────────────────────────────────────

        [Fact]
        public void TwoShrinesInRange_StackAdditively()
        {
            const float radius = 3f;
            const float potency = 0.15f;
            Env();
            PlaceShrine(5, 5, 1, radius, potency);
            PlaceShrine(5, 6, 1, radius, potency);
            int targetId = PlaceAttackTower(5, 7, TowerType.Basic);
            var shrineSys = ResolveShrineBuffs();
            // 两个 shrine 的注入 potency 相加。
            Assert.Equal(potency * 2f, shrineSys.GetCachedGoldBonus(targetId), 3);
        }

        // ─── Skipped targets ───────────────────────────────────────────────

        [Theory(DisplayName = "被驱散或非活跃的塔不接收 Shrine buff")]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void DispelledOrInactiveTower_ReceivesNoShrineBuff(bool dispelled, bool inactive)
        {
            const float radius = 3f;
            const float potency = 0.15f;
            Env();
            PlaceShrine(5, 5, 1, radius, potency);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            Store.TowerIsDispelled[targetId] = dispelled;
            Store.TowerActive[targetId] = !inactive;
            var shrineSys = ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
        }

        // ─── Dispel only affects dispelled towers ──────────────────────────

        [Fact]
        public void DispelFlag_OnlySkipsDispelledTowers()
        {
            const float radius = 3f;
            const float potency = 0.15f;
            Env();
            PlaceShrine(5, 5, 1, radius, potency);
            int t1 = PlaceAttackTower(5, 6, TowerType.Basic);
            int t2 = PlaceAttackTower(5, 7, TowerType.Basic);
            Store.TowerIsDispelled[t1] = true;
            var shrineSys = ResolveShrineBuffs();
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(t1));      // dispelled
            Assert.Equal(potency, shrineSys.GetCachedGoldBonus(t2), 3); // not dispelled
        }

        // ─── Defensive: unknown aura type ──────────────────────────────────

        [Fact]
        public void UnknownAuraType_LeavesCachesAtDefault()
        {
            const float radius = 3f;
            const float potency = 0.25f;
            Env();
            int shrineId = PlaceShrine(5, 5, 99, radius, potency);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            var shrineSys = ResolveShrineBuffs();
            // 未知 aura 走防御性默认分支：所有缓存保持 0。
            Assert.Equal(0f, shrineSys.GetCachedGoldBonus(targetId));
            Assert.Equal(0f, shrineSys.GetCachedManaRegen(targetId));
            Assert.Equal(0f, shrineSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, shrineSys.GetCachedAttackSpeedBonus(targetId));
        }
    }
}

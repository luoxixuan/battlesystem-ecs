using BattleSystemECS.Tests.Infrastructure;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests.Features.Towers
{
    /// <summary>
    /// Tests for Round 177 Direction 2: Beacon Tower (active command-post broadcast buff, no attack).
    /// Verifies:
    ///   1. SOA fields default to 0 / false (zero-overhead on hot path)
    ///   2. AddTower of a Beacon populates the fields correctly
    ///   3. DestroyEntity resets all Beacon fields (ID-reuse safety)
    ///   4. BeginFrame resets the per-frame cache arrays to 0
    ///   5. SetTurn/Resolve with no beacon on field stay in the O(1) sentinel path
    ///   6. Single Beacon broadcasts its configured damage + attack-speed bonus
    ///   7. Beacon does NOT buff itself
    ///   8. Target outside radius is not buffed
    ///   9. Two beacons in range stack additively, and beacons peer-buff each other
    ///  10. Dispelled / inactive targets are skipped
    ///  11. Radius=0 or both bonuses=0 beacon is inert; partial bonus only writes its own cache
    ///  12. Read helpers bounds-check negative / out-of-range tower ids
    /// </summary>
    public class TowerBeaconSystemTests : BattleTestBase
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

        /// <summary>布置一个显式写入 radius/dmg/atkSpd 的 Beacon（测试注入值，不依赖放置默认值）。</summary>
        private int PlaceBeacon(int x, int y, float radius, float dmgBonus, float atkSpdBonus)
        {
            int id = Placement.PlaceTower(x, y, TowerType.Beacon, 0f, 0, 0f, 50f);
            Store.TowerBeaconRadius[id] = radius;
            Store.TowerBeaconDmgBonus[id] = dmgBonus;
            Store.TowerBeaconAtkSpdBonus[id] = atkSpdBonus;
            return id;
        }

        /// <summary>复用基类 Placement，不再散落 new TowerPlacementSystem。</summary>
        private int PlaceAttackTower(int x, int y, TowerType type)
        {
            return Placement.PlaceTower(x, y, type, 10f, 3, 1f, 25f);
        }

        /// <summary>SetTurn + Resolve 的共享骨架，返回可查询缓存结果的系统实例。</summary>
        private TowerBeaconSystem ResolveBeaconBuffs()
        {
            var beaconSys = new TowerBeaconSystem(Store);
            beaconSys.SetTurn();
            beaconSys.ResolveBeaconBuffs();
            return beaconSys;
        }

        // ─── Field defaults ────────────────────────────────────────────────

        [Fact]
        public void ComponentStore_BeaconFields_DefaultToZero_OnAddTower()
        {
            // All four config fields default to 0 / false → no broadcast (zero-overhead on hot path).
            Env();
            int id = PlaceAttackTower(0, 0, TowerType.Basic);
            Assert.False(Store.TowerIsBeacon[id]);
            Assert.Equal(0f, Store.TowerBeaconRadius[id], 3);
            Assert.Equal(0f, Store.TowerBeaconDmgBonus[id], 3);
            Assert.Equal(0f, Store.TowerBeaconAtkSpdBonus[id], 3);
            // Per-frame caches also default to 0
            Assert.Equal(0f, Store.TowerBeaconCachedDmgBonus[id], 3);
            Assert.Equal(0f, Store.TowerBeaconCachedAtkSpdBonus[id], 3);
        }

        // ─── PlaceTower post-init ──────────────────────────────────────────

        [Fact]
        public void PlaceTower_Beacon_SetsNonInertBeaconFields()
        {
            Env();
            int id = Placement.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            Assert.True(Store.TowerIsBeacon[id]);
            // 放置路径必须给出“真正在广播”的非惰性配置；具体数值不属于测试契约。
            Assert.True(Store.TowerBeaconRadius[id] > 0f);
            Assert.True(Store.TowerBeaconDmgBonus[id] > 0f);
            Assert.True(Store.TowerBeaconAtkSpdBonus[id] > 0f);
        }

        // ─── DestroyEntity reset ───────────────────────────────────────────

        [Fact]
        public void ComponentStore_BeaconFields_Reset_OnDestroyEntity()
        {
            // CRITICAL: ID-reuse safety. After destroying a beacon and placing a fresh
            // tower in the recycled slot, the new tower must NOT inherit beacon state.
            Env();
            int beaconId = Placement.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            Assert.True(Store.TowerIsBeacon[beaconId]);
            Store.DestroyEntity(beaconId);
            int newId = Placement.PlaceTower(6, 6, TowerType.Basic, 10f, 3, 1f, 25f);
            // The new tower should be a regular Basic tower, NOT inherit beacon state
            Assert.False(Store.TowerIsBeacon[newId]);
            Assert.Equal(0f, Store.TowerBeaconRadius[newId], 3);
            Assert.Equal(0f, Store.TowerBeaconDmgBonus[newId], 3);
            Assert.Equal(0f, Store.TowerBeaconAtkSpdBonus[newId], 3);
            Assert.Equal(0f, Store.TowerBeaconCachedDmgBonus[newId], 3);
            Assert.Equal(0f, Store.TowerBeaconCachedAtkSpdBonus[newId], 3);
        }

        // ─── BeginFrame per-frame reset ────────────────────────────────────

        [Fact]
        public void BeginFrame_ResetsBeaconPerFrameCaches()
        {
            // BeginFrame() must wipe the per-frame cache arrays so the next frame's
            // ResolveBeaconBuffs() starts from a clean slate (no accumulation drift).
            Env();
            int beaconId = Placement.PlaceTower(5, 5, TowerType.Beacon, 0f, 0, 0f, 50f);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            float configuredDmg = Store.TowerBeaconDmgBonus[beaconId];
            // Simulate one frame's worth of beacon resolution
            Store.TowerBeaconCachedDmgBonus[targetId] = 0.99f;
            Store.TowerBeaconCachedAtkSpdBonus[targetId] = 0.42f;
            Store.BeginFrame();
            // Caches should be wiped
            Assert.Equal(0f, Store.TowerBeaconCachedDmgBonus[targetId], 3);
            Assert.Equal(0f, Store.TowerBeaconCachedAtkSpdBonus[targetId], 3);
            // The beacon's config fields are NOT touched by BeginFrame (those are persistent)
            Assert.True(Store.TowerIsBeacon[beaconId]);
            Assert.Equal(configuredDmg, Store.TowerBeaconDmgBonus[beaconId]);
        }

        // ─── No-beacon fast paths ──────────────────────────────────────────

        [Fact]
        public void SetTurn_NoBeaconOnField_ReportsNone()
        {
            Env();
            PlaceAttackTower(0, 0, TowerType.Basic);
            PlaceAttackTower(5, 5, TowerType.Sniper);
            var beaconSys = new TowerBeaconSystem(Store);
            beaconSys.SetTurn();
            Assert.False(beaconSys.AnyBeaconOnField);
        }

        [Fact]
        public void ResolveBeaconBuffs_NoBeaconOnField_LeavesCachesZero()
        {
            Env();
            int targetId = PlaceAttackTower(0, 0, TowerType.Basic);
            var beaconSys = ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Damage / AtkSpd aura broadcast ────────────────────────────────

        [Theory(DisplayName = "单个 Beacon 将显式注入的伤害/攻速加成广播到射程内塔")]
        [InlineData(0.15f, 0.20f)]
        [InlineData(0.30f, 0.05f)]
        public void SingleBeacon_BroadcastsConfiguredBonuses(float dmgBonus, float atkSpdBonus)
        {
            const float radius = 3f;
            Env();
            PlaceBeacon(5, 5, radius, dmgBonus, atkSpdBonus);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            var beaconSys = ResolveBeaconBuffs();
            Assert.Equal(dmgBonus, beaconSys.GetCachedDamageBonus(targetId), 3);
            Assert.Equal(atkSpdBonus, beaconSys.GetCachedAttackSpeedBonus(targetId), 3);
        }

        // ─── Self-buff skip ────────────────────────────────────────────────

        [Fact]
        public void Beacon_DoesNotBuffItself()
        {
            const float radius = 3f;
            const float dmgBonus = 0.15f;
            const float atkSpdBonus = 0.20f;
            Env();
            int beaconId = PlaceBeacon(5, 5, radius, dmgBonus, atkSpdBonus);
            var beaconSys = ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(beaconId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(beaconId));
        }

        // ─── Range gate ────────────────────────────────────────────────────

        [Fact]
        public void Target_OutsideRadius_NotBuffed()
        {
            const float radius = 3f;
            const float dmgBonus = 0.15f;
            const float atkSpdBonus = 0.20f;
            Env();
            PlaceBeacon(5, 5, radius, dmgBonus, atkSpdBonus);
            // Place a tower 10 cells away (beyond the injected radius 3)
            int farTargetId = PlaceAttackTower(15, 15, TowerType.Basic);
            var beaconSys = ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(farTargetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(farTargetId));
        }

        // ─── Additive stacking / peer buff ─────────────────────────────────

        [Fact]
        public void TwoBeaconsInRange_StackAdditively_AndPeerBuffEachOther()
        {
            // 两个 Beacon 与三个普通目标都互为射程内；目标吃两份、Beacon 吃对方的单份。
            const float radius = 3f;
            const float dmgBonus = 0.15f;
            const float atkSpdBonus = 0.20f;
            Env();
            int b1 = PlaceBeacon(4, 5, radius, dmgBonus, atkSpdBonus);
            int b2 = PlaceBeacon(6, 5, radius, dmgBonus, atkSpdBonus);
            int t1 = PlaceAttackTower(5, 5, TowerType.Basic);
            int t2 = PlaceAttackTower(4, 6, TowerType.Basic);
            int t3 = PlaceAttackTower(6, 6, TowerType.Basic);
            var beaconSys = ResolveBeaconBuffs();

            foreach (var target in new[] { t1, t2, t3 })
            {
                Assert.Equal(dmgBonus * 2f, beaconSys.GetCachedDamageBonus(target), 3);
                Assert.Equal(atkSpdBonus * 2f, beaconSys.GetCachedAttackSpeedBonus(target), 3);
            }
            // Beacons receive their peer's buff but NOT their own.
            Assert.Equal(dmgBonus, beaconSys.GetCachedDamageBonus(b1), 3);
            Assert.Equal(dmgBonus, beaconSys.GetCachedDamageBonus(b2), 3);
            Assert.Equal(atkSpdBonus, beaconSys.GetCachedAttackSpeedBonus(b1), 3);
            Assert.Equal(atkSpdBonus, beaconSys.GetCachedAttackSpeedBonus(b2), 3);
        }

        // ─── Skipped targets ───────────────────────────────────────────────

        [Fact]
        public void DispelledTower_DoesNotReceiveBeaconBuff()
        {
            const float radius = 3f;
            const float dmgBonus = 0.15f;
            const float atkSpdBonus = 0.20f;
            Env();
            PlaceBeacon(5, 5, radius, dmgBonus, atkSpdBonus);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            Store.TowerIsDispelled[targetId] = true; // mark dispelled
            var beaconSys = ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        [Fact]
        public void InactiveTower_NotBuffed()
        {
            const float radius = 3f;
            const float dmgBonus = 0.15f;
            const float atkSpdBonus = 0.20f;
            Env();
            PlaceBeacon(5, 5, radius, dmgBonus, atkSpdBonus);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            Store.TowerActive[targetId] = false;
            var beaconSys = ResolveBeaconBuffs();
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(targetId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(targetId));
        }

        // ─── Inert / partial beacon configs ────────────────────────────────

        [Theory(DisplayName = "惰性或部分配置的 Beacon 只传播非零字段，不产生幽灵加成")]
        [InlineData(0f, 0.20f, 0.20f, 0f, 0f)]   // 半径=0 → 整体惰性
        [InlineData(3f, 0f, 0f, 0f, 0f)]        // 双加成为 0 → 整体惰性
        [InlineData(3f, 0.15f, 0f, 0.15f, 0f)]  // 只配伤害 → 攻速保持 0
        public void InertOrPartialBeacon_OnlyConfiguredFieldsPropagate(
            float radius, float dmgBonus, float atkSpdBonus,
            float expectedDmg, float expectedAtkSpd)
        {
            Env();
            PlaceBeacon(5, 5, radius, dmgBonus, atkSpdBonus);
            int targetId = PlaceAttackTower(5, 6, TowerType.Basic);
            var beaconSys = ResolveBeaconBuffs();
            Assert.Equal(expectedDmg, beaconSys.GetCachedDamageBonus(targetId), 3);
            Assert.Equal(expectedAtkSpd, beaconSys.GetCachedAttackSpeedBonus(targetId), 3);
        }

        // ─── Read helper bounds-check ──────────────────────────────────────

        [Theory(DisplayName = "Beacon 缓存读取 helper 对越界 ID 返回 0")]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void ReadHelpers_OutOfRangeId_ReturnsZero(int towerId)
        {
            Env();
            var beaconSys = new TowerBeaconSystem(Store);
            Assert.Equal(0f, beaconSys.GetCachedDamageBonus(towerId));
            Assert.Equal(0f, beaconSys.GetCachedAttackSpeedBonus(towerId));
        }
    }
}

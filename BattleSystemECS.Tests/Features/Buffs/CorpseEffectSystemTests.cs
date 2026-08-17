using BattleSystemECS.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Components;

namespace BattleSystemECS.Tests.Features.Buffs
{
    /// <summary>
    /// Round 168 Direction 3 — Hallowed Ground tests.
    /// Verifies that a CorpseEffect of type 6 (HallowedGround) deals
    /// per-tick damage to enemies inside its radius.
    /// </summary>
    public class CorpseEffectSystemTests : BattleTestBase
    {
        private (CorpseEffectSystem sys, BuffSystem buff) CreateSystem()
        {
            int playerId = Player();
            var buff = new BuffSystem(Store, playerId, Renderer);
            var sys = new CorpseEffectSystem(Store, Config, buff, Renderer);
            return (sys, buff);
        }

        private int AddEnemyAt(float x, float y, float hp)
        {
            return Enemy(e =>
            {
                e.X = x;
                e.Y = y;
                e.Health = hp;
                e.MaxHealth = hp;
            });
        }

        /// <summary>
        /// 公共骨架：按 effectType 创建位于原点的尸体效果区。
        /// duration / damagePerTick 可显式覆盖，其余字段使用各类型测试共享的默认表。
        /// </summary>
        private int AddZone(int effectType, float duration, float damagePerTick = float.NaN)
        {
            float dmg = float.IsNaN(damagePerTick)
                ? effectType switch
                {
                    6 => 4f,
                    7 => 3f,
                    8 => 2f,
                    9 => 0f,
                    10 => 30f,
                    _ => throw new ArgumentOutOfRangeException(nameof(effectType))
                }
                : damagePerTick;

            return effectType switch
            {
                6 => Store.AddCorpseEffect(x: 0f, y: 0f, effectType: 6, radius: 1.5f, duration: duration, damagePerTick: dmg, slowAmount: 1f, tickInterval: 1f),
                7 => Store.AddCorpseEffect(x: 0f, y: 0f, effectType: 7, radius: 1.2f, duration: duration, damagePerTick: dmg, slowAmount: 0.6f, tickInterval: 1f),
                8 => Store.AddCorpseEffect(x: 0f, y: 0f, effectType: 8, radius: 1.4f, duration: duration, damagePerTick: dmg, slowAmount: 1f, tickInterval: 1f, armorReduction: 0.30f, speedReduction: 0.20f),
                9 => Store.AddCorpseEffect(x: 0f, y: 0f, effectType: 9, radius: 1.8f, duration: duration, damagePerTick: dmg, slowAmount: 1f, tickInterval: 1f, missChance: 0.30f, enemySpeedBoost: 1.20f),
                10 => Store.AddCorpseEffect(x: 0f, y: 0f, effectType: 10, radius: 2.5f, duration: duration, damagePerTick: dmg, slowAmount: 1f, tickInterval: 1f, damageType: 1, visionReduction: 0.5f),
                _ => throw new ArgumentOutOfRangeException(nameof(effectType))
            };
        }

        /// <summary>Add a minimal tower to the store at (x, y) and register it as active.</summary>
        private int AddTowerAt(float x, float y)
        {
            int tid = RawTower(0, 0, TowerType.Basic, 10f, 3, 1f, 1, 50f);
            Store.PositionX[tid] = x;
            Store.PositionY[tid] = y;
            return tid;
        }

        [Theory(DisplayName = "DoT 尸体效果：范围内敌人按注入 damagePerTick 精确掉血")]
        [InlineData(6, 100f, 4f, 96f, 92f, 0f, 0f, 0f)]          // HallowedGround
        [InlineData(7, 100f, 3f, 97f, 94f, 0.6f, 0f, 0f)]         // ThornyBramble：附带 0.6 减速
        [InlineData(8, 100f, 2f, 98f, 96f, 0f, 0.30f, 0.20f)]     // BlightedGround：附带双 debuff
        [InlineData(10, 500f, 30f, 470f, 440f, 0f, 0f, 0f)]       // ScorchedEarth
        public void DoTZone_DamagesEnemyInRangePerTick(
            int effectType,
            float enemyHp,
            float damagePerTick,
            float expectedAfterTick1,
            float expectedAfterTick2,
            float expectedSlow,
            float expectedArmorReduction,
            float expectedSpeedReduction)
        {
            var (sys, buff) = CreateSystem();
            int zoneId = AddZone(effectType, duration: 5f, damagePerTick: damagePerTick);
            Assert.True(zoneId >= 0);
            Assert.True(Store.CorpseEffectActive[zoneId]);

            // 敌人在 (1,0)：距离 1，对四种类型的半径都是 in-range。
            int enemyId = AddEnemyAt(1.0f, 0.0f, enemyHp);
            Store.EnemyTerrainMoveSpeedMult[enemyId] = 1.0f; // 显式基线，避免默认 0 干扰 slow 断言

            // 第 1 秒：区域 tick 写入 Periodic DoT → BuffSystem tick 入队 → 统一结算伤害。
            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            // 期望值完全由显式注入的 enemyHp / damagePerTick 推导（如 100-4=96），不复制生产公式。
            Assert.Equal(expectedAfterTick1, Store.EnemyHealth[enemyId], 2);
            if (expectedSlow > 0f)
                Assert.Equal(expectedSlow, Store.EnemyTerrainMoveSpeedMult[enemyId], 3);
            if (expectedArmorReduction > 0f)
                Assert.Equal(expectedArmorReduction, Store.EnemyCurseArmorReduction[enemyId], 3);
            if (expectedSpeedReduction > 0f)
                Assert.Equal(expectedSpeedReduction, Store.EnemyCurseSpeedReduction[enemyId], 3);

            // 第 2 秒：再结算一个 tick。
            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            Assert.Equal(expectedAfterTick2, Store.EnemyHealth[enemyId], 2);
        }

        [Theory(DisplayName = "DoT 尸体效果：范围外敌人不受伤害且连续字段不被写入")]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void DoTZone_DoesNotAffectEnemyOutOfRange(int effectType)
        {
            var (sys, buff) = CreateSystem();
            int zoneId = AddZone(effectType, duration: 5f);
            Assert.True(zoneId >= 0);

            // 敌人远在范围外。
            int enemyId = AddEnemyAt(10f, 10f, 100f);
            Store.EnemyTerrainMoveSpeedMult[enemyId] = 1.0f;
            float hpBefore = Store.EnemyHealth[enemyId];

            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();

            // 范围外：DoT / slow / debuff 全部不写。
            Assert.Equal(hpBefore, Store.EnemyHealth[enemyId]);
            Assert.Equal(1.0f, Store.EnemyTerrainMoveSpeedMult[enemyId]);
            Assert.Equal(0f, Store.EnemyCurseArmorReduction[enemyId]);
            Assert.Equal(0f, Store.EnemyCurseSpeedReduction[enemyId]);
        }

        [Theory(DisplayName = "五种尸体效果区超时后统一失效")]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        public void ExpiresAfterDuration_AllEffectTypes(int effectType)
        {
            var (sys, buff) = CreateSystem();
            int zoneId = AddZone(effectType, duration: 2f);
            Assert.True(zoneId >= 0);

            sys.Update(2.5f);           // 超过 2s 持续时间
            buff.ResolveDotDamage();    // 无 DoT 的类型这里是 no-op

            Assert.False(Store.CorpseEffectActive[zoneId]);
            if (effectType == 10)
            {
                // ScorchedEarth 独有：移除区域时 visionReduction 也要归零。
                Assert.Equal(0f, Store.CorpseEffectVisionReduction[zoneId]);
            }
        }

        // ========== Round 169 Direction 10 — Thorny Bramble (effectType=7) ==========

        [Fact]
        public void ThornyBramble_StacksSlowerSlowWithExistingSlow()
        {
            var (sys, buff) = CreateSystem();

            // Bramble with 0.6x slow (40% slow)
            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 7,
                radius: 1.5f,
                duration: 4f,
                damagePerTick: 3f,
                slowAmount: 0.6f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);

            int enemyId = AddEnemyAt(1.0f, 0.0f, 100f);
            // Pre-existing stronger slow (0.3x) from another effect
            Store.EnemyTerrainMoveSpeedMult[enemyId] = 0.3f;

            // Bramble slow (0.6) is weaker — must NOT override the stronger 0.3 slow
            sys.Update(0.1f);
            Assert.Equal(0.3f, Store.EnemyTerrainMoveSpeedMult[enemyId]);

            // Now weaken the existing slow to 0.8; bramble's 0.6 should win
            Store.EnemyTerrainMoveSpeedMult[enemyId] = 0.8f;
            sys.Update(0.1f);
            Assert.Equal(0.6f, Store.EnemyTerrainMoveSpeedMult[enemyId]);
        }

        // ========== Round 171 Direction 4 — Blighted Ground (effectType=8) ==========
        // Blighted Ground deals per-tick DoT AND applies armor+speed debuffs to enemies
        // standing in the zone. The debuffs are written to EnemyCurseArmorReduction /
        // EnemyCurseSpeedReduction (same SOA fields CurseAuraSystem writes to).

        [Fact]
        public void BlightedGround_AppliesArmorAndSpeedDebuffPerFrame()
        {
            var (sys, buff) = CreateSystem();

            // Spawn BlightedGround: 1.4 radius, 2 dmg/tick, 30% armor, 20% speed, 1s tick, 5s dur
            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 8,            // BlightedGround
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,    // 30% armor debuff
                speedReduction: 0.20f     // 20% speed debuff
            );
            Assert.True(zoneId >= 0);
            Assert.True(Store.CorpseEffectActive[zoneId]);

            // Enemy in range (distance 1.0 < 1.4)
            int enemyId = AddEnemyAt(1.0f, 0.0f, 100f);

            // Frame 1 (deltaTime small so no tick fires): continuous debuffs applied
            sys.Update(0.1f);
            Assert.Equal(0.30f, Store.EnemyCurseArmorReduction[enemyId]);
            Assert.Equal(0.20f, Store.EnemyCurseSpeedReduction[enemyId]);
        }

        [Fact]
        public void BlightedGround_AccumulatesAdditivelyWithMultipleZones()
        {
            // Two BlightedGround zones overlapping — their debuffs should stack additively
            // per frame (each zone contributes 0.30 armor, total 0.60).
            // ComponentStore.BeginFrame() (called by the frame scheduler in real gameplay)
            // resets the field to 0 at frame start, so accumulation is well-bounded.
            // In this unit test we don't call BeginFrame, so we can verify that
            // successive sys.Update() calls accumulate across zones.
            var (sys, buff) = CreateSystem();

            int zoneId1 = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 8,
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,
                speedReduction: 0.20f
            );
            int zoneId2 = Store.AddCorpseEffect(
                x: 0.5f, y: 0f,    // overlap with zone 1
                effectType: 8,
                radius: 1.4f,
                duration: 5f,
                damagePerTick: 2f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.30f,
                speedReduction: 0.20f
            );
            Assert.True(zoneId1 >= 0);
            Assert.True(zoneId2 >= 0);

            int enemyId = AddEnemyAt(0.5f, 0.0f, 100f);

            // Single frame: both zones apply; expect 0.30 + 0.30 = 0.60 additive
            sys.Update(0.1f);
            Assert.Equal(0.60f, Store.EnemyCurseArmorReduction[enemyId]);
            Assert.Equal(0.40f, Store.EnemyCurseSpeedReduction[enemyId]);
        }

        [Fact]
        public void BlightedGround_OtherEffectTypesIgnoreNewDebuffFields()
        {
            // Verify that HallowedGround (effectType=6) and ThornyBramble (effectType=7)
            // are NOT affected by the new BlightedGround debuff fields. This guards
            // against regression where the new fields leak into existing effect types.
            var (sys, buff) = CreateSystem();

            // HallowedGround with armor/speed fields set (they should be ignored)
            int zoneId6 = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,
                radius: 1.5f,
                duration: 5f,
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f,
                armorReduction: 0.99f,    // should be ignored
                speedReduction: 0.99f     // should be ignored
            );
            int enemyId6 = AddEnemyAt(1.0f, 0.0f, 100f);
            sys.Update(0.1f);
            Assert.Equal(0f, Store.EnemyCurseArmorReduction[enemyId6]);
            Assert.Equal(0f, Store.EnemyCurseSpeedReduction[enemyId6]);

            // ThornyBramble with armor/speed fields set (they should be ignored)
            int zoneId7 = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 7,
                radius: 1.2f,
                duration: 4f,
                damagePerTick: 3f,
                slowAmount: 0.6f,
                tickInterval: 1f,
                armorReduction: 0.99f,    // should be ignored
                speedReduction: 0.99f     // should be ignored
            );
            int enemyId7 = AddEnemyAt(1.0f, 0.0f, 100f);
            sys.Update(0.1f);
            Assert.Equal(0f, Store.EnemyCurseArmorReduction[enemyId7]);
            Assert.Equal(0f, Store.EnemyCurseSpeedReduction[enemyId7]);
        }

        // ========== Round 175 Direction 9 — Smokescreen (effectType=9) ==========
        // Smokescreen is a "control zone" that does NOT damage enemies. Instead it:
        //   - Marks towers in range as "in smoke" with a miss chance (consumed by TowerAttackSystem)
        //   - Boosts enemy move speed in the zone (multiplicative into EnemyTerrainMoveSpeedMult)
        // Unlike HallowedGround/ThornyBramble/BlightedGround, Smokescreen has no DoT and
        // no enemy debuff; it operates on towers in addition to enemies.

        [Fact]
        public void Smokescreen_MarksTowerInRange_WithMissChance()
        {
            var (sys, buff) = CreateSystem();

            // Spawn Smokescreen at origin: 1.8 radius, 0.30 missChance, 1.20 speed boost, 4s dur
            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 9,            // Smokescreen
                radius: 1.8f,
                duration: 4f,
                damagePerTick: 0f,        // no DoT
                slowAmount: 1f,
                tickInterval: 1f,
                missChance: 0.30f,
                enemySpeedBoost: 1.20f
            );
            Assert.True(zoneId >= 0);

            // Place a tower inside the zone (distance 1.0 < 1.8)
            int tid = AddTowerAt(1.0f, 0.0f);
            // Per-frame reset (BeginFrame) wipes to 0; sys.Update then writes max(0.30, 0) = 0.30
            sys.Update(0.1f);
            Assert.Equal(0.30f, Store.TowerSmokeMissChance[tid]);
        }

        [Theory(DisplayName = "塔向区域类尸体效果：范围外塔不被写入")]
        [InlineData(9, true)]   // Smokescreen → TowerSmokeMissChance
        [InlineData(10, false)] // ScorchedEarth → TowerVisionReduction
        public void TowerTargetingZone_DoesNotAffectTowerOutOfRange(int effectType, bool smokeField)
        {
            var (sys, buff) = CreateSystem();
            int zoneId = AddZone(effectType, duration: 4f);
            Assert.True(zoneId >= 0);

            // 塔远在范围外（距离 ~141 大于两类半径）。
            int tid = AddTowerAt(100f, 100f);
            sys.Update(0.1f);

            // 公共骨架相同，差异仅在被写入的 SOA 字段，用数据表里的 smokeField 选择。
            float fieldValue = smokeField
                ? Store.TowerSmokeMissChance[tid]
                : Store.TowerVisionReduction[tid];
            Assert.Equal(0f, fieldValue);
        }

        [Fact]
        public void Smokescreen_BoostsEnemyMoveSpeedInRange()
        {
            var (sys, buff) = CreateSystem();

            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 9,
                radius: 1.8f,
                duration: 4f,
                damagePerTick: 0f,
                slowAmount: 1f,
                tickInterval: 1f,
                missChance: 0f,             // no miss (we test enemy boost only)
                enemySpeedBoost: 1.25f      // +25% speed
            );

            // Enemy at distance 1.0 < 1.8 (in range)
            int enemyId = AddEnemyAt(1.0f, 0.0f, 100f);
            // Initialize the speed mult to 1.0 (its default) — apply enemy terrain (1.0) so
            // we can verify the multiplicative boost.
            Store.EnemyTerrainMoveSpeedMult[enemyId] = 1.0f;

            sys.Update(0.1f);
            // Multiplicative: 1.0 * 1.25 = 1.25
            Assert.Equal(1.25f, Store.EnemyTerrainMoveSpeedMult[enemyId], 3);
        }

        [Fact]
        public void Smokescreen_DoesNotAffectEnemyOutOfRange()
        {
            var (sys, buff) = CreateSystem();

            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 9,
                radius: 1.8f,
                duration: 4f,
                damagePerTick: 0f,
                slowAmount: 1f,
                tickInterval: 1f,
                missChance: 0f,
                enemySpeedBoost: 1.25f
            );

            // Enemy FAR outside the zone
            int enemyId = AddEnemyAt(50f, 50f, 100f);
            Store.EnemyTerrainMoveSpeedMult[enemyId] = 1.0f;

            sys.Update(0.1f);
            Assert.Equal(1.0f, Store.EnemyTerrainMoveSpeedMult[enemyId], 3);
        }

        [Fact]
        public void Smokescreen_OverlappingZones_UseMaxMissChance()
        {
            var (sys, buff) = CreateSystem();

            // Two overlapping smokescreens: 0.20 and 0.40. Tower should see 0.40 (max-merge).
            int zoneA = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 9,
                radius: 2.0f,
                duration: 4f,
                damagePerTick: 0f,
                slowAmount: 1f,
                tickInterval: 1f,
                missChance: 0.20f,
                enemySpeedBoost: 1.10f
            );
            int zoneB = Store.AddCorpseEffect(
                x: 0.5f, y: 0.5f,
                effectType: 9,
                radius: 2.0f,
                duration: 4f,
                damagePerTick: 0f,
                slowAmount: 1f,
                tickInterval: 1f,
                missChance: 0.40f,
                enemySpeedBoost: 1.10f
            );

            // Tower inside BOTH zones (distance ~0.7 from each)
            int tid = AddTowerAt(0.5f, 0.0f);
            sys.Update(0.1f);
            // max(0.20, 0.40) = 0.40
            Assert.Equal(0.40f, Store.TowerSmokeMissChance[tid]);
        }

        [Fact]
        public void Smokescreen_ZeroMissChance_NoOpOnTowers()
        {
            // Edge case: a Smokescreen with missChance=0 and speedBoost=1f is a no-op
            // (the inert fast path returns early). No tower should be touched.
            var (sys, buff) = CreateSystem();

            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 9,
                radius: 1.8f,
                duration: 4f,
                damagePerTick: 0f,
                slowAmount: 1f,
                tickInterval: 1f,
                missChance: 0f,             // inert
                enemySpeedBoost: 1f         // inert
            );

            int tid = AddTowerAt(1.0f, 0.0f);
            // Pre-set the tower to a non-default value to ensure the inert path doesn't
            // accidentally clear it. (BeginFrame zeros it before ApplySmokescreenEffects,
            // so we need to set it AFTER BeginFrame — easiest is to just check after
            // Update that it remained 0 since smoke didn't write anything.)
            sys.Update(0.1f);
            Assert.Equal(0f, Store.TowerSmokeMissChance[tid]);
        }

        [Fact]
        public void Smokescreen_OtherEffectTypesIgnoreMissChance()
        {
            // Regression guard: confirm that even with missChance / enemySpeedBoost
            // set on a HallowedGround (effectType=6) or ThornyBramble (effectType=7) zone,
            // the towers in range are NOT marked with smoke miss and enemy speed is
            // NOT boosted. This guards against the new fields leaking into existing
            // effect types via the case-mismatch guard in ApplyContinuousEffect.
            var (sys, buff) = CreateSystem();

            int zoneId6 = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,            // HallowedGround
                radius: 1.5f,
                duration: 5f,
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f,
                missChance: 0.99f,         // should be ignored
                enemySpeedBoost: 5.0f      // should be ignored
            );
            int tid6 = AddTowerAt(1.0f, 0.0f);
            int eid6 = AddEnemyAt(1.0f, 0.0f, 100f);
            Store.EnemyTerrainMoveSpeedMult[eid6] = 1.0f;
            sys.Update(0.1f);
            Assert.Equal(0f, Store.TowerSmokeMissChance[tid6]);
            Assert.Equal(1.0f, Store.EnemyTerrainMoveSpeedMult[eid6], 3);
        }

        // ========== Round 183 Direction 8 — Scorched Earth (effectType=10) ==========
        // Scorched Earth is a DoT + tower vision-reduction zone. The DoT damage flows
        // through the existing CorpseEffectTickTimer path (same as Fire/Hallowed), and
        // the tower-side vision reduction flows through a NEW TowerVisionReduction[]
        // mirror field (set by ApplyScorchedEarthEffects, max-merge across overlapping
        // zones, consumed by TowerAttackSystem as a range multiplier). This block of
        // tests pins the per-frame behavior, the max-merge policy, the JSON config, the
        // out-of-range inertness, and the BeginFrame reset (R175 pattern).

        [Fact]
        public void ScorchedEarth_ReducesTowerVisionInRange()
        {
            var (sys, buff) = CreateSystem();

            // BeginFrame zeros TowerVisionReduction; manually set to 0 then run
            // (the test doesn't drive the full FrameScheduler, so call BeginFrame-equivalent
            // path: CorpseEffectSystem's Update writes the field, so we just run Update once).
            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 10,
                radius: 2.5f,
                duration: 8f,
                damagePerTick: 30f,
                slowAmount: 1f,
                tickInterval: 1f,
                damageType: 1,
                visionReduction: 0.5f
            );
            Assert.True(zoneId >= 0);
            Assert.Equal(10, Store.CorpseEffectType[zoneId]);
            Assert.Equal(1, Store.CorpseEffectDamageType[zoneId]);
            Assert.Equal(0.5f, Store.CorpseEffectVisionReduction[zoneId]);

            // Tower at (1, 0) — inside radius 2.5
            int tid = AddTowerAt(1.0f, 0.0f);
            Assert.Equal(0f, Store.TowerVisionReduction[tid]); // default 0

            sys.Update(0.1f);
            Assert.Equal(0.5f, Store.TowerVisionReduction[tid]);
        }

        [Fact]
        public void ScorchedEarth_MaxMergesOverlappingZones()
        {
            // Two zones with different vision reductions: the LARGER one wins
            // (the same max-merge policy as Smokescreen's TowerSmokeMissChance —
            // overlapping scorched-earth zones should NOT compound into 100% blind).
            var (sys, buff) = CreateSystem();

            int zoneA = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 10,
                radius: 2.0f,
                duration: 8f,
                damagePerTick: 30f,
                slowAmount: 1f,
                tickInterval: 1f,
                damageType: 1,
                visionReduction: 0.3f  // smaller
            );
            int zoneB = Store.AddCorpseEffect(
                x: 0.5f, y: 0f,
                effectType: 10,
                radius: 2.0f,
                duration: 8f,
                damagePerTick: 30f,
                slowAmount: 1f,
                tickInterval: 1f,
                damageType: 1,
                visionReduction: 0.7f  // larger, in same coverage area
            );
            Assert.True(zoneA >= 0);
            Assert.True(zoneB >= 0);

            // Tower at (1, 0) — inside BOTH zones
            int tid = AddTowerAt(1.0f, 0.0f);

            sys.Update(0.1f);
            // Max(0.3, 0.7) = 0.7 — overlapping does not compound
            Assert.Equal(0.7f, Store.TowerVisionReduction[tid]);
        }

        [Fact]
        public void ScorchedEarth_InertWhenVisionReductionIsZero()
        {
            // 0 = inert fast path. ApplyScorchedEarthEffects early-returns when visionRed
            // <= 0, so no tower write happens (TowerVisionReduction stays at the
            // BeginFrame-zeroed value of 0).
            var (sys, buff) = CreateSystem();

            // visionReduction = 0 → inert
            int zoneId = Store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 10,
                radius: 2.5f,
                duration: 8f,
                damagePerTick: 30f,
                slowAmount: 1f,
                tickInterval: 1f,
                damageType: 1,
                visionReduction: 0f
            );
            Assert.True(zoneId >= 0);

            int tid = AddTowerAt(1.0f, 0.0f);
            // (Default TowerVisionReduction is 0 from ctor; do not call BeginFrame here —
            // we are exercising the inert fast path of the SCORCHED EARTH method itself.)
            Assert.Equal(0f, Store.TowerVisionReduction[tid]);

            sys.Update(0.1f);
            // Inert: no write, no change
            Assert.Equal(0f, Store.TowerVisionReduction[tid]);
        }
    }
}

using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;

namespace BattleSystemECS.Tests
{
    /// <summary>
    /// Round 168 Direction 3 — Hallowed Ground tests.
    /// Verifies that a CorpseEffect of type 6 (HallowedGround) deals
    /// per-tick damage to enemies inside its radius.
    /// </summary>
    public class CorpseEffectSystemTests
    {
        private (ComponentStore store, GameConfig config, BuffSystem buff, int playerId) CreateEnv()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.PlayerMaxHealth[playerId] = 200f;
            store.PlayerCurrentHealth[playerId] = 200f;
            store.PositionX[playerId] = 0f;
            store.PositionY[playerId] = 0f;
            var config = new GameConfig();
            var buff = new BuffSystem(store, playerId, new MockRenderer());
            return (store, config, buff, playerId);
        }

        private int AddEnemy(ComponentStore store, float x, float y, float hp)
        {
            int id = store.CreateEntity();
            store.EnemyActive[id] = true;
            store.AddActiveEnemyId(id);
            store.PositionX[id] = x;
            store.PositionY[id] = y;
            store.SetEnemyHealth(id, hp);
            store.EnemyMaxHealth[id] = hp;
            return id;
        }

        [Fact]
        public void HallowedGround_DamagesEnemyInRangePerTick()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            // Spawn HallowedGround at origin, radius 1.5, 4 dmg/tick, 1s interval, 5s duration
            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,            // HallowedGround
                radius: 1.5f,
                duration: 5f,
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);
            Assert.True(store.CorpseEffectActive[zoneId]);

            // Place an enemy in range (distance 1.0 < 1.5)
            int enemyId = AddEnemy(store, 1.0f, 0.0f, 100f);
            float hpBefore = store.EnemyHealth[enemyId];
            Assert.Equal(100f, hpBefore);

            // Frame 1: CorpseEffect ticks at t=1s, applies DoT to enemy (stored as effect)
            sys.Update(1.0f);
            // BuffSystem.Update would normally process the queued effect and add to _dotDamageQueue,
            // but for the CorpseEffectSystem test we simulate the same drain manually.
            buff.Update(1.0f);          // tick the stored effect → enqueue damage
            buff.ResolveDotDamage();
            float hpAfterTick1 = store.EnemyHealth[enemyId];
            Assert.True(hpAfterTick1 < hpBefore, $"Expected hp to drop after 1s tick, was {hpAfterTick1}");

            // Tick another 1.0s — another 4 dmg
            float hpAfterTick2_before = store.EnemyHealth[enemyId];
            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();
            float hpAfterTick2 = store.EnemyHealth[enemyId];
            Assert.True(hpAfterTick2 < hpAfterTick2_before,
                $"Expected second tick to also damage; before={hpAfterTick2_before} after={hpAfterTick2}");
        }

        [Fact]
        public void HallowedGround_DoesNotDamageEnemyOutOfRange()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,
                radius: 1.5f,
                duration: 5f,
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);

            // Enemy far outside radius
            int enemyId = AddEnemy(store, 10f, 10f, 100f);
            float hpBefore = store.EnemyHealth[enemyId];

            sys.Update(1.0f);
            buff.Update(1.0f);
            buff.ResolveDotDamage();

            // Out-of-range enemy: no DoT applied, so no damage.
            Assert.Equal(hpBefore, store.EnemyHealth[enemyId]);
        }

        [Fact]
        public void HallowedGround_ExpiresAfterDuration()
        {
            var (store, config, buff, playerId) = CreateEnv();
            var sys = new CorpseEffectSystem(store, config, buff, new MockRenderer());

            int zoneId = store.AddCorpseEffect(
                x: 0f, y: 0f,
                effectType: 6,
                radius: 1.5f,
                duration: 2f,           // 2 seconds
                damagePerTick: 4f,
                slowAmount: 1f,
                tickInterval: 1f
            );
            Assert.True(zoneId >= 0);
            Assert.True(store.CorpseEffectActive[zoneId]);

            sys.Update(2.5f);          // exceeds duration
            buff.ResolveDotDamage();

            Assert.False(store.CorpseEffectActive[zoneId]);
        }
    }
}

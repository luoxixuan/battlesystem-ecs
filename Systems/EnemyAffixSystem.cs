using System;
using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Per-enemy affix system — assigns 1-3 random affixes at spawn and applies effects each frame.
    /// 
    /// Supported affixes (bit flags in BuffType):
    ///   AffixExtraFast  — move speed ×1.5
    ///   AffixVampiric   — on kill: heal self maxHealth×0.05
    ///   AffixMolten     — on death: AoE maxHealth×0.3 damage (radius 2)
    ///   AffixShielding  — initial shield = maxHealth×0.5
    ///   AffixTeleporter — random teleport (5s cooldown)
    ///   AffixRegen      — regen maxHealth×0.02/second
    ///   AffixExplosive  — on death: all enemies take maxHealth×0.2 explosion
    /// </summary>
    public class EnemyAffixSystem : global::BattleSystemECS.Content.Contracts.IEnemyAffixDecorator
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;
        private readonly Random _random;
        private readonly int _playerId;

        // Affix candidates: all available affixes to randomly assign
        private static readonly BuffType[] AFFIX_CANDIDATES = new[]
        {
            BuffType.AffixExtraFast,
            BuffType.AffixVampiric,
            BuffType.AffixMolten,
            BuffType.AffixShielding,
            BuffType.AffixTeleporter,
            BuffType.AffixRegen,
            BuffType.AffixExplosive,
        };

        // Number of affixes to roll per enemy (1 to MAX_AFFIXES_PER_ENEMY)
        private const int MAX_AFFIXES_PER_ENEMY = 3;

        public EnemyAffixSystem(ComponentStore store, IRenderer renderer, int playerId = 0)
        {
            this.store = store;
            this.renderer = renderer;
            _playerId = playerId;
            _random = new Random();

            // Subscribe to kill events for AffixVampiric (on-kill healing) and affix death effects
            store.OnEnemyKilled += OnEnemyKilledHandler;
        }

        private void OnEnemyKilledHandler(int enemyId, int playerId)
        {
            // Vampiric self-heal on kill
            OnVampiricKill(enemyId);

            // Death effects (Molten AoE, Explosive global)
            float maxHealth = store.EnemyMaxHealth[enemyId];
            float posX = store.PositionX[enemyId];
            float posY = store.PositionY[enemyId];
            OnEnemyDeath(enemyId, maxHealth, posX, posY);
        }

        /// <summary>
        /// Assign random affixes to an enemy at spawn time.
        /// Called from WaveSpawningSystem after store.AddEnemy().
        /// </summary>
        public void AssignAffixesAtSpawn(int enemyId, float maxHealth)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;

            // Roll 1-3 affixes
            int affixCount = 1 + _random.Next(MAX_AFFIXES_PER_ENEMY);
            BuffType flags = BuffType.None;

            for (int i = 0; i < affixCount; i++)
            {
                int idx = _random.Next(AFFIX_CANDIDATES.Length);
                flags |= AFFIX_CANDIDATES[idx];
            }

            store.EnemyAffixFlags[enemyId] = flags;

            // Apply immediate effects for affixes that set state at spawn
            if ((flags & BuffType.AffixShielding) != 0)
            {
                // Initial shield = 50% of max health
                store.EnemyShield[enemyId] = maxHealth * 0.5f;
            }

            if ((flags & BuffType.AffixExtraFast) != 0)
            {
                // Boost base speed (store.EnemyMoveSpeedBase already set by WaveSpawning)
                store.EnemyMoveSpeed[enemyId] *= 1.5f;
            }
        }

        /// <summary>
        /// Called once per frame during WavePhase to apply ongoing affix effects.
        /// Uses _activeEnemyIds for O(1) iteration; all effects are read-only on other components
        /// (except EnemyMoveSpeed for ExtraFast, EnemyHealth for Regen/Vampiric).
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeIds = store.GetActiveEnemyIds();
            for (int i = 0; i < activeIds.Count; i++)
            {
                int id = activeIds[i];
                BuffType flags = store.EnemyAffixFlags[id];
                if (flags == BuffType.None) continue;

                // AffixExtraFast: maintain boosted speed
                if ((flags & BuffType.AffixExtraFast) != 0)
                {
                    store.EnemyMoveSpeed[id] = store.EnemyMoveSpeedBase[id] * 1.5f;
                }

                // AffixRegen: heal maxHealth×0.02 per second
                if ((flags & BuffType.AffixRegen) != 0)
                {
                    float heal = store.EnemyMaxHealth[id] * 0.02f * deltaTime;
                    store.ApplyEnemyResourceAuthority(id, id, new Core.GAS.AttributeKey(3), heal);
                }

                // AffixTeleporter: random teleport with 5s cooldown
                if ((flags & BuffType.AffixTeleporter) != 0)
                {
                    float cooldown = store.EnemyTeleportCooldown[id];
                    if (cooldown <= 0f)
                    {
                        // Teleport to random X position [0, 9] and forward Y
                        float newX = (float)_random.Next(0, 10);
                        float newY = store.PositionY[id] - 3f; // move forward 3 units
                        if (newY < 0f) newY = 0f;
                        store.PositionX[id] = newX;
                        store.PositionY[id] = newY;
                        store.EnemyTeleportCooldown[id] = 5f;
                    }
                }
            }
        }

        /// <summary>
        /// Called during enemy death resolution (frame-end serial phase).
        /// Handles Molten (AoE explosion) and Explosive (global explosion) affix effects.
        /// </summary>
        public void OnEnemyDeath(int enemyId, float maxHealth, float posX, float posY)
        {
            BuffType flags = store.EnemyAffixFlags[enemyId];
            if (flags == BuffType.None) return;

            // AffixMolten: explosion deals maxHealth×0.3 to enemies within radius 2
            if ((flags & BuffType.AffixMolten) != 0)
            {
                float explosionDamage = maxHealth * 0.3f;
                float radius = 2.0f;
                ApplyAoEToNearbyEnemies(enemyId, posX, posY, explosionDamage, radius);
            }

            // AffixExplosive: all enemies on the map take maxHealth×0.2 explosion damage
            if ((flags & BuffType.AffixExplosive) != 0)
            {
                float explosionDamage = maxHealth * 0.2f;
                ApplyExplosionToAllEnemies(enemyId, explosionDamage);
            }
        }

        /// <summary>
        /// Called when an enemy with AffixVampiric kills a target.
        /// Heals the killer by maxHealth×0.05.
        /// </summary>
        public void OnVampiricKill(int killerId)
        {
            if (!store.HasAffix(killerId, BuffType.AffixVampiric)) return;
            if (killerId < 0 || killerId >= ComponentStore.MAX_ENTITIES) return;
            float heal = store.EnemyMaxHealth[killerId] * 0.05f;
            store.ApplyEnemyResourceAuthority(killerId, killerId, new Core.GAS.AttributeKey(3), heal);
        }

        private void ApplyAoEToNearbyEnemies(int sourceId, float posX, float posY, float damage, float radius)
        {
            var activeIds = store.GetActiveEnemyIds();
            for (int i = 0; i < activeIds.Count; i++)
            {
                int id = activeIds[i];
                if (id == sourceId) continue;
                if (!store.PositionActive[id]) continue;

                float dx = store.PositionX[id] - posX;
                float dy = store.PositionY[id] - posY;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist <= radius)
                {
                    var source = store.GetEntityHandle(sourceId);
                    var target = store.GetEntityHandle(id);
                    if (source.IsValid && target.IsValid) store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, damage, DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(id), ownerPlayerId: _playerId));
                }
            }
        }

        private void ApplyExplosionToAllEnemies(int sourceId, float damage)
        {
            var activeIds = store.GetActiveEnemyIds();
            for (int i = 0; i < activeIds.Count; i++)
            {
                int id = activeIds[i];
                if (id == sourceId) continue;
                var source = store.GetEntityHandle(sourceId);
                var target = store.GetEntityHandle(id);
                if (source.IsValid && target.IsValid) store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, damage, DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(id), ownerPlayerId: _playerId));
            }
        }
    }
}

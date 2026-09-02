using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Demolish System — processes tower sacrifice/demolish requests.
    /// 
    /// When a tower is marked for demolish (via TowerPlacementSystem.DemolishTower),
    /// this system detonates it with an AoE effect, applies damage and CC to all
    /// enemies in radius, then permanently destroys the tower entity.
    /// 
    /// Execution model: serial (tower demolish is triggered by player action,
    /// not every frame, so no need for parallel collection).
    /// </summary>
    public class TowerDemolishSystem
    {
        private ComponentStore store;
        private global::BattleSystemECS.Content.Contracts.IEffectCommandPort buffSystem;

        // Reusable list for AoE queries — avoids per-call allocation
        private List<int> _aoeTargets = new List<int>(128);

        // Effect type constants (mirrors GameConfig.TowerDemolishConfig.DemolishEffectType)
        private const int EFFECT_TYPE_NONE = 0;
        private const int EFFECT_TYPE_FIRE = 1;
        private const int EFFECT_TYPE_ICE = 2;
        private const int EFFECT_TYPE_LIGHTNING = 3;
        private const int EFFECT_TYPE_POISON = 4;
        private const int EFFECT_TYPE_ARCANE = 5;

        public TowerDemolishSystem(ComponentStore store, global::BattleSystemECS.Content.Contracts.IEffectCommandPort buffSystem)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.buffSystem = buffSystem;
        }

        /// <summary>
        /// Process all towers marked for demolish this frame.
        /// Called by FrameScheduler after TowerAttackSystem (before TowerAttack resolves).
        /// 
        /// For each marked tower:
        /// 1. Query spatial grid for enemies in demolish radius
        /// 2. Apply direct damage to all targets
        /// 3. Apply CC effect (stun/slow/DoT) based on demolish effect type
        /// 4. Destroy the tower entity
        /// </summary>
        public void Update()
        {
            var activeTowerIds = store.ActiveTowerIds;

            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];

                if (!store.TowerIsMarkedForDemolish[towerId]) continue;

                // Consume the mark immediately — prevents double-processing
                store.TowerIsMarkedForDemolish[towerId] = false;

                float radius = store.TowerDemolishEffectRadius[towerId];
                if (radius <= 0f) continue; // No AoE radius configured

                float damage = store.TowerDemolishDamage[towerId];
                int effectType = store.TowerDemolishEffectType[towerId];
                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];

                // Query spatial grid for enemies in range — O(cells) instead of O(enemies)
                _aoeTargets.Clear();
                store.SpatialGrid.GetEnemiesInRange(store, towerX, towerY, (int)radius, _aoeTargets);

                // Apply demolish effects to all targets
                for (int j = 0; j < _aoeTargets.Count; j++)
                {
                    int enemyId = _aoeTargets[j];

                    // Apply raw damage (no armor penetration for demolish damage)
                    ApplyDemolishDamage(enemyId, damage);

                    // Apply CC/DoT effect based on effect type
                    ApplyDemolishEffect(enemyId, effectType, towerId);
                }

                // Destroy tower entity (handles ActiveTowerIds removal and state cleanup)
                // No gold refund — this is a sacrifice
                store.DestroyEntity(towerId);
            }
        }

        /// <summary>
        /// Apply raw demolish damage to an enemy. Damage is additive and does not
        /// trigger on-hit effects (stun, slow, lifesteal) from the tower config.
        /// </summary>
        private void ApplyDemolishDamage(int enemyId, float damage)
        {
            if (damage <= 0f) return;
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;

            float currentHealth = store.EnemyHealth[enemyId];
            store.ApplyDamageAuthority(store.PlayerEntityId, enemyId, damage, 0, stage: Core.GAS.DamageAmountStage.Raw);

            // Queue death if killed — mirror the pattern used by TowerAttackSystem
            if (store.IsEnemyPendingDeath(enemyId) && store.EnemyActive[enemyId])
            {
                // Find which player owns this tower (playerId from the demolishing tower's context)
                // For demolish, use playerId=1 (default single-player)
                int playerId = 1;
                store.QueueEnemyDeath(enemyId, playerId);
            }
        }

        /// <summary>
        /// Apply secondary effect (stun, slow, DoT) based on demolish effect type.
        /// 火焰/毒素：通过伤害队列施加燃烧或中毒周期效果（由 IEffectCommandPort 处理）。
        /// Ice: freeze stun (EnemyStunDurationLeft)
        /// Lightning: stun (EnemyStunDurationLeft)
        /// Arcane: no CC
        /// </summary>
        private void ApplyDemolishEffect(int enemyId, int effectType, int towerId)
        {
            switch (effectType)
            {
                case EFFECT_TYPE_FIRE:
                case EFFECT_TYPE_POISON:
                    // 周期伤害由 IEffectCommandPort 的中毒/燃烧机制处理。
                    // 通过 global::BattleSystemECS.Content.Contracts.IEffectCommandPort 创建类型化的燃烧/中毒周期效果。
                    if (buffSystem != null)
                    {
                        ApplyDotEffect(enemyId, effectType, towerId);
                    }
                    break;

                case EFFECT_TYPE_ICE:
                case EFFECT_TYPE_LIGHTNING:
                    // Stun: set EnemyStunDurationLeft directly
                    // Per-type CC immunity (Round 97): Stun bit or Unstoppable blocks this effect-stun
                    if (store.IsCCImmuneTo(enemyId, CCImmunityConfig.Mask_Stun)) break;
                    int stunDuration = effectType == EFFECT_TYPE_ICE ? 2 : 1;
                    store.EnemyStunDurationLeft[enemyId] = Math.Max(store.EnemyStunDurationLeft[enemyId], stunDuration);
                    break;

                case EFFECT_TYPE_ARCANE:
                default:
                    // No secondary effect
                    break;
            }
        }

        /// <summary>
        /// 通过 IEffectCommandPort 为火焰/毒素拆除效果施加周期伤害。
        /// </summary>
        private void ApplyDotEffect(int enemyId, int effectType, int towerId)
        {
            // Get demolish config from the tower's stored values
            // TowerDemolishDotDamage / TowerDemolishDotInterval stored per-tower
            float storedDotDmg = store.TowerDemolishDotDamage[towerId];
            float storedDotInterval = store.TowerDemolishDotInterval[towerId];
            float storedDotDuration = store.TowerDemolishDotDuration[towerId];

            string effectName = effectType == EFFECT_TYPE_FIRE ? "DemolishBurn" : "DemolishPoison";
            // Default values if no stored config
            float dotDamage = effectType == EFFECT_TYPE_FIRE ? 5f : 3f;
            float dotDuration = 3f;
            float dotInterval = 1f;

            if (storedDotDmg > 0f)
            {
                dotDamage = storedDotDmg;
                dotDuration = storedDotDuration > 0f ? storedDotDuration : 3f;
                dotInterval = storedDotInterval > 0f ? storedDotInterval : 1f;
            }

            if (buffSystem != null)
            {
                var dotDef = GameplayEffectDef.Periodic(effectName, AttributeSetDefinitions.ENEMY_HEALTH,
                    dotDamage, dotDuration, dotInterval);
                buffSystem.ApplyDot(enemyId, dotDef);
            }
        }
    }
}

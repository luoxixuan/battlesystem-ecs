using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Elemental Reaction System — handles element application and reaction triggering.
    /// 
    /// Two-phase pattern:
    ///   Phase 1 (Apply): when a tower/ability applies an element, check existing elements,
    ///                    queue reactions, set new element timer
    ///   Phase 2 (Tick):  each frame, decrement element timers; expire elements with no remaining time
    /// 
    /// Reaction rules:
    ///   Frozen      (Ice + Fire):  enemy stunned (stops moving), duration = ice_timer
    ///   Shatter     (Frozen + Physical): extra flat damage, brief stun
    ///   Superconduct (Lightning + Poison): AoE lightning damage around target
    ///   Overload    (Fire + Lightning): explosion damage
    ///   Pyroclastic (Fire + Poison): fire explosion
    ///   Melt        (Fire + Ice): fire damage ×1.5 boost
    /// </summary>
    public class ElementalReactionSystem
    {
        private ComponentStore store;
        private int playerId;
        private IRenderer logger;

        // Ping-pong double-buffer for reaction damage events (parallel collect → serial apply)
        private List<(int enemyId, float damage, string reactionType)>[] _reactionDamageQueue = new List<(int, float, string)>[2];
        private readonly object _reactionDamageLock = new object();
        private int _reactionQueueIdx = 0;

        // Reaction definitions (reaction → extra damage multiplier / effect)
        private const float OVERLOAD_BASE_DAMAGE = 30f;
        private const float SUPERCONDUCT_BASE_DAMAGE = 20f;
        private const float PYROCLASTIC_BASE_DAMAGE = 25f;
        private const float SHATTER_BASE_DAMAGE = 15f;
        private const float FROZEN_DURATION_FACTOR = 1f; // ice_timer in seconds
        private const float SHATTER_STUN_DURATION = 0.5f;

        // ── Round 83: Elemental Exposure (Direction 5) ──
        // When the enemy has active elements and gains a new bit, the exposure window
        // refreshes to EXPOSURE_DURATION seconds. While the window is active and
        // EnemyExposureTimer > 0, incoming damage from any element NOT already in the
        // exposure mask (or from a non-element attack) is multiplied by EXPOSURE_BONUS_PCT.
        // This is the "marked by element A, vulnerable to element B" anti-synergy loop.
        private const float EXPOSURE_DURATION = 3.0f;
        private const float EXPOSURE_BONUS_PCT = 0.30f; // +30% damage to "off-element" hits

        // Element index constants matching EnemyElementTimer array layout
        // Timer for element at bit (1 << ordinal) is stored at timer[entityId * 4 + ordinal]
        private const int FIRE_IDX = 0;
        private const int ICE_IDX = 1;
        private const int LIGHTNING_IDX = 2;
        private const int POISON_IDX = 3;

        public ElementalReactionSystem(ComponentStore store, int playerId, IRenderer logger = null)
        {
            this.store = store;
            this.playerId = playerId;
            this.logger = logger;
            _reactionDamageQueue[0] = new List<(int, float, string)>(64);
            _reactionDamageQueue[1] = new List<(int, float, string)>(64);
        }

        /// <summary>
        /// Called from TowerAttackSystem/SkillSystem when an attack applies an element.
        /// Checks existing elements on target → triggers reaction if any.
        /// Returns the reaction type triggered (for calling code to apply additional effects).
        /// </summary>
        public ElementalReactionType ApplyElement(int enemyId, ElementType element, float duration)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return ElementalReactionType.None;
            if (element == ElementType.None) return ElementalReactionType.None;

            int elemIdx = ElementOrdinal(element);
            if (elemIdx < 0) return ElementalReactionType.None;

            ElementType existing = store.EnemyElementStatus[enemyId];
            ElementType otherElements = existing & ~element; // strip the being-applied element

            // Update element timer: set to duration (refresh if already present)
            store.EnemyElementTimer[enemyId * 4 + elemIdx] = duration;

            // Add element to status mask
            store.EnemyElementStatus[enemyId] = existing | element;

            // Check for reactions with other present elements
            if (otherElements == ElementType.None) return ElementalReactionType.None;

            return ComputeReaction(element, otherElements, enemyId);
        }

        private ElementalReactionType ComputeReaction(ElementType applied, ElementType existingOnTarget, int enemyId)
        {
            bool hasFire = (applied & ElementType.Fire) != 0 || (existingOnTarget & ElementType.Fire) != 0;
            bool hasIce = (applied & ElementType.Ice) != 0 || (existingOnTarget & ElementType.Ice) != 0;
            bool hasLightning = (applied & ElementType.Lightning) != 0 || (existingOnTarget & ElementType.Lightning) != 0;
            bool hasPoison = (applied & ElementType.Poison) != 0 || (existingOnTarget & ElementType.Poison) != 0;

            // Frozen: Ice + Fire
            if (hasFire && hasIce)
            {
                TriggerReaction(enemyId, ElementalReactionType.Frozen);
                // Clear both fire and ice elements (they consumed each other)
                int fireIdx = FIRE_IDX;
                int iceIdx = ICE_IDX;
                store.EnemyElementStatus[enemyId] &= ~(ElementType.Fire | ElementType.Ice);
                // Frozen lasts as long as the shorter of the two timers
                float fireTimer = store.EnemyElementTimer[enemyId * 4 + fireIdx];
                float iceTimer = store.EnemyElementTimer[enemyId * 4 + iceIdx];
                float frozenDur = Math.Min(fireTimer, iceTimer) * FROZEN_DURATION_FACTOR;
                if (frozenDur > 0f)
                {
                    ApplyFrozen(enemyId, frozenDur);
                }
                return ElementalReactionType.Frozen;
            }

            // Overload: Fire + Lightning
            if (hasFire && hasLightning)
            {
                float dmg = OVERLOAD_BASE_DAMAGE;
                TriggerReaction(enemyId, ElementalReactionType.Overload);
                _reactionDamageQueue[_reactionQueueIdx].Add((enemyId, dmg, "Overload"));
                return ElementalReactionType.Overload;
            }

            // Pyroclastic: Fire + Poison
            if (hasFire && hasPoison)
            {
                float dmg = PYROCLASTIC_BASE_DAMAGE;
                TriggerReaction(enemyId, ElementalReactionType.Pyroclastic);
                _reactionDamageQueue[_reactionQueueIdx].Add((enemyId, dmg, "Pyroclastic"));
                return ElementalReactionType.Pyroclastic;
            }

            // Superconduct: Lightning + Poison
            if (hasLightning && hasPoison)
            {
                float dmg = SUPERCONDUCT_BASE_DAMAGE;
                TriggerReaction(enemyId, ElementalReactionType.Superconduct);
                _reactionDamageQueue[_reactionQueueIdx].Add((enemyId, dmg, "Superconduct"));
                // Also create a "cold zone" effect — just apply slow debuff via BuffSystem
                // (handled externally via returned reaction type)
                return ElementalReactionType.Superconduct;
            }

            return ElementalReactionType.None;
        }

        /// <summary>
        /// Called when a Frozen enemy is hit by any attack (Shatter reaction).
        /// </summary>
        public void TriggerShatter(int enemyId, float attackDamage)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;
            if ((store.EnemyElementStatus[enemyId] & ElementType.Ice) == 0) return; // not frozen

            float shatterDmg = SHATTER_BASE_DAMAGE + attackDamage * 0.5f;
            TriggerReaction(enemyId, ElementalReactionType.Shatter);
            _reactionDamageQueue[_reactionQueueIdx].Add((enemyId, shatterDmg, "Shatter"));

            // Clear ice element
            store.EnemyElementStatus[enemyId] &= ~ElementType.Ice;
            store.EnemyElementTimer[enemyId * 4 + ICE_IDX] = 0f;

            ApplyStun(enemyId, SHATTER_STUN_DURATION);
        }

        /// <summary>
        /// Called from TowerAttackSystem to boost fire damage if target has Fire element (Melt).
        /// Returns damage multiplier.
        /// </summary>
        public float GetMeltDamageBonus(int enemyId)
        {
            if ((store.EnemyElementStatus[enemyId] & ElementType.Fire) == 0) return 1f;
            if ((store.EnemyElementStatus[enemyId] & ElementType.Ice) == 0) return 1f;
            return 1.5f;
        }

        private void ApplyFrozen(int enemyId, float duration)
        {
            // Apply stun via BuffSystem (freeze = stun equivalent)
            // Use the existing stun mechanism through buffs
            var frozenDef = new Core.GAS.GameplayEffectDef(
                name: "Frozen",
                type: Core.GAS.EffectType.Duration,
                attrIdx: -1,
                op: Core.GAS.AttributeModifierOp.Add,
                magnitude: 0f,
                duration: duration
            );
            frozenDef.StackingBehavior = Core.GAS.StackingBehavior.None;
            frozenDef.MaxStacks = 1;
            var application = Core.GAS.LegacyEffectAdapter.CreateApplication(frozenDef,
                store.GetEntityHandle(playerId), store.GetEntityHandle(enemyId));
            store.TryAddGameplayEffect(enemyId, application, out _);
        }

        private void ApplyStun(int enemyId, float duration)
        {
            var stunDef = new Core.GAS.GameplayEffectDef(
                name: "ShatterStun",
                type: Core.GAS.EffectType.Duration,
                attrIdx: -1,
                op: Core.GAS.AttributeModifierOp.Add,
                magnitude: 0f,
                duration: duration
            );
            stunDef.StackingBehavior = Core.GAS.StackingBehavior.None;
            stunDef.MaxStacks = 1;
            var application = Core.GAS.LegacyEffectAdapter.CreateApplication(stunDef,
                store.GetEntityHandle(playerId), store.GetEntityHandle(enemyId));
            store.TryAddGameplayEffect(enemyId, application, out _);
        }

        /// <summary>
        /// Tick element timers — called each frame in WavePhase.
        /// Decrements all element timers, expires elements with no remaining time.
        /// Also drains the pending-shield-break queue to trigger reactions between
        /// the shield's break-element and any existing elements on the target.
        /// </summary>
        public void Update(float deltaTime)
        {
            // Process pending shield breaks first (serial phase — safe to read/write)
            if (store.PendingShieldBreaks != null && store.PendingShieldBreaks.Count > 0)
            {
                for (int i = 0; i < store.PendingShieldBreaks.Count; i++)
                {
                    OnShieldBroken(store.PendingShieldBreaks[i]);
                }
                store.PendingShieldBreaks.Clear();
            }

            var activeEnemyIds = store.GetCachedActiveEnemyIds();
            foreach (int enemyId in activeEnemyIds)
            {
                ElementType status = store.EnemyElementStatus[enemyId];
                if (status == ElementType.None) continue;

                bool changed = false;
                if ((status & ElementType.Fire) != 0)
                {
                    float timer = store.EnemyElementTimer[enemyId * 4 + FIRE_IDX] - deltaTime;
                    if (timer <= 0f) { status &= ~ElementType.Fire; store.EnemyElementTimer[enemyId * 4 + FIRE_IDX] = 0f; }
                    else { store.EnemyElementTimer[enemyId * 4 + FIRE_IDX] = timer; }
                    changed = true;
                }
                if ((status & ElementType.Ice) != 0)
                {
                    float timer = store.EnemyElementTimer[enemyId * 4 + ICE_IDX] - deltaTime;
                    if (timer <= 0f) { status &= ~ElementType.Ice; store.EnemyElementTimer[enemyId * 4 + ICE_IDX] = 0f; }
                    else { store.EnemyElementTimer[enemyId * 4 + ICE_IDX] = timer; }
                    changed = true;
                }
                if ((status & ElementType.Lightning) != 0)
                {
                    float timer = store.EnemyElementTimer[enemyId * 4 + LIGHTNING_IDX] - deltaTime;
                    if (timer <= 0f) { status &= ~ElementType.Lightning; store.EnemyElementTimer[enemyId * 4 + LIGHTNING_IDX] = 0f; }
                    else { store.EnemyElementTimer[enemyId * 4 + LIGHTNING_IDX] = timer; }
                    changed = true;
                }
                if ((status & ElementType.Poison) != 0)
                {
                    float timer = store.EnemyElementTimer[enemyId * 4 + POISON_IDX] - deltaTime;
                    if (timer <= 0f) { status &= ~ElementType.Poison; store.EnemyElementTimer[enemyId * 4 + POISON_IDX] = 0f; }
                    else { store.EnemyElementTimer[enemyId * 4 + POISON_IDX] = timer; }
                    changed = true;
                }

                if (changed) store.EnemyElementStatus[enemyId] = status;
            }

            // ── Round 83: Elemental Exposure window maintenance (Direction 5) ──
            // Two-phase: if status has any active element AND mask differs from current
            // status, refresh the exposure window to EXPOSURE_DURATION seconds. If status
            // is None but the exposure timer is still ticking, decay it; when it hits zero
            // clear the mask so future status changes re-arm the window. O(active enemies).
            for (int k = 0; k < activeEnemyIds.Count; k++)
            {
                int enemyId = activeEnemyIds[k];
                ElementType status = store.EnemyElementStatus[enemyId];
                ElementType exposure = store.EnemyExposureMask[enemyId];

                if (status != ElementType.None)
                {
                    // Elemental activity present: refresh the exposure window only if the
                    // current element mask differs from the recorded exposure (a new bit
                    // has been added or a bit has been removed since we last sampled).
                    if (status != exposure)
                    {
                        store.EnemyExposureMask[enemyId] = status;
                        store.EnemyExposureTimer[enemyId] = EXPOSURE_DURATION;
                    }
                }
                else if (store.EnemyExposureTimer[enemyId] > 0f)
                {
                    // No active element but exposure window still alive: decay the timer.
                    float timer = store.EnemyExposureTimer[enemyId] - deltaTime;
                    if (timer <= 0f)
                    {
                        store.EnemyExposureTimer[enemyId] = 0f;
                        store.EnemyExposureMask[enemyId] = ElementType.None;
                    }
                    else
                    {
                        store.EnemyExposureTimer[enemyId] = timer;
                    }
                }
            }
        }

        /// <summary>
        /// Returns the damage multiplier applied to incoming damage against this enemy
        /// from a source element. Elemental Exposure (Round 83) makes the enemy take
        /// +EXPOSURE_BONUS_PCT damage when hit by an element not in the exposure mask
        /// (or by a non-element attack). The function is O(1) and allocates nothing —
        /// it is a pure bitwise test on the already-cached exposure mask.
        /// </summary>
        public float GetExposureDamageMultiplier(int enemyId, ElementType sourceElement)
        {
            if (store.EnemyExposureTimer[enemyId] <= 0f) return 1f;
            ElementType mask = store.EnemyExposureMask[enemyId];
            if (mask == ElementType.None) return 1f;
            // Source is None OR source bits are entirely disjoint from mask → off-element
            // hit. Apply the exposure vulnerability bonus.
            if (sourceElement == ElementType.None || (sourceElement & mask) == 0)
            {
                return 1f + EXPOSURE_BONUS_PCT;
            }
            return 1f;
        }

        /// <summary>
        /// Resolve queued reaction damage. Call after Update(), before ResolveEnemiesKilledThisFrame.
        /// </summary>
        public void ResolveReactionDamage()
        {
            int readIdx = _reactionQueueIdx;
            int writeIdx = 1 - _reactionQueueIdx;
            _reactionQueueIdx = writeIdx;
            _reactionDamageQueue[writeIdx].Clear();

            foreach (var (enemyId, damage, reactionType) in _reactionDamageQueue[readIdx])
            {
                if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) continue;
                float currentHealth = store.EnemyHealth[enemyId];
                if (currentHealth <= 0f) continue;

                store.EnemyHealth[enemyId] -= damage;
                logger?.Log($"[ELEMENT] {reactionType} reaction on enemy {enemyId}: -{damage:F1} HP");
                if (store.EnemyHealth[enemyId] <= 0f)
                    store.QueueEnemyDeath(enemyId, playerId);
            }
        }

        private int ElementOrdinal(ElementType element)
        {
            return element switch
            {
                ElementType.Fire => FIRE_IDX,
                ElementType.Ice => ICE_IDX,
                ElementType.Lightning => LIGHTNING_IDX,
                ElementType.Poison => POISON_IDX,
                _ => -1
            };
        }

        private void TriggerReaction(int enemyId, ElementalReactionType reactionType)
        {
            logger?.Log($"[ELEMENT] Reaction triggered on enemy {enemyId}: {reactionType}");
        }

        /// <summary>
        /// Called when an enemy's elemental shield is broken (in serial phase from
        /// ApplyEnemyDamage). Applies the configured break-reaction element and checks
        /// for any further reactions against existing elements on the target.
        /// </summary>
        public void OnShieldBroken(int enemyId)
        {
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return;
            ElementType breakElement = store.EnemyShieldBreakReaction[enemyId];
            if (breakElement == ElementType.None) return;

            // Get current element mask (after the shield-break timer was set in ApplyEnemyDamage)
            ElementType existing = store.EnemyElementStatus[enemyId] & ~breakElement;
            if (existing == ElementType.None) return;

            // Check for reactions with already-applied elements
            var reaction = ComputeReaction(breakElement, existing, enemyId);
            if (reaction != ElementalReactionType.None)
            {
                logger?.Log($"[ELEMENT] Shield break triggered {reaction} on enemy {enemyId}");
            }
        }
    }
}

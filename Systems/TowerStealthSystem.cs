#nullable enable
using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower Stealth System — manages tower stealth state, decloak mechanics, and True Sight detection.
    /// 
    /// Stealth types:
    /// - Type 0: None (no stealth)
    /// - Type 1 (Passive): Always stealthed; enemies without True Sight cannot target it.
    /// - Type 2 (Active): Stealthed until timer expires OR until tower attacks (decloak-on-fire).
    /// - Type 3 (Semi-Stealth): Takes 50% damage while stealthed; does NOT decloak on attack.
    /// 
    /// True Sight: enemies with EnemyHasTrueSight=true can detect and target stealthed towers.
    /// Semi-Stealth: towers take reduced damage (50%) while stealthed; they remain targetable but harder to kill.
    /// 
    /// Key design:
    /// - Stealth does NOT affect tower attacks — only enemy target acquisition.
    /// - Semi-stealth (type 3) towers are always targetable but take reduced damage.
    /// - AoE damage still hits stealthed towers (they are not invisible to area effects).
    /// 
    /// Execution: CombatGroup, before TowerAttack. Also called after tower attacks for decloak-on-fire.
    /// </summary>
    public class TowerStealthSystem : global::BattleSystemECS.Content.Contracts.ITowerTargetingView
    {
        private readonly ComponentStore store;
        private readonly int playerId;

        public TowerStealthSystem(ComponentStore store, int playerId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
        }

        /// <summary>
        /// Called once per turn from FrameScheduler.Tick() at start of CombatGroup.
        /// Manages stealth timers and auto-decloak for active (type 2) stealth.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                int stealthType = store.TowerStealthType[towerId];
                if (stealthType == 0) continue; // no stealth

                // Type 2 (Active): decrement timer and decloak when expired
                if (stealthType == 2)
                {
                    if (store.TowerStealthTimer[towerId] > 0f)
                    {
                        store.TowerStealthTimer[towerId] -= deltaTime;
                        if (store.TowerStealthTimer[towerId] <= 0f)
                        {
                            store.TowerStealthTimer[towerId] = 0f;
                            store.TowerIsStealthed[towerId] = false;
                        }
                    }
                }

                // Type 1 (Passive) and Type 2: auto-decloak check — if any True Sight enemy is in range,
                // the tower is effectively revealed (handled at targeting time via CanTargetTower)
                // Type 3 (Semi-Stealth): always targetable, no timer needed
            }
        }

        /// <summary>
        /// Called from TowerAttackSystem after a stealthed tower fires an attack.
        /// Triggers decloak for Type 2 (Active) stealth towers with DecloakOnFire=true.
        /// </summary>
        public void OnTowerAttacked(int towerId)
        {
            if (!store.TowerActive[towerId]) return;
            int stealthType = store.TowerStealthType[towerId];
            if (stealthType != 2) return; // only type 2 decloaks on fire
            if (!store.TowerDecloakOnFire[towerId]) return;

            store.TowerIsStealthed[towerId] = false;
            store.TowerStealthTimer[towerId] = 0f;
        }

        /// <summary>
        /// Called from EnemyAISystem and SuicideBombSystem when selecting a tower target.
        /// Returns true if the given tower can be targeted by the given enemy.
        /// 
        /// Rules:
        /// - No stealth (type 0): always targetable
        /// - Semi-stealth (type 3): always targetable (but takes 50% damage)
        /// - Passive/Active (type 1/2): targetable only if enemy has True Sight
        /// </summary>
        public bool CanTargetTower(int towerId, int enemyId)
        {
            if (!store.TowerActive[towerId]) return false;
            
            int stealthType = store.TowerStealthType[towerId];
            if (stealthType == 0) return true;     // no stealth
            if (stealthType == 3) return true; // semi-stealth: always targetable

            // Type 1 (Passive) or Type 2 (Active): check True Sight
            bool isStealthed = store.TowerIsStealthed[towerId];
            if (!isStealthed) return true;

            // Enemy has True Sight — can target through stealth
            if (store.EnemyHasTrueSight[enemyId]) return true;

            return false;
        }

        /// <summary>
        /// Returns the damage multiplier for a tower that is currently stealthed.
        /// Semi-stealth (type 3) takes 50% damage while stealthed.
        /// Other types take full damage (stealth is cosmetic for targeting only).
        /// </summary>
        public float GetStealthDamageMultiplier(int towerId)
        {
            if (!store.TowerIsStealthed[towerId]) return 1f;
            int stealthType = store.TowerStealthType[towerId];
            if (stealthType == 3) return 0.5f; // Semi-stealth: 50% damage
            return 1f; // Other types: full damage (stealth doesn't block damage)
        }

        /// <summary>
        /// Activates stealth on a tower.
        /// For Type 2 (Active): starts the timer with the given duration.
        /// For Type 1 (Passive): just sets IsStealthed=true.
        /// </summary>
        public void ActivateStealth(int towerId, int stealthType, float duration = 0f)
        {
            if (!store.TowerActive[towerId]) return;
            store.TowerStealthType[towerId] = stealthType;
            store.TowerIsStealthed[towerId] = true;
            store.TowerDecloakOnFire[towerId] = (stealthType == 2);
            if (stealthType == 2)
            {
                store.TowerStealthTimer[towerId] = duration;
                store.TowerStealthDuration[towerId] = duration;
            }
        }

        /// <summary>
        /// Manually decloaks a tower (e.g. when a True Sight enemy comes in range).
        /// </summary>
        public void DeclaimTower(int towerId)
        {
            if (!store.TowerActive[towerId]) return;
            store.TowerIsStealthed[towerId] = false;
            store.TowerStealthTimer[towerId] = 0f;
        }
    }
}

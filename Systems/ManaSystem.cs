using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Mana/Energy Pool System — provides a second resource dimension for skills.
    /// 
    /// Design:
    /// - Each player has a mana pool (PlayerMana) that regenerates over time.
    /// - Mana caps at PlayerMaxMana, which can be increased by tech tree / upgrades.
    /// - Skills have a mana cost; casting checks if mana is sufficient before executing.
    /// - Different regen rates for BuildPhase (higher, for preparation) and WavePhase (normal).
    /// - ManaSystem.Update() is called every frame during BuildPhase and WavePhase.
    /// </summary>
    public class ManaSystem
    {
        private ComponentStore store;
        private IRenderer renderer;
        private GameConfig gameConfig;
        private TechTreeSystem techTreeSystem;
        private readonly bool hasTechTreeSystem;
        private readonly int playerId;

        public ManaSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig, int playerId, TechTreeSystem techTreeSystem = null)
        {
            this.store = store;
            this.renderer = renderer;
            this.gameConfig = gameConfig;
            this.playerId = playerId;
            this.techTreeSystem = techTreeSystem;
            this.hasTechTreeSystem = techTreeSystem != null;
        }

        /// <summary>
        /// Initialize mana pool at game start.
        /// </summary>
        public void Initialize()
        {
            float baseMana = gameConfig.Mana.BaseMana;
            float maxMana = gameConfig.Mana.MaxManaBase;
            float regen = gameConfig.Mana.ManaRegenPerSec;

            // Tech tree can modify initial mana
            if (hasTechTreeSystem)
            {
                float bonus = techTreeSystem.GetMaxManaBonus();
                maxMana += bonus;
                regen += techTreeSystem.GetManaRegenBonus();
            }

            store.PlayerMana[playerId] = baseMana;
            store.PlayerMaxMana[playerId] = maxMana;
            store.PlayerManaRegen[playerId] = regen;
            store.PlayerManaCost[playerId] = gameConfig.Mana.ManaCostMultiplier;
        }

        /// <summary>
        /// Called at the start of each frame to cache stats from tech tree.
        /// </summary>
        public void SetTurn()
        {
            if (hasTechTreeSystem)
            {
                float maxBonus = techTreeSystem.GetMaxManaBonus();
                float regenBonus = techTreeSystem.GetManaRegenBonus();
                float costMult = techTreeSystem.GetManaCostMultiplier();

                store.PlayerMaxMana[playerId] = gameConfig.Mana.MaxManaBase + maxBonus;
                store.PlayerManaRegen[playerId] = gameConfig.Mana.ManaRegenPerSec + regenBonus;
                store.PlayerManaCost[playerId] = costMult;
            }
        }

        /// <summary>
        /// Update mana pool: regenerate based on elapsed time.
        /// </summary>
        public void Update(float deltaTime, bool isBuildPhase)
        {
            float regen = store.PlayerManaRegen[playerId];

            // Use higher regen rate in BuildPhase
            if (isBuildPhase)
            {
                float buildRegenMult = gameConfig.Mana.ManaRegenBuildPhase / Math.Max(gameConfig.Mana.ManaRegenPerSec, 0.001f);
                regen *= buildRegenMult;
            }

            // Regenerate mana
            if (regen > 0f)
            {
                float current = store.PlayerMana[playerId];
                float max = store.PlayerMaxMana[playerId];
                float regenerated = regen * deltaTime;
                store.PlayerMana[playerId] = Math.Min(current + regenerated, max);
            }
        }

        /// <summary>
        /// Check if the player has enough mana to cast a skill with the given base cost.
        /// Returns true if mana is sufficient (after applying PlayerManaCost multiplier).
        /// </summary>
        public bool HasEnoughMana(float baseCost)
        {
            if (baseCost <= 0f) return true; // Free skills always castable
            float costMult = store.PlayerManaCost[playerId];
            float actualCost = baseCost * costMult;
            return store.PlayerMana[playerId] >= actualCost;
        }

        /// <summary>
        /// Consume mana for casting a skill. Returns true if mana was successfully consumed.
        /// </summary>
        public bool ConsumeMana(float baseCost)
        {
            if (baseCost <= 0f) return true;
            float costMult = store.PlayerManaCost[playerId];
            float actualCost = baseCost * costMult;
            float current = store.PlayerMana[playerId];
            if (current < actualCost) return false;
            store.PlayerMana[playerId] = current - actualCost;
            return true;
        }

        /// <summary>
        /// Get current mana.
        /// </summary>
        public float GetCurrentMana()
        {
            return store.PlayerMana[playerId];
        }

        /// <summary>
        /// Get maximum mana.
        /// </summary>
        public float GetMaxMana()
        {
            return store.PlayerMaxMana[playerId];
        }

        /// <summary>
        /// Get mana regen rate per second.
        /// </summary>
        public float GetManaRegen()
        {
            return store.PlayerManaRegen[playerId];
        }
    }
}
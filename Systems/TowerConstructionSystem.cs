using System;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Tower construction system - manages tower build times.
    /// 
    /// When a tower has ConstructionTime > 0, it enters a construction phase where it
    /// cannot attack. Progress increases each frame until 1.0, then the tower becomes
    /// fully operational.
    /// 
    /// If IsVulnerableDuringConstruction is true, enemies can attack the tower's
    /// ConstructionHP while it's being built. If HP reaches 0, construction is cancelled.
    /// </summary>
    public class TowerConstructionSystem
    {
        private ComponentStore store;
        private IRenderer logger;

        public TowerConstructionSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.logger = logger;
        }

        /// <summary>
        /// Called at the start of each frame (during SetTurn).
        /// No-op for this system since it doesn't maintain per-frame state.
        /// </summary>
        public void SetTurn() { }

        /// <summary>
        /// Update construction progress for all towers under construction.
        /// Called every frame during both BuildPhase and WavePhase.
        /// </summary>
        /// <param name="deltaTime">Time elapsed since last frame (seconds)</param>
        public void Update(float deltaTime)
        {
            var towerIds = store.ActiveTowerIds;
            for (int i = 0; i < towerIds.Count; i++)
            {
                int towerId = towerIds[i];
                if (!store.TowerIsConstructing[towerId]) continue;

                float progress = store.TowerConstructionProgress[towerId];
                if (progress >= 1f)
                {
                    // Construction complete
                    store.TowerIsConstructing[towerId] = false;
                    continue;
                }

                // Progress construction based on time
                float totalTime = store.TowerConstructionTime[towerId];
                if (totalTime > 0f)
                {
                    float progressPerSecond = 1f / totalTime;
                    store.TowerConstructionProgress[towerId] = Math.Min(1f, progress + progressPerSecond * deltaTime);
                }
                else
                {
                    // No construction time required — instant complete
                    store.TowerConstructionProgress[towerId] = 1f;
                }

                // Check for completion this frame
                if (store.TowerConstructionProgress[towerId] >= 1f)
                {
                    store.TowerIsConstructing[towerId] = false;
                    string towerType = store.TowerType[towerId] ?? "Unknown";
                    logger.Log($"[CONSTRUCTION] 塔 #{towerId} ({towerType}) 建造完成");
                }
            }
        }

        /// <summary>
        /// Apply damage to a tower's construction HP (called when enemies attack building towers).
        /// If HP reaches 0, construction is cancelled and the tower is destroyed.
        /// </summary>
        /// <param name="towerId">Tower entity ID</param>
        /// <param name="damage">Damage amount</param>
        public void DamageConstruction(int towerId, float damage)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return;
            if (!store.TowerActive[towerId]) return;
            if (!store.TowerIsConstructing[towerId]) return;
            if (!store.TowerIsVulnerableDuringConstruction[towerId]) return;

            float currentHP = store.TowerConstructionHP[towerId];
            currentHP -= damage;
            store.TowerConstructionHP[towerId] = currentHP;

            if (currentHP <= 0f)
            {
                // Construction failed — destroy the tower
                string towerType = store.TowerType[towerId] ?? "Unknown";
                logger.Log($"[CONSTRUCTION] 塔 #{towerId} ({towerType}) 建造失败：HP 耗尽");
                store.DestroyEntity(towerId);
            }
        }
    }
}
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Aura Tower System — applies range-based buffs to nearby friendly towers.
    /// Two-phase: parallel collection of aura effects, serial application.
    /// Only towers marked as IsAuraTower participate in the aura logic.
    /// </summary>
    public class AuraTowerSystem
    {
        private ComponentStore store;
        private List<int> _auraTowerIds;
        private List<int> _candidateTowers;
        private float[] _cachedAuraAttackSpeedBonus;
        private float[] _cachedAuraDamageBonus;

        public AuraTowerSystem(ComponentStore store)
        {
            this.store = store;
            _auraTowerIds = new List<int>(64);
            _candidateTowers = new List<int>(128);
            _cachedAuraAttackSpeedBonus = Array.Empty<float>();
            _cachedAuraDamageBonus = Array.Empty<float>();
        }

        /// <summary>
        /// Called once per turn after SpatialGrid is rebuilt and before TowerAttackSystem parallel loop.
        /// Collects all aura tower IDs so the parallel loop can do O(1) aura checks.
        /// </summary>
        public void SetTurn()
        {
            _auraTowerIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                if (store.TowerIsAuraTower[towerId])
                    _auraTowerIds.Add(towerId);
            }
        }

        /// <summary>
        /// Resolve aura buffs from all aura towers onto nearby friendly towers.
        /// Called in the serial phase after the parallel damage/debuff collection.
        /// </summary>
        public void ResolveAuraBuffs()
        {
            if (_auraTowerIds.Count == 0) return;

            // Ensure cache arrays are large enough
            var towerIds = store.ActiveTowerIds;
            int count = towerIds.Count;
            if (_cachedAuraAttackSpeedBonus.Length < count)
            {
                _cachedAuraAttackSpeedBonus = new float[count];
                _cachedAuraDamageBonus = new float[count];
            }
            else
            {
                Array.Clear(_cachedAuraAttackSpeedBonus, 0, count);
                Array.Clear(_cachedAuraDamageBonus, 0, count);
            }

            // Phase 1: collect aura bonuses from each aura tower
            for (int ai = 0; ai < _auraTowerIds.Count; ai++)
            {
                int auraTowerId = _auraTowerIds[ai];
                float auraRadius = store.TowerAuraRadius[auraTowerId];
                if (auraRadius <= 0f) continue;

                float ax = store.PositionX[auraTowerId];
                float ay = store.PositionY[auraTowerId];
                int auraRadiusSq = (int)(auraRadius * auraRadius);

                // Find all towers in range — linear scan over active towers (O(n_towers) ≤ 200, acceptable for serial aura phase)
                for (int ti = 0; ti < count; ti++)
                {
                    int targetTowerId = towerIds[ti];
                    if (targetTowerId == auraTowerId) continue; // don't buff self
                    if (!store.TowerActive[targetTowerId]) continue;
                    // Skip towers that are dispelled (aura/synergy buffs cleared, cannot receive new ones)
                    if (store.TowerIsDispelled[targetTowerId]) continue;

                    float tx = store.PositionX[targetTowerId];
                    float ty = store.PositionY[targetTowerId];
                    float dx = tx - ax;
                    float dy = ty - ay;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > auraRadiusSq) continue;

                    float dmgBonus = store.TowerAuraDamageBonus[auraTowerId];
                    float spdBonus = store.TowerAuraAttackSpeedBonus[auraTowerId];

                    _cachedAuraDamageBonus[ti] += dmgBonus;
                    _cachedAuraAttackSpeedBonus[ti] += spdBonus;
                }
            }

            // Phase 2: apply accumulated bonuses to each active tower
            for (int ti = 0; ti < count; ti++)
            {
                int towerId = towerIds[ti];
                float dmgBonus = _cachedAuraDamageBonus[ti];
                float spdBonus = _cachedAuraAttackSpeedBonus[ti];
                if (dmgBonus > 0f)
                    store.TowerAttackDamage[towerId] *= (1f + dmgBonus);
                if (spdBonus > 0f)
                    store.TowerAttackSpeed[towerId] *= (1f + spdBonus);
            }
        }
    }
}
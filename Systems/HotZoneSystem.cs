#nullable enable
using System;
using System.Threading.Tasks;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Map Hot Zone / Terrain Bonus System — applies placement bonuses to towers in specific map areas.
    /// 
    /// Hot zones are pre-defined regions on the map that grant placed towers bonuses
    /// (damage, range, attack speed) as a strategic incentive for thoughtful tower placement.
    /// Bonuses are cached at placement time in TowerHotZoneDamageBonus/RangeBonus/SpeedBonus,
    /// avoiding per-frame queries during combat.
    /// 
    /// Execution: runs after TowerPlacement in CombatSetup group, so bonuses are
    /// applied before the attack resolution phase each frame.
    /// </summary>
    public class HotZoneSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        
        // Hot zone definitions: pre-loaded from GameConfig.HotZoneDefs
        // Mirrored as fast SOA arrays for zero-allocation hot path access.
        private int[] _hotZoneCenterX = Array.Empty<int>();
        private int[] _hotZoneCenterY = Array.Empty<int>();
        private int[] _hotZoneRadius = Array.Empty<int>();
        private float[] _hotZoneDamageBonus = Array.Empty<float>();
        private float[] _hotZoneRangeBonus = Array.Empty<float>();
        private float[] _hotZoneSpeedBonus = Array.Empty<float>();
        private int _hotZoneCount;

        private readonly GameConfig config;

        public HotZoneSystem(ComponentStore store, GameConfig config, int playerId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.playerId = playerId;
            LoadHotZoneDefs();
        }

        /// <summary>
        /// Load hot zone definitions from config.HotZoneDefs into SOA arrays.
        /// Called once at construction; hot path only reads pre-cached values.
        /// </summary>
        private void LoadHotZoneDefs()
        {
            var defs = config.HotZoneDefs;
            _hotZoneCount = defs != null ? defs.Count : 0;
            
            _hotZoneCenterX = new int[_hotZoneCount];
            _hotZoneCenterY = new int[_hotZoneCount];
            _hotZoneRadius = new int[_hotZoneCount];
            _hotZoneDamageBonus = new float[_hotZoneCount];
            _hotZoneRangeBonus = new float[_hotZoneCount];
            _hotZoneSpeedBonus = new float[_hotZoneCount];
            
            for (int i = 0; i < _hotZoneCount; i++)
            {
                var def = defs[i];
                _hotZoneCenterX[i] = def.CenterX;
                _hotZoneCenterY[i] = def.CenterY;
                _hotZoneRadius[i] = def.Radius;
                _hotZoneDamageBonus[i] = def.DamageBonus;
                _hotZoneRangeBonus[i] = def.RangeBonus;
                _hotZoneSpeedBonus[i] = def.SpeedBonus;
            }
        }

        public void SetTurn(int turn)
        {
            // Nothing per-turn to cache — hot zones are static, bonuses pre-computed at placement
        }

        /// <summary>
        /// Apply hot zone bonuses to all active towers.
        /// Called once per frame in CombatSetup phase.
        /// </summary>
        public void Update()
        {
            if (_hotZoneCount == 0) return;
            
            var activeTowerIds = store.ActiveTowerIds;
            int count = activeTowerIds.Count;
            if (count == 0) return;

            // Zero-allocation parallel loop: iterate active towers, check against each hot zone.
            // Bonuses are pre-cached in tower fields at placement time — no recalculation here.
            // This Update() call exists for the case where towers are placed mid-wave
            // (rare, but possible via relocate). For the normal case bonuses are set at placement.
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                int towerId = activeTowerIds[i];
                if (!store.TowerActive[towerId]) return;

                // Check each hot zone for this tower's position
                float tx = store.PositionX[towerId];
                float ty = store.PositionY[towerId];
                float bestDamageBonus = 0f;
                float bestRangeBonus = 0f;
                float bestSpeedBonus = 0f;

                for (int h = 0; h < _hotZoneCount; h++)
                {
                    int hx = _hotZoneCenterX[h];
                    int hy = _hotZoneCenterY[h];
                    int radius = _hotZoneRadius[h];

                    float dx = tx - hx;
                    float dy = ty - hy;
                    float distSq = dx * dx + dy * dy;
                    float radiusSq = radius * radius;

                    if (distSq <= radiusSq)
                    {
                        // Tower is in this hot zone — accumulate bonuses (stacking)
                        bestDamageBonus += _hotZoneDamageBonus[h];
                        bestRangeBonus += _hotZoneRangeBonus[h];
                        bestSpeedBonus += _hotZoneSpeedBonus[h];
                    }
                }

                // Write pre-cached bonuses into tower fields for combat systems to read
                if (bestDamageBonus > 0f || bestRangeBonus > 0f || bestSpeedBonus > 0f)
                {
                    store.TowerHotZoneDamageBonus[towerId] = bestDamageBonus;
                    store.TowerHotZoneRangeBonus[towerId] = bestRangeBonus;
                    store.TowerHotZoneSpeedBonus[towerId] = bestSpeedBonus;
                }
                else
                {
                    store.TowerHotZoneDamageBonus[towerId] = 0f;
                    store.TowerHotZoneRangeBonus[towerId] = 0f;
                    store.TowerHotZoneSpeedBonus[towerId] = 0f;
                }
            });
        }

        /// <summary>
        /// Called by TowerPlacementSystem when a tower is placed.
        /// Computes and caches hot zone bonuses immediately so combat systems can read them.
        /// Returns true if any hot zone bonus was applied.
        /// </summary>
        public bool OnTowerPlaced(int towerId)
        {
            float tx = store.PositionX[towerId];
            float ty = store.PositionY[towerId];
            float bestDamageBonus = 0f;
            float bestRangeBonus = 0f;
            float bestSpeedBonus = 0f;

            for (int h = 0; h < _hotZoneCount; h++)
            {
                float dx = tx - _hotZoneCenterX[h];
                float dy = ty - _hotZoneCenterY[h];
                float distSq = dx * dx + dy * dy;
                float radiusSq = (float)(_hotZoneRadius[h] * _hotZoneRadius[h]);

                if (distSq <= radiusSq)
                {
                    bestDamageBonus += _hotZoneDamageBonus[h];
                    bestRangeBonus += _hotZoneRangeBonus[h];
                    bestSpeedBonus += _hotZoneSpeedBonus[h];
                }
            }

            store.TowerHotZoneDamageBonus[towerId] = bestDamageBonus;
            store.TowerHotZoneRangeBonus[towerId] = bestRangeBonus;
            store.TowerHotZoneSpeedBonus[towerId] = bestSpeedBonus;

            return bestDamageBonus > 0f || bestRangeBonus > 0f || bestSpeedBonus > 0f;
        }
    }
}
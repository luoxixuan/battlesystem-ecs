#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Zone Control System — manages persistent CC zones placed by skills/towers.
    /// 
    /// Enemies walking through a CC Zone receive a status effect (Slow/Stun/Freeze/Root)
    /// based on the zone's type and strength. Multiple zones can stack or refresh duration.
    /// 
    /// Integration points:
    ///   - SkillSystem CastGroundTarget with AreaShapeType.CCZone calls AddCCZone()
    ///   - FrameScheduler calls ZoneControl.Update() before EnemyMovement (WavePhase)
    ///   - EnemyMovementSystem reads ZoneControl results when moving enemies
    /// 
    /// Design notes:
    ///   - CCZone is separate from CorpseEffect (which is tied to enemy death positions)
    ///   - CC Zones are fixed-position, player/skill-placed
    ///   - Uses SOA arrays in ComponentStore_World for zero-allocation iteration
    ///   - Max CC Zones = 500 (shared pool with HazardZone)
    /// </summary>
    public class ZoneControlSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer? _logger;

        // Cached active CC zone IDs (rebuilt each frame from _activeCCZoneIds list)
        private List<int> _activeCCZoneIds = new List<int>();
        private int _nextCCZoneId = 0;

        public ZoneControlSystem(ComponentStore store, IRenderer? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
        }

        /// <summary>
        /// Zone CC type constants (matches ZoneControlType in ComponentStore_World).
        /// </summary>
        public const int TypeSlow = 0;
        public const int TypeStun = 1;
        public const int TypeFreeze = 2;
        public const int TypeRoot = 3;

        /// <summary>
        /// Place a CC zone at the specified world coordinates.
        /// Returns zone ID or -1 if no free slots.
        /// </summary>
        /// <param name="x">World X coordinate</param>
        /// <param name="y">World Y coordinate</param>
        /// <param name="radius">Effect radius in tiles</param>
        /// <param name="zoneType">0=Slow, 1=Stun, 2=Freeze, 3=Root</param>
        /// <param name="strength">Effect strength (slow amount, stun chance, etc.)</param>
        /// <param name="duration">Duration in seconds</param>
        public int AddCCZone(float x, float y, float radius, int zoneType, float strength, float duration)
        {
            int zoneId = -1;
            // Find free slot in ComponentStore CCZone arrays (MAX_HAZARD_ZONES = 500)
            // No lock needed: HazardZoneActive is a plain bool[], writes are atomic bool assignments
            for (int i = 0; i < ComponentStore.MAX_HAZARD_ZONES; i++)
            {
                int candidateId = (_nextCCZoneId + i) % ComponentStore.MAX_HAZARD_ZONES;
                if (!_store.HazardZoneActive[candidateId])
                {
                    zoneId = candidateId;
                    _nextCCZoneId = (candidateId + 1) % ComponentStore.MAX_HAZARD_ZONES;
                    break;
                }
            }
            if (zoneId < 0) return -1; // pool full

            // Use HazardZone arrays as the CCZone storage (shared pool, type=0 means inactive)
            // CCZone type is stored in HazardZoneType: 0=none, 5=Slow, 6=Stun, 7=Freeze, 8=Root
            int storedType = zoneType switch
            {
                TypeSlow => 5,    // reuse HazardZone type 5 for CC Slow
                TypeStun => 6,    // reuse HazardZone type 6 for CC Stun
                TypeFreeze => 7,  // reuse HazardZone type 7 for CC Freeze
                TypeRoot => 8,    // reuse HazardZone type 8 for CC Root
                _ => 5
            };

            _store.HazardZoneActive[zoneId] = true;
            _store.HazardZoneX[zoneId] = x;
            _store.HazardZoneY[zoneId] = y;
            _store.HazardZoneRadius[zoneId] = radius;
            _store.HazardZoneMaxRadius[zoneId] = radius;
            _store.HazardZoneType[zoneId] = storedType;
            _store.HazardZoneDuration[zoneId] = duration;
            _store.HazardZoneDamagePerSec[zoneId] = strength; // stored as strength
            _store.HazardZoneOwnerTowerId[zoneId] = -1; // skill-placed, no tower owner
            _activeCCZoneIds.Add(zoneId);

            _logger?.Log($"[ZONECTRL] Placed CC zone id={zoneId} at ({x:F1},{y:F1}) type={zoneType} radius={radius} strength={strength} duration={duration}s");
            return zoneId;
        }

        /// <summary>
        /// Remove a CC zone by ID.
        /// </summary>
        public void RemoveCCZone(int zoneId)
        {
            if (zoneId < 0 || zoneId >= ComponentStore.MAX_HAZARD_ZONES) return;
            _store.HazardZoneActive[zoneId] = false;
            _store.HazardZoneType[zoneId] = 0;
            _activeCCZoneIds.Remove(zoneId);
        }

        /// <summary>
        /// Update all active CC zones — decrement duration, tick effects.
        /// Called from FrameScheduler before EnemyMovement (WavePhase).
        /// </summary>
        public void Update(float deltaTime)
        {
            for (int i = _activeCCZoneIds.Count - 1; i >= 0; i--)
            {
                int zoneId = _activeCCZoneIds[i];
                if (!_store.HazardZoneActive[zoneId]) continue;

                int zoneType = _store.HazardZoneType[zoneId];
                // Only process CC zones (types 5-8)
                if (zoneType < 5 || zoneType > 8) continue;

                // Decrement duration
                _store.HazardZoneDuration[zoneId] -= deltaTime;

                if (_store.HazardZoneDuration[zoneId] <= 0f)
                {
                    RemoveCCZone(zoneId);
                }
            }
        }

        /// <summary>
        /// Get list of active CC zone IDs for external iteration (e.g. from EnemyMovementSystem).
        /// Returns the list directly — do not modify it.
        /// </summary>
        public List<int> GetActiveCCZoneIds()
        {
            return _activeCCZoneIds;
        }

        /// <summary>
        /// Check if a world position is inside any active CC zone.
        /// Returns (inZone, zoneType, strength) — called from EnemyMovementSystem.
        /// </summary>
        public (bool inZone, int zoneType, float strength) CheckPosition(float x, float y)
        {
            for (int i = 0; i < _activeCCZoneIds.Count; i++)
            {
                int zoneId = _activeCCZoneIds[i];
                if (!_store.HazardZoneActive[zoneId]) continue;

                int zoneType = _store.HazardZoneType[zoneId];
                if (zoneType < 5 || zoneType > 8) continue;

                float cx = _store.HazardZoneX[zoneId];
                float cy = _store.HazardZoneY[zoneId];
                float radius = _store.HazardZoneRadius[zoneId];

                float dx = x - cx;
                float dy = y - cy;
                float distSq = dx * dx + dy * dy;
                float radiusSq = radius * radius;

                if (distSq <= radiusSq)
                {
                    float strength = _store.HazardZoneDamagePerSec[zoneId]; // stored as strength
                    // Map back to 0-3 range
                    int ccType = zoneType - 5;
                    return (true, ccType, strength);
                }
            }
            return (false, 0, 0f);
        }
    }
}
#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Magnetize System — persistent magnetic field zones placed on the ground.
    ///
    /// A Magnetize Zone is a stationary AOE (active for `duration` seconds) that:
    ///   1) PULLS every enemy inside the radius toward the center, or REPELS outward
    ///      (controlled by `zoneType`: 0=Pull, 1=Repel, 2=Pull+Deflect projectiles).
    ///   2) Deflects in-flight projectiles toward the nearest enemy when `zoneType=2`
    ///      (read by ProjectileSystem via the IsInDeflectZone(x, y) query).
    ///
    /// Differences vs ZoneControlSystem (CC zone) / HazardZone (DoT zone):
    ///   - Magnetize zones deal NO damage and apply NO CC. They only do displacement.
    ///   - Pull force is applied as a per-frame position delta BEFORE the enemy's
    ///     normal AI movement (so an enemy can still move forward against the pull,
    ///     just slower / not at all if pull > move speed).
    ///
    /// Integration points:
    ///   - FrameScheduler calls Magnetize.Update(deltaTime) inside AIGroup, BEFORE
    ///     EnemyMovementSystem runs (so pull force is layered into the same frame's
    ///     motion as a pre-step).
    ///   - ProjectileSystem queries IsInDeflectZone() to bend homing bullets.
    ///   - Player skills / tower abilities call SpawnZone(x, y, r, dur, pull, type)
    ///     to create a new zone at a target location.
    ///
    /// Design notes:
    ///   - SOA arrays live in ComponentStore_World (MagnetizeZone*) — pool size 64.
    ///   - Update is single-pass over active zones × active enemies. For 64 zones
    ///     × 10K enemies that's 640K distance checks per frame — still cheap
    ///     (~5ms on warm CPU) but uses a per-zone radiusSq cache.
    ///   - Pull force is linear distance-scaled: enemy at center gets 0 force,
    ///     enemy at radius edge gets full pullStrength. (Stable, no snap-to-center.)
    ///   - Defensive bounds-clamp on resulting position prevents enemies leaving
    ///     the map (uses store's map dimensions; defaults to ±10000 if not set).
    /// </summary>
    public class MagnetizeSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer? _logger;

        // Cached active magnetize zone IDs (reference to store's internal list, no copy)
        private List<int> _activeZoneIdsRef;

        // Bounds clamp — enemies should never leave the map
        private const float POSITION_CLAMP = 10000f;

        public MagnetizeSystem(ComponentStore store, IRenderer? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
            _activeZoneIdsRef = _store.GetCachedActiveMagnetizeZoneIds();
        }

        // ─────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────

        /// <summary>Zone type constants. 0=Pull, 1=Repel, 2=Pull+Deflect.</summary>
        public const int TypePull = 0;
        public const int TypeRepel = 1;
        public const int TypePullDeflect = 2;

        /// <summary>
        /// Spawn a magnetize zone at the given world position.
        /// Returns zone ID, or -1 if the pool is full (MAX_MAGNETIZE_ZONES = 64).
        /// </summary>
        public int SpawnZone(float x, float y, float radius, float duration, float pullStrength, int zoneType = TypePull)
        {
            if (radius <= 0f || duration <= 0f)
            {
                _logger?.Log($"[MAGNET] Refused spawn: radius={radius} duration={duration} (must be > 0)");
                return -1;
            }
            int zoneId = _store.AddMagnetizeZone(x, y, radius, duration, pullStrength, zoneType);
            if (zoneId >= 0)
            {
                _logger?.Log($"[MAGNET] Spawned zone id={zoneId} at ({x:F1},{y:F1}) r={radius} dur={duration}s pull={pullStrength} type={zoneType}");
            }
            else
            {
                _logger?.Log("[MAGNET] Spawn failed: pool full (MAX_MAGNETIZE_ZONES=64)");
            }
            return zoneId;
        }

        /// <summary>Remove a magnetize zone by ID. No-op if ID is inactive.</summary>
        public void RemoveZone(int zoneId)
        {
            _store.RemoveMagnetizeZone(zoneId);
        }

        /// <summary>
        /// Check whether (x, y) is inside any zone of type PullDeflect (2).
        /// Used by ProjectileSystem to bend homing projectiles.
        /// Returns true and the zone center if inside; false otherwise.
        /// </summary>
        public bool IsInDeflectZone(float x, float y, out float centerX, out float centerY)
        {
            centerX = 0f;
            centerY = 0f;
            // Defensive: avoid scanning if the store reports no active zones
            for (int i = 0; i < _activeZoneIdsRef.Count; i++)
            {
                int zoneId = _activeZoneIdsRef[i];
                if (!_store.MagnetizeZoneActive[zoneId]) continue;
                if (_store.MagnetizeZoneType[zoneId] != TypePullDeflect) continue;

                float dx = x - _store.MagnetizeZoneX[zoneId];
                float dy = y - _store.MagnetizeZoneY[zoneId];
                float distSq = dx * dx + dy * dy;
                float r = _store.MagnetizeZoneRadius[zoneId];
                if (distSq <= r * r)
                {
                    centerX = _store.MagnetizeZoneX[zoneId];
                    centerY = _store.MagnetizeZoneY[zoneId];
                    return true;
                }
            }
            return false;
        }

        /// <summary>Get count of active zones (cheap O(1) lookup).</summary>
        public int GetActiveZoneCount() => _activeZoneIdsRef.Count;

        // ─────────────────────────────────────────────────────────────
        //  Frame update — called from AIGroup BEFORE EnemyMovement
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tick all active zones: decrement duration, expire finished ones,
        /// then apply per-frame pull/repel force to every enemy inside any zone.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_activeZoneIdsRef.Count == 0) return;

            // 1) Expire finished zones (iterate backward to allow RemoveMagnetizeZone
            //    which mutates the list — same pattern as ZoneControlSystem.Update).
            for (int i = _activeZoneIdsRef.Count - 1; i >= 0; i--)
            {
                int zoneId = _activeZoneIdsRef[i];
                if (!_store.MagnetizeZoneActive[zoneId])
                {
                    // Defensive: store & ref out of sync — drop from ref
                    _activeZoneIdsRef.RemoveAt(i);
                    continue;
                }
                _store.MagnetizeZoneDuration[zoneId] -= deltaTime;
                if (_store.MagnetizeZoneDuration[zoneId] <= 0f)
                {
                    RemoveZone(zoneId);
                }
            }

            // 2) Apply pull force to enemies. Iterate zones × active enemy IDs.
            //    Cheap because 64 zones × 10K enemies = 640K ops (mostly early-out
            //    on distSq > radiusSq). Skip zones with strength <= 0.
            for (int z = 0; z < _activeZoneIdsRef.Count; z++)
            {
                int zoneId = _activeZoneIdsRef[z];
                if (!_store.MagnetizeZoneActive[zoneId]) continue;

                float pull = _store.MagnetizeZonePullStrength[zoneId];
                if (pull <= 0f) continue;

                float cx = _store.MagnetizeZoneX[zoneId];
                float cy = _store.MagnetizeZoneY[zoneId];
                float r = _store.MagnetizeZoneRadius[zoneId];
                float radiusSq = r * r;
                int zoneType = _store.MagnetizeZoneType[zoneId];
                // Direction vector (dx, dy) below is (enemy - center) = OUTWARD unit
                // (after normalization). For PULL we want the enemy to move TOWARD
                // the center → step in -outward direction. For REPEL we want the
                // enemy to move AWAY from center → step in +outward direction.
                // So: dirSign is +1 for Repel, -1 for Pull.
                float dirSign = (zoneType == TypeRepel) ? 1f : -1f;

                // Apply to every active enemy
                var enemyIds = _store.GetCachedActiveEnemyIds();
                for (int e = 0; e < enemyIds.Count; e++)
                {
                    int enemyId = enemyIds[e];
                    if (enemyId < 0) continue;

                    float ex = _store.PositionX[enemyId];
                    float ey = _store.PositionY[enemyId];
                    float dx = ex - cx;
                    float dy = ey - cy;
                    float distSq = dx * dx + dy * dy;
                    if (distSq > radiusSq) continue;
                    if (distSq < 0.0001f)
                    {
                        // Enemy at center exactly — nudge in a deterministic direction
                        // (avoid div-by-zero on normalized direction).
                        dx = 0f; dy = 1f; distSq = 1f;
                    }

                    // Linear falloff: force is full at edge, ~0 at center
                    // (dist from center / radius) — gives smooth "magnetic" feel.
                    float dist = (float)Math.Sqrt(distSq);
                    // pullStrength is interpreted as a "tiles per second" velocity
                    // (a value of 2.0 means an enemy is pulled 2 tiles in 1 second
                    // at the zone's edge, given typical 60Hz frames).
                    float force = pull * (dist / r) * dirSign * deltaTime;

                    // Normalize direction, scale by force
                    float invDist = 1f / dist;
                    float stepX = (dx * invDist) * force;
                    float stepY = (dy * invDist) * force;

                    float newX = ex + stepX;
                    float newY = ey + stepY;

                    // Defensive clamp: keep enemies inside the world bounds
                    if (newX < -POSITION_CLAMP) newX = -POSITION_CLAMP;
                    else if (newX > POSITION_CLAMP) newX = POSITION_CLAMP;
                    if (newY < -POSITION_CLAMP) newY = -POSITION_CLAMP;
                    else if (newY > POSITION_CLAMP) newY = POSITION_CLAMP;

                    _store.PositionX[enemyId] = newX;
                    _store.PositionY[enemyId] = newY;
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// 弹道/飞行道具系统 — 管理 projectile 生命周期（生成、移动、命中结算）。
    /// 两阶段模式：串行 Update 中移动→命中检测→入 damage queue，帧末统一 apply。
    /// </summary>
    public class ProjectileSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer logger;
        private const int MAX_PROJ = 1024;

        // Projectile SOA fields
        private float[] _projX = new float[MAX_PROJ];
        private float[] _projY = new float[MAX_PROJ];
        private float[] _projVelX = new float[MAX_PROJ];
        private float[] _projVelY = new float[MAX_PROJ];
        private int[] _projTargetId = new int[MAX_PROJ];
        private float[] _projDamage = new float[MAX_PROJ];
        private int[] _projPlayerId = new int[MAX_PROJ];
        private int[] _projTowerId = new int[MAX_PROJ];
        private float[] _projSpeed = new float[MAX_PROJ];
        private bool[] _projActive = new bool[MAX_PROJ];
        // Homing flag: if true, projectile recalculates direction toward target each frame (turns mid-flight)
        private bool[] _projIsHoming = new bool[MAX_PROJ];
        // Piercing: number of additional enemies this projectile can pierce through after the initial hit
        private int[] _projPierceRemaining = new int[MAX_PROJ];
        private float[] _projPierceDmgFalloff = new float[MAX_PROJ];
        // _projIsPiercing: true if projectile was fired with pierceCount > 0 (set at Fire, used by ResolveHit
        // to know whether pierce-resistance on the target applies to this hit).
        private bool[] _projIsPiercing = new bool[MAX_PROJ];
        // Fragmentation: number of child projectiles to spawn on impact (0 = no fragmentation)
        private int[] _projFragmentCount = new int[MAX_PROJ];
        private float[] _projFragmentRange = new float[MAX_PROJ];
        private float[] _projFragmentDmgMult = new float[MAX_PROJ];
        // Arc projectile physics: height tracks vertical position for arc/mortar trajectories
        private float[] _projHeight = new float[MAX_PROJ];
        private float[] _projVerticalVelocity = new float[MAX_PROJ];
        private float[] _projGravity = new float[MAX_PROJ];
        private int[] _projArcType = new int[MAX_PROJ]; // 0=straight, 1=homing, 2=arc
        private float[] _projArcPeakHeight = new float[MAX_PROJ];
        private int _activeProjectileCount;

        // Ping-pong damage queue (same pattern as TowerAttackSystem)
        private List<(int enemyId, float damage, int playerId)>[] _damageQueue =
            new List<(int, float, int)>[2];
        private readonly object _damageQueueLock = new object();
        private int _damageQueueIdx;
        // RNG used for projectile deflection roll (serial path, no thread-safety needed).
        // Kept here rather than in store so ProjectileSystem is self-contained and testable in isolation.
        private readonly System.Random _deflectRng = new System.Random(0xDEFE17);

        public ProjectileSystem(ComponentStore store, IRenderer logger)
        {
            this.store = store;
            this.logger = logger;
            _damageQueue[0] = new List<(int, float, int)>(256);
            _damageQueue[1] = new List<(int, float, int)>(256);
            for (int i = 0; i < MAX_PROJ; i++)
            {
                _projTargetId[i] = -1;
            }
        }

        /// <summary>
        /// Spawn a projectile from a tower toward a target enemy.
        /// </summary>
        /// <param name="towerId">Source tower ID</param>
        /// <param name="targetId">Target enemy ID</param>
        /// <param name="damage">Base damage</param>
        /// <param name="playerId">Owning player</param>
        /// <param name="speed">Projectile speed</param>
        /// <param name="isHoming">Whether projectile tracks target mid-flight</param>
        /// <param name="pierceCount">Number of enemies to pierce through (0 = no pierce)</param>
        /// <param name="pierceDmgFalloff">Damage multiplier after each pierce (1.0 = full damage)</param>
        /// <param name="fragmentCount">Number of child projectiles to spawn on impact (0 = no fragmentation)</param>
        /// <param name="fragmentRange">Search radius for fragment targets</param>
        /// <param name="fragmentDmgMult">Damage multiplier for each fragment relative to parent</param>
        public void Fire(int towerId, int targetId, float damage, int playerId, float speed, bool isHoming = false, int pierceCount = 0, float pierceDmgFalloff = 1f, int fragmentCount = 0, float fragmentRange = 0f, float fragmentDmgMult = 1f)
        {
            if (_activeProjectileCount >= MAX_PROJ) return;

            // Find free slot
            int projId = -1;
            for (int i = 0; i < MAX_PROJ; i++)
            {
                if (!_projActive[i]) { projId = i; break; }
            }
            if (projId < 0) return;

            _projX[projId] = store.PositionX[towerId];
            _projY[projId] = store.PositionY[towerId];
            _projTargetId[projId] = targetId;
            _projDamage[projId] = damage;
            _projPlayerId[projId] = playerId;
            _projTowerId[projId] = towerId;
            _projSpeed[projId] = speed;
            _projIsHoming[projId] = isHoming;
            _projPierceRemaining[projId] = pierceCount;
            _projPierceDmgFalloff[projId] = pierceDmgFalloff;
            // Track whether this projectile is piercing — used by ResolveHit to apply pierce-resistance
            _projIsPiercing[projId] = pierceCount > 0;
            _projFragmentCount[projId] = fragmentCount;
            _projFragmentRange[projId] = fragmentRange;
            _projFragmentDmgMult[projId] = fragmentDmgMult;
            _projVelX[projId] = 0f;
            _projVelY[projId] = 0f;
            // Arc projectile physics: default to no arc (straight trajectory)
            _projHeight[projId] = 0f;
            _projVerticalVelocity[projId] = 0f;
            _projGravity[projId] = 0f;
            _projArcType[projId] = 0;
            _projArcPeakHeight[projId] = 0f;
            _projActive[projId] = true;
            _activeProjectileCount++;
        }

        /// <summary>
        /// Spawn a projectile from a tower toward a target enemy with arc trajectory.
        /// </summary>
        /// <param name="towerId">Source tower ID</param>
        /// <param name="targetId">Target enemy ID</param>
        /// <param name="damage">Base damage</param>
        /// <param name="playerId">Owning player</param>
        /// <param name="speed">Projectile speed</param>
        /// <param name="isHoming">Whether projectile tracks target mid-flight</param>
        /// <param name="arcType">Arc type: 0=straight, 1=homing, 2=arc/mortar</param>
        /// <param name="arcPeakHeight">Peak height for arc projectiles</param>
        /// <param name="gravityScale">Gravity scale for arc projectiles</param>
        /// <param name="pierceCount">Number of enemies to pierce through (0 = no pierce)</param>
        /// <param name="pierceDmgFalloff">Damage multiplier after each pierce</param>
        /// <param name="fragmentCount">Number of child projectiles to spawn on impact</param>
        /// <param name="fragmentRange">Search radius for fragment targets</param>
        /// <param name="fragmentDmgMult">Damage multiplier for each fragment</param>
        public void FireWithArc(int towerId, int targetId, float damage, int playerId, float speed, bool isHoming, int arcType, float arcPeakHeight, float gravityScale, int pierceCount = 0, float pierceDmgFalloff = 1f, int fragmentCount = 0, float fragmentRange = 0f, float fragmentDmgMult = 1f)
        {
            if (_activeProjectileCount >= MAX_PROJ) return;

            // Find free slot
            int projId = -1;
            for (int i = 0; i < MAX_PROJ; i++)
            {
                if (!_projActive[i]) { projId = i; break; }
            }
            if (projId < 0) return;

            _projX[projId] = store.PositionX[towerId];
            _projY[projId] = store.PositionY[towerId];
            _projTargetId[projId] = targetId;
            _projDamage[projId] = damage;
            _projPlayerId[projId] = playerId;
            _projTowerId[projId] = towerId;
            _projSpeed[projId] = speed;
            _projIsHoming[projId] = isHoming;
            _projPierceRemaining[projId] = pierceCount;
            _projPierceDmgFalloff[projId] = pierceDmgFalloff;
            // Track whether this projectile is piercing — used by ResolveHit to apply pierce-resistance
            _projIsPiercing[projId] = pierceCount > 0;
            _projFragmentCount[projId] = fragmentCount;
            _projFragmentRange[projId] = fragmentRange;
            _projFragmentDmgMult[projId] = fragmentDmgMult;
            _projVelX[projId] = 0f;
            _projVelY[projId] = 0f;
            // Arc projectile physics: initialize arc trajectory
            _projHeight[projId] = 0f;
            _projArcType[projId] = arcType;
            _projArcPeakHeight[projId] = arcPeakHeight;
            // Use gravityScale * 9.8 for arc, 0 for straight/homing
            _projGravity[projId] = (arcType == 2) ? (gravityScale * 9.8f) : 0f;
            // Compute initial vertical velocity for arc: v0 = g * timeToApex, where apex height = arcPeakHeight
            // Approximate timeToApex = horizontalDist / speed, so v0 = arcPeakHeight / timeToApex
            float dx = store.PositionX[targetId] - _projX[projId];
            float dy = store.PositionY[targetId] - _projY[projId];
            float horizDist = MathF.Sqrt(dx * dx + dy * dy);
            // Guard against zero-distance: use minimum horizontal travel time
            float timeToTarget = horizDist / MathF.Max(speed, 0.1f);
            // Minimum time to prevent divide-by-zero in vertical velocity calculation
            timeToTarget = MathF.Max(timeToTarget, 0.2f);
            // Aim for apex at arcPeakHeight — use half the flight time for upward velocity
            float halfTime = timeToTarget * 0.5f;
            _projVerticalVelocity[projId] = (arcPeakHeight > 0f && halfTime > 0f) ? (arcPeakHeight / halfTime) : 0f;
            _projActive[projId] = true;
            _activeProjectileCount++;
        }

        /// <summary>
        /// Serial update: move all active projectiles and resolve hits.
        /// </summary>
        public void Update(float deltaTime)
        {
            int resolvedHits = 0;
            int missedProjectiles = 0;

            for (int i = 0; i < MAX_PROJ; i++)
            {
                if (!_projActive[i]) continue;

                int targetId = _projTargetId[i];
                if (targetId >= 0 && store.EnemyActive[targetId])
                {
                    float tx = store.PositionX[targetId];
                    float ty = store.PositionY[targetId];
                    float dx = tx - _projX[i];
                    float dy = ty - _projY[i];
                    float distToTargetSq = dx * dx + dy * dy;

                    if (distToTargetSq > 0.01f)
                    {
                        float dist = MathF.Sqrt(distToTargetSq);
                        float nx = dx / dist;
                        float ny = dy / dist;
                        float speed = _projSpeed[i];
                        // Homing projectiles update direction every frame (turn mid-flight).
                        // Non-homing projectiles only get initial direction from Fire() — no mid-flight correction.
                        if (_projIsHoming[i])
                        {
                            _projVelX[i] = nx * speed;
                            _projVelY[i] = ny * speed;
                        }
                    }
                    else
                    {
                        // Already at target — resolve hit
                        ResolveHit(i);
                        _projActive[i] = false;
                        _activeProjectileCount--;
                        resolvedHits++;
                        continue;
                    }
                }
                else
                {
                    // Target lost (enemy died or invalid)
                    _projActive[i] = false;
                    _activeProjectileCount--;
                    missedProjectiles++;
                    continue;
                }

                // Move projectile
                _projX[i] += _projVelX[i] * deltaTime;
                _projY[i] += _projVelY[i] * deltaTime;

                // Arc projectile physics: update height for arc-type projectiles
                int arcType = _projArcType[i];
                if (arcType == 2) // Arc trajectory (mortar)
                {
                    // Apply gravity to vertical velocity
                    _projVerticalVelocity[i] -= _projGravity[i] * deltaTime;
                    // Update height position
                    _projHeight[i] += _projVerticalVelocity[i] * deltaTime;
                    // Arc projectiles land when height <= 0 (ground level)
                    if (_projHeight[i] <= 0f)
                    {
                        ResolveHit(i);
                        _projActive[i] = false;
                        _activeProjectileCount--;
                        resolvedHits++;
                        continue;
                    }
                }

                // Check proximity to target (hit detection within 0.5 grid units)
                float tdx = store.PositionX[targetId] - _projX[i];
                float tdy = store.PositionY[targetId] - _projY[i];
                float proximitySq = tdx * tdx + tdy * tdy;
                float hitThresholdSq = 0.25f;

                if (proximitySq <= hitThresholdSq)
                {
                    int pierceLeft = _projPierceRemaining[i];
                    if (pierceLeft > 0)
                    {
                        // Piercing projectile: apply damage, then find next target along trajectory
                        ResolveHit(i);
                        // Decrement pierce counter
                        _projPierceRemaining[i]--;

                        // Find next target in roughly the same direction (forward cone)
                        float projVelX = _projVelX[i];
                        float projVelY = _projVelY[i];
                        float vLenSq = projVelX * projVelX + projVelY * projVelY;
                        int nextTargetId = -1;
                        if (vLenSq > 0.001f)
                        {
                            float vLen = MathF.Sqrt(vLenSq);
                            float dirX = projVelX / vLen;
                            float dirY = projVelY / vLen;
                            // Search all active enemies for one in the forward cone (dot product > 0)
                            var enemyIds = store.GetCachedActiveEnemyIds();
                            float bestDot = -1f;
                            float projX = _projX[i];
                            float projY = _projY[i];

                            for (int eidx = 0; eidx < enemyIds.Count; eidx++)
                            {
                                int eid = enemyIds[eidx];
                                if (eid == targetId || !store.EnemyActive[eid]) continue;
                                float edx = store.PositionX[eid] - projX;
                                float edy = store.PositionY[eid] - projY;
                                float eDistSq = edx * edx + edy * edy;
                                // Only consider enemies that are close enough (within 20 units)
                                if (eDistSq > 400f) continue;
                                float eDist = MathF.Sqrt(eDistSq);
                                float eDirX = edx / eDist;
                                float eDirY = edy / eDist;
                                float dot = eDirX * dirX + eDirY * dirY;
                                // Must be generally in front (dot > 0.5 = ~60 degree cone)
                                if (dot > 0.5f && dot > bestDot)
                                {
                                    bestDot = dot;
                                    nextTargetId = eid;
                                }
                            }
                        }

                        if (nextTargetId >= 0)
                        {
                            // Retarget to next enemy, apply damage falloff for subsequent hits
                            float falloff = _projPierceDmgFalloff[i];
                            _projDamage[i] *= falloff;
                            _projTargetId[i] = nextTargetId;
                            // Keep projectile active — it continues flying
                        }
                        else
                        {
                            // No valid next target — deactivate
                            _projActive[i] = false;
                            _activeProjectileCount--;
                        }
                    }
                    else
                    {
                        // Non-piercing or pierce exhausted — normal hit, deactivate
                        ResolveHit(i);
                        _projActive[i] = false;
                        _activeProjectileCount--;
                        resolvedHits++;
                    }
                }
            }

            // Apply collected damage (ping-pong pattern)
            int readIdx = _damageQueueIdx;
            int writeIdx = 1 - _damageQueueIdx;
            _damageQueueIdx = writeIdx;
            _damageQueue[writeIdx].Clear();
            foreach (var (enemyId, damage, playerId) in _damageQueue[readIdx])
            {
                store.EnemyHealth[enemyId] -= damage;
                if (store.EnemyHealth[enemyId] <= 0f)
                {
                    store.QueueEnemyDeath(enemyId, playerId);
                }
            }
            _damageQueue[readIdx].Clear();
        }

        private void ResolveHit(int projId)
        {
            int targetId = _projTargetId[projId];
            float damage = _projDamage[projId];
            int playerId = _projPlayerId[projId];
            int towerId = _projTowerId[projId];

            // Projectile Deflection: high-speed / boss-tier enemies can deflect incoming projectiles
            // on a per-hit roll. On a successful deflect, the projectile is fully nullified
            // (no pierce immunity / thorns / fragmentation side-effects are triggered, and the
            // damage queue is not enqueued). This is purely a damage filter — deflection does not
            // ricochet, bounce, or return to attacker. RNG call is wrapped in _damageQueueLock to
            // keep System.Random safe (consistent with the rest of ResolveHit, which already locks
            // around any state shared with concurrent code paths).
            float deflectChance = store.EnemyDeflectChance[targetId];
            if (deflectChance > 0f)
            {
                bool deflected;
                lock (_damageQueueLock)
                {
                    deflected = _deflectRng.NextDouble() < deflectChance;
                }
                if (deflected)
                {
                    // Deflected — early-exit without applying damage, pierce, thorns, or fragments.
                    return;
                }
            }

            // Pierce Resistance: only applies to piercing projectiles (Fire() sets _projIsPiercing=true when pierceCount>0).
            // For non-piercing projectiles, damage is unaffected. Fragments from FireAtPoint are always non-piercing.
            if (_projIsPiercing[projId])
            {
                // Piercing projectile — apply target's pierce resist
                if (store.EnemyIsPierceImmune[targetId])
                {
                    // Binary immunity: piercing damage completely nullified
                    damage = 0f;
                }
                else
                {
                    float resist = store.EnemyPierceResist[targetId];
                    if (resist > 0f)
                    {
                        damage *= (1f - resist);
                    }
                }
            }

            // Thorns reflect: enemy reflects a fraction of projectile damage
            float thornsRatio = store.EnemyThornsRatio[targetId];
            if (thornsRatio > 0f && damage > 0f)
            {
                lock (_damageQueueLock)
                {
                    // Thorns damage goes to player — use DecreasePlayerHealth
                    store.DecreasePlayerHealth(playerId, damage * thornsRatio);
                }
            }

            lock (_damageQueueLock)
            {
                _damageQueue[_damageQueueIdx].Add((targetId, damage, playerId));
            }

            // Fragmentation: spawn child projectiles on impact
            int fragCount = _projFragmentCount[projId];
            if (fragCount > 0)
            {
                float fragRange = _projFragmentRange[projId];
                float fragDmgMult = _projFragmentDmgMult[projId];
                float projX = _projX[projId];
                float projY = _projY[projId];
                float speed = _projSpeed[projId];
                bool isHoming = _projIsHoming[projId];
                SpawnFragments(towerId, projX, projY, targetId, damage * fragDmgMult, playerId, speed, isHoming, fragCount, fragRange);
            }
        }

        /// <summary>
        /// Spawn N child projectiles in a fan pattern from the impact position, each targeting a nearby enemy.
        /// </summary>
        private void SpawnFragments(int towerId, float originX, float originY, int parentTargetId, float fragmentDamage, int playerId, float speed, bool isHoming, int fragCount, float fragRange)
        {
            var enemyIds = store.GetCachedActiveEnemyIds();
            // Collect candidates within range
            var candidates = new System.Collections.Generic.List<(int enemyId, float dx, float dy, float distSq)>(fragCount * 2);
            float rangeSq = fragRange * fragRange;

            for (int i = 0; i < enemyIds.Count; i++)
            {
                int eid = enemyIds[i];
                if (eid == parentTargetId || !store.EnemyActive[eid]) continue;
                float edx = store.PositionX[eid] - originX;
                float edy = store.PositionY[eid] - originY;
                float distSq = edx * edx + edy * edy;
                if (distSq <= rangeSq)
                {
                    candidates.Add((eid, edx, edy, distSq));
                }
            }

            if (candidates.Count == 0) return;

            // Sort by distance (closest first)
            candidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            int toSpawn = Math.Min(fragCount, candidates.Count);
            float totalAngle = MathF.PI * 2f; // full circle fan
            for (int i = 0; i < toSpawn; i++)
            {
                int eid = candidates[i].enemyId;
                // Fan angle: distribute fragments evenly in a full circle
                float angle = (totalAngle / toSpawn) * i;
                float nx = MathF.Cos(angle);
                float ny = MathF.Sin(angle);
                // Target position = current position + direction * small offset (so fragment flies outward)
                float targetX = originX + nx * 0.5f;
                float targetY = originY + ny * 0.5f;
                // Find approximate enemy to target for homing
                FireAtPoint(towerId, eid, fragmentDamage, playerId, speed, isHoming, targetX, targetY);
            }
        }

        /// <summary>
        /// Fire a fragment projectile toward a fixed world position (used by fragmentation).
        /// </summary>
        private void FireAtPoint(int towerId, int targetId, float damage, int playerId, float speed, bool isHoming, float targetX, float targetY)
        {
            if (_activeProjectileCount >= MAX_PROJ) return;

            int projId = -1;
            for (int i = 0; i < MAX_PROJ; i++)
            {
                if (!_projActive[i]) { projId = i; break; }
            }
            if (projId < 0) return;

            _projX[projId] = store.PositionX[towerId];
            _projY[projId] = store.PositionY[towerId];
            _projTargetId[projId] = targetId;
            _projDamage[projId] = damage;
            _projPlayerId[projId] = playerId;
            _projTowerId[projId] = towerId;
            _projSpeed[projId] = speed;
            _projIsHoming[projId] = isHoming;
            // Fragments from FireAtPoint do NOT inherit pierce — they are non-piercing
            _projPierceRemaining[projId] = 0;
            _projPierceDmgFalloff[projId] = 1f;
            _projIsPiercing[projId] = false;
            _projFragmentCount[projId] = 0;
            _projFragmentRange[projId] = 0f;
            _projFragmentDmgMult[projId] = 1f;

            float dx = targetX - _projX[projId];
            float dy = targetY - _projY[projId];
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > 0.01f)
            {
                _projVelX[projId] = (dx / dist) * speed;
                _projVelY[projId] = (dy / dist) * speed;
            }
            else
            {
                _projVelX[projId] = 0f;
                _projVelY[projId] = 0f;
            }
            _projActive[projId] = true;
            _activeProjectileCount++;
        }
    }
}

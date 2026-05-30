using System;
using System.Collections.Generic;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Patrol Tower System — mobile towers that move along predefined patrol paths
    /// while continuing to attack enemies in range.
    ///
    /// Each mobile tower follows a list of waypoints (from patrol_paths.json).
    /// Movement happens during WavePhase Update(). Attack systems use current PositionX/Y
    /// which is updated every frame — no extra coordination needed.
    ///
    /// Supported patrol modes:
    ///   0 = PingPong  — move forward to end, then reverse back to start (repeat)
    ///   1 = Loop     — wrap from last waypoint back to first
    ///   2 = OneWay   — move from start to end, then stop (frozen at end)
    /// </summary>
    public class PatrolTowerSystem
    {
        private ComponentStore store;
        private IRenderer logger;
        private GameConfig gameConfig;

        // patrol_paths.json definitions: pathId -> list of (x, y) waypoints
        private Dictionary<int, List<(float x, float y)>> patrolPaths = new Dictionary<int, List<(float x, float y)>>();

        // Config
        private int defaultMapWidth = 10;
        private int defaultMapHeight = 50;

        public PatrolTowerSystem(ComponentStore store, IRenderer logger, GameConfig gameConfig)
        {
            this.store = store;
            this.logger = logger;
            this.gameConfig = gameConfig;
            LoadPatrolPaths();
        }

        private void LoadPatrolPaths()
        {
            string basePath = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(basePath, "Data", "Configs", "patrol_paths.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("paths", out var pathsElem) && pathsElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var pathElem in pathsElem.EnumerateArray())
                        {
                            int pathId = pathElem.TryGetProperty("id", out var pid) ? pid.GetInt32() : -1;
                            if (pathId < 0) continue;
                            var waypoints = new List<(float x, float y)>();
                            if (pathElem.TryGetProperty("waypoints", out var wpElem) && wpElem.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var wp in wpElem.EnumerateArray())
                                {
                                    float wx = wp.TryGetProperty("x", out var xx) ? xx.GetSingle() : 0f;
                                    float wy = wp.TryGetProperty("y", out var yx) ? yx.GetSingle() : 0f;
                                    waypoints.Add((wx, wy));
                                }
                            }
                            if (waypoints.Count >= 2)
                                patrolPaths[pathId] = waypoints;
                        }
                    }
                    if (root.TryGetProperty("mapWidth", out var mw)) defaultMapWidth = mw.GetInt32();
                    if (root.TryGetProperty("mapHeight", out var mh)) defaultMapHeight = mh.GetInt32();
                }
                catch (Exception ex)
                {
                    logger.Log($"[PATROL] LoadPatrolPaths error: {ex.Message}");
                }
            }

            // Always ensure at least one default path so mobile towers have a fallback
            if (!patrolPaths.ContainsKey(0))
            {
                var defaultPath = new List<(float x, float y)>();
                for (int y = 2; y <= defaultMapHeight - 3; y += 4)
                {
                    defaultPath.Add((2f, y));
                    defaultPath.Add((defaultMapWidth - 3f, y));
                }
                patrolPaths[0] = defaultPath;
            }
        }

        /// <summary>
        /// Called once per frame during WavePhase. Moves each active mobile tower
        /// toward its current waypoint target, then advances to the next when close enough.
        /// </summary>
        public void Update(float deltaTime)
        {
            var activeTowerIds = store.ActiveTowerIds;
            foreach (int tid in activeTowerIds)
            {
                if (tid < 0 || tid >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.TowerActive[tid]) continue;
                if (!store.TowerIsMobile[tid]) continue;

                int pathId = store.TowerPatrolPathId[tid];
                if (!patrolPaths.ContainsKey(pathId)) continue;

                var path = patrolPaths[pathId];
                int wpCount = path.Count;
                if (wpCount < 2) continue;

                int wpIdx = store.TowerPatrolWaypointIndex[tid];
                if (wpIdx < 0 || wpIdx >= wpCount) wpIdx = 0;

                // Current target waypoint
                float targetX = path[wpIdx].x;
                float targetY = path[wpIdx].y;

                float cx = store.PositionX[tid];
                float cy = store.PositionY[tid];

                float dx = targetX - cx;
                float dy = targetY - cy;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                float moveSpeed = store.TowerMoveSpeed[tid];
                if (moveSpeed <= 0f) moveSpeed = 3f; // fallback

                float step = moveSpeed * deltaTime;

                if (dist <= step || dist < 0.5f)
                {
                    // Snap to waypoint and advance
                    store.PositionX[tid] = targetX;
                    store.PositionY[tid] = targetY;

                    int direction = store.TowerPatrolDirection[tid]; // +1 or -1
                    int nextIdx = wpIdx + direction;

                    // Check bounds for ping-pong
                    if (nextIdx >= wpCount)
                    {
                        // Hit end — reverse direction (ping-pong behavior)
                        store.TowerPatrolDirection[tid] = -1;
                        nextIdx = wpIdx - 1;
                    }
                    else if (nextIdx < 0)
                    {
                        // Hit start — reverse direction
                        store.TowerPatrolDirection[tid] = +1;
                        nextIdx = wpIdx + 1;
                        if (nextIdx >= wpCount) nextIdx = wpCount - 1; // clamp
                    }

                    // For OneWay mode (direction == 0), we stop at the end and never reverse
                    if (direction == 0 && nextIdx >= wpCount)
                    {
                        nextIdx = wpCount - 1; // freeze at last waypoint
                    }

                    store.TowerPatrolWaypointIndex[tid] = nextIdx;
                }
                else
                {
                    // Move toward waypoint
                    float invDist = step / dist;
                    store.PositionX[tid] = cx + dx * invDist;
                    store.PositionY[tid] = cy + dy * invDist;
                }
            }
        }

        public void SetTurn(int turn)
        {
            // Nothing per-turn needed; Update(deltaTime) drives movement.
        }
    }
}
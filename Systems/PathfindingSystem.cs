using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Path / waypoint system for enemy navigation.
    /// Manages path tables and assigns each enemy a current waypoint index.
    /// Replaces the simple direction=-1 Y-axis movement with waypoint following.
    /// </summary>
    public class PathfindingSystem
    {
        private Core.ComponentStore store;

        /// <summary>
        /// A single waypoint in a path.
        /// </summary>
        public struct Waypoint
        {
            public readonly float X;
            public readonly float Y;
            public Waypoint(float x, float y) { X = x; Y = y; }
        }

        /// <summary>
        /// A named path composed of ordered waypoints.
        /// </summary>
        public struct Path
        {
            public readonly string Id;
            public readonly List<Waypoint> Waypoints;
            public Path(string id, List<Waypoint> waypoints)
            {
                Id = id;
                Waypoints = waypoints;
            }
        }

        private readonly Dictionary<string, Path> _paths = new Dictionary<string, Path>();
        private List<int> _activeEnemyList;

        public PathfindingSystem(Core.ComponentStore store)
        {
            this.store = store;
            InitDefaultPaths();
        }

        private void InitDefaultPaths()
        {
            // Default path: enemies enter at top (y=19) and move toward bottom (y=0)
            // Straight Y-axis path (replicates existing behavior)
            var straightPath = new List<Waypoint>
            {
                new Waypoint(4f, 19f),
                new Waypoint(4f, 15f),
                new Waypoint(4f, 10f),
                new Waypoint(4f, 5f),
                new Waypoint(4f, 0f)
            };
            _paths["default"] = new Path("default", straightPath);

            // Path with a branch fork: enemies choose between left and right routes
            var leftBranch = new List<Waypoint>
            {
                new Waypoint(2f, 19f),
                new Waypoint(2f, 12f),
                new Waypoint(2f, 6f),
                new Waypoint(2f, 0f)
            };
            _paths["fork_left"] = new Path("fork_left", leftBranch);

            var rightBranch = new List<Waypoint>
            {
                new Waypoint(7f, 19f),
                new Waypoint(7f, 12f),
                new Waypoint(7f, 6f),
                new Waypoint(7f, 0f)
            };
            _paths["fork_right"] = new Path("fork_right", rightBranch);

            // Ring / loop path for patrol behavior
            var ringPath = new List<Waypoint>
            {
                new Waypoint(2f, 15f),
                new Waypoint(7f, 15f),
                new Waypoint(7f, 5f),
                new Waypoint(2f, 5f),
                new Waypoint(2f, 15f)
            };
            _paths["ring"] = new Path("ring", ringPath);
        }

        public void SetTurn(int turn)
        {
            _activeEnemyList = store.GetCachedActiveEnemyIds();
            AdvanceWaypoints();
        }

        /// <summary>
        /// Advance each enemy's waypoint index: move toward next node, or mark at-goal if past last.
        /// Called once per turn before EnemyMovementSystem.Update().
        /// </summary>
        private void AdvanceWaypoints()
        {
            var activeEnemyIds = _activeEnemyList;
            if (activeEnemyIds == null) return;

            Parallel.For(0, activeEnemyIds.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                int enemyId = activeEnemyIds[i];
                if (!store.EnemyActive[enemyId]) return;

                int pathId = store.EnemyPathId[enemyId];
                if (pathId < 0) return; // no path assigned, skip

                string pathKey = GetPathKey(pathId);
                if (pathKey == null || !_paths.TryGetValue(pathKey, out Path path) || path.Waypoints.Count == 0)
                    return;

                int nodeIdx = store.EnemyPathNodeIndex[enemyId];
                if (nodeIdx < 0 || nodeIdx >= path.Waypoints.Count) return;

                // Current target waypoint
                Waypoint target = path.Waypoints[nodeIdx];
                float ex = store.PositionX[enemyId];
                float ey = store.PositionY[enemyId];

                // Compute squared distance to current waypoint
                float dx = target.X - ex;
                float dy = target.Y - ey;
                float distSq = dx * dx + dy * dy;
                float arrivalThresholdSq = 0.25f; // 0.5^2 = arrival threshold

                if (distSq <= arrivalThresholdSq)
                {
                    // Arrived at current node — advance to next
                    int nextIdx = nodeIdx + 1;
                    if (nextIdx >= path.Waypoints.Count)
                    {
                        // Past last waypoint — enemy has reached goal (leaks through)
                        store.EnemyPathNodeIndex[enemyId] = -1; // signals "at goal"
                    }
                    else
                    {
                        store.EnemyPathNodeIndex[enemyId] = nextIdx;
                    }
                }
            });
        }

        /// <summary>
        /// Get the direction vector from an enemy to its current target waypoint.
        /// Returns (dx, dy) normalized direction, or (0,-1) if no valid path.
        /// </summary>
        public (float dx, float dy) GetDirectionToNextNode(int enemyId)
        {
            if (!store.EnemyActive[enemyId]) return (0f, -1f);

            int pathId = store.EnemyPathId[enemyId];
            if (pathId < 0) return (0f, -1f);

            string pathKey = GetPathKey(pathId);
            if (pathKey == null || !_paths.TryGetValue(pathKey, out Path path))
                return (0f, -1f);

            int nodeIdx = store.EnemyPathNodeIndex[enemyId];
            if (nodeIdx < 0 || nodeIdx >= path.Waypoints.Count)
                return (0f, -1f);

            Waypoint target = path.Waypoints[nodeIdx];
            float ex = store.PositionX[enemyId];
            float ey = store.PositionY[enemyId];
            float dx = target.X - ex;
            float dy = target.Y - ey;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) return (0f, -1f);
            return (dx / len, dy / len);
        }

        /// <summary>
        /// Get the current waypoint node for an enemy (for visualization/debugging).
        /// Returns null if enemy has no valid path.
        /// </summary>
        public Waypoint? GetCurrentWaypoint(int enemyId)
        {
            if (!store.EnemyActive[enemyId]) return null;

            int pathId = store.EnemyPathId[enemyId];
            if (pathId < 0) return null;

            string pathKey = GetPathKey(pathId);
            if (pathKey == null || !_paths.TryGetValue(pathKey, out Path path))
                return null;

            int nodeIdx = store.EnemyPathNodeIndex[enemyId];
            if (nodeIdx < 0 || nodeIdx >= path.Waypoints.Count)
                return null;

            return path.Waypoints[nodeIdx];
        }

        /// <summary>
        /// Assign an enemy to a path by path ID.
        /// </summary>
        public void AssignPath(int enemyId, int pathId)
        {
            store.EnemyPathId[enemyId] = pathId;
            store.EnemyPathNodeIndex[enemyId] = 0; // start at first waypoint
        }

        /// <summary>
        /// Convert pathId int to path key string.
        /// 0 = default, 1 = fork_left, 2 = fork_right, 3 = ring
        /// </summary>
        private static string GetPathKey(int pathId)
        {
            return pathId switch
            {
                0 => "default",
                1 => "fork_left",
                2 => "fork_right",
                3 => "ring",
                _ => "default"
            };
        }

        /// <summary>
        /// Get total path count.
        /// </summary>
        public int PathCount => _paths.Count;
    }
}
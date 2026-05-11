using System;
using System.Collections.Generic;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Grid-based spatial hash for O(1) range queries.
    /// Divides the map into cells; finding enemies in range N
    /// means only checking the ~9 cells within that range.
    /// </summary>
    public class GridSpatialHash
    {
        private readonly float cellSize;
        private readonly int gridWidth;
        private readonly int gridHeight;

        // Maps (cellX, cellY) → list of entity IDs in that cell
        private Dictionary<(int, int), List<int>> cells = new Dictionary<(int, int), List<int>>();
        // Maps entityId → its current cell (for fast removal)
        private Dictionary<int, (int, int)> entityCells = new Dictionary<int, (int, int)>();

        public GridSpatialHash(float cellSize, int mapWidth, int mapHeight)
        {
            this.cellSize = cellSize;
            this.gridWidth = (int)Math.Ceiling(mapWidth / cellSize);
            this.gridHeight = (int)Math.Ceiling(mapHeight / cellSize);
        }

        /// <summary>
        /// Register an entity at position (x, y).
        /// </summary>
        public void Add(int entityId, float x, float y)
        {
            var cell = WorldToCell(x, y);
            if (!cells.TryGetValue(cell, out var list))
            {
                list = new List<int>();
                cells[cell] = list;
            }
            list.Add(entityId);
            entityCells[entityId] = cell;
        }

        /// <summary>
        /// Update an entity's position.
        /// </summary>
        public void Move(int entityId, float x, float y)
        {
            var newCell = WorldToCell(x, y);
            if (entityCells.TryGetValue(entityId, out var oldCell))
            {
                if (oldCell == newCell) return; // Still in same cell

                // Remove from old cell
                if (cells.TryGetValue(oldCell, out var oldList))
                    oldList.Remove(entityId);

                // Add to new cell
                if (!cells.TryGetValue(newCell, out var newList))
                {
                    newList = new List<int>();
                    cells[newCell] = newList;
                }
                newList.Add(entityId);
                entityCells[entityId] = newCell;
            }
            else
            {
                Add(entityId, x, y);
            }
        }

        /// <summary>
        /// Remove an entity from the hash.
        /// </summary>
        public void Remove(int entityId)
        {
            if (entityCells.TryGetValue(entityId, out var cell))
            {
                if (cells.TryGetValue(cell, out var list))
                    list.Remove(entityId);
                entityCells.Remove(entityId);
            }
        }

        /// <summary>
        /// Get all entity IDs within squared distance rangeSq of (x, y).
        /// Uses Manhattan-ish cell scan — checks all cells in the bounding box,
        /// then validates with squared distance (no Math.Sqrt needed).
        /// </summary>
        public List<int> GetInRange(float x, float y, float range)
        {
            int rangeCells = (int)Math.Ceiling(range / cellSize);
            int cx = CellX(x);
            int cy = CellY(y);
            var result = new List<int>();

            for (int dx = -rangeCells; dx <= rangeCells; dx++)
            {
                for (int dy = -rangeCells; dy <= rangeCells; dy++)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if (nx < 0 || nx >= gridWidth || ny < 0 || ny >= gridHeight) continue;

                    if (cells.TryGetValue((nx, ny), out var list))
                    {
                        foreach (var eid in list)
                            result.Add(eid);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Get entity IDs within range, filtered to those actually within squared distance.
        /// </summary>
        public List<int> GetInRangeFiltered(float x, float y, float range, Dictionary<int, float> posX, Dictionary<int, float> posY)
        {
            int rangeCells = (int)Math.Ceiling(range / cellSize);
            int cx = CellX(x);
            int cy = CellY(y);
            var result = new List<int>();
            float rangeSq = range * range;

            for (int dx = -rangeCells; dx <= rangeCells; dx++)
            {
                for (int dy = -rangeCells; dy <= rangeCells; dy++)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if (nx < 0 || nx >= gridWidth || ny < 0 || ny >= gridHeight) continue;

                    if (cells.TryGetValue((nx, ny), out var list))
                    {
                        foreach (var eid in list)
                        {
                            // Squared distance — no Math.Sqrt
                            float ddx = posX[eid] - x;
                            float ddy = posY[eid] - y;
                            if (ddx * ddx + ddy * ddy <= rangeSq)
                                result.Add(eid);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Get count of entities in a cell (for debugging).
        /// </summary>
        public int CountInCell(int cx, int cy)
        {
            return cells.TryGetValue((cx, cy), out var list) ? list.Count : 0;
        }

        /// <summary>
        /// Total registered entities.
        /// </summary>
        public int Count => entityCells.Count;

        private (int, int) WorldToCell(float x, float y)
        {
            return (CellX(x), CellY(y));
        }

        private int CellX(float x) => (int)(x / cellSize);
        private int CellY(float y) => (int)(y / cellSize);
    }
}
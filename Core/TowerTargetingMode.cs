namespace BattleSystemECS.Components
{
    /// <summary>
    /// Tower targeting mode — controls which enemy the tower selects as its primary target.
    /// </summary>
    public enum TowerTargetingMode
    {
        Nearest      = 0,  // Closest enemy (default)
        Furthest     = 1,  // Farthest enemy
        LowestHealth = 2,  // Enemy with the lowest current health
        HighestHealth = 3, // Enemy with the highest current health
        FirstSpawned = 4,  // Oldest enemy by spawn frame
        LastSpawned  = 5,  // Newest enemy by spawn frame
        Intercept    = 6   // PointDefense: intercept enemy projectiles
    }
}

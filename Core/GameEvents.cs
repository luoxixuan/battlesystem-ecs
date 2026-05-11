namespace BattleSystemECS.Core
{
    /// <summary>
    /// Canonical event type constants used across the game.
    /// </summary>
    public static class GameEvents
    {
        public const string EnemyKilled      = "enemy_killed";
        public const string WaveCleared      = "wave_cleared";
        public const string WaveStarted      = "wave_started";
        public const string AllWavesCleared   = "all_waves_cleared";
        public const string PlayerDamaged    = "player_damaged";
        public const string PlayerUpgraded   = "player_upgraded";
        public const string PlayerDied       = "player_died";
        public const string LevelStarted     = "level_started";
        public const string LevelCompleted   = "level_completed";
        public const string GameOver         = "game_over";
        public const string Victory          = "victory";
        public const string TowerPlaced      = "tower_placed";
        public const string TowerUpgraded    = "tower_upgraded";
        public const string TowerAttacked    = "tower_attacked";
        public const string GoldChanged      = "gold_changed";
        public const string EnemySpawned     = "enemy_spawned";
        public const string TurnStarted      = "turn_started";
        public const string TurnEnded        = "turn_ended";
        public const string EnemyCharging    = "enemy_charging";
        public const string EnemyChargeReleased = "enemy_charge_released";
        public const string EnemyDodged      = "enemy_dodged";
    }

    // ── Event Data Transfer Objects ──

    public class EnemyKilledEvent
    {
        public int EnemyId;
        public string MonsterType;
        public float X;
        public float Y;
        public float GoldReward;
    }

    public class WaveEvent
    {
        public int WaveNumber;
        public int LevelNumber;
        public int TotalWaves;
    }

    public class PlayerDamagedEvent
    {
        public float Damage;
        public float RemainingHealth;
        public int AttackerId;
    }

    public class PlayerUpgradedEvent
    {
        public int NewLevel;
        public float NewThreshold;
    }

    public class LevelEvent
    {
        public int LevelNumber;
        public string LevelName;
        public int TotalWaves;
    }

    public class GameOverEvent
    {
        public string Reason;
        public int LevelReached;
        public int TotalLevels;
        public int TotalKills;
        public int WavesCleared;
    }

    public class TowerEvent
    {
        public int TowerId;
        public float X;
        public float Y;
        public string TowerType;
        public int Level;
    }

    public class GoldChangedEvent
    {
        public float Amount;       // delta (negative for spend)
        public float NewTotal;
        public string Source;      // "kill", "wave_bonus", "tower_build", "tower_upgrade"
    }

    public class EnemySpawnedEvent
    {
        public int EnemyId;
        public string MonsterType;
        public float X;
        public float Y;
        public float Health;
    }
}

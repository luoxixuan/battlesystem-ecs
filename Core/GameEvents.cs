namespace BattleSystemECS.Core
{
    /// <summary>
    /// Canonical event type constants used across the game.
    /// Only events that have at least one publisher are declared here.
    /// </summary>
    public static class GameEvents
    {
        // Actively published events (verified against all Subscribe/Publish call sites)
        public const string PlayerDamaged         = "player_damaged";
        public const string EnemyCharging         = "enemy_charging";
        public const string EnemyChargeReleased   = "enemy_charge_released";
    }

    // ── Event Data Transfer Objects ──
    // Only DTOs that are actually instantiated in the codebase are kept.

    public class PlayerDamagedEvent
    {
        public float Damage;
        public float RemainingHealth;
        public int AttackerId;
    }

    public class EnemyChargingEvent
    {
        public int EnemyId;
        public int Turn;
        public float Damage;
    }

    public class EnemyChargeReleasedEvent
    {
        public int EnemyId;
        public int Turn;
        public float Damage;
    }
}

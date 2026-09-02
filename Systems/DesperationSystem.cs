using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Desperation (Last Stand) System — when player base lives drop to critical levels,
    /// all towers receive damage and attack speed bonuses as a comeback mechanic.
    /// 
    /// Two thresholds:
    ///   Lives <= 3  (low): +20% damage, +20% attack speed
    ///   Lives <= 1  (critical): +50% damage, +50% attack speed
    /// 
    /// Cached bonuses are read by TowerAttackSystem each frame — zero per-tower query overhead.
    /// </summary>
    public class DesperationSystem : global::BattleSystemECS.Content.Contracts.IDesperationView
    {
        private readonly ComponentStore store;

        /// <summary>Cached damage multiplier bonus (e.g. 0.20 = +20%). Applied to all towers.</summary>
        public float DamageBonus { get; private set; }

        /// <summary>Cached attack speed multiplier bonus (e.g. 0.20 = +20% speed). Applied to all towers.</summary>
        public float SpeedBonus { get; private set; }

        // ── Threshold configuration ─────────────────────────────────────
        // Hard-coded defaults; can be migrated to GameConfig later.
        private const int THRESHOLD_LOW = 3;
        private const float DAMAGE_BONUS_LOW = 0.20f;
        private const float SPEED_BONUS_LOW = 0.20f;

        private const int THRESHOLD_CRITICAL = 1;
        private const float DAMAGE_BONUS_CRITICAL = 0.50f;
        private const float SPEED_BONUS_CRITICAL = 0.50f;

        public DesperationSystem(ComponentStore store)
        {
            this.store = store;
        }

        /// <summary>
        /// Evaluate current player lives and update cached bonuses.
        /// Called once per frame from BuildPhase and WavePhase.
        /// </summary>
        public void Update()
        {
            int lives = store.GetPlayerBaseLives(store.PlayerEntityId);

            if (lives <= THRESHOLD_CRITICAL)
            {
                DamageBonus = DAMAGE_BONUS_CRITICAL;
                SpeedBonus = SPEED_BONUS_CRITICAL;
            }
            else if (lives <= THRESHOLD_LOW)
            {
                DamageBonus = DAMAGE_BONUS_LOW;
                SpeedBonus = SPEED_BONUS_LOW;
            }
            else
            {
                DamageBonus = 0f;
                SpeedBonus = 0f;
            }
        }

        /// <summary>Reset bonuses to zero (e.g. on new game start).</summary>
        public void Reset()
        {
            DamageBonus = 0f;
            SpeedBonus = 0f;
        }
    }
}

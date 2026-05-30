using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// N-Hit Shield System — blocks incoming attacks for N layers regardless of damage amount.
    /// 
    /// Each tower/player attack that hits a shielded enemy consumes exactly 1 layer and deals
    /// 0 damage. This creates a distinct defense layer from HP shields (which scale with damage)
    /// and is specifically countered by "low hit count, high damage" attacks.
    /// 
    /// Mechanics:
    /// - Layer consumed on every incoming attack (tower or player)
    /// - When count reaches 0, damage passes through normally
    /// - Optional regeneration: configurable interval between layer restores
    /// - Boss enemies are typically immune (max layers = 0)
    /// 
    /// Usage from TowerAttackSystem / PlayerTowerAttackSystem:
    ///   if (_hitShieldSystem != null && _hitShieldSystem.ConsumeHitShield(enemyId))
    ///       continue; // damage was blocked, don't apply damage
    /// </summary>
    public class HitShieldSystem
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;

        // Cached per-turn to avoid per-frame store lookups
        private int[] _cachedActiveEnemyIds;
        private int _cachedCount;
        private bool _turnCached;

        public HitShieldSystem(ComponentStore store, IRenderer renderer = null)
        {
            this.store = store;
            this.renderer = renderer;
            // Use GetActiveEnemySpan() which returns ReadOnlySpan<int> — convert to array for caching
            var span = store.GetActiveEnemySpan();
            _cachedActiveEnemyIds = span.ToArray();
            _cachedCount = _cachedActiveEnemyIds.Length;
            _turnCached = false;
        }

        /// <summary>
        /// Called once per turn at the start of CombatGroup to cache the active enemy list.
        /// </summary>
        public void SetTurn(int currentTurn)
        {
            var span = store.GetActiveEnemySpan();
            _cachedActiveEnemyIds = span.ToArray();
            _cachedCount = _cachedActiveEnemyIds.Length;
            _turnCached = true;
        }

        /// <summary>
        /// Called from TowerAttackSystem / PlayerTowerAttackSystem to check and consume a hit shield.
        /// Returns true if the hit was blocked (hit shield consumed 1 layer), false otherwise.
        /// When true is returned, the caller should skip damage application for this attack.
        /// </summary>
        public bool ConsumeHitShield(int enemyId)
        {
            if (!ComponentStore.IsValidEntity(enemyId)) return false;
            if (!store.EnemyActive[enemyId]) return false;

            float count = store.EnemyHitShieldCount[enemyId];
            if (count <= 0f) return false;

            store.EnemyHitShieldCount[enemyId] = count - 1f;
            return true;
        }

        /// <summary>
        /// Updates hit shield regeneration each frame.
        /// Timer decrements by deltaTime; when timer reaches 0 and count < max, restore 1 layer.
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!_turnCached) return;

            for (int i = 0; i < _cachedCount; i++)
            {
                int enemyId = _cachedActiveEnemyIds[i];
                if (!store.EnemyActive[enemyId]) continue;

                float max = store.EnemyHitShieldMax[enemyId];
                float count = store.EnemyHitShieldCount[enemyId];
                float timer = store.EnemyHitShieldTimer[enemyId];
                float regenInterval = store.EnemyHitShieldRegenInterval[enemyId];

                // Skip if no regen configured or already at max
                if (regenInterval <= 0f) continue;
                if (count >= max) continue;

                if (timer > 0f)
                {
                    // Countdown mode: timer is counting down
                    timer -= deltaTime;
                    if (timer <= 0f)
                    {
                        // Restore 1 layer and reset timer
                        store.EnemyHitShieldCount[enemyId] = count + 1f;
                        store.EnemyHitShieldTimer[enemyId] = regenInterval;
                    }
                    else
                    {
                        store.EnemyHitShieldTimer[enemyId] = timer;
                    }
                }
                else
                {
                    // Timer not started: start it
                    store.EnemyHitShieldTimer[enemyId] = regenInterval;
                }
            }

            _turnCached = false;
        }

        /// <summary>
        /// Sets up hit shield on an enemy at spawn time.
        /// </summary>
        public void SetupHitShield(int enemyId, float maxLayers, float regenInterval = 0f)
        {
            if (!ComponentStore.IsValidEntity(enemyId)) return;
            store.EnemyHitShieldCount[enemyId] = maxLayers;
            store.EnemyHitShieldMax[enemyId] = maxLayers;
            store.EnemyHitShieldTimer[enemyId] = regenInterval > 0f ? regenInterval : 0f;
            store.EnemyHitShieldRegenInterval[enemyId] = regenInterval > 0f ? regenInterval : 0f;
        }
    }
}
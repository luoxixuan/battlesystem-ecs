using System.Collections.Generic;
using BattleSystemECS.Components;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Kill-Triggered Cooldown Reset System — ARPG/Roguelike mechanic.
    ///
    /// Subscribes to ComponentStore.OnTowerKill (per-tower reset) and
    /// ComponentStore.OnEnemyKilled (per-player skill reset). Both events fire
    /// serially inside ResolveEnemiesKilledThisFrame, so direct SOA writes are safe.
    ///
    /// Behavior:
    /// - Tower kill: if TowerResetOnKill[towerId] > 0, advance the tower's
    ///   attack timer so it can fire sooner.
    ///     mode 1 (Full): set TowerLastAttackTime to a very large sentinel (≥ attackInterval),
    ///                    so the tower is "ready to fire" on the next Update.
    ///     mode 2 (Partial): subtract TowerResetAmount seconds, clamped at 0.
    /// - Enemy kill (any source): if PlayerSkillResetOnKill[playerIdKill] > 0,
    ///   reset all unlocked global skill cooldowns for that player.
    ///     mode 1 (Full): zero all 8 skill cooldowns.
    ///     mode 2 (Partial): subtract PlayerSkillResetAmount from each, clamped at 0.
    ///
    /// Lazy-apply semantics (important contract):
    ///   - All fields default to 0 (disabled). A tower that never gets a kill never
    ///     triggers any default lookup, so the feature is fully backward compatible.
    ///   - On the FIRST kill by a tower, if TowerResetOnKill[towerId] == 0, the
    ///     system consults the static TowerTypeDefaults dictionary and writes the
    ///     type-specific default. After that, the field is non-zero and the lazy
    ///     path is bypassed on subsequent kills.
    ///   - Trade-off: a tower whose type has a default in TowerTypeDefaults
    ///     cannot be explicitly disabled via the SOA field (0 is interpreted as
    ///     "use type default"). This is acceptable because the dictionary is
    ///     the source of truth for per-type behavior. JSON config can be added
    ///     later by introducing a separate "explicit" flag.
    /// </summary>
    public class KillCooldownResetSystem
    {
        private const int MAX_GLOBAL_SKILLS = 8;
        // Sentinel value well above any plausible attack interval. The tower-attack
        // readiness check is: if (TowerLastAttackTime[towerId] < attackInterval) skip.
        // Setting TowerLastAttackTime to a value ≥ attackInterval makes the tower
        // fire on the very next Update, after which the attack code resets it to 0f.
        private const float READY_SENTINEL = 1e6f;

        private readonly ComponentStore store;
        private readonly GameConfig config;
        // Idempotency guard: WireDependencies() is normally called once, but defend
        // against re-init / test reset paths stacking duplicate handlers.
        private bool _subscribed;

        // Hardcoded per-TowerType defaults — demonstration values applied on
        // the first kill so the feature works out-of-the-box without JSON config.
        // Key: TowerType. Value: (reset mode, amount in seconds).
        // mode 0 = disabled, 1 = full reset on kill, 2 = partial (subtract amount).
        private static readonly Dictionary<TowerType, (int Mode, float Amount)> TowerTypeDefaults
            = new Dictionary<TowerType, (int, float)>
            {
                // Sniper: high single-target damage, reward accuracy with cooldown reset
                { TowerType.Sniper, (2, 0.3f) },
                // Tesla: rapid chain, partial reset maintains pressure
                { TowerType.Tesla,  (2, 0.15f) },
                // Leech: life-steal towers gain full reset to keep pressure up
                { TowerType.Leech,  (1, 0f) },
            };

        public KillCooldownResetSystem(ComponentStore store, GameConfig config, int playerId)
        {
            this.store = store;
            this.config = config;
            // playerId parameter reserved for future per-player default lookup
            // (e.g., tech-tree unlock that sets PlayerSkillResetOnKill[playerId]).
            _ = playerId;
        }

        /// <summary>
        /// Subscribe to OnTowerKill and OnEnemyKilled events. Called once by
        /// SystemRegistry.WireDependencies().
        /// </summary>
        public void SubscribeToEvents()
        {
            if (_subscribed) return;
            _subscribed = true;
            store.OnTowerKill += HandleTowerKill;
            store.OnEnemyKilled += HandleEnemyKilled;
        }

        /// <summary>
        /// OnTowerKill handler: apply tower cooldown reset.
        /// </summary>
        private void HandleTowerKill(int enemyId, int playerIdKill, int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return;
            if (!store.TowerActive[towerId]) return;

            // Lazy-apply type defaults on first kill (see class doc for semantics).
            if (store.TowerResetOnKill[towerId] == 0)
            {
                if (TowerTypeDefaults.TryGetValue(store.TowerType[towerId], out var def))
                {
                    store.TowerResetOnKill[towerId] = def.Mode;
                    store.TowerResetAmount[towerId] = def.Amount;
                }
            }

            int mode = store.TowerResetOnKill[towerId];
            if (mode == 0) return;

            switch (mode)
            {
                case 1: // Full reset → ready to fire next frame
                    store.TowerLastAttackTime[towerId] = READY_SENTINEL;
                    break;
                case 2: // Partial reset → reduce remaining cooldown
                    float amount = store.TowerResetAmount[towerId];
                    if (amount > 0f)
                    {
                        store.TowerLastAttackTime[towerId] =
                            System.Math.Max(0f, store.TowerLastAttackTime[towerId] - amount);
                    }
                    break;
            }
        }

        /// <summary>
        /// OnEnemyKilled handler: apply player skill cooldown reset for any kill,
        /// regardless of source (tower, player attack, DoT, etc.).
        /// </summary>
        private void HandleEnemyKilled(int enemyId, int playerIdKill)
        {
            // playerIdKill is the player array index (not entity), so validate against MAX_PLAYERS.
            if ((uint)playerIdKill >= ComponentStore.MAX_PLAYERS) return;

            int mode = store.PlayerSkillResetOnKill[playerIdKill];
            if (mode == 0) return;

            switch (mode)
            {
                case 1: // Full reset → zero all unlocked skill cooldowns
                    for (int i = 0; i < MAX_GLOBAL_SKILLS; i++)
                    {
                        int idx = playerIdKill * MAX_GLOBAL_SKILLS + i;
                        // Only reset slots that are actually unlocked — locked slots
                        // may hold stale or default cooldown values that we must not
                        // touch (they will be initialized correctly when the skill
                        // becomes available).
                        if (store.PlayerGlobalSkillUnlocked[idx])
                        {
                            store.PlayerGlobalSkillCooldown[idx] = 0f;
                        }
                    }
                    break;
                case 2: // Partial → subtract amount, clamped at 0
                    float amount = store.PlayerSkillResetAmount[playerIdKill];
                    if (amount > 0f)
                    {
                        for (int i = 0; i < MAX_GLOBAL_SKILLS; i++)
                        {
                            int idx = playerIdKill * MAX_GLOBAL_SKILLS + i;
                            if (!store.PlayerGlobalSkillUnlocked[idx]) continue;
                            float cd = store.PlayerGlobalSkillCooldown[idx];
                            if (cd > 0f)
                            {
                                store.PlayerGlobalSkillCooldown[idx] = System.Math.Max(0f, cd - amount);
                            }
                        }
                    }
                    break;
            }
        }
    }
}

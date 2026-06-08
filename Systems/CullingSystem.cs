using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Culling System — Round 206 Direction 1.
    /// HP-threshold instant-execute for high-burst towers. Complements DeathMark
    /// (stack-based execute over many hits) by handling the "one big hit finishes
    /// off the wounded boss" case — a 5%-HP Boss with 1000 HP left cannot be
    /// chipped down by a Sniper/Mortar dealing 500 damage per shot, but if the
    /// boss is at or below EnemyCullingThresholdPct, that single 500-damage hit
    /// culls the boss for a bonus gold payout.
    ///
    /// Trigger conditions (all must hold for a cull to fire):
    ///   1. CullingConfig.Enabled = true (master switch)
    ///   2. Enemy is active, NOT invulnerable, NOT EnemyExecuteImmune
    ///   3. Enemy is at or below its culling threshold (EnemyCullingThresholdPct,
    ///      or CullingConfig.DefaultThresholdPct as fallback) AFTER the hit lands
    ///   4. Tower has TowerIsCullingTower = true
    ///   5. Tower's TowerCullingDamagePct (or CullingConfig.DefaultDamagePct) is
    ///      &gt; 0, and the hit damage is &gt;= enemy.MaxHealth * damagePct
    ///
    /// Per-frame cost: O(activeEnemies) when CullingSystem.Update is wired, but
    /// the per-tower hook (TryCull) is event-driven (called from TowerAttackSystem
    /// after a successful hit). The Update() tick is a no-op when no culling is
    /// in progress — the per-frame work is on the per-hit call path, not the
    /// per-frame loop. Sentinel-gated: per-tower / per-enemy opt-out flags make
    /// the hot path branch-cheap when the subsystem is unused.
    /// </summary>
    public class CullingSystem
    {
        private readonly ComponentStore store;
        private readonly int playerId;
        private CullingConfig config;

        /// <summary>
        /// Fired when a culling kill lands. Subscribers can read the enemy/tower/player
        /// ids to apply extra effects (gold payout, sound effect, screen popup, etc.).
        /// The default GoldSystem wiring pays BaseBonusGold * (1 + stacks * pct) gold.
        /// Signature: (enemyId, towerId, playerId, bonusGold).
        /// </summary>
        public event Action<int, int, int, float> OnCullingKilled;

        public CullingSystem(ComponentStore store, int playerId = 0)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.playerId = playerId;
            this.config = CullingSubsystemConfig.Default;
        }

        /// <summary>Override the default Culling configuration (typically called by
        /// GameManager.Initialize after loading culling.json).</summary>
        public void LoadConfig(CullingConfig cfg)
        {
            this.config = cfg ?? CullingSubsystemConfig.Default;
        }

        /// <summary>Read-only access to current config (used by tests).</summary>
        public CullingConfig Config => config;

        /// <summary>
        /// Per-frame tick. Currently a no-op (the system is event-driven via TryCull);
        /// exposed so it can be wired into the scheduler for future periodic logic
        /// (e.g. per-wave decay of PlayerCullingStacks, which is handled by an
        /// OnWaveStart hook instead — see CullingSystem.OnWaveStart).
        /// </summary>
        public void Update(float deltaTime)
        {
            // Event-driven system: no per-frame work. The per-hit hot path is TryCull.
        }

        /// <summary>
        /// Per-frame cull scan. Called from TowerAttackSystem after all damage has been
        /// applied this frame. Walks every active culling-enabled tower and tries to cull
        /// the first enemy in its attack range whose HP is already at or below the
        /// threshold.
        ///
        /// SEMANTIC NOTE: this is a "cull-on-touch" cleanup pass — if the tower has any
        /// enemy in range that meets the HP-threshold condition, the tower's BASE damage
        /// is used as the "hitDamage" gate value. This is intentionally permissive: a
        /// high-DPS tower that has chipped an enemy down to cull range will execute
        /// them on the next attack tick, even if the chipping hits were individually
        /// small. Per-hit strict culling is NOT enforced here (cull is a wave-clear
        /// mechanism, not a single-strike execution). TryCull still verifies the
        /// damage-gate using the supplied proxy value.
        ///
        /// Sentinel-gated: when no TowerIsCullingTower is on the field, this is a single
        /// O(activeTowers) scan that returns immediately. When at least one culling tower
        /// exists, the inner loop is O(cullingTowers * enemies) worst case, but typically
        /// culling is rare (few towers, gated by threshold).
        /// </summary>
        public void ScanAndCull(TowerAttackSystem towerAttackSystem)
        {
            if (!config.Enabled) return;
            if (towerAttackSystem == null) return;

            var activeTowers = store.ActiveTowerIds;
            int towerCount = activeTowers.Count;
            for (int i = 0; i < towerCount; i++)
            {
                int towerId = activeTowers[i];
                if (!store.TowerIsCullingTower[towerId]) continue;

                // Use the tower's current attack damage as the hitDamage proxy. CullingSystem
                // will re-check the damage-gate inside TryCull (this avoids re-running the
                // resistance / crit chain).
                float hitDamage = store.TowerAttackDamage[towerId];
                if (hitDamage <= 0f) continue;

                // Find the first enemy in attack range whose HP is at-or-below the threshold.
                // We use the tower's range as a quick spatial filter, then defer the threshold
                // check to TryCull itself (so per-enemy EnemyCullingThresholdPct is honored).
                // PositionX/PositionY is the shared SOA for both tower and enemy entities.
                float range = store.TowerRange[towerId];
                float rangeSq = range * range;
                float towerX = store.PositionX[towerId];
                float towerY = store.PositionY[towerId];

                var activeEnemies = store.ActiveEnemyIds;
                int enemyCount = activeEnemies.Count;
                for (int j = 0; j < enemyCount; j++)
                {
                    int enemyId = activeEnemies[j];
                    if (!store.EnemyActive[enemyId]) continue;
                    if (store.EnemyHealth[enemyId] <= 0f) continue;

                    float dx = store.PositionX[enemyId] - towerX;
                    float dy = store.PositionY[enemyId] - towerY;
                    if (dx * dx + dy * dy > rangeSq) continue;

                    // Found a candidate. TryCull runs the threshold + damage + immune gates.
                    if (TryCull(towerId, enemyId, hitDamage))
                    {
                        // Culling applied. Stop scanning this tower's enemies for this frame —
                        // one cull per tower per frame keeps the bonus gold from snowballing
                        // and mirrors the DeathMark auto-execute semantics.
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Try to cull the target enemy. Called from TowerAttackSystem after a successful
        /// hit has been resolved (so EnemyHealth reflects post-hit HP). Returns true if
        /// a culling kill was applied.
        ///
        /// Logic:
        ///   1. Sentinel gates (config disabled, enemy inactive/invulnerable/immune) → no-op
        ///   2. Resolve effective threshold / damagePct (per-entity override or config default)
        ///   3. Compute HP fraction after hit; if &lt;= threshold AND hitDamage &gt;= damagePct * MaxHP
        ///      → set HP to 0, fire OnCullingKilled, increment PlayerCullingStacks, QueueEnemyDeath
        ///
        /// Does NOT apply damage — that is the caller's responsibility. TryCull only
        /// checks the post-hit state and triggers the cull event when conditions are met.
        /// </summary>
        /// <param name="towerId">The attacking tower entity id.</param>
        /// <param name="enemyId">The target enemy entity id.</param>
        /// <param name="hitDamage">Damage dealt by the attack (post-resistance, pre-floor).</param>
        /// <returns>True if a culling kill was applied (HP set to 0, death queued).</returns>
        public bool TryCull(int towerId, int enemyId, float hitDamage)
        {
            if (!config.Enabled) return false;
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return false;
            if (enemyId < 0 || enemyId >= ComponentStore.MAX_ENTITIES) return false;
            if (!store.EnemyActive[enemyId]) return false;
            // Hard opt-out: execute-immune enemies cannot be culled (mirrors DeathMark).
            if (store.EnemyExecuteImmune[enemyId]) return false;
            // Invulnerable enemies also cannot be culled (consistent with DeathMark's
            // auto-execute gate).
            if (store.EnemyIsInvulnerable[enemyId]) return false;

            // Tower must have the culling flag set.
            if (!store.TowerIsCullingTower[towerId]) return false;

            // Resolve effective threshold: per-enemy override, or config default.
            float thresholdPct = store.EnemyCullingThresholdPct[enemyId];
            if (thresholdPct <= 0f)
            {
                thresholdPct = config.DefaultThresholdPct;
                if (thresholdPct <= 0f) return false; // both opt-out → culling disabled for this pair
            }

            // Resolve effective damagePct: per-tower override, or config default.
            float damagePct = store.TowerCullingDamagePct[towerId];
            if (damagePct <= 0f)
            {
                damagePct = config.DefaultDamagePct;
                if (damagePct <= 0f) return false; // both opt-out → culling disabled for this pair
            }

            // Compute post-hit HP fraction.
            float currentHp = store.EnemyHealth[enemyId];
            if (currentHp <= 0f) return false; // already dead from this hit's damage

            float maxHp = store.EnemyMaxHealth[enemyId];
            if (maxHp <= 0f) return false; // malformed enemy

            float hpFraction = currentHp / maxHp;
            if (hpFraction > thresholdPct) return false; // not yet culling-eligible

            // Damage gate: hit must be large enough to qualify.
            float requiredDamage = maxHp * damagePct;
            if (hitDamage < requiredDamage) return false; // hit too small for cull

            // All conditions met → execute the cull.
            // Set HP to 0 (mirror DeathMark auto-execute semantics).
            store.EnemyHealth[enemyId] = 0f;

            // Compute bonus gold FIRST using PRE-increment stacks (per doc contract:
            // "per-stack bonus gold payout on subsequent culls"). Then increment.
            int stacksPlayerId = (playerId >= 0 && playerId < ComponentStore.MAX_PLAYERS) ? playerId : 0;
            int preStacks = 0;
            if (store.PlayerCullingStacks != null && stacksPlayerId < store.PlayerCullingStacks.Length)
            {
                preStacks = store.PlayerCullingStacks[stacksPlayerId];
            }
            float bonusGold = config.BaseBonusGold * (1f + preStacks * config.PlayerStackBonusGoldPct);

            // Increment per-player stacks (with cap) AFTER computing bonus gold.
            if (store.PlayerCullingStacks != null && stacksPlayerId < store.PlayerCullingStacks.Length)
            {
                int stacks = preStacks + 1;
                int cap = config.MaxPlayerStacks;
                if (cap > 0 && stacks > cap) stacks = cap;
                store.PlayerCullingStacks[stacksPlayerId] = stacks;
            }
            if (bonusGold < 0f) bonusGold = 0f; // defensive: stack cap can't go negative

            // Queue the death (caller may also have queued, but the death-resolution
            // pass deduplicates via the per-enemy death latch).
            store.QueueEnemyDeath(enemyId, stacksPlayerId);

            // Fire event last (so subscribers see the post-cull state).
            OnCullingKilled?.Invoke(enemyId, towerId, stacksPlayerId, bonusGold);

            return true;
        }

        /// <summary>
        /// Reset per-player culling stacks. Called from the OnWaveStart latch (wired
        /// in SystemRegistry) so each wave starts with 0 stacks (combo resets).
        /// No-op when config is disabled.
        /// </summary>
        public void OnWaveStart()
        {
            if (!config.Enabled) return;
            if (store.PlayerCullingStacks == null) return;
            int len = store.PlayerCullingStacks.Length;
            for (int i = 0; i < len; i++)
            {
                store.PlayerCullingStacks[i] = 0;
            }
        }

        /// <summary>
        /// Per-player gold payout helper. Computes the bonus gold for a culling kill
        /// given the player's current stack count. Used by the OnCullingKilled subscriber
        /// (GoldSystem wiring) to pay the reward. Exposed for testability.
        /// </summary>
        public float ComputeBonusGold(int stacks)
        {
            if (stacks < 0) stacks = 0;
            return config.BaseBonusGold * (1f + stacks * config.PlayerStackBonusGoldPct);
        }

        /// <summary>Read-only access to the per-player stack count (test helper).</summary>
        public int GetPlayerStacks(int playerId)
        {
            if (playerId < 0 || playerId >= ComponentStore.MAX_PLAYERS) return 0;
            return store.PlayerCullingStacks[playerId];
        }
    }

    /// <summary>
    /// CullingConfig tunable configuration. Subsystem is opt-in via per-tower
    /// (TowerIsCullingTower) and per-enemy (EnemyCullingThresholdPct) flags, but
    /// CullingConfig.Enabled is the master switch.
    /// </summary>
    public static class CullingSubsystemConfig
    {
        /// <summary>Default state — all flags false, all thresholds 0. Subsystem is opt-in.</summary>
        public static readonly CullingConfig Default = new CullingConfig();
    }
}

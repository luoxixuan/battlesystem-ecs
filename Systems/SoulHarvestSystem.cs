using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 196 Direction 3 — Soul Harvest System.
    ///
    /// Tracks per-player "soul" currency earned from enemy kills. Souls are a parallel
    /// economy alongside gold (GoldSystem) and combo (ComboSystem), used by soul-cost
    /// skills (Soul Bomb, Resurrect, etc.). Three layered state machines:
    ///
    ///   1. Per-kill harvest:  OnEnemyKilled event → add EnemySoulValue[enemyId] souls
    ///                          to PlayerSoulCount[playerId], clamped to PlayerSoulCap.
    ///   2. Per-frame regen:  Update(dt) → add PlayerSoulRegen[i] * dt to PlayerSoulCount[i].
    ///   3. Spend / consume:  TrySpendSouls(playerId, cost) → deduct from PlayerSoulCount,
    ///                          returning true on success, false on insufficient funds.
    ///
    /// Lazy / sentinel-gated fast path: when PlayerSoulRegen[i] ≤ 0 and there are no
    /// pending events, Update() is O(MAX_PLAYERS) with a single float compare per slot
    /// (no per-frame work for the regen loop on the no-op branch).
    ///
    /// Run group: PostDeath (alongside ComboSystem, NecromancerSystem). OnEnemyKilled
    /// fires inside ResolveEnemiesKilledThisFrame which runs BEFORE PostDeath.Execute,
    /// so by the time Update() runs the per-frame regen tick, all kills for this frame
    /// have already been credited. The contract: a kill is observable in PlayerSoulCount
    /// before any per-frame regen tick for the same frame.
    ///
    /// Backward compatibility: all default fields are 0. A new game with no soul setup
    /// behaves identically to a pre-soul-harvest build — every enemy grants 1 soul
    /// (default EnemySoulValue), but PlayerSoulCount starts at 0 and no skill has
    /// SoulCost > 0 by default, so no actual spending occurs. Safe to drop in.
    /// </summary>
    public class SoulHarvestSystem
    {
        private readonly ComponentStore store;
        private readonly SoulHarvestConfig config;
        private readonly IRenderer renderer;
        private bool _subscribed;

        // Sentinel cap for unbounded cap=0 reads. config.DefaultCap is the real cap
        // applied on AddPlayer; this constant is the upper bound for "infinite cap"
        // (config.DefaultCap=0 or negative would otherwise let souls overflow the int range).
        private const float MAX_SOUL_CAP_SENTINEL = 1_000_000f;

        public SoulHarvestSystem(ComponentStore store, SoulHarvestConfig config, IRenderer renderer = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.config = config ?? new SoulHarvestConfig();
            this.renderer = renderer;
        }

        /// <summary>
        /// Wire OnEnemyKilled subscription. Call once from SystemRegistry.WireDependencies.
        /// Idempotent (re-calls are no-ops via _subscribed guard) so a test reset path
        /// that re-runs the wire phase doesn't stack duplicate handlers.
        /// </summary>
        public void SubscribeToEvents()
        {
            if (_subscribed) return;
            _subscribed = true;
            store.OnEnemyKilled += HandleEnemyKilled;
        }

        /// <summary>
        /// Per-frame regen tick. Adds PlayerSoulRegen[i] * dt to PlayerSoulCount[i] for
        /// every player slot, clamping to PlayerSoulCap[i]. Sentinel-gated: slots with
        /// regen=0 are a no-op (we still touch them because the loop is cheap, but the
        /// per-slot work is two float ops when regen=0). Update returns early on
        /// negative dt (no work to do).
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;
            int max = store.PlayerSoulCount.Length; // == MAX_PLAYERS
            for (int i = 0; i < max; i++)
            {
                float regen = store.PlayerSoulRegen[i];
                if (regen <= 0f) continue; // fast path: no regen = no work
                float cap = ResolveCap(i);
                float current = store.PlayerSoulCount[i];
                if (current >= cap) continue; // already at cap, skip arithmetic
                float next = current + regen * deltaTime;
                if (next > cap) next = cap;
                store.PlayerSoulCount[i] = next;
            }
        }

        /// <summary>
        /// OnEnemyKilled handler — credited on every kill regardless of source (tower,
        /// DoT, player attack). Reads EnemySoulValue[enemyId] (default 1f, set in
        /// AddEnemy), adds BaseSoulPerKill (config), then writes PlayerSoulCount clamped
        /// to PlayerSoulCap. Telemetry: increments PlayerSoulEarnedTotal by the actual
        /// amount credited (after cap-clamp).
        /// </summary>
        private void HandleEnemyKilled(int enemyId, int playerId)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return;
            if (!ComponentStore.IsValidEntity(enemyId)) return;

            float soulValue = store.EnemySoulValue[enemyId];
            if (soulValue <= 0f) return; // no soul reward configured for this enemy

            float totalReward = soulValue + Math.Max(0f, config.BaseSoulPerKill);
            if (totalReward <= 0f) return;

            float cap = ResolveCap(playerId);
            float current = store.PlayerSoulCount[playerId];
            // Self-heal: if current is over cap (e.g. cap was lowered by SetSoulCap
            // after souls were earned), clamp down before crediting. Without this,
            // the player would stay over-cap until a TrySpendSouls drains them.
            if (current > cap) current = cap;
            float next = current + totalReward;
            float credited = next;
            if (credited > cap)
            {
                credited = cap;
            }
            float actualDelta = credited - current;
            if (actualDelta <= 0f) return; // already at cap, no soul earned this kill

            store.PlayerSoulCount[playerId] = credited;
            store.PlayerSoulEarnedTotal[playerId] += actualDelta;
            renderer?.Log($"[SOUL] Player {playerId} harvested +{actualDelta:F0} souls (now {credited:F0}/{cap:F0})");
        }

        /// <summary>
        /// Spend `cost` souls from the given player. Returns true on success, false
        /// on insufficient funds or invalid player. Clamps cost to ≥ 0 (0 = free
        /// spending, always succeeds). Increments PlayerSoulSpentTotal only when
        /// the spend actually deducts (cost &gt; 0 AND sufficient funds).
        /// </summary>
        public bool TrySpendSouls(int playerId, float cost)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return false;
            if (cost <= 0f) return true; // free / no-op spend is always allowed
            float current = store.PlayerSoulCount[playerId];
            if (current < cost)
            {
                renderer?.Log($"[SOUL] Spend REJECTED: player {playerId} has {current:F0} souls, need {cost:F0}");
                return false;
            }
            store.PlayerSoulCount[playerId] = current - cost;
            store.PlayerSoulSpentTotal[playerId] += cost;
            renderer?.Log($"[SOUL] Player {playerId} spent {cost:F0} souls (now {store.PlayerSoulCount[playerId]:F0})");
            return true;
        }

        /// <summary>
        /// Add souls directly (e.g. for level-up reward, soul-drain tower proc, quest
        /// completion). Clamped to PlayerSoulCap. No event fired — pure SOA write.
        /// </summary>
        public void AddSouls(int playerId, float amount)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return;
            if (amount <= 0f) return;
            float cap = ResolveCap(playerId);
            float current = store.PlayerSoulCount[playerId];
            float next = current + amount;
            if (next > cap) next = cap;
            float actualDelta = next - current;
            if (actualDelta <= 0f) return;
            store.PlayerSoulCount[playerId] = next;
            store.PlayerSoulEarnedTotal[playerId] += actualDelta;
        }

        /// <summary>
        /// Set the per-player cap to a custom value. Pass 0 to use config.DefaultCap
        /// (the auto-applied AddPlayer default). The cap is clamped to [0, sentinel]
        /// so a malformed config can't trigger integer overflow downstream.
        /// </summary>
        public void SetSoulCap(int playerId, float cap)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return;
            store.PlayerSoulCap[playerId] = Math.Clamp(cap, 0f, MAX_SOUL_CAP_SENTINEL);
        }

        /// <summary>
        /// Set the per-player passive regen (souls / second). Clamped to [0, 1000]
        /// to prevent a malformed config from granting millions of souls per second.
        /// </summary>
        public void SetSoulRegen(int playerId, float regenPerSecond)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return;
            store.PlayerSoulRegen[playerId] = Math.Clamp(regenPerSecond, 0f, 1000f);
        }

        /// <summary>
        /// Reset per-player soul state. Called by AddPlayer so a recycled player
        /// entity doesn't inherit a stale soul balance from a prior game.
        /// </summary>
        public void ResetPlayer(int playerId)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return;
            store.PlayerSoulCount[playerId] = 0f;
            store.PlayerSoulCap[playerId] = config.DefaultCap;
            store.PlayerSoulRegen[playerId] = config.DefaultRegenPerSecond;
            store.PlayerSoulSpentTotal[playerId] = 0f;
            store.PlayerSoulEarnedTotal[playerId] = 0f;
        }

        // ── Read helpers ──────────────────────────────────────────────

        public float GetSoulCount(int playerId)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return 0f;
            return store.PlayerSoulCount[playerId];
        }

        public float GetSoulCap(int playerId)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return 0f;
            return ResolveCap(playerId);
        }

        public bool HasEnoughSouls(int playerId, float cost)
        {
            if ((uint)playerId >= ComponentStore.MAX_PLAYERS) return false;
            if (cost <= 0f) return true;
            return store.PlayerSoulCount[playerId] >= cost;
        }

        /// <summary>
        /// Resolve effective cap. When the SOA cap is unset (0), fall back to
        /// config.DefaultCap. If config also has DefaultCap=0 (intentionally unbounded),
        /// use the sentinel to prevent integer overflow in arithmetic.
        /// </summary>
        private float ResolveCap(int playerId)
        {
            float cap = store.PlayerSoulCap[playerId];
            if (cap > 0f) return cap;
            cap = config.DefaultCap;
            if (cap > 0f) return cap;
            return MAX_SOUL_CAP_SENTINEL;
        }
    }
}

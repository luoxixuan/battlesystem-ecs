using System;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Crest System (Round 178+ Direction 5) — wave-indexed periodic buffs
    /// ("Crest of Fury" / "Tide of Healing" / "Crest of Bounty" / etc.) that
    /// apply multiplicative / additive bonuses to enemies or players during
    /// the matching wave. Complements AdaptiveDifficulty (continuous
    /// difficulty scaling) with discrete "named" buff waves that the player
    /// can recognize and plan for (e.g. "wave 7 is a Crest of Fury wave —
    /// save my heal skill").
    ///
    /// Lifecycle per wave:
    /// 1. OnWaveStart → <see cref="HandleWaveStart"/> reads the current wave
    ///    index from <see cref="WaveSpawningSystem.GetCurrentWave"/> and
    ///    iterates the Crests roster. For each crest whose TriggerWaves
    ///    contains the current wave, it stamps the cached damage / regen /
    ///    gold / mult fields onto every active enemy and every player
    ///    (gated by TargetScope).
    /// 2. During the wave, the hot paths (TowerAttackSystem, GoldSystem,
    ///    the per-enemy regen tick) read the cached fields.
    /// 3. OnWaveComplete → <see cref="HandleWaveComplete"/> resets the
    ///    per-enemy caches to defaults (1f damage mult, 0f regen) and the
    ///    per-player caches to defaults (1f mult, 1f gold, empty id). This
    ///    prevents a stale crest bonus from leaking into the next wave.
    ///
    /// Sentinel-gated fast path: when CrestConfig.Enabled == false OR the
    /// Crests array is empty, the system is a no-op and the per-enemy /
    /// per-player caches stay at default (1f for mults, 0f for regen).
    /// </summary>
    public class CrestSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;

        public CrestSystem(ComponentStore store, GameConfig gameConfig)
        {
            this.store = store;
            this.gameConfig = gameConfig;
        }

        /// <summary>
        /// Inject the WaveSpawningSystem so HandleWaveStart can read the
        /// current 1-based wave index. Optional — when null, no crests
        /// ever fire (sentinel "no spawner wired yet"). Called from
        /// SystemRegistry.WireDependencies().
        /// </summary>
        // ── Event handlers ────────────────────────────────────────────

        /// <summary>
        /// OnWaveStart hook. Iterates the Crests roster; for each crest
        /// whose TriggerWaves contains the current 1-based wave index,
        /// stamps the cached fields. The wave index is read from the
        /// injected WaveSpawningSystem.
        ///
        /// For enemy-side stamps, we walk the active enemy list
        /// (O(activeEnemies) per crest, typically &lt; 200). For player-side
        /// stamps we iterate MAX_PLAYERS (10, O(1) effectively).
        ///
        /// Multiple crests can match the same wave — in that case the
        /// multiplicative fields compose multiplicatively and the additive
        /// regen fields stack additively (matches the design intent: a
        /// CrestOfFury + CrestOfBounty co-fire = +20% enemy damage AND
        /// +50% player gold).
        /// </summary>
        public void HandleWaveStart(int wave)
        {
            var cfg = gameConfig.Crest;
            if (cfg == null || !cfg.Enabled) return;
            if (cfg.Crests == null || cfg.Crests.Length == 0) return;
            if (wave <= 0) return;

            bool anyEnemyDmg = false;
            bool anyEnemyRegen = false;
            bool anyPlayerDmg = false;
            bool anyPlayerGold = false;
            string activeCrestId = "";
            float enemyDmgMult = 1f;
            float enemyRegen = 0f;
            float playerDmgMult = 1f;
            float playerGoldMult = 1f;

            for (int c = 0; c < cfg.Crests.Length; c++)
            {
                var crest = cfg.Crests[c];
                if (crest == null) continue;
                if (crest.TriggerWaves == null || crest.TriggerWaves.Length == 0) continue;
                if (!ContainsWave(crest.TriggerWaves, wave)) continue;

                // First matching crest's id is the "primary" crest id (for
                // HUD / debug). Multiple crests → keep the first.
                if (string.IsNullOrEmpty(activeCrestId)) activeCrestId = crest.Id ?? "";

                bool touchesEnemy =
                    string.Equals(crest.TargetScope, "enemy", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(crest.TargetScope, "both", StringComparison.OrdinalIgnoreCase);
                bool touchesPlayer =
                    string.Equals(crest.TargetScope, "player", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(crest.TargetScope, "both", StringComparison.OrdinalIgnoreCase);

                if (touchesEnemy)
                {
                    if (crest.EnemyDamageMult > 0f && crest.EnemyDamageMult != 1f)
                    {
                        enemyDmgMult *= crest.EnemyDamageMult;
                        anyEnemyDmg = true;
                    }
                    if (crest.EnemyRegenPerSec > 0f)
                    {
                        enemyRegen += crest.EnemyRegenPerSec;
                        anyEnemyRegen = true;
                    }
                }
                if (touchesPlayer)
                {
                    if (crest.PlayerDamageMult > 0f && crest.PlayerDamageMult != 1f)
                    {
                        playerDmgMult *= crest.PlayerDamageMult;
                        anyPlayerDmg = true;
                    }
                    if (crest.PlayerGoldMult > 0f && crest.PlayerGoldMult != 1f)
                    {
                        playerGoldMult *= crest.PlayerGoldMult;
                        anyPlayerGold = true;
                    }
                }
            }

            // Apply to active enemies.
            if (anyEnemyDmg || anyEnemyRegen)
            {
                var activeEnemies = store.ActiveEnemyIds;
                for (int i = 0; i < activeEnemies.Count; i++)
                {
                    int eid = activeEnemies[i];
                    if (eid < 0 || eid >= ComponentStore.MAX_ENTITIES) continue;
                    if (!store.EnemyActive[eid]) continue;
                    if (anyEnemyDmg) store.EnemyCrestDamageMult[eid] = enemyDmgMult;
                    if (anyEnemyRegen) store.EnemyCrestRegenPerSec[eid] = enemyRegen;
                }
            }

            // Apply to players. Always iterate MAX_PLAYERS so the slots
            // reset cleanly even when no player joined yet.
            int playerCount = ComponentStore.MAX_PLAYERS;
            for (int p = 0; p < playerCount; p++)
            {
                store.PlayerCrestActiveId[p] = (anyEnemyDmg || anyEnemyRegen || anyPlayerDmg || anyPlayerGold) ? activeCrestId : "";
                if (anyPlayerDmg) store.PlayerCrestDamageMult[p] = playerDmgMult;
                if (anyPlayerGold) store.PlayerCrestGoldMult[p] = playerGoldMult;
            }
        }

        /// <summary>
        /// OnWaveComplete hook. Force-reset every active enemy's crest cache
        /// back to defaults (1f damage mult, 0f regen) and every player's
        /// crest cache back to defaults (1f damage / gold, empty id). This
        /// is the cleanup path that prevents a stale crest from leaking
        /// into the next wave.
        /// </summary>
        public void HandleWaveComplete()
        {
            // No "isEnabled" gate here: the cleanup is cheap (O(activeEnemies
            // + MAX_PLAYERS)) and runs even when the system is disabled
            // so a disabled-then-enabled session doesn't see stale data.
            var activeEnemies = store.ActiveEnemyIds;
            for (int i = 0; i < activeEnemies.Count; i++)
            {
                int eid = activeEnemies[i];
                if (eid < 0 || eid >= ComponentStore.MAX_ENTITIES) continue;
                if (!store.EnemyActive[eid]) continue;
                store.EnemyCrestDamageMult[eid] = 1f;
                store.EnemyCrestRegenPerSec[eid] = 0f;
            }
            int playerCount = ComponentStore.MAX_PLAYERS;
            for (int p = 0; p < playerCount; p++)
            {
                store.PlayerCrestActiveId[p] = "";
                store.PlayerCrestDamageMult[p] = 1f;
                store.PlayerCrestGoldMult[p] = 1f;
            }
        }

        // ── Per-frame tick (no-op for CrestSystem — all work is event-driven) ──
        /// <summary>
        /// Per-frame tick. CrestSystem is event-driven (OnWaveStart /
        /// OnWaveComplete), so this method is intentionally a no-op. Kept
        /// as a public method so the BuildGroup / CombatGroup can wire it
        /// uniformly with other systems.
        /// </summary>
        public void Update(float deltaTime)
        {
            // Intentionally empty — CrestSystem is event-driven.
            // The signature matches ISystemGroup convention so the
            // scheduler can call it without a null check.
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static bool ContainsWave(int[] waves, int wave)
        {
            for (int i = 0; i < waves.Length; i++)
            {
                if (waves[i] == wave) return true;
            }
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using BattleSystemECS.Core;
using BattleSystemECS.Components;
using BattleSystemECS.Core.GAS;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Thorns Aura System — passive tower-centered damage aura on enemies.
    /// (Round 126 Direction 4)
    ///
    /// Towers with TowerIsThornsTower=true AND a non-zero TowerThornsRadius and
    /// TowerThornsDps are "thorns emitters". Every TowerThornsInterval seconds (or every
    /// frame when interval=0), each thorns emitter deals TowerThornsDps damage to every
    /// active enemy in range. Like a poison cloud pinned to the tower — distinct from
    /// on-hit reflect (which is per-attack) and from projectile splash (which is on
    /// impact); this is a constant standing pressure zone.
    ///
    /// Design notes:
    ///  - Designers opt-in via TowerConfig.IsThornsTower=true. All four fields default
    ///    to false/0, so the zero-overhead fast path is preserved: towers that never
    ///    opt in pay no per-frame cost beyond the SetTurn loop's `IsThornsTower` bool
    ///    check (which the JIT folds into a single load + branch).
    ///  - Multiple thorns emitters in range stack ADDITIVELY on each enemy. Two towers
    ///    each dealing 5 DPS to the same enemy → that enemy takes 10 DPS from the
    ///    combined aura. (Same stacking rule as HealAura's additive heal.)
    ///  - Damage is applied with raw `EnemyHealth[eid] -= dmg` (plus QueueEnemyDeath
    ///    when HP crosses 0). This is safe because ThornsAuraSystem runs in the serial
    ///    SkillBuffGroup phase, after the parallel damage collection phase, and before
    ///    ResolveEnemiesKilledThisFrame — so queueing deaths here is correct.
    ///  - The system is serial (no Parallel.For). Thorns towers are rare (support
    ///    role) and the inner loop scans active enemies serially with a small list.
    ///  - Invulnerable enemies (EnemyIsInvulnerable) are skipped — Boss phases that
    ///    grant invuln block thorns damage just like they block any other source.
    ///  - We do NOT apply thorns damage to the thorns tower itself (no self-damage),
    ///    but that's automatically true because we iterate enemies not towers.
    /// </summary>
    public class ThornsAuraSystem
    {
        private ComponentStore store;
        // Cached list of thorns-emitter tower IDs. Rebuilt each frame by SetTurn() to
        // avoid scanning the full ActiveTowerIds every Update. Bounded by the number
        // of thorns towers on the field (typically 0-2).
        private List<int> _thornsTowerIds;
        // Cached enemy list reused across updates. Sized to MAX_ENEMIES so the inner
        // loop is allocation-free across frames.
        private int[] _enemyBuffer;

        public ThornsAuraSystem(ComponentStore store)
        {
            this.store = store;
            _thornsTowerIds = new List<int>(16);
            _enemyBuffer = new int[ComponentStore.MAX_ENTITIES];
        }

        /// <summary>
        /// Cache all thorns-emitter tower IDs for the upcoming Update. Called once per
        /// frame (typically right after SpatialGrid rebuild so the emitter list is fresh).
        /// The "is-thorns" bool check is the dominant fast path: non-thorns towers (the
        /// vast majority) skip the per-frame interval/dps validation entirely.
        /// </summary>
        public void SetTurn()
        {
            _thornsTowerIds.Clear();
            var activeTowerIds = store.ActiveTowerIds;
            for (int i = 0; i < activeTowerIds.Count; i++)
            {
                int towerId = activeTowerIds[i];
                // Early-out: opt-in flag is the single bool check. radius>0 && dps>0 are
                // checked defensively in Update; setting the opt-in flag with radius=0
                // would mean the tower is intentionally inert (designer can set true +
                // radius=0 to "disable" without re-toggling the flag).
                if (store.TowerIsThornsTower[towerId])
                    _thornsTowerIds.Add(towerId);
            }
        }

        /// <summary>
        /// Apply thorns damage ticks to all enemies in range of any thorns emitter.
        /// Called once per frame in the WavePhase SkillBuffGroup (Phase 9, serial
        /// segment after damage collection, before ResolveEnemiesKilledThisFrame).
        ///
        /// Internally ticks the per-emitter cooldown and resets it on fire. The single-
        /// pass tick+fire+reset pattern (vs splitting into a pre-tick loop + fire loop)
        /// is intentional — splitting it would make the fire-loop's "timer > 0 ? skip"
        /// check always true once we reset the timer to interval on expiry, and the
        /// thorns tick would never fire. Same lesson as HealAuraSystem.
        /// </summary>
        /// <param name="deltaTime">frame delta in seconds (used to decrement per-emitter timer).</param>
        /// <param name="playerId">the player ID (used to attribute QueueEnemyDeath on kill).</param>
        public void Update(float deltaTime, int playerId)
        {
            if (_thornsTowerIds.Count == 0) return;
            var activeEnemyIds = store.ActiveEnemyIds;
            int activeEnemyCount = activeEnemyIds.Count;
            if (activeEnemyCount == 0) return;

            // Per-emitter single-pass loop: tick cooldown, decide fire, then reset.
            for (int ti = 0; ti < _thornsTowerIds.Count; ti++)
            {
                int emitterId = _thornsTowerIds[ti];
                float interval = store.TowerThornsInterval[emitterId];
                float timer = store.TowerThornsTimer[emitterId];

                if (interval <= 0f)
                {
                    // interval=0 means "fire every frame" — keep timer at 0 so the
                    // fire branch below triggers every frame.
                    store.TowerThornsTimer[emitterId] = 0f;
                }
                else
                {
                    // Decrement; if it expired, fire (after the if-block) and reset.
                    timer -= deltaTime;
                    if (timer > 0f)
                    {
                        // Still on cooldown — write back and skip fire.
                        store.TowerThornsTimer[emitterId] = timer;
                        continue;
                    }
                    // Expired: reset to interval for the next cycle. (Falls through
                    // to the fire block below.)
                    store.TowerThornsTimer[emitterId] = interval;
                }

                float radius = store.TowerThornsRadius[emitterId];
                if (radius <= 0f) continue; // defensive: SetTurn filter should have caught this
                float dps = store.TowerThornsDps[emitterId];
                if (dps <= 0f) continue;    // defensive: ditto

                // Continuous-per-frame scaling: when interval=0, we want DPS to mean
                // "damage per second" so the designer can set a value like 5 and the
                // tower actually does 5*deltaTime damage per frame. When interval>0,
                // we want DPS to mean "damage per tick" so the designer specifies the
                // burst size per cycle. Same convention as HealAuraSystem.
                float tickDamage = interval <= 0f ? dps * deltaTime : dps;
                if (tickDamage <= 0f) continue;

                float emitterX = store.PositionX[emitterId];
                float emitterY = store.PositionY[emitterId];
                float radiusSq = radius * radius;

                // Scan all active enemies in range. O(n_enemies) per emitter. Thorns
                // towers are rare (a handful at most) and the active-enemy count is
                // bounded by ~10K in benchmark mode. We do a serial scan with no
                // SpatialGrid here because:
                //   (a) the distance check is a single fused-multiply-add vs the cell
                //       overhead of GetEnemiesInRange (which does cell hashing).
                //   (b) thorns towers are NOT a per-tower hot path — only thorns
                //       emitters scan, and most towers are not thorns emitters.
                //   (c) parity with HealAuraSystem (which also does plain O(n) scans
                //       over its target set, no SpatialGrid).
                for (int ei = 0; ei < activeEnemyCount; ei++)
                {
                    int enemyId = activeEnemyIds[ei];
                    if (!store.EnemyActive[enemyId]) continue;
                    // Skip dead / pending-kill enemies (HP<=0 means destroy flag is
                    // set; the entity will be reaped this frame anyway).
                    if (store.EnemyHealth[enemyId] <= 0f) continue;
                    // Boss invuln phases block all incoming damage including thorns.
                    if (store.EnemyIsInvulnerable[enemyId]) continue;

                    float dx = store.PositionX[enemyId] - emitterX;
                    float dy = store.PositionY[enemyId] - emitterY;
                    if (dx * dx + dy * dy > radiusSq) continue;

                    // Apply thorns damage. We do NOT respect EnemyShield here because
                    // thorns is typically physical-damage (the AuraTowerSystem applies
                    // both dmgReduction AND dmgTakenIncrease for curses; thorns is its
                    // own thing). Future iteration could add a damage-type field. For
                    // now: raw HP reduction, no shield, no resistance.
                    var source = store.GetEntityHandle(emitterId);
                    var target = store.GetEntityHandle(enemyId);
                    if (source.IsValid && target.IsValid)
                        store.DamageResolver.TryApply(new Core.GAS.DamageRequest(source, target, tickDamage, DamageType.True, ElementType.None, DamageFlags.None, DamageAmountStage.Raw, DamageCommitBoundary.GameplayResolve, store.AllocateGameplaySequence(enemyId), ownerPlayerId: playerId));
                }
            }
        }
    }
}

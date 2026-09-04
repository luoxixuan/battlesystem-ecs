using System;
using BattleSystemECS.Core;
using BattleSystemECS.Components;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Echo Clone System — Round 201 Direction 8.
    ///
    /// When a parent tower has TowerCanSpawnEcho=true (set at PlaceTower from
    /// TowerConfig.SpawnsEcho > 0 and EchoDuration > 0), every combat frame the
    /// system rolls the dice (TowerEchoChance) and, on success, spawns a transient
    /// "phantom tower" at the parent's position. The phantom mirrors the parent's
    /// damage × TowerEchoDamageMult for EchoDuration seconds, then expires
    /// cleanly (DestroyEntity → slot recycled to free list).
    ///
    /// Design rationale (mirrors AggroSystem's sentinel-gated pattern):
    ///   - Per-frame Update() is O(active_towers) only when at least one parent
    ///     has a spawn opportunity (cooldown==0, chance>0, duration>0). When
    ///     no parent on the field can spawn an echo, the sentinel drops and
    ///     Update() is O(1) on the spawn-roll phase.
    ///   - Echoes are ordinary tower slots (TowerActive=true, TowerIsEcho=true).
    ///     They share TowerAttackSystem's main hot path so they fire exactly
    ///     like a real tower. The TowerIsEcho=true flag is set so the spawn
    ///     roll in this system skips echoes (no phantom-of-phantom chains).
    ///   - The clone's damage is written ONCE at spawn time, not refreshed
    ///     each frame. Intentional: if the parent gets a damage buff
    ///     mid-life, the clone keeps its snapshot (matches "phantom of
    ///     THIS moment" semantics).
    ///   - The clone's lifetime is tracked via TowerPlaceTime[echo] (set to
    ///     Time.TotalTime at spawn) and TowerEchoExpireTurn[echo] (set to the
    ///     duration in seconds). Update() checks (Time.TotalTime -
    ///     PlaceTime) >= duration to decide expiry. TowerEchoExpireTurn is
    ///     therefore "duration in seconds" not "turn of expiry" — the field
    ///     name was chosen for short identifier parity with the placeholder
    ///     schema; tests assert the behaviour via the duration API.
    ///   - The clone is destroyed via ComponentStore.DestroyEntity(), which
    ///     handles the free-list push, tile-occupancy release, and
    ///     per-archetype reset hooks (including the echo-field reset block
    ///     added in Round 201).
    ///
    /// Distinction from related systems:
    ///   - SummonCircle: summons enemy mobs (enemy-side).
    ///   - TowerShrine / TowerBeacon: persistent aura emitters (no expiry).
    ///   - EchoCloneSystem: transient (time-bounded) player-side phantom tower.
    ///
    /// Per-frame cost: O(n_active_towers) only when at least one parent has
    /// TowerCanSpawnEcho=true. Otherwise O(1) (single bool check on the
    /// _hasAnyEchoCapableParent sentinel).
    /// </summary>
    public class EchoCloneSystem
    {
        private readonly ComponentStore store;

        // Sentinel: true if at least one parent on the field can currently
        // spawn an echo (TowerCanSpawnEcho=true). Set true on first opt-in
        // parent detected by Update. Cleared when the per-frame tick finds
        // no opt-in parent remains. The sentinel is the O(1) fast-path gate.
        private bool _hasAnyEchoCapableParent;
        public bool HasAnyEchoCapableParent => _hasAnyEchoCapableParent;

        // Cumulative count of echoes ever spawned by this system (exposed for
        // tests and HUD telemetry). Survives restarts via the system lifetime
        // — typically one system per game.
        private int _totalEchoesSpawned;
        public int TotalEchoesSpawned => _totalEchoesSpawned;

        // Cumulative count of echoes that have expired naturally (lifetime
        // elapsed). Echoes destroyed by other means (mass dispel) go through
        // DestroyEntity and are NOT counted here.
        private int _totalEchoesExpired;
        public int TotalEchoesExpired => _totalEchoesExpired;

        // Sentinel: true if at least one echo is currently alive on the
        // field. Drives the echo-expiry phase fast-path.
        private bool _hasAnyLiveEcho;
        public bool HasAnyLiveEcho => _hasAnyLiveEcho;

        public EchoCloneSystem(ComponentStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // ─── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Force-spawn an echo of <paramref name="parentTowerId"/> regardless
        /// of the dice roll or cooldown. Returns the spawned echo's entity id,
        /// or -1 on failure (invalid parent / no free slot / parent is an
        /// echo / parent's TowerCanSpawnEcho is false / duration is 0).
        ///
        /// On success: echo created in a free entity slot, inherits parent's
        /// position, TowerAttackDamage = parent's damage × EchoDamageMult,
        /// PlaceTime = Time.TotalTime, ExpireTurn = duration in seconds.
        /// Parent's spawn cooldown is reset to TowerEchoMaxCooldown.
        /// </summary>
        public int ForceSpawnEcho(int parentTowerId)
        {
            if (!IsValidParent(parentTowerId)) return -1;

            int echoId = store.CreateEntity();
            if (echoId < 0) return -1; // pool exhausted

            // Snapshot parent's position so the echo fires from the same
            // tile. AddPosition also writes the tile-occupancy cache; the
            // parent's tile is already marked occupied, so the second mark
            // is a no-op write of true.
            store.AddPosition(echoId,
                (int)store.PositionX[parentTowerId],
                (int)store.PositionY[parentTowerId]);

            // AddTower sets TowerActive=true, copies parent stats, and
            // initializes all the per-tower SOA fields to inert defaults
            // (including the echo fields added in Round 201). We pass
            // the echo's adjusted damage (parent × EchoDamageMult).
            store.AddTower(
                echoId,
                store.TowerType[parentTowerId],
                store.TowerAttackDamage[parentTowerId] * store.TowerEchoDamageMult[parentTowerId],
                store.TowerRange[parentTowerId],
                store.TowerAttackSpeed[parentTowerId],
                store.TowerLevel[parentTowerId],
                0f,    // cost is irrelevant for an echo
                "standard" // echo has no upgrade path
            );

            // Mark as echo and set lifetime markers.
            store.TowerIsEcho[echoId] = true;
            store.TowerEchoParentId[echoId] = parentTowerId;
            store.TowerPlaceTime[echoId] = Time.TotalTime;
            float duration = store.TowerEchoDuration[parentTowerId];
            // ExpireTurn stores the duration in seconds (int truncation
            // is fine for typical 5-15s lifetimes; Update() recomputes the
            // exact age from PlaceTime for the >= duration check).
            store.TowerEchoExpireTurn[echoId] = (int)Math.Ceiling(duration);

            // Defensive reset of the parent's echo-config fields on the
            // echo slot. AddTower's signature doesn't cover these (they're
            // TowerConfig-derived, set by PlaceTower), so a recycled slot
            // could carry stale opt-in flags from a previous owner. We
            // explicitly clear them to make the "echo doesn't spawn
            // echoes" invariant independent of slot history.
            store.TowerCanSpawnEcho[echoId] = false;
            store.TowerEchoChance[echoId] = 0f;
            store.TowerEchoDuration[echoId] = 0f;
            store.TowerEchoMaxCooldown[echoId] = 0f;
            store.TowerEchoSpawnCooldown[echoId] = 0f;
            // Keep the sentinel fields consistent with "echo never expires
            // because of cooldown, never spawns another echo".

            // Reset parent cooldown so the parent cannot spawn a second
            // echo immediately (matches "wait EchoSpawnCooldown seconds
            // between echoes" semantics from the config).
            store.TowerEchoSpawnCooldown[parentTowerId] = store.TowerEchoMaxCooldown[parentTowerId];

            _totalEchoesSpawned++;
            _hasAnyLiveEcho = true;
            _hasAnyEchoCapableParent = true;
            return echoId;
        }

        /// <summary>
        /// Read-only helper: is this tower id a currently-live echo clone?
        /// Returns true only for tower slots with TowerIsEcho=true and the
        /// lifetime not yet elapsed.
        /// </summary>
        public bool IsEcho(int towerId)
        {
            if (!IsValidTower(towerId)) return false;
            if (!store.TowerIsEcho[towerId]) return false;
            // Treat negative ExpireTurn as "infinite lifetime" sentinel
            // (not produced by ForceSpawnEcho, but defensive against any
            // future code that manually sets TowerIsEcho=true).
            int exp = store.TowerEchoExpireTurn[towerId];
            if (exp < 0) return true;
            float age = Time.TotalTime - store.TowerPlaceTime[towerId];
            return age < exp;
        }

        /// <summary>
        /// Manually destroy an echo before its lifetime elapses. Returns
        /// true if the echo existed and was destroyed. Useful for "dispel
        /// echo" skills and tests. Note: does NOT update _hasAnyLiveEcho
        /// sentinel — Phase 1 of the next Update() tick self-corrects it
        /// (single frame of unnecessary iteration, no functional impact).
        /// </summary>
        public bool DestroyEcho(int echoId)
        {
            if (!IsValidTower(echoId)) return false;
            if (!store.TowerIsEcho[echoId]) return false;
            store.DestroyEntity(echoId);
            return true;
        }

        // ─── Per-frame lifecycle ───────────────────────────────────────────

        // Sentinel re-scan throttle: how often (in frames) to perform a full
        // active-tower scan when _hasAnyEchoCapableParent is false. Towers
        // opt in at PlaceTower time but the sentinel only flips true via
        // either (a) ForceSpawnEcho or (b) this throttled re-scan. Without
        // the re-scan, a brand-new game with an echo-capable parent would
        // never roll dice (the sentinel is false on construction). 60 frames
        // @ 60fps = 1s — opt-in latency is bounded to 1s, scan cost is
        // O(N/60) per second of opt-in parents. 60 is a hand-tuned default;
        // bumping it higher saves CPU at the cost of opt-in delay.
        private const int OPT_IN_RESCAN_INTERVAL = 60;
        private int _optInRescanCounter;

        /// <summary>
        /// Three-phase per-frame tick:
        ///   Phase 1: expire live echoes (when _hasAnyLiveEcho is true).
        ///   Phase 2: cooldown tick + spawn roll for opt-in parents
        ///            (when _hasAnyEchoCapableParent is true).
        ///   Phase 3: throttled opt-in re-scan (every OPT_IN_RESCAN_INTERVAL
        ///            frames) to detect newly placed echo-capable parents
        ///            when no echo has yet been spawned.
        ///
        /// Fast-path: if both sentinels are false and the throttled counter
        /// is not yet 0, Update() returns after a single bool-OR check (O(1)).
        /// </summary>
        public void Update(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            // ── Phase 1: expire live echoes ─────────────────────────────
            if (_hasAnyLiveEcho)
            {
                var activeTowers = store.ActiveTowerIds;
                int count = activeTowers.Count;
                bool stillAnyLive = false;
                for (int i = 0; i < count; i++)
                {
                    int tid = activeTowers[i];
                    if (!store.TowerActive[tid]) continue;
                    if (!store.TowerIsEcho[tid]) continue;

                    int exp = store.TowerEchoExpireTurn[tid];
                    if (exp < 0)
                    {
                        // Sentinel: no expiry set. Keep alive.
                        stillAnyLive = true;
                        continue;
                    }
                    float age = Time.TotalTime - store.TowerPlaceTime[tid];
                    if (age >= exp)
                    {
                        // Expired — destroy. DestroyEntity will set
                        // TowerActive=false and reset all per-entity fields
                        // (including the echo fields added in Round 201).
                        store.DestroyEntity(tid);
                        _totalEchoesExpired++;
                    }
                    else
                    {
                        stillAnyLive = true;
                    }
                }
                _hasAnyLiveEcho = stillAnyLive;
            }

            // ── Phase 2: cooldown tick + spawn roll ─────────────────────
            // Fast-path gate: if no parent on the field is currently opt-in,
            // skip the per-frame walk. The opt-in re-scan in Phase 3 will
            // re-arm this sentinel if a fresh parent opts in.
            if (_hasAnyEchoCapableParent)
            {
                var activeTowers2 = store.ActiveTowerIds;
                int count2 = activeTowers2.Count;
                bool stillAnyOptIn = false;
                for (int i = 0; i < count2; i++)
                {
                    int tid = activeTowers2[i];
                    if (!store.TowerActive[tid]) continue;
                    if (store.TowerIsEcho[tid]) continue; // echoes don't spawn echoes

                    // Tick parent cooldown.
                    float cd = store.TowerEchoSpawnCooldown[tid];
                    if (cd > 0f)
                    {
                        cd -= deltaTime;
                        if (cd < 0f) cd = 0f;
                        store.TowerEchoSpawnCooldown[tid] = cd;
                    }

                    // Check opt-in for this parent.
                    if (store.TowerCanSpawnEcho[tid] && store.TowerEchoChance[tid] > 0f)
                    {
                        stillAnyOptIn = true;
                        // Roll only when cooldown is ready.
                        if (cd <= 0f && RollChance(store.TowerEchoChance[tid]))
                        {
                            ForceSpawnEcho(tid);
                        }
                    }
                }

                // Drop the opt-in sentinel if no parent currently opts in.
                // Phase 3 will re-arm it on the next re-scan tick.
                _hasAnyEchoCapableParent = stillAnyOptIn;
            }

            // ── Phase 3: throttled opt-in re-scan ───────────────────────
            // Detects newly placed echo-capable parents. Without this, a
            // fresh game (sentinel = false on construction) would never
            // roll dice for the first time. The scan is the same as Phase 2
            // minus the dice roll — we just re-arm the sentinel and tick
            // any leftover cooldowns. Cost: O(N_active) every 60 frames
            // (1 Hz at 60fps). For an N=500 wave, that's ~8 towers/ms of
            // scan work per second — negligible.
            if (++_optInRescanCounter >= OPT_IN_RESCAN_INTERVAL)
            {
                _optInRescanCounter = 0;
                if (!_hasAnyEchoCapableParent)
                {
                    var activeTowers3 = store.ActiveTowerIds;
                    int count3 = activeTowers3.Count;
                    for (int i = 0; i < count3; i++)
                    {
                        int tid = activeTowers3[i];
                        if (!store.TowerActive[tid]) continue;
                        if (store.TowerIsEcho[tid]) continue;
                        if (store.TowerCanSpawnEcho[tid] && store.TowerEchoChance[tid] > 0f)
                        {
                            _hasAnyEchoCapableParent = true;
                            break; // sentinel armed — Phase 2 will run next frame
                        }
                    }
                }
            }
        }

        // ─── Internal helpers ─────────────────────────────────────────────

        private bool RollChance(float p)
        {
            if (p <= 0f) return false;
            if (p >= 1f) return true;
            return store.Determinism.NextDouble() < p;
        }

        private bool IsValidParent(int parentTowerId)
        {
            if (parentTowerId < 0 || parentTowerId >= ComponentStore.MAX_ENTITIES) return false;
            if (!store.TowerActive[parentTowerId]) return false;
            if (store.TowerIsEcho[parentTowerId]) return false; // echoes don't spawn echoes
            if (!store.TowerCanSpawnEcho[parentTowerId]) return false;
            if (store.TowerEchoDuration[parentTowerId] <= 0f) return false;
            return true;
        }

        private bool IsValidTower(int towerId)
        {
            if (towerId < 0 || towerId >= ComponentStore.MAX_ENTITIES) return false;
            return store.TowerActive[towerId];
        }
    }
}

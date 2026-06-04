#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Totem System — placed stationary buff/aura objects summoned by the player.
    ///
    /// A Totem is dropped at a target (x, y) and persists for `duration` seconds
    /// (or until `charges` triggers are consumed, whichever comes first). While
    /// alive, it pulses every `triggerInterval` seconds with an effect depending
    /// on its type:
    ///
    ///   1 = Healing Totem   — restores `effectPerTick` HP to the owner (heal-self)
    ///   2 = Mana Spring     — restores `effectPerTick` mana to the owner (mana-self)
    ///   3 = Searing Totem   — deals `effectPerTick` damage to ALL enemies within radius
    ///   4 = Tremor Totem    — stuns ALL enemies within radius for 1 turn
    ///
    /// Design notes:
    ///   - SOA fields + accessors live in ComponentStore_Player (Totem* arrays,
    ///     PlayerTotemCooldown[]). Pool size 32 (MAX_TOTEMS) — small because
    ///     totems are short-lived and expensive.
    ///   - All damage is applied directly to EnemyHealth array (Searing is a
    ///     single-frame tick, not a damage queue — it doesn't benefit from the
    ///     parallel-safe two-stage pattern because the totem is unique and the
    ///     hit set is small).
    ///   - Tremor stun uses the existing EnemyStunDuration field.
    ///   - Healing Totem restores OWNER only, not AoE — players place it next to
    ///     themselves (tactical decision). Searing/Tremor ARE AoE on enemies.
    ///   - Mana Spring is single-target (owner only) — could later be made AoE
    ///     if ally units are added.
    ///   - Per-player PlayerTotemCooldown throttles re-summon (default 2s).
    ///   - Update is O(MAX_TOTEMS=32) outer × O(MAX_ENEMIES=10K) inner — cheap
    ///     because most totems are time-based with low charge counts.
    /// </summary>
    public class TotemSystem
    {
        private readonly ComponentStore _store;
        private readonly IRenderer? _logger;

        // ── Totem type constants (mirror TotemType semantics) ──
        public const int TypeNone = 0;
        public const int TypeHealing = 1;
        public const int TypeManaSpring = 2;
        public const int TypeSearing = 3;
        public const int TypeTremor = 4;

        // ── Defaults (used when JSON missing or fields omitted) ──
        private const float DEFAULT_PLAYER_TOTEM_COOLDOWN = 2f; // seconds after spawn
        private const float POSITION_CLAMP = 10000f;

        // ── Cached defs (loaded once at construction) ──
        private readonly Dictionary<string, TotemDef> _defs = new Dictionary<string, TotemDef>();
        // Index by totemType for O(1) lookup during Update
        private readonly Dictionary<int, TotemDef> _defsByType = new Dictionary<int, TotemDef>();

        public TotemSystem(ComponentStore store, IRenderer? logger = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
            LoadTotemDefs();
        }

        /// <summary>Per-totem definition loaded from totems.json.</summary>
        public class TotemDef
        {
            public string TotemId { get; set; } = "";
            public string Name { get; set; } = "";
            public int TotemType { get; set; } = 0;
            public float EffectPerTick { get; set; } = 0f;
            public float Radius { get; set; } = 2.5f;
            public float Duration { get; set; } = 15f;
            public int Charges { get; set; } = 0;
            public float TriggerInterval { get; set; } = 1f;
            public string Description { get; set; } = "";
        }

        // ─────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Spawn a totem of the given type at (x, y) for the given player.
        /// Returns the totem slot id, or -1 if refused (cooldown / pool full / invalid).
        /// The totem is spawned even if owner has 0 HP (allows totem persistence across
        /// death — defensive design choice consistent with Magnetize zones).
        /// </summary>
        public int SpawnTotem(int playerId, int totemType, float x, float y)
        {
            if (playerId < 0 || playerId >= _store.PlayerTotemCooldown.Length)
            {
                _logger?.Log($"[TOTEM] Refused spawn: playerId={playerId} out of range");
                return -1;
            }
            if (totemType < TypeHealing || totemType > TypeTremor)
            {
                _logger?.Log($"[TOTEM] Refused spawn: totemType={totemType} (must be 1..4)");
                return -1;
            }
            if (_store.PlayerTotemCooldown[playerId] > 0f)
            {
                _logger?.Log($"[TOTEM] Player {playerId} on cooldown ({_store.PlayerTotemCooldown[playerId]:F1}s) — cannot place totem");
                return -1;
            }
            if (!_defsByType.TryGetValue(totemType, out var def))
            {
                // Defensive fallback: use type defaults so a missing JSON entry
                // doesn't completely break totem placement.
                def = new TotemDef
                {
                    TotemId = $"totem_type_{totemType}",
                    Name = $"Totem Type {totemType}",
                    TotemType = totemType,
                    EffectPerTick = (totemType == TypeHealing) ? 5f : (totemType == TypeManaSpring) ? 8f : 12f,
                    Radius = 2.5f,
                    Duration = (totemType == TypeTremor) ? 10f : 15f,
                    Charges = (totemType == TypeSearing) ? 8 : (totemType == TypeTremor) ? 3 : 0,
                    TriggerInterval = (totemType == TypeTremor) ? 3f : (totemType == TypeSearing) ? 0.5f : 1f,
                };
                _logger?.Log($"[TOTEM] Warning: no def found for type {totemType}, using defaults");
            }

            // Defensive clamp so totems never end up off-map
            if (x < -POSITION_CLAMP) x = -POSITION_CLAMP;
            else if (x > POSITION_CLAMP) x = POSITION_CLAMP;
            if (y < -POSITION_CLAMP) y = -POSITION_CLAMP;
            else if (y > POSITION_CLAMP) y = POSITION_CLAMP;

            int slot = _store.AddTotem(playerId, totemType, x, y, def.Duration, def.Charges, def.TriggerInterval);
            if (slot < 0)
            {
                _logger?.Log("[TOTEM] Pool full (MAX_TOTEMS=32) — totem not placed");
                return -1;
            }
            // Set player cooldown (throttle spam — even if pool has room, player
            // can't place another for DEFAULT_PLAYER_TOTEM_COOLDOWN seconds).
            _store.PlayerTotemCooldown[playerId] = DEFAULT_PLAYER_TOTEM_COOLDOWN;
            _logger?.Log($"[TOTEM] Player {playerId} placed {def.Name} at ({x:F1},{y:F1}) — dur={def.Duration:F0}s charges={def.Charges} trigger={def.TriggerInterval:F1}s");
            return slot;
        }

        /// <summary>Remove a totem by slot id. Safe to call with invalid id.</summary>
        public void RemoveTotem(int slotId)
        {
            _store.RemoveTotem(slotId);
        }

        /// <summary>Get count of active totems. O(MAX_TOTEMS) scan, cheap.</summary>
        public int GetActiveTotemCount()
        {
            int count = 0;
            int n = _store.TotemActive.Length;
            for (int i = 0; i < n; i++)
            {
                if (_store.TotemActive[i]) count++;
            }
            return count;
        }

        // ─────────────────────────────────────────────────────────────
        //  Frame update — called from FrameScheduler after skill ticks
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Tick all active totems: decrement duration, decrement trigger cooldown,
        /// fire effect on trigger, expire on duration/charges end.
        /// </summary>
        public void Update(float deltaTime)
        {
            int n = _store.TotemActive.Length;
            // 1) Per-player cooldown tick (always, even with no active totem —
            //    covers the case where SpawnTotem was refused but cooldown was
            //    somehow set elsewhere; cheap because MAX_PLAYERS=10).
            for (int pid = 0; pid < _store.PlayerTotemCooldown.Length; pid++)
            {
                if (_store.PlayerTotemCooldown[pid] > 0f)
                {
                    _store.PlayerTotemCooldown[pid] -= deltaTime;
                    if (_store.PlayerTotemCooldown[pid] < 0f)
                        _store.PlayerTotemCooldown[pid] = 0f;
                }
            }

            // 2) Iterate all totem slots
            for (int i = 0; i < n; i++)
            {
                if (!_store.TotemActive[i]) continue;

                // Tick duration
                _store.TotemDurationLeft[i] -= deltaTime;
                if (_store.TotemDurationLeft[i] <= 0f)
                {
                    _logger?.Log($"[TOTEM] Totem {i} (type={_store.TotemType[i]}) expired (duration)");
                    _store.RemoveTotem(i);
                    continue;
                }

                // Tick trigger cooldown
                _store.TotemCooldown[i] -= deltaTime;
                if (_store.TotemCooldown[i] > 0f) continue; // not ready to fire

                // Fire effect based on type
                int totemType = _store.TotemType[i];
                int ownerId = _store.TotemOwnerId[i];
                float tx = _store.TotemPosX[i];
                float ty = _store.TotemPosY[i];
                // Look up def (fallback to defaults if not registered)
                _defsByType.TryGetValue(totemType, out var def);
                float effect = def?.EffectPerTick ?? DefaultEffectFor(totemType);
                float radius = def?.Radius ?? 2.5f;
                float radiusSq = radius * radius;

                switch (totemType)
                {
                    case TypeHealing:
                        ApplyHealTick(ownerId, effect);
                        break;
                    case TypeManaSpring:
                        ApplyManaTick(ownerId, effect);
                        break;
                    case TypeSearing:
                        ApplySearingAoe(tx, ty, radiusSq, effect);
                        break;
                    case TypeTremor:
                        ApplyTremorStun(tx, ty, radiusSq);
                        break;
                }

                // Consume a charge (if charges > 0; 0 = unlimited time-based)
                if (_store.TotemChargesLeft[i] > 0)
                {
                    _store.TotemChargesLeft[i]--;
                    if (_store.TotemChargesLeft[i] <= 0)
                    {
                        _logger?.Log($"[TOTEM] Totem {i} (type={totemType}) expired (charges depleted)");
                        _store.RemoveTotem(i);
                        continue;
                    }
                }
                // Reset trigger cooldown for next tick
                _store.TotemCooldown[i] = def?.TriggerInterval ?? DefaultIntervalFor(totemType);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Effect implementations
        // ─────────────────────────────────────────────────────────────

        private static float DefaultEffectFor(int totemType) => totemType switch
        {
            TypeHealing => 5f,
            TypeManaSpring => 8f,
            TypeSearing => 12f,
            TypeTremor => 0f,
            _ => 0f,
        };

        private static float DefaultIntervalFor(int totemType) => totemType switch
        {
            TypeHealing => 1f,
            TypeManaSpring => 1f,
            TypeSearing => 0.5f,
            TypeTremor => 3f,
            _ => 1f,
        };

        /// <summary>
        /// Healing Totem: restore HP to the owner. Skips if owner is dead or at max HP.
        /// Direct write to PlayerCurrentHealth with clamp to PlayerMaxHealth.
        /// </summary>
        private void ApplyHealTick(int ownerId, float amount)
        {
            if (ownerId < 0 || ownerId >= _store.PlayerMaxHealth.Length) return;
            float maxHp = _store.PlayerMaxHealth[ownerId];
            if (maxHp <= 0f) return; // not initialized
            float curHp = _store.PlayerCurrentHealth[ownerId];
            if (curHp <= 0f) return; // dead
            if (curHp >= maxHp) return; // already full
            float newHp = curHp + amount;
            if (newHp > maxHp) newHp = maxHp;
            _store.PlayerCurrentHealth[ownerId] = newHp;
        }

        /// <summary>
        /// Mana Spring: restore mana to the owner. Direct write to PlayerMana with
        /// clamp to PlayerMaxMana. Skips if owner is dead (mana 0) or at max.
        /// </summary>
        private void ApplyManaTick(int ownerId, float amount)
        {
            if (ownerId < 0 || ownerId >= _store.PlayerMaxMana.Length) return;
            float maxMana = _store.PlayerMaxMana[ownerId];
            if (maxMana <= 0f) return; // not initialized
            float curMana = _store.PlayerMana[ownerId];
            if (curMana >= maxMana) return; // already full
            float newMana = curMana + amount;
            if (newMana > maxMana) newMana = maxMana;
            _store.PlayerMana[ownerId] = newMana;
        }

        /// <summary>
        /// Searing Totem: deal damage to every active enemy within radius.
        /// Direct write to EnemyHealth (we trust the totem's "owned by player"
        /// semantic — no parallel-safety concern because the totem fires
        /// serially in the frame's main loop after damage queues have resolved).
        /// </summary>
        private void ApplySearingAoe(float tx, float ty, float radiusSq, float damage)
        {
            if (damage <= 0f) return;
            var activeEnemyIds = _store.GetCachedActiveEnemyIds();
            for (int e = 0; e < activeEnemyIds.Count; e++)
            {
                int enemyId = activeEnemyIds[e];
                if (enemyId < 0) continue;
                if (!_store.EnemyActive[enemyId] || !_store.PositionActive[enemyId]) continue;
                float dx = _store.PositionX[enemyId] - tx;
                float dy = _store.PositionY[enemyId] - ty;
                float distSq = dx * dx + dy * dy;
                if (distSq > radiusSq) continue;
                float curHp = _store.EnemyHealth[enemyId];
                float newHp = curHp - damage;
                if (newHp < 0f) newHp = 0f;
                _store.EnemyHealth[enemyId] = newHp;
            }
        }

        /// <summary>
        /// Tremor Totem: stun every active enemy within radius for 1 turn.
        /// Uses existing EnemyStunDuration field (read by EnemyAISystem).
        /// </summary>
        private void ApplyTremorStun(float tx, float ty, float radiusSq)
        {
            var activeEnemyIds = _store.GetCachedActiveEnemyIds();
            for (int e = 0; e < activeEnemyIds.Count; e++)
            {
                int enemyId = activeEnemyIds[e];
                if (enemyId < 0) continue;
                if (!_store.EnemyActive[enemyId] || !_store.PositionActive[enemyId]) continue;
                float dx = _store.PositionX[enemyId] - tx;
                float dy = _store.PositionY[enemyId] - ty;
                float distSq = dx * dx + dy * dy;
                if (distSq > radiusSq) continue;
                // Per-type CC immunity (Round 97): Stun bit or Unstoppable blocks this totem-stun
                if (_store.IsCCImmuneTo(enemyId, CCImmunityConfig.Mask_Stun)) continue;
                _store.EnemyStunDurationLeft[enemyId] = 1;
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  JSON loading
        // ─────────────────────────────────────────────────────────────

        private void LoadTotemDefs()
        {
            const string totemFile = "Data/Configs/totems.json";
            try
            {
                if (!File.Exists(totemFile))
                {
                    _logger?.Log("[TOTEM] totems.json not found at " + totemFile + " — using built-in defaults");
                    return;
                }
                string json = File.ReadAllText(totemFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger?.Log("[TOTEM] totems.json is empty — using built-in defaults");
                    return;
                }
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    _logger?.Log("[TOTEM] totems.json root is not an array — using built-in defaults");
                    return;
                }
                int count = 0;
                foreach (var elem in root.EnumerateArray())
                {
                    var def = new TotemDef
                    {
                        TotemId = elem.TryGetProperty("totemId", out var tid) ? tid.GetString() ?? "" : "",
                        Name = elem.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                        TotemType = elem.TryGetProperty("totemType", out var tt) ? tt.GetInt32() : 0,
                        EffectPerTick = elem.TryGetProperty("effectPerTick", out var ep) ? (float)ep.GetDouble() : 0f,
                        Radius = elem.TryGetProperty("radius", out var rd) ? (float)rd.GetDouble() : 2.5f,
                        Duration = elem.TryGetProperty("duration", out var du) ? (float)du.GetDouble() : 15f,
                        Charges = elem.TryGetProperty("charges", out var ch) ? ch.GetInt32() : 0,
                        TriggerInterval = elem.TryGetProperty("triggerInterval", out var ti) ? (float)ti.GetDouble() : 1f,
                        Description = elem.TryGetProperty("description", out var ds) ? ds.GetString() ?? "" : "",
                    };
                    if (string.IsNullOrEmpty(def.TotemId)) continue;
                    _defs[def.TotemId] = def;
                    // Index by type (last def wins if duplicate types)
                    _defsByType[def.TotemType] = def;
                    count++;
                }
                _logger?.Log($"[TOTEM] Loaded {count} totem defs from {totemFile}");
            }
            catch (Exception ex)
            {
                _logger?.Log("[TOTEM] Failed to load totems.json: " + ex.Message);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Ascension / Difficulty Modifier System.
    ///
    /// Manages the collection of player-selected challenge modifiers (Slay the Spire-style).
    /// Modifiers are applied persistently across levels and waves, affecting:
    ///   - Enemy HP / damage / speed / regen
    ///   - Tower damage / attack speed / range
    ///   - Player starting gold / lives
    ///   - Gold earned multiplier
    ///   - Scoring multiplier
    ///
    /// Integration points:
    ///   - WaveSpawningSystem.OnSpawnEnemy(): applies EnemyHpMult, EnemySpeedMult, EnemyGoldBonus
    ///   - EnemyAbilitySystem / EnemyMovementSystem: read pre-applied stats (already scaled)
    ///   - TowerAttackSystem: reads TowerDamageMult / TowerAttackSpeedMult / TowerRangePenalty
    ///   - GoldSystem: reads GoldEarnedMult
    ///   - GameManager: reads PlayerStartGold / PlayerStartLives at new game
    ///   - Score tracking: ScoreMultiplier is read externally for score computation
    ///
    /// Usage:
    ///   var ascension = new AscensionSystem(store, logger, gameConfig);
    ///   ascension.SelectModifier("no_gold");          // activate a modifier
    ///   ascension.SelectModifier("fast_enemies");
    ///   ascension.OnNewGame();                          // apply starting modifiers
    ///   // During wave:
    ///   ascension.ApplyEnemyScaling(enemyId);          // apply per-enemy HP/speed scaling
    /// </summary>
    public class AscensionSystem : global::BattleSystemECS.Content.Contracts.IAscensionDecorator
    {
        private readonly ComponentStore store;
        private readonly IRenderer renderer;

        // Modifier definitions (loaded once from config)
        private readonly AscensionModifierDef[] _modifierDefs;

        // Currently selected modifier IDs (player choices)
        private readonly List<string> _selectedModifiers = new List<string>();

        // Active modifiers (resolved defs with stack counts)
        private readonly Dictionary<string, AscensionModifierDef> _activeModifiers = new Dictionary<string, AscensionModifierDef>();

        // Cached multiplier products (recomputed when modifiers change)
        private float _enemyHpMult = 1.0f;
        private float _enemyDamageMult = 1.0f;
        private float _enemySpeedMult = 1.0f;
        private float _enemyGoldBonus = 0f;
        private float _enemyRegenRate = 0f;
        private float _towerDamageMult = 1.0f;
        private float _towerAttackSpeedMult = 1.0f;
        private int _towerRangePenalty = 0;
        private float _playerStartGold = -1f;
        private int _playerStartLives = -1;
        private float _goldEarnedMult = 1.0f;
        private float _scoreMultiplier = 1.0f;

        public AscensionSystem(ComponentStore store, IRenderer renderer, GameConfig gameConfig)
        {
            this.store = store;
            this.renderer = renderer;
            _modifierDefs = LoadAscensionModifiers();

            // Override with config file definitions if available
            if (gameConfig.AscensionModifiers != null && gameConfig.AscensionModifiers.Length > 0)
                _modifierDefs = gameConfig.AscensionModifiers;

            RebuildCache();
        }

        private AscensionModifierDef[] LoadAscensionModifiers()
        {
            const string path = "Data/Configs/ascension_modifiers.json";
            try
            {
                if (!File.Exists(path)) return Array.Empty<AscensionModifierDef>();
                string json = File.ReadAllText(path);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var list = new List<AscensionModifierDef>();
                if (doc.RootElement.TryGetProperty("modifiers", out var arr))
                {
                    foreach (var elem in arr.EnumerateArray())
                    {
                        var def = new AscensionModifierDef();
                        def.Id = elem.TryGetProperty("id", out var id) ? id.GetString() : "";
                        def.Name = elem.TryGetProperty("name", out var nm) ? nm.GetString() : def.Id;
                        def.Description = elem.TryGetProperty("description", out var desc) ? desc.GetString() : "";
                        def.Category = elem.TryGetProperty("category", out var cat) ? cat.GetString() : "enemy";
                        def.CanStack = elem.TryGetProperty("canStack", out var cs) && cs.GetBoolean();
                        def.MaxStack = elem.TryGetProperty("maxStack", out var ms) ? ms.GetInt32() : 1;
                        def.EnemyHpMult = elem.TryGetProperty("enemyHpMult", out var ehp) ? ehp.GetSingle() : 1.0f;
                        def.EnemyDamageMult = elem.TryGetProperty("enemyDamageMult", out var edm) ? edm.GetSingle() : 1.0f;
                        def.EnemySpeedMult = elem.TryGetProperty("enemySpeedMult", out var esm) ? esm.GetSingle() : 1.0f;
                        def.EnemyGoldBonus = elem.TryGetProperty("enemyGoldBonus", out var egb) ? egb.GetSingle() : 0f;
                        def.EnemyRegenRate = elem.TryGetProperty("enemyRegenRate", out var err) ? err.GetSingle() : 0f;
                        def.TowerDamageMult = elem.TryGetProperty("towerDamageMult", out var tdm) ? tdm.GetSingle() : 1.0f;
                        def.TowerAttackSpeedMult = elem.TryGetProperty("towerAttackSpeedMult", out var tasm) ? tasm.GetSingle() : 1.0f;
                        def.TowerRangePenalty = elem.TryGetProperty("towerRangePenalty", out var trp) ? trp.GetInt32() : 0;
                        def.PlayerStartGold = elem.TryGetProperty("playerStartGold", out var psg) ? psg.GetSingle() : -1f;
                        def.PlayerStartLives = elem.TryGetProperty("playerStartLives", out var psl) ? psl.GetInt32() : -1;
                        def.GoldEarnedMult = elem.TryGetProperty("goldEarnedMult", out var gem) ? gem.GetSingle() : 1.0f;
                        def.ScoreMultiplier = elem.TryGetProperty("scoreMultiplier", out var sm) ? sm.GetSingle() : 1.0f;
                        list.Add(def);
                    }
                }
                renderer?.Log($"[ASCENSION] Loaded {list.Count} modifier definitions from {path}");
                return list.ToArray();
            }
            catch (Exception ex)
            {
                renderer?.Log($"[ASCENSION] Failed to load modifiers: {ex.Message}");
                return Array.Empty<AscensionModifierDef>();
            }
        }

        /// <summary>
        /// Returns the number of currently active modifiers.
        /// </summary>
        public int ActiveModifierCount => _activeModifiers.Count;

        /// <summary>
        /// Returns the total score multiplier from all active modifiers.
        /// </summary>
        public float GetScoreMultiplier() => _scoreMultiplier;

        /// <summary>
        /// Returns the gold earned multiplier.
        /// </summary>
        public float GetGoldEarnedMultiplier() => _goldEarnedMult;

        /// <summary>
        /// Returns the tower damage multiplier.
        /// </summary>
        public float GetTowerDamageMultiplier() => _towerDamageMult;

        /// <summary>
        /// Returns the tower attack speed multiplier.
        /// </summary>
        public float GetTowerAttackSpeedMultiplier() => _towerAttackSpeedMult;

        /// <summary>
        /// Returns the tower range penalty (tiles).
        /// </summary>
        public int GetTowerRangePenalty() => _towerRangePenalty;

        /// <summary>
        /// Returns the enemy HP multiplier (use when spawning enemies).
        /// </summary>
        public float GetEnemyHpMultiplier() => _enemyHpMult;

        /// <summary>
        /// Returns the enemy speed multiplier.
        /// </summary>
        public float GetEnemySpeedMultiplier() => _enemySpeedMult;

        /// <summary>
        /// Returns the enemy gold bonus flat addend.
        /// </summary>
        public float GetEnemyGoldBonus() => _enemyGoldBonus;

        /// <summary>
        /// Returns the player start gold override (-1 = use default).
        /// </summary>
        public float GetPlayerStartGold() => _playerStartGold;

        /// <summary>
        /// Returns the player start lives override (-1 = use default).
        /// </summary>
        public int GetPlayerStartLives() => _playerStartLives;

        /// <summary>
        /// Activate a modifier by its string ID.
        /// Silently ignores unknown IDs.
        /// </summary>
        public void SelectModifier(string modifierId)
        {
            if (string.IsNullOrEmpty(modifierId)) return;

            var def = FindDef(modifierId);
            if (def == null)
            {
                renderer?.Log($"[ASCENSION] Unknown modifier: {modifierId}");
                return;
            }

            if (_activeModifiers.TryGetValue(modifierId, out var existing))
            {
                if (def.CanStack && existing != null)
                {
                    // Increment stack count (track as int via MaxStack usage)
                    int currentStack = GetModifierStack(modifierId);
                    int newStack = Math.Min(currentStack + 1, def.MaxStack);
                    SetModifierStack(modifierId, newStack);
                    renderer?.Log($"[ASCENSION] Stacked '{def.Name}' to x{newStack}");
                }
                // else already active and non-stacking — ignore
                return;
            }

            // Add new modifier
            _activeModifiers[modifierId] = def;
            SetModifierStack(modifierId, 1);
            RebuildCache();
            renderer?.Log($"[ASCENSION] Activated: {def.Name} — {def.Description}");
        }

        /// <summary>
        /// Deactivate a modifier by its string ID.
        /// </summary>
        public void DeselectModifier(string modifierId)
        {
            if (string.IsNullOrEmpty(modifierId)) return;
            if (!_activeModifiers.ContainsKey(modifierId)) return;

            var def = FindDef(modifierId);
            if (def != null)
            {
                renderer?.Log($"[ASCENSION] Deactivated: {def.Name}");
            }
            _activeModifiers.Remove(modifierId);
            ClearModifierStack(modifierId);
            RebuildCache();
        }

        /// <summary>
        /// Returns a list of all active modifier IDs.
        /// </summary>
        public IReadOnlyList<string> GetActiveModifierIds()
        {
            return _selectedModifiers;
        }

        /// <summary>
        /// Returns the full description of a modifier by ID.
        /// </summary>
        public string GetModifierDescription(string modifierId)
        {
            var def = FindDef(modifierId);
            return def?.Description ?? string.Empty;
        }

        /// <summary>
        /// Returns the display name of a modifier.
        /// </summary>
        public string GetModifierName(string modifierId)
        {
            var def = FindDef(modifierId);
            return def?.Name ?? modifierId;
        }

        /// <summary>
        /// Applies per-enemy scaling to an enemy that was just spawned.
        /// Called by WaveSpawningSystem when an enemy entity is created.
        /// </summary>
        public void ApplyEnemyScaling(int enemyId)
        {
            if (!store.EnemyActive[enemyId]) return;

            // HP: apply multiplier on top of base HP
            float baseHp = store.EnemyMaxHealth[enemyId];
            store.EnemyMaxHealth[enemyId] = baseHp * _enemyHpMult;
            store.EnemyHealth[enemyId] = Math.Min(store.EnemyHealth[enemyId], store.EnemyMaxHealth[enemyId]);

            // Speed: apply multiplier (already has difficulty/wave scaling in EnemyMoveSpeed)
            store.EnemyMoveSpeed[enemyId] *= _enemySpeedMult;
        }

        /// <summary>
        /// Recomputes cached multipliers from the current set of active modifiers.
        /// Called whenever modifiers change.
        /// </summary>
        private void RebuildCache()
        {
            _enemyHpMult = 1.0f;
            _enemyDamageMult = 1.0f;
            _enemySpeedMult = 1.0f;
            _enemyGoldBonus = 0f;
            _enemyRegenRate = 0f;
            _towerDamageMult = 1.0f;
            _towerAttackSpeedMult = 1.0f;
            _towerRangePenalty = 0;
            _playerStartGold = -1f;
            _playerStartLives = -1;
            _goldEarnedMult = 1.0f;
            _scoreMultiplier = 1.0f;

            foreach (var kvp in _activeModifiers)
            {
                var def = kvp.Value;
                int stack = GetModifierStack(kvp.Key);
                float stackMult = stack; // e.g. stack=3 → multiply effect by 3

                _enemyHpMult       += (def.EnemyHpMult - 1.0f) * stackMult;
                _enemyDamageMult   *= (float)Math.Pow(def.EnemyDamageMult, stack);
                _enemySpeedMult    *= (float)Math.Pow(def.EnemySpeedMult, stack);
                _enemyGoldBonus    += def.EnemyGoldBonus * stackMult;
                _enemyRegenRate    += def.EnemyRegenRate * stackMult;
                _towerDamageMult   *= (float)Math.Pow(def.TowerDamageMult, stack);
                _towerAttackSpeedMult *= (float)Math.Pow(def.TowerAttackSpeedMult, stack);
                _towerRangePenalty += def.TowerRangePenalty * stack;
                if (def.PlayerStartGold >= 0)
                    _playerStartGold = _playerStartGold < 0 ? def.PlayerStartGold : Math.Min(_playerStartGold, def.PlayerStartGold);
                if (def.PlayerStartLives >= 0)
                    _playerStartLives = _playerStartLives < 0 ? def.PlayerStartLives : Math.Min(_playerStartLives, def.PlayerStartLives);
                _goldEarnedMult    *= (float)Math.Pow(def.GoldEarnedMult, stack);
                _scoreMultiplier   *= (float)Math.Pow(def.ScoreMultiplier, stack);
            }

            // Clamp reasonable ranges
            _enemyHpMult = Math.Max(0.1f, _enemyHpMult);
            _enemyDamageMult = Math.Max(0.1f, _enemyDamageMult);
            _enemySpeedMult = Math.Max(0.1f, _enemySpeedMult);
            _towerDamageMult = Math.Max(0.1f, _towerDamageMult);
            _towerAttackSpeedMult = Math.Max(0.1f, _towerAttackSpeedMult);
            _goldEarnedMult = Math.Max(0.0f, _goldEarnedMult);
            _scoreMultiplier = Math.Max(1.0f, _scoreMultiplier);
        }

        /// <summary>
        /// Returns the modifier definition by ID, or null if not found.
        /// </summary>
        private AscensionModifierDef FindDef(string id)
        {
            foreach (var def in _modifierDefs)
            {
                if (def.Id == id) return def;
            }
            return null;
        }

        // ── Per-modifier stack tracking via ComponentStore arrays ─────────────────
        // We store stacks as parallel arrays in the ComponentStore.
        // Stack values are indexed by a deterministic hash of the modifier ID.

        private const int MAX_TRACKED_STACKS = 64;

        private static readonly Dictionary<string, int> _stackIndexMap = new Dictionary<string, int>();
        private static int _nextStackIndex = 0;

        private int GetModifierStackIndex(string modifierId)
        {
            if (_stackIndexMap.TryGetValue(modifierId, out var idx))
                return idx;
            int newIdx = _nextStackIndex++;
            _stackIndexMap[modifierId] = newIdx;
            return newIdx;
        }

        private int GetModifierStack(string modifierId)
        {
            int idx = GetModifierStackIndex(modifierId);
            if (idx >= store.AscensionModifierStacks.Length) return 0;
            return store.AscensionModifierStacks[idx];
        }

        private void SetModifierStack(string modifierId, int stack)
        {
            int idx = GetModifierStackIndex(modifierId);
            if (idx < store.AscensionModifierStacks.Length)
                store.AscensionModifierStacks[idx] = stack;
        }

        private void ClearModifierStack(string modifierId)
        {
            if (_stackIndexMap.TryGetValue(modifierId, out var idx))
                _stackIndexMap.Remove(modifierId);
        }
    }
}

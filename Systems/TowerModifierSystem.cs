#nullable enable
using System;
using BattleSystemECS.Core;
using BattleSystemECS.Config;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Round 145 Direction 3 — Per-Tower Modifier Pool (塔类型专精重随).
    ///
    /// Each tower rolls ONE modifier from a designer-configurable weighted pool at
    /// placement time. The rolled modifier persists for the tower's lifetime and is
    /// read by combat systems (e.g. TowerAttackSystem, BuffSystem) when their trigger
    /// event fires — i.e. the modifier is consumed lazily, not polled per frame.
    ///
    /// Distinct from the affix system (Round 35):
    ///   • Affixes: 1-3 slots per tower, stackable stat rerolls, BuildPhase-only.
    ///   • Modifiers: 1 slot per tower, single roll, persistent (placement → destroy).
    ///   • Modifiers carry a designer-defined "Stat" string that consumers branch on.
    ///
    /// Design:
    ///   • Inert when TowerModifiers[] is empty (most common case in tests / minimal config).
    ///     RollAtPlacement() then becomes a no-op: -1 stays -1.
    ///   • Weighted random uses per-modifier Weight (1..N, default 1). Sum of all weights
    ///     must be > 0 — the loader clamps weight=0 to 1 to keep the contract safe.
    ///   • Optional `minRarity` gate lets a designer restrict the roll to Rare+ towers
    ///     (e.g. boss-elite towers only). The default 0 = no gate.
    ///   • RerollModifier(towerId) is the "reroll for gold" API — drops the existing
    ///     modifier and re-rolls from the pool. The caller is responsible for any gold
    ///     charge (kept out of the system so it stays pure).
    ///
    /// Hot-path impact: zero. All public methods are BuildPhase-only (placement /
    /// Reroll). The read helpers (GetTowerModifierId / GetModifierStat / etc.) are
    /// pure array reads and may be called from any frame phase.
    /// </summary>
    public class TowerModifierSystem
    {
        private readonly ComponentStore store;
        private readonly Random rng;
        // GameConfig reference — needed to look up the modifier pool (TowerModifiers[])
        // and resolve an index back to a TowerModifierDef for read helpers. Optional:
        // when null, the system is inert (RollAtPlacement → -1, read helpers → "" / 0).
        private GameConfig? _gameConfig;

        // Optional minimum rarity gate (0 = no gate, 4 = Legendary only).
        // Designer-tunable via SetMinRarity(int) for variant rulesets.
        private int _minRarity = 0;

        public TowerModifierSystem(ComponentStore store, GameConfig? gameConfig = null, int seed = 13579)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this._gameConfig = gameConfig;
            this.rng = new Random(seed);
        }

        /// <summary>
        /// Optional late-binding of the designer's GameConfig — required for any pool
        /// lookup. Without it, the modifier pool is treated as empty (inert system).
        /// </summary>
        public void SetGameConfig(GameConfig gameConfig) => _gameConfig = gameConfig;

        /// <summary>
        /// Optional late-binding of the designer's min-rarity gate.
        /// 0 (default) means all rarities are eligible.
        /// </summary>
        public void SetMinRarity(int minRarity) => _minRarity = minRarity < 0 ? 0 : (minRarity > 4 ? 4 : minRarity);

        /// <summary>Returns the configured minimum rarity gate (0 = none).</summary>
        public int GetMinRarity() => _minRarity;

        /// <summary>
        /// Roll a single modifier for a freshly-placed tower and store it.
        /// Called by TowerPlacementSystem after AddTower() returns.
        ///
        /// Safe no-op when:
        ///   • towerId is invalid (>= MAX_ENTITIES or non-positive)
        ///   • The modifier pool is empty
        ///   • The tower is not active (TActive==false — guard against caller bugs)
        ///
        /// Returns the index into GameConfig.TowerModifiers[] that was rolled,
        /// or -1 if no modifier was assigned.
        /// </summary>
        public int RollAtPlacement(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return -1;
            if (!store.TowerActive[towerId]) return -1;

            int idx = RollWeightedIndex();
            if (idx < 0)
            {
                store.SetTowerModifier(towerId, -1, 0f, 0);
                return -1;
            }
            var def = _gameConfig?.GetTowerModifierDef(idx);
            store.SetTowerModifier(towerId, idx, def?.Magnitude ?? 0f, def?.Rarity ?? 0);
            return idx;
        }

        /// <summary>
        /// Drop the current modifier (if any) and roll a fresh one.
        /// Use this for "reforge" / "re-roll for gold" gameplay hooks.
        /// Returns the new modifier index, or -1 if no modifier was rolled.
        /// </summary>
        public int RerollModifier(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return -1;
            if (!store.TowerActive[towerId]) return -1;
            // Clear first to avoid the (small) chance of rolling the same one — caller can
            // re-apply if they want identical; the reroll semantics here are "fresh roll".
            store.SetTowerModifier(towerId, -1, 0f, 0);
            return RollAtPlacement(towerId);
        }

        /// <summary>
        /// Explicitly clear a tower's modifier (e.g. on sale / destroy).
        /// No-op for invalid entities.
        /// </summary>
        public void ClearModifier(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return;
            store.SetTowerModifier(towerId, -1, 0f, 0);
        }

        // ════════════════════════════════════════════════════════════════════
        // Read helpers — safe to call from any frame phase.
        // All return safe defaults for invalid / unrolled entities.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Returns the modifier index into GameConfig.TowerModifiers[], or -1.</summary>
        public int GetModifierId(int towerId) => store.GetTowerModifierId(towerId);

        /// <summary>Returns the cached magnitude (0f if no modifier).</summary>
        public float GetModifierMagnitude(int towerId) => store.GetTowerModifierMagnitude(towerId);

        /// <summary>Returns the cached rarity (0 if no modifier).</summary>
        public int GetModifierRarity(int towerId) => store.GetTowerModifierRarity(towerId);

        /// <summary>Returns the modifier's "Stat" string ("" if no modifier).</summary>
        public string GetModifierStat(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return "";
            int idx = store.GetTowerModifierId(towerId);
            if (idx < 0) return "";
            var def = _gameConfig?.GetTowerModifierDef(idx);
            return def?.Stat ?? "";
        }

        /// <summary>Returns the modifier's display name ("" if no modifier).</summary>
        public string GetModifierName(int towerId)
        {
            if (!ComponentStore.IsValidEntity(towerId)) return "";
            int idx = store.GetTowerModifierId(towerId);
            if (idx < 0) return "";
            var def = _gameConfig?.GetTowerModifierDef(idx);
            return def?.Name ?? "";
        }

        /// <summary>True iff the tower has a non-inert modifier rolled.</summary>
        public bool HasModifier(int towerId) => store.HasTowerModifier(towerId);

        // ════════════════════════════════════════════════════════════════════
        // Internal — weighted random + min-rarity filter.
        // ════════════════════════════════════════════════════════════════════

        private int RollWeightedIndex()
        {
            var pool = _gameConfig?.TowerModifiers;
            if (pool == null || pool.Length == 0) return -1;

            // Sum weights for the eligible subset (>= minRarity).
            int total = 0;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i] == null) continue;
                if (pool[i].Rarity < _minRarity) continue;
                int w = pool[i].Weight;
                if (w < 1) w = 1;
                total += w;
            }
            if (total <= 0) return -1;

            double r = rng.NextDouble() * total;
            double cum = 0;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i] == null) continue;
                if (pool[i].Rarity < _minRarity) continue;
                int w = pool[i].Weight;
                if (w < 1) w = 1;
                cum += w;
                if (r < cum) return i;
            }
            // Floating-point edge: return the last eligible index.
            for (int i = pool.Length - 1; i >= 0; i--)
            {
                if (pool[i] == null) continue;
                if (pool[i].Rarity < _minRarity) continue;
                return i;
            }
            return -1;
        }
    }
}

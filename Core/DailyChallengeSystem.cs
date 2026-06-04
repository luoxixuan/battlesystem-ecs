#nullable enable
using System;
using System.Collections.Generic;
using BattleSystemECS.Config;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Daily Challenge / Rotating Seed system (Round 105 Direction 9).
    ///
    /// Generates a deterministic, date-keyed "daily run": the same calendar date
    /// always produces the same seed, which selects a fixed set of modifiers from
    /// a JSON-loaded pool. Players get a fresh combination every day (Slay-the-Spire
    /// / Arknights "daily seed" pattern) — the only way to retry is to wait until
    /// tomorrow.
    ///
    /// Design:
    /// - **Zero-ECS-overhead**: no per-frame work. The system runs ONCE at game
    ///   startup (called from GameConfigLoader.LoadDailyModifiers) and resolves
    ///   a snapshot of modifiers into GameConfig.Daily.
    /// - **Deterministic by date**: seed = FNV-1a(date.ToString("yyyy-MM-dd")).
    ///   Same date → same seed → same modifier picks (verified by tests).
    /// - **Stateless**: no stored mutable state. Each call to
    ///   <see cref="ResolveForDate"/> is pure given the same input pool.
    /// - **Backward-compatible**: when <c>daily_modifiers.json</c> is missing or
    ///   empty, <see cref="ResolveForDate"/> returns an empty list and
    ///   <see cref="ApplyToConfig"/> is a no-op. Stock towers / waves are
    ///   unaffected.
    /// </summary>
    public static class DailyChallengeSystem
    {
        /// <summary>
        /// FNV-1a (32-bit) hash of a string — small, fast, well-distributed for
        /// short ASCII inputs like ISO date strings. Used here purely as a seed
        /// generator, NOT for security. The offset basis and prime match the
        /// reference algorithm.
        /// </summary>
        public static int HashDateSeed(string dateIso)
        {
            if (string.IsNullOrEmpty(dateIso)) return 0;
            // FNV-1a 32-bit constants
            const uint FNV_OFFSET = 2166136261u;
            const uint FNV_PRIME = 16777619u;
            uint hash = FNV_OFFSET;
            for (int i = 0; i < dateIso.Length; i++)
            {
                hash ^= dateIso[i];
                hash *= FNV_PRIME;
            }
            return unchecked((int)hash);
        }

        /// <summary>
        /// Pick <paramref name="count"/> distinct indices from
        /// <paramref name="poolSize"/> using a seeded xorshift32 RNG. Used to
        /// select modifier slots without replacement so the same modifier is
        /// never applied twice in one daily run.
        ///
        /// Deterministic for a given (seed, count, poolSize) tuple — verified by
        /// DailyChallengeTests.SeedSelectsSameModifiersAcrossSameDate.
        /// </summary>
        /// <param name="seed">Seed value (typically from <see cref="HashDateSeed"/>).</param>
        /// <param name="count">Number of distinct indices to return. Clamped to
        /// [0, poolSize] — if count &gt; poolSize we return poolSize entries
        /// (a full permutation, no repeats possible).</param>
        /// <param name="poolSize">Total number of available modifiers in the pool.</param>
        /// <returns>List of distinct indices in [0, poolSize), length = clamped count.</returns>
        public static List<int> SeedSelectIndices(int seed, int count, int poolSize)
        {
            var result = new List<int>(Math.Min(count, poolSize));
            if (poolSize <= 0) return result;
            int clampedCount = Math.Min(count, poolSize);
            if (clampedCount <= 0) return result;

            // xorshift32 — single 32-bit state, deterministic, fast
            uint state = unchecked((uint)seed);
            if (state == 0) state = 0x9E3779B9u; // avoid degenerate all-zero stream

            // Mark slots used. Heap-allocate lazily for poolSize > 64 — small
            // daily pools (typically 16-32) use a stack-style bitmask instead.
            bool[] used;
            if (poolSize <= 64)
            {
                ulong usedMask = 0ul;
                int picked = 0;
                while (picked < clampedCount)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    int idx = (int)(state % (uint)poolSize);
                    ulong bit = 1ul << idx;
                    if ((usedMask & bit) == 0)
                    {
                        usedMask |= bit;
                        result.Add(idx);
                        picked++;
                    }
                }
                return result;
            }

            used = new bool[poolSize];
            int picked2 = 0;
            int safety = 0;
            const int safetyCap = 1024;
            while (picked2 < clampedCount && safety < safetyCap)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                int idx = (int)(state % (uint)poolSize);
                if (!used[idx])
                {
                    used[idx] = true;
                    result.Add(idx);
                    picked2++;
                }
                safety++;
            }
            return result;
        }

        /// <summary>
        /// Resolve today's daily challenge. Pure function — no global state mutation.
        /// Caller is expected to pass the loaded modifier pool and to call
        /// <see cref="ApplyToConfig"/> with the returned list.
        /// </summary>
        /// <param name="pool">The list of available modifiers (loaded from JSON).</param>
        /// <param name="date">The date to resolve for. Pass <c>DateTime.Today</c>
        /// for the production path; tests pass a specific date.</param>
        /// <param name="modifierCount">How many modifiers to pick (default 3).</param>
        /// <returns>DailyChallengeResult with seed and selected modifiers.</returns>
        public static DailyChallengeResult ResolveForDate(
            List<DailyModifierDef> pool, DateTime date, int modifierCount = 3)
        {
            if (pool == null || pool.Count == 0 || modifierCount <= 0)
            {
                return new DailyChallengeResult
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    Seed = 0,
                    Selected = new List<DailyModifierDef>()
                };
            }
            string iso = date.ToString("yyyy-MM-dd");
            int seed = HashDateSeed(iso);
            var indices = SeedSelectIndices(seed, modifierCount, pool.Count);
            var selected = new List<DailyModifierDef>(indices.Count);
            foreach (int i in indices)
            {
                if (i >= 0 && i < pool.Count) selected.Add(pool[i]);
            }
            return new DailyChallengeResult
            {
                Date = iso,
                Seed = seed,
                Selected = selected
            };
        }

        /// <summary>
        /// Apply a resolved daily challenge to a GameConfig instance. The applied
        /// values are:
        /// - <c>DailyDamageMult</c> = product of all selected damage multipliers
        /// - <c>DailyGoldMult</c>  = product of all selected gold multipliers
        /// - <c>DailyEnemyHpMult</c> = product of all selected enemy HP multipliers
        /// - <c>DailyStartingGoldBonus</c> = sum of all selected starting-gold bonuses
        ///
        /// Defaults are 1.0 (multiplicative) or 0 (additive) — when the result is
        /// empty, the GameConfig stays at neutral values and behavior is
        /// indistinguishable from a non-daily run.
        /// </summary>
        public static void ApplyToConfig(GameConfig config, DailyChallengeResult result)
        {
            if (config == null || result == null || result.Selected == null) return;
            float dmg = 1.0f;
            float gold = 1.0f;
            float enemyHp = 1.0f;
            float startGoldBonus = 0f;
            foreach (var mod in result.Selected)
            {
                if (mod == null) continue;
                if (mod.DamageMult > 0f) dmg *= mod.DamageMult;
                if (mod.GoldMult > 0f) gold *= mod.GoldMult;
                if (mod.EnemyHpMult > 0f) enemyHp *= mod.EnemyHpMult;
                startGoldBonus += mod.StartingGoldBonus;
            }
            config.DailyDamageMult = dmg;
            config.DailyGoldMult = gold;
            config.DailyEnemyHpMult = enemyHp;
            config.DailyStartingGoldBonus = startGoldBonus;
            config.DailyLastResult = result;
        }
    }

    /// <summary>
    /// Result of a daily challenge resolution. Immutable snapshot returned by
    /// <see cref="DailyChallengeSystem.ResolveForDate"/> — caller is expected
    /// to either discard it or pass it to
    /// <see cref="DailyChallengeSystem.ApplyToConfig"/>.
    /// </summary>
    public class DailyChallengeResult
    {
        /// <summary>ISO-8601 date string ("yyyy-MM-dd") this challenge was resolved for.</summary>
        public string Date { get; set; } = "";
        /// <summary>FNV-1a hash of the date — exposed for debugging / telemetry.</summary>
        public int Seed { get; set; } = 0;
        /// <summary>Selected modifier definitions (length = clamped to pool size).</summary>
        public List<DailyModifierDef> Selected { get; set; } = new List<DailyModifierDef>();
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Wave Preview / Scouting System — Roguelike TD "know your enemy" feature.
    ///
    /// Each frame (or on wave-start trigger), pre-computes a preview of the next wave's
    /// enemy composition, using WaveConfig.EnemyTypes (multi-type) or MonsterType fallback.
    /// The preview is gated by ComponentStore.PlayerWavePreviewLevel[playerId]:
    ///   0 = None  → no preview
    ///   1 = Vague → only total enemy count and unique monster-type names
    ///   2 = Precise → adds per-type count, HP, damage, armor, skills, shield, flying flag
    ///
    /// In benchmark mode (non-interactive), preview is always computed and cached but
    /// never logged — keeps perf benchmarks stable.
    ///
    /// Integration:
    ///   - Constructed in SystemRegistry.CreateAll
    ///   - Subscribe() subscribes to WaveSpawningSystem.OnWaveStart
    ///   - GetNextWaveSummary() returns preview string (or empty when disabled)
    /// </summary>
    public class WavePreviewSystem
    {
        private readonly ComponentStore store;
        private readonly GameConfig gameConfig;
        private readonly int playerId;

        // Cached preview of the next wave (recomputed on OnWaveStart).
        private int _cachedPreviewWaveNumber = -1;   // 0 = no preview cached
        private List<WavePreviewEntry> _cachedEntries = new List<WavePreviewEntry>(8);
        private int _cachedTotalCount;

        // Public events (subscribed by UI/renderer)
        public event Action<WavePreviewData> OnPreviewUpdated;

        public WavePreviewSystem(ComponentStore store, GameConfig gameConfig, int playerId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.gameConfig = gameConfig ?? throw new ArgumentNullException(nameof(gameConfig));
            this.playerId = playerId;
        }

        /// <summary>
        /// Wire to WaveSpawningSystem.OnWaveStart. Idempotent (guards against double-subscribe).
        /// On each wave start, pre-computes the preview of the *next* wave.
        /// </summary>
        public void HandleWaveStart(int level, int wave)
        {
            // On each wave start, pre-compute the preview of the wave AFTER this one
            // using the current level/wave from the wave spawner. This avoids redundant
            // double-computation and stale cache from a separate inline Update() call.
            if (level <= 0) level = 1;
            if (wave <= 0) return;
            RecomputePreview(level, wave + 1);
        }

        /// <summary>
        /// Called each frame from FrameScheduler (BuildPhase / Intermission is the typical
        /// "decision" phase; safe to also call in WavePhase after a wave completes). Recomputes
        /// the next-wave preview if the cached wave is stale.
        /// </summary>
        public void Update(int currentLevel, int currentWave)
        {
            // currentWave is 1-based (the wave about to start or in progress).
            // We preview the *next* one.
            int nextWave = currentWave + 1;
            if (nextWave == _cachedPreviewWaveNumber) return;

            RecomputePreview(currentLevel, nextWave);
        }

        private void RecomputePreview(int currentLevel, int nextWave)
        {
            _cachedEntries.Clear();
            _cachedTotalCount = 0;
            // Note: _cachedPreviewWaveNumber is set AFTER all early-return validations below,
            // so that HasPreview correctly reports "no preview" when validation fails.

            int level = currentLevel > 0 ? currentLevel : 1;
            var levelConfig = gameConfig.GetLevelConfig(level);
            if (levelConfig == null || levelConfig.Waves == null) return;

            // nextWave is 1-based, Waves are 0-indexed
            int idx = nextWave - 1;
            if (idx < 0 || idx >= levelConfig.Waves.Count) return;

            var wave = levelConfig.Waves[idx];
            if (wave == null) return;

            // All early-return validations passed — mark preview as valid from this point on.
            _cachedPreviewWaveNumber = nextWave;

            // Build per-type entries using EnemyTypes[] (multi-type) or MonsterType fallback
            if (wave.EnemyTypes != null && wave.EnemyTypes.Count > 0)
            {
                foreach (var entry in wave.EnemyTypes)
                {
                    if (entry == null || entry.Count <= 0) continue;
                    var monster = gameConfig.GetMonsterConfig(entry.MonsterType);
                    // Apply rhythm scaling to preview count so the UI matches what will actually spawn
                    int scaledCount = wave.GetEnemyCountForType(entry.MonsterType);
                    _cachedEntries.Add(new WavePreviewEntry
                    {
                        MonsterType = entry.MonsterType,
                        Count = scaledCount,
                        Name = monster?.Name ?? entry.MonsterType,
                        Health = monster?.Health ?? 0f,
                        Damage = monster?.Damage ?? 0f,
                        Armor = monster?.Armor ?? 0f,
                        Skills = monster?.Skills ?? new List<string>(),
                        IsFlying = monster?.IsFlying ?? false,
                        HasShield = (monster?.Shield ?? 0f) > 0f
                    });
                    _cachedTotalCount += scaledCount;
                }
            }
            else if (!string.IsNullOrEmpty(wave.MonsterType))
            {
                var monster = gameConfig.GetMonsterConfig(wave.MonsterType);
                int scaledCount = wave.GetEnemyCountForType(wave.MonsterType);
                _cachedEntries.Add(new WavePreviewEntry
                {
                    MonsterType = wave.MonsterType,
                    Count = scaledCount,
                    Name = monster?.Name ?? wave.MonsterType,
                    Health = monster?.Health ?? 0f,
                    Damage = monster?.Damage ?? 0f,
                    Armor = monster?.Armor ?? 0f,
                    Skills = monster?.Skills ?? new List<string>(),
                    IsFlying = monster?.IsFlying ?? false,
                    HasShield = (monster?.Shield ?? 0f) > 0f
                });
                _cachedTotalCount += scaledCount;
            }

            OnPreviewUpdated?.Invoke(new WavePreviewData
            {
                Level = level,
                WaveNumber = nextWave,
                TotalCount = _cachedTotalCount,
                // Defensive copy: subscribers may hold this past the next RecomputePreview
                // call, which would otherwise clear the same list reference.
                Entries = new List<WavePreviewEntry>(_cachedEntries)
            });
        }

        /// <summary>
        /// Build a human-readable summary gated by the player's preview level.
        /// Returns empty string when preview is disabled (level=0) or no preview cached.
        /// </summary>
        public string GetNextWaveSummary()
        {
            int level = store.PlayerWavePreviewLevel[playerId];
            if (level <= 0) return string.Empty;
            if (_cachedPreviewWaveNumber <= 0) return string.Empty;
            if (_cachedEntries.Count == 0 && _cachedTotalCount == 0) return string.Empty;

            var sb = new StringBuilder(128);
            sb.Append("[SCOUT] Wave ").Append(_cachedPreviewWaveNumber)
              .Append(" — ").Append(_cachedTotalCount).Append(" enemies");

            if (level >= 1)
            {
                sb.Append(" | Types: ");
                bool first = true;
                foreach (var e in _cachedEntries)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(e.Name).Append('×').Append(e.Count);
                }
            }

            if (level >= 2)
            {
                sb.Append(" | Details: ");
                bool first = true;
                foreach (var e in _cachedEntries)
                {
                    if (!first) sb.Append("; ");
                    first = false;
                    sb.Append(e.Name)
                      .Append("(HP:").Append((int)e.Health)
                      .Append(",Dmg:").Append((int)e.Damage)
                      .Append(",Arm:").Append((int)e.Armor)
                      .Append(",Fly:").Append(e.IsFlying ? 'Y' : 'N')
                      .Append(",Shield:").Append(e.HasShield ? 'Y' : 'N')
                      .Append(",Skills:").Append(e.Skills.Count)
                      .Append(')');
                }
            }

            return sb.ToString();
        }

        public bool HasPreview => _cachedPreviewWaveNumber > 0;
        public int CachedPreviewWave => _cachedPreviewWaveNumber;
        public int CachedTotalCount => _cachedTotalCount;
        public IReadOnlyList<WavePreviewEntry> CachedEntries => _cachedEntries;
    }

    /// <summary>Per-monster-type summary entry for wave preview.</summary>
    public class WavePreviewEntry
    {
        public string MonsterType;
        public int Count;
        public string Name;
        public float Health;
        public float Damage;
        public float Armor;
        public List<string> Skills = new List<string>();
        public bool IsFlying;
        public bool HasShield;
    }

    /// <summary>Snapshot pushed to subscribers when a preview is recomputed.</summary>
    public class WavePreviewData
    {
        public int Level;
        public int WaveNumber;
        public int TotalCount;
        public List<WavePreviewEntry> Entries = new List<WavePreviewEntry>();
    }
}

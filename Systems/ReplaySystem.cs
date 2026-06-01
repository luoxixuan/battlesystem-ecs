#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BattleSystemECS.Config;
using BattleSystemECS.Core;

namespace BattleSystemECS.Systems
{
    /// <summary>
    /// Replay / Recording System — lightweight per-frame telemetry for offline analysis.
    ///
    /// Records one JSON object per frame containing key game-state signals:
    ///   - Frame number (turn), game phase, current level/wave
    ///   - Player stats: health, max-health, gold, base lives, current level
    ///   - Counts: active enemies, active towers, total kills (cumulative)
    ///   - FPS sample for the frame
    ///
    /// Storage format: JSONL (one JSON object per line) at `Data/Replays/replay-{sessionId}.jsonl`.
    /// Each line is independently parseable — corrupt/lost lines don't break the whole file.
    ///
    /// Activation: gated by `GameConfig.Replay.Enabled` (default false). When disabled
    /// RecordFrame is a single boolean short-circuit (no allocation, no I/O, zero hot-path
    /// cost beyond the flag check).
    ///
    /// Integration:
    ///   - Constructed in SystemRegistry.CreateAll
    ///   - FrameScheduler.Tick() calls RecordFrame(turn, fps) at end of WavePhase
    ///   - Flush() / Dispose() finalize the file
    ///   - Load() deserializes back to in-memory frames for downstream analysis
    /// </summary>
    public class ReplaySystem : IDisposable
    {
        private readonly ComponentStore _store;
        private readonly ReplayConfig _config;
        private readonly int _playerId;
        private readonly string _filePath;
        private StreamWriter? _writer;
        private bool _disposed;
        private bool _headerWritten;

        // Running statistics
        private int _previousKills;
        private int _frameCount;
        private int _maxFrames;

        // Cached JsonSerializer options (avoid per-frame allocation)
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        public string FilePath => _filePath;
        public int RecordedFrameCount => _frameCount;
        public bool IsActive => _writer != null;

        public ReplaySystem(ComponentStore store, GameConfig gameConfig, int playerId,
                            string replayDir = "Data/Replays", string? sessionId = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _config = gameConfig?.Replay ?? new ReplayConfig();
            _playerId = playerId;
            _maxFrames = _config.MaxFrames > 0 ? _config.MaxFrames : int.MaxValue;

            sessionId ??= DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            _filePath = Path.Combine(replayDir, $"replay-{sessionId}.jsonl");

            // Lazy directory creation — only if recording is enabled
            if (_config.Enabled)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
        }

        /// <summary>
        /// Open the output stream. No-op when disabled.
        /// </summary>
        public void BeginRecording()
        {
            if (!_config.Enabled) return;
            if (_writer != null) return; // idempotent

            try
            {
                _writer = new StreamWriter(_filePath, append: false) { AutoFlush = false };
                _headerWritten = false;
                _frameCount = 0;
                _previousKills = _store.TotalKills;
                WriteHeader();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disable recording on I/O failure so we don't repeatedly throw
                _writer?.Dispose();
                _writer = null;
                Console.Error.WriteLine($"[ReplaySystem] Failed to open {_filePath}: {ex.Message}");
            }
        }

        private void WriteHeader()
        {
            if (_writer == null || _headerWritten) return;
            var header = new
            {
                type = "header",
                version = 1,
                timestamp = DateTime.UtcNow.ToString("O"),
                maxEntities = ComponentStore.MAX_ENTITIES,
                playerId = _playerId
            };
            _writer.WriteLine(JsonSerializer.Serialize(header, _jsonOptions));
            _headerWritten = true;
        }

        /// <summary>
        /// Record one frame snapshot. No-op when recording is disabled or the writer
        /// failed to open. Caller passes the externally-measured FPS (so we don't
        /// add a Stopwatch to the hot path).
        /// </summary>
        public void RecordFrame(int turn, int level, int wave, float fps)
        {
            if (_writer == null) return;
            if (_frameCount >= _maxFrames) return;

            // Snapshot reads (single struct, no shared-state mutation under our read).
            // Read in dependency order: counts first (cheap), then per-entity aggregates.
            int activeEnemies = _store.GetCachedActiveEnemyIds().Count;
            int activeTowers = _store.ActiveTowerIds.Count;
            int kills = _store.TotalKills;
            int killsThisFrame = kills - _previousKills;
            _previousKills = kills;

            var frame = new
            {
                type = "frame",
                turn,
                level,
                wave,
                fps = (double)fps,
                activeEnemies,
                activeTowers,
                totalKills = kills,
                killsThisFrame,
                playerHealth = _store.PlayerCurrentHealth[_playerId],
                playerMaxHealth = _store.PlayerMaxHealth[_playerId],
                playerGold = _store.PlayerGold[_playerId],
                playerLives = _store.PlayerBaseLives[_playerId],
                playerLevel = _store.PlayerCurrentLevel[_playerId]
            };

            _writer.WriteLine(JsonSerializer.Serialize(frame, _jsonOptions));

            // Auto-flush every N frames to bound data loss on crash
            _frameCount++;
            if (_config.FlushInterval > 0 && _frameCount % _config.FlushInterval == 0)
            {
                _writer.Flush();
            }
        }

        /// <summary>
        /// Close the output stream. Safe to call multiple times.
        /// </summary>
        public void EndRecording()
        {
            if (_writer == null) return;
            try
            {
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Best-effort flush; ignore flush errors during shutdown
            }
        }

        /// <summary>
        /// Read a previously-recorded replay into memory. Returns empty list if file
        /// doesn't exist. Lines that fail to parse are silently skipped (forward-compat).
        /// </summary>
        public static List<ReplayFrame> Load(string filePath)
        {
            var frames = new List<ReplayFrame>();
            if (!File.Exists(filePath)) return frames;

            // StreamReader yields one line at a time — no full-file allocation
            using var reader = new StreamReader(filePath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("type", out var t) &&
                        t.GetString() == "frame")
                    {
                        frames.Add(JsonSerializer.Deserialize<ReplayFrame>(line) ?? new ReplayFrame());
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed lines — partial replays are still useful
                }
            }
            return frames;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EndRecording();
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Strongly-typed per-frame snapshot for analysis.</summary>
    public class ReplayFrame
    {
        public string? type { get; set; }
        public int turn { get; set; }
        public int level { get; set; }
        public int wave { get; set; }
        public double fps { get; set; }
        public int activeEnemies { get; set; }
        public int activeTowers { get; set; }
        public int totalKills { get; set; }
        public int killsThisFrame { get; set; }
        public float playerHealth { get; set; }
        public float playerMaxHealth { get; set; }
        public float playerGold { get; set; }
        public int playerLives { get; set; }
        public int playerLevel { get; set; }
    }
}

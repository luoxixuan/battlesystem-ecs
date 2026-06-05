using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// Curve shape selector for <see cref="CurveTable"/>.
    ///
    /// Each shape expects different JSON fields in <see cref="CurveDef"/>:
    ///   - Identity:     always 1.0 (no scaling, hot-path default)
    ///   - Linear:       1 + (x - 1) * Coefficient   (matches legacy 1.0f + (wave-1) * X)
    ///   - Exponential:  Coefficient ^ (x - 1)        (compound multiplier per step)
    ///   - Logarithmic:  1 + log(x + 1) * Coefficient (front-loaded growth)
    ///   - Sigmoid:      1 + Coefficient / (1 + exp(-Steepness * (x - Midpoint)))
    ///   - Piecewise:    linear interpolation of (ControlPoints[i].X, ControlPoints[i].Y) sorted by X
    /// </summary>
    public enum CurveType
    {
        Identity = 0,
        Linear = 1,
        Exponential = 2,
        Logarithmic = 3,
        Sigmoid = 4,
        Piecewise = 5,
    }

    /// <summary>
    /// Static description of a single named curve. Loaded from <c>Data/Configs/curves.json</c>.
    /// Immutable; safe to share across threads once loaded.
    /// </summary>
    public class CurveDef
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "Identity";

        // Linear / Exponential / Logarithmic: linear growth coefficient (per-step, x-1 normalized).
        public float Coefficient { get; set; } = 0f;

        // Sigmoid: inflection x and steepness k.
        public float Midpoint { get; set; } = 10f;
        public float Steepness { get; set; } = 0.5f;

        // Piecewise: ordered list of (X, Y) control points. Will be sorted on load.
        public List<float[]> ControlPoints { get; set; } = new List<float[]>();

        // Resolved (cached) enum form of Type. Set once during load.
        public CurveType ResolvedType { get; set; } = CurveType.Identity;

        /// <summary>
        /// Evaluates the curve at x. x is wave index (1-based) for difficulty scaling,
        /// but the math is domain-agnostic — any non-negative x works.
        /// </summary>
        public float Evaluate(float x)
        {
            switch (ResolvedType)
            {
                case CurveType.Identity:
                    return 1.0f;

                case CurveType.Linear:
                    // 1 + (x - 1) * Coefficient  →  at x=1 returns 1.0 (legacy compatibility)
                    return 1.0f + (x - 1.0f) * Coefficient;

                case CurveType.Exponential:
                {
                    // Coefficient ^ (x - 1)  →  at x=1 returns 1.0
                    if (x <= 1.0f) return 1.0f;
                    if (Coefficient <= 0f) return 1.0f;
                    // Guard against overflow: cap x at 60 (2^60 ≈ 1e18) to keep float sane.
                    float exponent = Math.Min(x - 1.0f, 60.0f);
                    return (float)Math.Pow(Coefficient, exponent);
                }

                case CurveType.Logarithmic:
                {
                    // 1 + ln(x + 1) * Coefficient  →  at x=0 returns 1.0, front-loads growth
                    if (x < 0.0f) x = 0.0f;
                    return 1.0f + (float)Math.Log(x + 1.0f) * Coefficient;
                }

                case CurveType.Sigmoid:
                {
                    // 1 + Coefficient / (1 + exp(-Steepness * (x - Midpoint)))
                    // Smoothstep-like: at x=0 returns 1 + Coefficient / (1 + exp(Steepness * Midpoint))
                    //                                  (≈ 1 for large Midpoint)
                    //                  at x=Midpoint returns 1 + Coefficient / 2
                    //                  at x=large returns 1 + Coefficient
                    double z = -Steepness * (x - Midpoint);
                    // Clamp z to avoid exp() overflow.
                    if (z > 60.0) z = 60.0;
                    else if (z < -60.0) z = -60.0;
                    double sigmoid = 1.0 / (1.0 + Math.Exp(z));
                    return 1.0f + Coefficient * (float)sigmoid;
                }

                case CurveType.Piecewise:
                {
                    if (ControlPoints == null || ControlPoints.Count == 0) return 1.0f;
                    if (ControlPoints.Count == 1) return ControlPoints[0][1];
                    // Below first point: clamp to first Y.
                    if (x <= ControlPoints[0][0]) return ControlPoints[0][1];
                    // Above last point: clamp to last Y.
                    int last = ControlPoints.Count - 1;
                    if (x >= ControlPoints[last][0]) return ControlPoints[last][1];
                    // Find segment containing x and lerp.
                    for (int i = 0; i < last; i++)
                    {
                        float x0 = ControlPoints[i][0];
                        float x1 = ControlPoints[i + 1][0];
                        if (x >= x0 && x <= x1)
                        {
                            float y0 = ControlPoints[i][1];
                            float y1 = ControlPoints[i + 1][1];
                            float t = (x1 - x0) > 1e-9f ? (x - x0) / (x1 - x0) : 0f;
                            return y0 + (y1 - y0) * t;
                        }
                    }
                    return ControlPoints[last][1]; // unreachable
                }

                default:
                    return 1.0f;
            }
        }
    }

    /// <summary>
    /// Global, lazy-loaded curve registry. Lookups by id are O(1) dictionary reads.
    /// Thread-safe: <see cref="Load"/> is guarded by a lock; reads after load are lock-free.
    ///
    /// Hot-path contract: <see cref="Evaluate"/> on an unknown id returns <c>1.0f</c> (no-op)
    /// so a missing curve never crashes the spawn loop. This matches the existing
    /// "linear default" pattern: a fresh install with no curves.json behaves identically
    /// to the pre-curve codebase.
    /// </summary>
    public static class CurveTable
    {
        private static readonly object _loadLock = new object();
        private static Dictionary<string, CurveDef> _curves = new Dictionary<string, CurveDef>();
        private static bool _loaded = false;

        /// <summary>
        /// Loads curves from <c>Data/Configs/curves.json</c>. Safe to call multiple times;
        /// subsequent calls are no-ops once <see cref="_loaded"/> is true.
        /// On any I/O / parse error, logs to the renderer (if provided) and keeps the
        /// identity-default registry — i.e. the system degrades gracefully.
        /// </summary>
        public static void Load(string configPath = "Data/Configs/curves.json", IRenderer renderer = null)
        {
            lock (_loadLock)
            {
                if (_loaded) return;
                try
                {
                    if (File.Exists(configPath))
                    {
                        string json = File.ReadAllText(configPath);
                        var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("curves", out var arr) &&
                            arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var elem in arr.EnumerateArray())
                            {
                                var def = ParseCurve(elem);
                                if (def != null && !string.IsNullOrEmpty(def.Id))
                                {
                                    _curves[def.Id] = def;
                                }
                            }
                        }
                        renderer?.Log($"[CURVE] Loaded {_curves.Count} curves from {configPath}");
                    }
                    else
                    {
                        renderer?.Log($"[CURVE] No curves.json at {configPath}, using identity defaults");
                    }
                }
                catch (Exception ex)
                {
                    renderer?.Log($"[CURVE] Failed to load curves.json: {ex.Message}, using identity defaults");
                    // Don't rethrow — keep the (possibly empty) registry so the game still runs.
                }
                _loaded = true;
            }
        }

        /// <summary>
        /// Test-only hook to inject a single curve definition. Resets <see cref="_loaded"/>
        /// so <see cref="Load"/> will re-run on next call.
        /// </summary>
        public static void ResetForTests()
        {
            lock (_loadLock)
            {
                _curves = new Dictionary<string, CurveDef>();
                _loaded = false;
            }
        }

        /// <summary>
        /// Registers a curve under the given id. Overwrites any existing curve with the same id.
        /// Useful for tests and for in-code defaults.
        /// </summary>
        public static void Register(CurveDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id)) return;
            lock (_loadLock)
            {
                _curves[def.Id] = def;
            }
        }

        /// <summary>
        /// Returns the number of registered curves. Test-only helper; cheap
        /// (one lock acquire + dictionary count).
        /// </summary>
        public static int Count
        {
            get
            {
                lock (_loadLock)
                {
                    return _curves.Count;
                }
            }
        }

        /// <summary>
        /// Returns the named curve, or null if not registered.
        /// </summary>
        public static CurveDef Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            // Read the dictionary reference under _loadLock so we always see a
            // fully-published instance. The lock also serializes against Load /
            // Register / ResetForTests, so the local reference is immutable for
            // the rest of this call (dictionary contents can still be mutated
            // concurrently, but each individual write is atomic and we never
            // observe a torn entry). This is correct under the .NET memory
            // model without needing Volatile.Read / MemoryBarrier.
            lock (_loadLock)
            {
                _curves.TryGetValue(id, out var def);
                return def;
            }
        }

        /// <summary>
        /// Hot-path evaluation. Returns 1.0f for null/empty/missing ids so the caller
        /// can multiply unconditionally without a null check.
        /// </summary>
        public static float Evaluate(string id, float x)
        {
            if (string.IsNullOrEmpty(id)) return 1.0f;
            var def = Get(id);
            if (def == null) return 1.0f;
            return def.Evaluate(x);
        }

        private static CurveDef ParseCurve(JsonElement elem)
        {
            try
            {
                var def = new CurveDef();
                if (elem.TryGetProperty("id", out var idEl))
                    def.Id = idEl.GetString();
                if (elem.TryGetProperty("type", out var typeEl))
                    def.Type = typeEl.GetString() ?? "Identity";
                if (elem.TryGetProperty("coefficient", out var coefEl))
                    def.Coefficient = coefEl.GetSingle();
                if (elem.TryGetProperty("midpoint", out var mpEl))
                    def.Midpoint = mpEl.GetSingle();
                if (elem.TryGetProperty("steepness", out var stEl))
                    def.Steepness = stEl.GetSingle();
                if (elem.TryGetProperty("controlPoints", out var cpEl) &&
                    cpEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pt in cpEl.EnumerateArray())
                    {
                        if (pt.ValueKind == JsonValueKind.Array && pt.GetArrayLength() == 2)
                        {
                            float[] xy = new float[2];
                            xy[0] = pt[0].GetSingle();
                            xy[1] = pt[1].GetSingle();
                            def.ControlPoints.Add(xy);
                        }
                    }
                    // Sort ascending by X so Piecewise can binary-search or linear-scan
                    // without needing a caller-supplied ordering.
                    def.ControlPoints.Sort((a, b) => a[0].CompareTo(b[0]));
                }

                // Resolve the string Type into the enum once, up front.
                switch (def.Type?.ToLowerInvariant())
                {
                    case "linear":       def.ResolvedType = CurveType.Linear; break;
                    case "exponential":  def.ResolvedType = CurveType.Exponential; break;
                    case "logarithmic":  def.ResolvedType = CurveType.Logarithmic; break;
                    case "sigmoid":      def.ResolvedType = CurveType.Sigmoid; break;
                    case "piecewise":    def.ResolvedType = CurveType.Piecewise; break;
                    case "identity":
                    default:             def.ResolvedType = CurveType.Identity; break;
                }
                return def;
            }
            catch
            {
                return null;
            }
        }
    }
}

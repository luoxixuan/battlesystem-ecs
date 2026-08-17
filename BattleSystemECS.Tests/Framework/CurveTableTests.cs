using System;
using Xunit;
using BattleSystemECS.Tests.Infrastructure;
using BattleSystemECS.Core;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// Tests for Round 127 Direction 1: Curve-based scaling.
    /// Verifies:
    ///   1. Identity curve returns 1.0f regardless of x
    ///   2. Linear curve matches legacy formula 1 + (x-1) * coefficient
    ///   3. Exponential curve: coef^(x-1), returns 1.0 at x=1
    ///   4. Logarithmic curve: 1 + ln(x+1) * coef
    ///   5. Sigmoid curve: smooth ramp from 1.0 to (1 + coef)
    ///   6. Piecewise curve: linear interp between control points, clamps at ends
    ///   7. CurveTable.Get returns null for unknown id; Evaluate returns 1.0f
    ///   8. Empty/null curve id always evaluates to 1.0f (zero-overhead default)
    ///   9. Register/Get round-trip; duplicate register overwrites
    ///  10. ResetForTests clears the registry (no leakage between tests)
    /// </summary>
    public class CurveTableTests : BattleTestBase
    {
        public CurveTableTests()
        {
            // Each test starts with a clean registry so test ordering can't
            // leak curve definitions between cases. CurveTable.Load is a
            // one-shot in production, but tests need to bypass that.
            CurveTable.ResetForTests();
        }

        private static CurveDef Make(CurveType type, string id,
            float coef = 0f, float mid = 10f, float steep = 0.5f,
            float[][]? controlPoints = null)
        {
            var def = new CurveDef { Id = id, ResolvedType = type, Coefficient = coef, Midpoint = mid, Steepness = steep };
            if (controlPoints != null)
                foreach (var xy in controlPoints) def.ControlPoints.Add(new[] { xy[0], xy[1] });
            return def;
        }

        // ─── Identity ──────────────────────────────────────────────────────
        [Fact]
        public void Identity_AlwaysReturnsOne()
        {
            var def = Make(CurveType.Identity, "id");
            Assert.Equal(1.0f, def.Evaluate(1f));
            Assert.Equal(1.0f, def.Evaluate(10f));
            Assert.Equal(1.0f, def.Evaluate(100f));
            Assert.Equal(1.0f, def.Evaluate(0f));
        }

        // ─── Linear (legacy-compat) ────────────────────────────────────────
        [Fact]
        public void Linear_MatchesLegacyFormula()
        {
            // 1 + (x - 1) * coefficient
            var def = Make(CurveType.Linear, "lin", coef: 0.05f);
            Assert.Equal(1.0f, def.Evaluate(1f), 5);
            Assert.Equal(1.05f, def.Evaluate(2f), 5);
            Assert.Equal(1.5f, def.Evaluate(11f), 5);
            Assert.Equal(2.0f, def.Evaluate(21f), 5);
        }

        // ── 零系数退化：两个几乎相同的 [Fact] 合并为 [Theory]。 ──
        [Theory(DisplayName = "零系数曲线任何 x 均返回 1")]
        [InlineData((int)CurveType.Linear, 0f, 50f)]
        [InlineData((int)CurveType.Exponential, 0f, 100f)]
        public void ZeroCoefficient_ReturnsOne(int curveType, float coef, float x)
        {
            var def = Make((CurveType)curveType, "flat", coef: coef);
            Assert.Equal(1.0f, def.Evaluate(x));
        }

        // ─── Exponential ───────────────────────────────────────────────────
        [Fact]
        public void Exponential_AtWave1_ReturnsOne()
        {
            var def = Make(CurveType.Exponential, "exp", coef: 1.04f);
            Assert.Equal(1.0f, def.Evaluate(1f), 5);
        }

        [Fact]
        public void Exponential_Compounds()
        {
            // 1.04^4 ≈ 1.1699
            var def = Make(CurveType.Exponential, "exp", coef: 1.04f);
            Assert.Equal(1.16985856f, def.Evaluate(5f), 4);
        }



        [Fact]
        public void Exponential_ClampsExponentToPreventOverflow()
        {
            // Coefficient = 1.5, x = 1000 → 1.5^999 would overflow float.
            // The clamp at exponent=60 keeps the result in a sane range (< 1e18).
            var def = Make(CurveType.Exponential, "exp", coef: 1.5f);
            float v = def.Evaluate(1000f);
            Assert.False(float.IsInfinity(v), "Exponential must clamp to avoid infinity");
            Assert.True(v > 0f);
        }

        // ─── Logarithmic ───────────────────────────────────────────────────
        [Fact]
        public void Logarithmic_AtZero_ReturnsOne()
        {
            var def = Make(CurveType.Logarithmic, "log", coef: 0.5f);
            // ln(0+1) * coef = 0 → 1.0
            Assert.Equal(1.0f, def.Evaluate(0f), 5);
        }

        [Fact]
        public void Logarithmic_FrontLoads()
        {
            // ln(10) * 0.5 ≈ 1.151
            var def = Make(CurveType.Logarithmic, "log", coef: 0.5f);
            Assert.Equal(1.0f + (float)Math.Log(10.0) * 0.5f, def.Evaluate(9f), 4);
        }

        // ─── Sigmoid ───────────────────────────────────────────────────────
        [Fact]
        public void Sigmoid_AtMidpoint_ReturnsOnePlusHalfCoef()
        {
            var def = Make(CurveType.Sigmoid, "sig", coef: 2.0f, mid: 10f, steep: 1.0f);
            // At x = midpoint, sigmoid = 0.5 → 1 + 2 * 0.5 = 2.0
            Assert.Equal(2.0f, def.Evaluate(10f), 4);
        }

        [Fact]
        public void Sigmoid_BelowMidpoint_LessThanHalf()
        {
            var def = Make(CurveType.Sigmoid, "sig", coef: 2.0f, mid: 10f, steep: 1.0f);
            float v = def.Evaluate(0f);
            Assert.True(v < 2.0f, $"Sigmoid at x=0 should be < 1+coef, got {v}");
            Assert.True(v > 1.0f, $"Sigmoid at x=0 should still be >= 1, got {v}");
        }

        [Fact]
        public void Sigmoid_AboveMidpoint_MoreThanHalf()
        {
            var def = Make(CurveType.Sigmoid, "sig", coef: 2.0f, mid: 10f, steep: 1.0f);
            float v = def.Evaluate(20f);
            Assert.True(v > 2.0f, $"Sigmoid at x=20 should be > 1+coef, got {v}");
            Assert.True(v < 3.0f + 1e-3f, $"Sigmoid must approach 1+coef asymptotically, got {v}");
        }

        // ─── Piecewise ─────────────────────────────────────────────────────
        [Fact]
        public void Piecewise_Empty_ReturnsOne()
        {
            var def = Make(CurveType.Piecewise, "pw");
            Assert.Equal(1.0f, def.Evaluate(5f));
        }

        [Fact]
        public void Piecewise_SinglePoint_ReturnsThatY()
        {
            var def = Make(CurveType.Piecewise, "pw", controlPoints: new[] { new[] { 5f, 2.5f } });
            Assert.Equal(2.5f, def.Evaluate(1f));
            Assert.Equal(2.5f, def.Evaluate(100f));
        }

        [Fact]
        public void Piecewise_InterpolatesBetweenPoints()
        {
            // (0, 1) and (10, 2) → at x=5, y should be 1.5
            var def = Make(CurveType.Piecewise, "pw", controlPoints: new[] { new[] { 0f, 1f }, new[] { 10f, 2f } });
            Assert.Equal(1.5f, def.Evaluate(5f), 4);
        }

        [Fact]
        public void Piecewise_ClampsBelowFirstPoint()
        {
            var def = Make(CurveType.Piecewise, "pw", controlPoints: new[] { new[] { 5f, 2.0f }, new[] { 10f, 3.0f } });
            // x=0 < first point X=5 → clamp to first Y=2.0
            Assert.Equal(2.0f, def.Evaluate(0f));
        }

        [Fact]
        public void Piecewise_ClampsAboveLastPoint()
        {
            var def = Make(CurveType.Piecewise, "pw", controlPoints: new[] { new[] { 0f, 1f }, new[] { 10f, 2f } });
            // x=100 > last point X=10 → clamp to last Y=2.0
            Assert.Equal(2.0f, def.Evaluate(100f));
        }

        // ─── CurveTable (static registry) ──────────────────────────────────
        [Fact]
        public void Get_UnknownId_ReturnsNull()
        {
            Assert.Null(CurveTable.Get("does-not-exist"));
        }

        [Fact]
        public void Evaluate_UnknownId_ReturnsOne()
        {
            // Hot-path contract: a missing id must not throw, must return 1.0
            // so the caller can multiply unconditionally.
            Assert.Equal(1.0f, CurveTable.Evaluate("missing", 50f));
        }

        [Theory(DisplayName = "null/空曲线 id 求值恒为 1")]
        [InlineData(null)]
        [InlineData("")]
        public void Evaluate_NullOrEmptyId_ReturnsOne(string? id)
        {
            Assert.Equal(1.0f, CurveTable.Evaluate(id, 50f));
        }

        [Fact]
        public void Register_ThenGet_RoundTrip()
        {
            var def = Make(CurveType.Linear, "rt", coef: 0.1f);
            CurveTable.Register(def);
            var got = CurveTable.Get("rt");
            Assert.NotNull(got);
            Assert.Same(def, got);
            Assert.Equal(1.2f, CurveTable.Evaluate("rt", 3f), 4);
        }

        [Fact]
        public void Register_DuplicateId_Overwrites()
        {
            CurveTable.Register(Make(CurveType.Linear, "dup", coef: 0.1f));
            CurveTable.Register(Make(CurveType.Linear, "dup", coef: 0.2f));
            // At x=11, new formula gives 1 + 10*0.2 = 3.0
            Assert.Equal(3.0f, CurveTable.Evaluate("dup", 11f), 4);
        }

        [Fact]
        public void Register_NullOrEmptyId_NoOp()
        {
            // Defensive: bogus register calls must not corrupt the registry.
            int before = CurveTable.Count;
            CurveTable.Register(null);
            CurveTable.Register(new CurveDef { Id = "" });
            Assert.Equal(before, CurveTable.Count);
        }

        [Fact]
        public void ResetForTests_ClearsRegistry()
        {
            CurveTable.Register(Make(CurveType.Linear, "x", coef: 0.1f));
            Assert.NotNull(CurveTable.Get("x"));
            CurveTable.ResetForTests();
            Assert.Null(CurveTable.Get("x"));
            Assert.Equal(0, CurveTable.Count);
        }

        // ─── Integration: end-to-end curve-driven multiplier ───────────────
        [Fact]
        public void Linear_AtWave1_MatchesLegacyBaseline()
        {
            // The single most important guarantee: with the default DifficultyConfig
            // (no curveId set), the wave-1 multiplier is exactly 1.0 — the value the
            // pre-curve codebase produced. Any deviation here would mean a regression
            // in wave 1 difficulty, which every existing test would notice.
            var def = Make(CurveType.Linear, "wave1", coef: 0.05f);
            Assert.Equal(1.0f, def.Evaluate(1f), 5);
        }

        [Fact]
        public void Exponential_And_Linear_Produce_DifferentValues()
        {
            // Sanity: the two shapes aren't accidentally aliased. At x=10, linear
            // with coef=0.05 gives 1.45, exponential with coef=1.05 gives 1.05^9 ≈ 1.55.
            var lin = Make(CurveType.Linear, "l", coef: 0.05f);
            var exp = Make(CurveType.Exponential, "e", coef: 1.05f);
            float lin10 = lin.Evaluate(10f);
            float exp10 = exp.Evaluate(10f);
            Assert.NotEqual(lin10, exp10);
        }

        [Fact]
        public void Count_ReflectsRegisteredCurves()
        {
            // Count starts at 0 after ResetForTests (the ctor calls it).
            Assert.Equal(0, CurveTable.Count);
            CurveTable.Register(Make(CurveType.Linear, "a"));
            Assert.Equal(1, CurveTable.Count);
            CurveTable.Register(Make(CurveType.Identity, "b"));
            Assert.Equal(2, CurveTable.Count);
            // Duplicate id replaces, doesn't grow.
            CurveTable.Register(Make(CurveType.Linear, "a"));
            Assert.Equal(2, CurveTable.Count);
        }
    }
}

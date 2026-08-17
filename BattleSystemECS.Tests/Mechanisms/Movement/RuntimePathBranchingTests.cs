using System;
using System.Collections.Generic;
using Xunit;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;

namespace BattleSystemECS.Tests.Mechanisms.Movement
{
    /// <summary>
    /// Invariants for the Runtime Path Branching / Junction system
    /// (Round 121 Direction 1). Tests verify:
    ///  1. JunctionDef has safe defaults (id="" but other fields set to harmless values)
    ///  2. JunctionPolicy enum covers HpBased/TowerDensityBased/TypeBased
    ///  3. PathfindingSystem.HasJunctions defaults to false (zero-overhead fast path)
    ///  4. AddJunction sets HasJunctions = true
    ///  5. GetJunction returns the registered def by (sourcePathId, nodeIndex)
    ///  6. ClearJunctions resets HasJunctions to false
    ///  7. AddJunction with null is a safe no-op
    ///  8. EvaluateJunction returns -1 for null def
    ///  9. HpBased: high HP → long path; low HP → short path
    /// 10. HpBased: max HP ≤ 0 treated as low HP (no division-by-zero)
    /// 11. TowerDensityBased: count > threshold → short path; ≤ threshold → long path
    /// 12. TypeBased: isBossType → long path; not boss → short path
    /// 13. Unknown policy value → safe fallback to short path
    /// 14. EnemyPathSegmentStartIndex defaults to 0 (backward-compatible)
    /// 15. Junction at last waypoint of source path is registered correctly
    /// 16. Two junctions on different (path, node) pairs are independent
    /// 17. Re-adding a junction with the same (path, node) overwrites the previous
    /// </summary>
    public class RuntimePathBranchingTests : BattleTestBase
    {
        // ─── Config invariants ─────────────────────────────────────────────

        [Fact]
        public void JunctionDef_HasSafeDefaults()
        {
            var j = new JunctionDef();
            Assert.Equal("", j.Id);
            Assert.Equal(0, j.SourcePathId);
            Assert.Equal(0, j.NodeIndex);
            Assert.Equal(JunctionPolicy.HpBased, j.Policy);
            // 相对不变量：阈值必须是合法概率区间、半径与计数阈值必须为正。
            Assert.True(j.HpLongPathThreshold > 0f && j.HpLongPathThreshold < 1f);
            Assert.True(j.TowerDensityRadius > 0f);
            Assert.True(j.TowerDensityShortPathThreshold > 0);
            Assert.NotNull(j.BossTypeTags);
            Assert.Empty(j.BossTypeTags);
            Assert.NotEqual(j.ShortPathId, j.LongPathId);
        }

        [Fact]
        public void JunctionPolicy_CoversThreeCases()
        {
            // Three explicit policies must exist as distinct enum values.
            Assert.NotEqual(JunctionPolicy.HpBased, JunctionPolicy.TowerDensityBased);
            Assert.NotEqual(JunctionPolicy.HpBased, JunctionPolicy.TypeBased);
            Assert.NotEqual(JunctionPolicy.TowerDensityBased, JunctionPolicy.TypeBased);
        }

        // ─── PathfindingSystem: junction registration ──────────────────────

        [Fact]
        public void PathfindingSystem_HasJunctions_DefaultsFalse()
        {
            // No junctions registered yet → fast path is a single bool check.
            var sys = new PathfindingSystem(Store);
            Assert.False(sys.HasJunctions);
        }

        [Fact]
        public void AddJunction_SetsHasJunctions()
        {
            var sys = new PathfindingSystem(Store);
            sys.AddJunction(new JunctionDef { SourcePathId = 0, NodeIndex = 1 });
            Assert.True(sys.HasJunctions);
        }

        [Fact]
        public void AddJunction_NullIsSafeNoOp()
        {
            var sys = new PathfindingSystem(Store);
            sys.AddJunction(null);
            Assert.False(sys.HasJunctions);
        }

        [Fact]
        public void GetJunction_ReturnsRegisteredDef()
        {
            var sys = new PathfindingSystem(Store);
            var j = new JunctionDef { Id = "test", SourcePathId = 0, NodeIndex = 2, Policy = JunctionPolicy.HpBased };
            sys.AddJunction(j);
            var fetched = sys.GetJunction(0, 2);
            Assert.NotNull(fetched);
            Assert.Equal("test", fetched.Id);
            Assert.Equal(JunctionPolicy.HpBased, fetched.Policy);
        }

        [Fact]
        public void GetJunction_ReturnsNullForUnregistered()
        {
            var sys = new PathfindingSystem(Store);
            Assert.Null(sys.GetJunction(99, 99));
        }

        [Fact]
        public void GetJunction_ReturnsNullWhenNoneRegistered()
        {
            // Even with a non-zero path/node, an empty junction map returns null.
            var sys = new PathfindingSystem(Store);
            Assert.Null(sys.GetJunction(0, 1));
        }

        [Fact]
        public void ClearJunctions_ResetsToEmpty()
        {
            var sys = new PathfindingSystem(Store);
            sys.AddJunction(new JunctionDef { SourcePathId = 0, NodeIndex = 1 });
            sys.AddJunction(new JunctionDef { SourcePathId = 1, NodeIndex = 0 });
            Assert.True(sys.HasJunctions);
            sys.ClearJunctions();
            Assert.False(sys.HasJunctions);
            Assert.Null(sys.GetJunction(0, 1));
            Assert.Null(sys.GetJunction(1, 0));
        }

        [Fact]
        public void ReAddSameJunction_OverwritesPrevious()
        {
            // Same (path, node) → new def replaces the old one.
            var sys = new PathfindingSystem(Store);
            sys.AddJunction(new JunctionDef { Id = "v1", SourcePathId = 0, NodeIndex = 1 });
            sys.AddJunction(new JunctionDef { Id = "v2", SourcePathId = 0, NodeIndex = 1, Policy = JunctionPolicy.TypeBased });
            var j = sys.GetJunction(0, 1);
            Assert.NotNull(j);
            Assert.Equal("v2", j.Id);
            Assert.Equal(JunctionPolicy.TypeBased, j.Policy);
        }

        [Fact]
        public void TwoJunctions_AtDifferentPositions_AreIndependent()
        {
            // Two junctions at different (path, node) keys must be independently retrievable.
            var sys = new PathfindingSystem(Store);
            var j1 = new JunctionDef { Id = "j1", SourcePathId = 0, NodeIndex = 1 };
            var j2 = new JunctionDef { Id = "j2", SourcePathId = 1, NodeIndex = 0 };
            sys.AddJunction(j1);
            sys.AddJunction(j2);
            Assert.Equal("j1", sys.GetJunction(0, 1).Id);
            Assert.Equal("j2", sys.GetJunction(1, 0).Id);
            Assert.Null(sys.GetJunction(0, 0)); // not registered
            Assert.Null(sys.GetJunction(1, 1)); // not registered
        }

        // ─── EvaluateJunction: static policy decisions ─────────────────────

        [Fact]
        public void EvaluateJunction_NullDef_ReturnsNegativeOne()
        {
            int result = PathfindingSystem.EvaluateJunction(null, 50f, 100f, false, 0);
            Assert.Equal(-1, result);
        }

        [Fact]
        public void EvaluateJunction_HpBased_HighHpTakesLongPath()
        {
            var j = new JunctionDef
            {
                Policy = JunctionPolicy.HpBased,
                HpLongPathThreshold = 0.75f,
                ShortPathId = 0,
                LongPathId = 2,
            };
            // 90/100 = 0.9 > 0.75 → long path
            int result = PathfindingSystem.EvaluateJunction(j, 90f, 100f, false, 0);
            Assert.Equal(2, result);
        }

        [Fact]
        public void EvaluateJunction_HpBased_LowHpTakesShortPath()
        {
            var j = new JunctionDef
            {
                Policy = JunctionPolicy.HpBased,
                HpLongPathThreshold = 0.75f,
                ShortPathId = 0,
                LongPathId = 2,
            };
            // 50/100 = 0.5 < 0.75 → short path
            int result = PathfindingSystem.EvaluateJunction(j, 50f, 100f, false, 0);
            Assert.Equal(0, result);
        }

        [Fact]
        public void EvaluateJunction_HpBased_ZeroMaxHpDoesNotDivideByZero()
        {
            // maxHp = 0 → ratio = 0 → short path. No NaN, no crash.
            var j = new JunctionDef
            {
                Policy = JunctionPolicy.HpBased,
                HpLongPathThreshold = 0.75f,
                ShortPathId = 7,
                LongPathId = 8,
            };
            int result = PathfindingSystem.EvaluateJunction(j, 100f, 0f, false, 0);
            Assert.Equal(7, result);
        }

        [Fact]
        public void EvaluateJunction_TowerDensityBased_HighCountTakesShortPath()
        {
            var j = new JunctionDef
            {
                Policy = JunctionPolicy.TowerDensityBased,
                TowerDensityShortPathThreshold = 2,
                ShortPathId = 0,
                LongPathId = 3,
            };
            // count = 5 > 2 → short path
            int result = PathfindingSystem.EvaluateJunction(j, 100f, 100f, false, 5);
            Assert.Equal(0, result);
        }

        [Fact]
        public void EvaluateJunction_TowerDensityBased_LowCountTakesLongPath()
        {
            var j = new JunctionDef
            {
                Policy = JunctionPolicy.TowerDensityBased,
                TowerDensityShortPathThreshold = 2,
                ShortPathId = 0,
                LongPathId = 3,
            };
            // count = 1 ≤ 2 → long path
            int result = PathfindingSystem.EvaluateJunction(j, 100f, 100f, false, 1);
            Assert.Equal(3, result);
        }

        [Fact]
        public void EvaluateJunction_TypeBased_BossTakesLongPath()
        {
            var j = new JunctionDef
            {
                Policy = JunctionPolicy.TypeBased,
                ShortPathId = 0,
                LongPathId = 4,
            };
            int result = PathfindingSystem.EvaluateJunction(j, 100f, 100f, true, 0);
            Assert.Equal(4, result);
        }

        [Fact]
        public void EvaluateJunction_TypeBased_NonBossTakesShortPath()
        {
            var j = new JunctionDef
            {
                Policy = JunctionPolicy.TypeBased,
                ShortPathId = 0,
                LongPathId = 4,
            };
            int result = PathfindingSystem.EvaluateJunction(j, 100f, 100f, false, 0);
            Assert.Equal(0, result);
        }

        [Fact]
        public void EvaluateJunction_UnknownPolicy_FallsBackToShortPath()
        {
            // An out-of-range enum value (e.g. corrupted config) must not crash.
            // Cast an int outside the defined enum range to JunctionPolicy.
            var j = new JunctionDef
            {
                Policy = (JunctionPolicy)99,
                ShortPathId = 5,
                LongPathId = 6,
            };
            int result = PathfindingSystem.EvaluateJunction(j, 100f, 100f, true, 100);
            Assert.Equal(5, result);
        }

        // ─── SOA field: EnemyPathSegmentStartIndex ─────────────────────────

        [Fact]
        public void EnemyPathSegmentStartIndex_DefaultsToZero_OnNewEntity()
        {
            // A newly-created enemy has EnemyPathSegmentStartIndex = 0 (fresh path).
            int eid = Store.CreateEntity();
            Assert.Equal(0, Store.EnemyPathSegmentStartIndex[eid]);
        }
    }
}

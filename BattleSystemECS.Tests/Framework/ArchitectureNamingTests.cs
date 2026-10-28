using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class ArchitectureNamingTests
    {
        [Fact]
        public void ProductionAndTestSourceHasNoMigrationStageLabels()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var stagePattern = new Regex("M" + "[0-9]+", RegexOptions.CultureInvariant);
            foreach (string directory in new[] { "Core", "Systems", "BattleSystemECS.Tests" })
            {
                string path = Path.Combine(root, directory);
                foreach (string file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(path, file);
                    string[] segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    if (Array.Exists(segments, segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(segment, "TestResults", StringComparison.OrdinalIgnoreCase))) continue;
                    Assert.DoesNotMatch(stagePattern, Path.GetFileName(file));
                    Assert.DoesNotMatch(stagePattern, File.ReadAllText(file));
                }
            }
        }

        [Fact]
        public void ProductionEffectRuntimeDoesNotUseLegacyDefinitionAsStateOwner()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var legacyStateWrite = new Regex(@"\.Definition\.(RemainingTime|TicksRemaining|RefreshDuration)\s*=", RegexOptions.CultureInvariant);
            foreach (string directory in new[] { "Core", "Systems" })
            {
                foreach (string file in Directory.GetFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
                    Assert.DoesNotMatch(legacyStateWrite, File.ReadAllText(file));
            }

            string buffSystem = File.ReadAllText(Path.Combine(root, "Systems", "BuffSystem.cs"));
            Assert.DoesNotContain("store.GetEffect(", buffSystem, StringComparison.Ordinal);
            Assert.DoesNotContain("store.SetEffect(", buffSystem, StringComparison.Ordinal);
            Assert.Contains("TryGetActiveEffectAt", buffSystem, StringComparison.Ordinal);
        }

        [Fact]
        public void FrameCompositionDoesNotRecognizeContentIdentifiers()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string graph = File.ReadAllText(Path.Combine(root, "Core", "FrameSystemGraph.cs"));
            string scheduler = File.ReadAllText(Path.Combine(root, "Core", "FrameScheduler.cs"));
            string source = graph + scheduler;

            // Bug 回归：调度层只能识别通用边界，具体内容标识必须留在 Registry/GAS。
            Assert.DoesNotContain("new AbilityId", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new EffectId", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new TriggerId", source, StringComparison.Ordinal);
            Assert.DoesNotMatch(new Regex(@"\b(?:ability|effect|skill)[-_]?\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), source);
        }

        [Fact]
        public void NoLegacyExecutionPathRemainsInCore()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            // Bug 回归：legacy 执行路径已退役，Core 不得再出现 TickLegacy / ExecuteLegacy / 执行模式枚举。
            foreach (string file in Directory.GetFiles(Path.Combine(root, "Core"), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                Assert.DoesNotContain("TickLegacy", text, StringComparison.Ordinal);
                Assert.DoesNotContain("ExecuteLegacy", text, StringComparison.Ordinal);
                Assert.DoesNotContain("FrameSchedulerExecutionMode", text, StringComparison.Ordinal);
            }
        }

    }
}

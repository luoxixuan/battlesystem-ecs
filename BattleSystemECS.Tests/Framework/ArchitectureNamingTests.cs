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
    }
}

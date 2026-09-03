using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// Phase 3 守卫：生产玩家伤害只能经 ResourceResolver / 白名单负 CurrentHealth 写入。
    /// </summary>
    public sealed class PlayerDamageAuthorityGuardTests
    {
        private static string RepoRoot =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        private static IEnumerable<string> EnumerateProductionCs(params string[] relativeDirs)
        {
            foreach (string relative in relativeDirs)
            {
                string path = Path.Combine(RepoRoot, relative);
                foreach (string file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(path, file);
                    string[] segments = relativePath.Split(
                        new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries);
                    if (Array.Exists(segments, s =>
                            string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    yield return file;
                }
            }
        }

        [Fact]
        public void DecreasePlayerHealth_OnlyCalledFromResourceResolverInProduction()
        {
            var callPattern = new Regex(@"\bDecreasePlayerHealth\s*\(", RegexOptions.CultureInvariant);
            var offenders = new List<string>();
            foreach (string file in EnumerateProductionCs("Core", "Systems"))
            {
                string text = File.ReadAllText(file);
                if (!callPattern.IsMatch(text)) continue;
                string name = Path.GetFileName(file);
                // 定义站点 + 唯一权威调用方。
                if (string.Equals(name, "ComponentStore_Player.cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, "ResourceResolver.cs", StringComparison.OrdinalIgnoreCase)) continue;
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }

            Assert.True(offenders.Count == 0,
                "生产侧 DecreasePlayerHealth 调用方只能是 ResourceResolver；违规: " +
                string.Join(", ", offenders));
        }

        [Fact]
        public void NegativePlayerCurrentHealthResourceWrites_OnlyWhitelistSystems()
        {
            // §6.6：负 AttributeKey(3) 写玩家血量只允许 BossTrailAoe / SuicideBomb。
            var negativeKey3 = new Regex(@"AttributeKey\s*\(\s*3\s*\)\s*,\s*-", RegexOptions.CultureInvariant);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BossTrailAoeSystem.cs",
                "SuicideBombSystem.cs",
            };
            var offenders = new List<string>();
            foreach (string file in EnumerateProductionCs("Systems"))
            {
                string text = File.ReadAllText(file);
                if (!negativeKey3.IsMatch(text)) continue;
                string name = Path.GetFileName(file);
                if (allowed.Contains(name)) continue;
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }

            Assert.True(offenders.Count == 0,
                "负 CurrentHealth(AttributeKey(3)) 玩家写入白名单只能变短；违规: " +
                string.Join(", ", offenders));

            foreach (string allowedName in allowed)
            {
                string path = Path.Combine(RepoRoot, "Systems", allowedName);
                Assert.True(File.Exists(path), "白名单文件缺失: " + allowedName);
                Assert.Matches(negativeKey3, File.ReadAllText(path));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    /// <summary>
    /// P5 守卫：GAS / 伤害 / 技能 / 生成生产路径禁止 Rng.Shared 与无种子 new Random()。
    /// </summary>
    public sealed class SimulationRngGuardTests
    {
        private static string RepoRoot =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        private static readonly Regex SharedRng = new Regex(@"\bRng\.Shared\b", RegexOptions.CultureInvariant);
        private static readonly Regex UnseededRandom = new Regex(@"new\s+Random\s*\(\s*\)", RegexOptions.CultureInvariant);

        [Fact]
        public void GasAndListedSimulationSystems_DoNotUseWallClockRng()
        {
            var offenders = new List<string>();
            foreach (string file in EnumerateGuardedFiles())
            {
                string text = File.ReadAllText(file);
                string relative = Path.GetRelativePath(RepoRoot, file).Replace('\\', '/');
                if (SharedRng.IsMatch(text))
                    offenders.Add(relative + ": Rng.Shared");
                if (UnseededRandom.IsMatch(text))
                    offenders.Add(relative + ": new Random()");
            }

            Assert.True(offenders.Count == 0,
                "模拟热路径禁止 Rng.Shared / 无种子 new Random()；违规: " +
                string.Join("; ", offenders));
        }

        private static IEnumerable<string> EnumerateGuardedFiles()
        {
            string gas = Path.Combine(RepoRoot, "Core", "GAS");
            foreach (string file in Directory.GetFiles(gas, "*.cs"))
                yield return file;

            string[] systems =
            {
                "EchoCloneSystem.cs",
                "EnemyTeleportSystem.cs",
                "PortalSystem.cs",
                "PointDefenseSystem.cs",
                "SkillSystem.cs",
                "UpgradeSystem.cs",
                "WeatherSystem.cs",
                "EnemyFissionSystem.cs",
                "EnemyCloneSystem.cs",
                "AutoSkillSystem.cs",
                "PickupSystem.cs",
                "EnemyAffixSystem.cs",
                "RandomEventSystem.cs",
                "WaveSpawningSystem.cs",
                "CraftingSystem.cs",
                "PreFightBuffSystem.cs",
                "ReforgeSystem.cs",
                "TowerModifierSystem.cs"
            };
            foreach (string name in systems)
                yield return Path.Combine(RepoRoot, "Systems", name);
        }
    }
}

using System;
using System.IO;
using BattleSystemECS.Config;
using BattleSystemECS.Core.GAS;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Integration
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class StrictConfigFileSystemCollection
    {
        public const string Name = "Strict configuration filesystem";
    }

    [Collection(StrictConfigFileSystemCollection.Name)]
    public sealed class StrictAuxiliaryConfigLoadingTests
    {
        [Fact]
        public void StrictLoaderRejectsMissingRequiredAuxiliaryWithRelativeAndAbsolutePath()
        {
            using var fixture = new ConfigFixture();
            const string relativePath = "Data/Configs/behavior_trees.json";
            string absolutePath = Path.GetFullPath(relativePath);
            File.Delete(relativePath);

            var error = Assert.Throws<CatalogValidationException>(() =>
                GameConfigLoader.LoadStrictCatalog(new MockRenderer()));

            Assert.Contains(relativePath, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(absolutePath, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StrictLoaderRejectsMissingHeroConfigWithRelativeAndAbsolutePath()
        {
            using var fixture = new ConfigFixture();
            string relativePath = Path.Combine("Data", "Configs", "hero_skills.json");
            string absolutePath = Path.GetFullPath(relativePath);
            File.Delete(relativePath);

            var error = Assert.Throws<CatalogValidationException>(() =>
                GameConfigLoader.LoadStrictCatalog(new MockRenderer()));

            Assert.Contains(relativePath, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(absolutePath, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StrictLoaderRejectsMalformedHeroConfigWithRelativeAndAbsolutePath()
        {
            using var fixture = new ConfigFixture();
            string relativePath = Path.Combine("Data", "Configs", "hero_skills.json");
            string absolutePath = Path.GetFullPath(relativePath);
            File.WriteAllText(relativePath, "{");

            var error = Assert.Throws<CatalogValidationException>(() =>
                GameConfigLoader.LoadStrictCatalog(new MockRenderer()));

            Assert.Contains(relativePath, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(absolutePath, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StrictLoaderRejectsMalformedOptionalAuxiliaryInsteadOfFallingBack()
        {
            using var fixture = new ConfigFixture();
            const string relativePath = "Data/Configs/weather.json";
            string absolutePath = Path.GetFullPath(relativePath);
            File.WriteAllText(relativePath, "{");

            var error = Assert.Throws<CatalogValidationException>(() =>
                GameConfigLoader.LoadStrictCatalog(new MockRenderer()));

            Assert.Contains(relativePath, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(absolutePath, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StrictLoaderRejectsUnexpectedOptionalRootKind()
        {
            using var fixture = new ConfigFixture();
            const string relativePath = "Data/Configs/weather.json";
            File.WriteAllText(relativePath, "[]");

            var error = Assert.Throws<CatalogValidationException>(() =>
                GameConfigLoader.LoadStrictCatalog(new MockRenderer()));

            Assert.Contains(relativePath, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("expected an object", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StrictLoaderAllowsMissingOptionalAuxiliary()
        {
            using var fixture = new ConfigFixture();
            File.Delete("Data/Configs/weather.json");
            var renderer = new MockRenderer();

            var config = GameConfigLoader.LoadStrictCatalog(renderer);

            Assert.NotNull(config.CompiledCatalog);
            Assert.True(config.StrictCatalogReferences);
            Assert.True(renderer.HasLogContaining("Weather config file not found"));
        }

        [Fact]
        public void CompatibilityLoaderRetainsFallbackForMissingAndMalformedAuxiliaries()
        {
            using var fixture = new ConfigFixture();
            File.Delete("Data/Configs/behavior_trees.json");
            File.WriteAllText("Data/Configs/weather.json", "{");
            var renderer = new MockRenderer();

            var config = GameConfigLoader.LoadConfig(renderer);

            Assert.NotNull(config);
            Assert.Empty(config.BehaviorTrees);
            Assert.True(renderer.HasLogContaining("Behavior trees file not found"));
            Assert.True(renderer.HasLogContaining("Failed to load weather config"));
        }

        private sealed class ConfigFixture : IDisposable
        {
            private readonly string previousDirectory;
            private readonly string root;

            public ConfigFixture()
            {
                previousDirectory = Directory.GetCurrentDirectory();
                root = Path.Combine(Path.GetTempPath(), "strict-config-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                File.Copy(Path.Combine(previousDirectory, "game_config.json"), Path.Combine(root, "game_config.json"));
                CopyDirectory(Path.Combine(previousDirectory, "Data"), Path.Combine(root, "Data"));
                Directory.SetCurrentDirectory(root);
            }

            public void Dispose()
            {
                Directory.SetCurrentDirectory(previousDirectory);
                Directory.Delete(root, recursive: true);
            }

            private static void CopyDirectory(string source, string destination)
            {
                Directory.CreateDirectory(destination);
                foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
            }
        }
    }
}

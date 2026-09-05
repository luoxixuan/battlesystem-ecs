using System;
using BattleSystemECS.Components;
using BattleSystemECS.Core;
using BattleSystemECS.Config;
using Xunit;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using BattleSystemECS.Tests.Infrastructure;
using System.Collections.Generic;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class SystemInstallerProductionFlowTests
    {
        [Fact]
        public void ProductionInstallerRecordsNullPreflightRejectionBeforeRethrowingArgumentNullException()
        {
            var installer = new ProductionSystemInstaller();
            var logger = new CapturingLogger();

            var error = Assert.Throws<ArgumentNullException>(() => installer.Install(
                null!, new ComponentStore(), new GameConfig(), logger, 0,
                new StateMachine(), new FrameScheduler(new ComponentStore(), new GameConfig())));

            Assert.Equal("registry", error.ParamName);
            var rejected = Assert.Single(installer.LastDescriptors);
            Assert.Equal("registry", rejected.Id);
            Assert.Equal(SystemRegistrationState.Rejected, rejected.State);
            Assert.Equal(installer.LastInstallationSessionId, rejected.SessionId);
            Assert.Contains("stage=Construction", rejected.Reason, StringComparison.Ordinal);
            Assert.Contains("exceptionType=System.ArgumentNullException", rejected.Reason, StringComparison.Ordinal);
            Assert.Contains("reason=Value cannot be null", rejected.Reason, StringComparison.Ordinal);
            Assert.Contains(logger.Messages, message =>
                message.Contains("rejected id=registry", StringComparison.Ordinal) &&
                message.Contains("session=" + installer.LastInstallationSessionId, StringComparison.Ordinal));
        }

        [Fact]
        public void ProductionInstallerBuildsAndSealsTheRealRegistryComposition()
        {
            var store = new ComponentStore();
            var config = GameConfigLoader.LoadConfigStrict(new NullLogger());
            var logger = new NullLogger();
            var stateMachine = new StateMachine();
            var scheduler = new FrameScheduler(store, config);
            var registry = new SystemRegistry();

            var installer = new ProductionSystemInstaller();
            installer.Install(registry, store, config, logger, 0,
                stateMachine, scheduler);

            Assert.True(scheduler.IsCompositionSealed);
            Assert.Equal(FrameGraphCompositionKind.ProductionRegistry, scheduler.CompositionKind);
            Assert.All(scheduler.FrameGraphPlan, node =>
                Assert.True(node.Metadata.AccessProfile.RequiresSystemBinding,
                    "Production node bypassed registration contract: " + node.Metadata.Id.Value));
            Assert.NotNull(registry.Skill);
            Assert.NotNull(registry.EnemyAI);
            Assert.Contains(installer.LastDescriptors, d => d.Id.IndexOf("Skill", StringComparison.Ordinal) >= 0 && d.State == SystemRegistrationState.Registered);
            Assert.Contains(installer.LastDescriptors, d => d.State == SystemRegistrationState.Disabled && d.Reason.Length > 0);
            Assert.Equal(installer.LastDescriptors.Count, System.Linq.Enumerable.Select(installer.LastDescriptors, d => d.Id).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void ProductionInstallerRejectsAlreadySealedComposition()
        {
            var store = new ComponentStore();
            var config = GameConfigLoader.LoadConfigStrict(new NullLogger());
            var logger = new NullLogger();
            var stateMachine = new StateMachine();
            var scheduler = new FrameScheduler(store, config);
            var registry = new SystemRegistry();

            new ProductionSystemInstaller().Install(registry, store, config, logger, 0,
                stateMachine, scheduler);

            var second = new ProductionSystemInstaller();
            Assert.Throws<InvalidOperationException>(() =>
                second.Install(new SystemRegistry(), store, config, logger, 0, stateMachine, scheduler));
            SystemRegistrationDescriptor rejected = Assert.Single(second.LastDescriptors);
            Assert.False(string.IsNullOrWhiteSpace(second.LastInstallationSessionId));
            Assert.Equal(second.LastInstallationSessionId, rejected.SessionId);
            Assert.Contains("stage=Binding", rejected.Reason, StringComparison.Ordinal);
            Assert.Contains("session=" + second.LastInstallationSessionId, rejected.Reason, StringComparison.Ordinal);
            Assert.Contains("exceptionType=System.InvalidOperationException", rejected.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void ProductionInstallerRecordsConstructionFailureBeforeRethrowingOriginalException()
        {
            var store = new ComponentStore();
            var config = GameConfigLoader.LoadConfigStrict(new NullLogger());
            config.Combo.TriggerThreshold = 0;
            var logger = new CapturingLogger();
            var installer = new ProductionSystemInstaller();

            Assert.Throws<BattleSystemECS.Core.GAS.CatalogValidationException>(() =>
                installer.Install(new SystemRegistry(), store, config, logger, 0,
                    new StateMachine(), new FrameScheduler(store, config)));

            SystemRegistrationDescriptor rejected = Assert.Single(installer.LastDescriptors);
            Assert.False(string.IsNullOrWhiteSpace(installer.LastInstallationSessionId));
            Assert.Equal(installer.LastInstallationSessionId, rejected.SessionId);
            Assert.Equal("bootstrap", rejected.Id);
            Assert.Equal(SystemRegistrationState.Rejected, rejected.State);
            Assert.Contains("stage=Construction", rejected.Reason, StringComparison.Ordinal);
            Assert.Contains(logger.Messages, message =>
                message.Contains("rejected id=bootstrap", StringComparison.Ordinal) &&
                message.Contains("session=" + installer.LastInstallationSessionId, StringComparison.Ordinal) &&
                message.Contains("stage=Construction", StringComparison.Ordinal));
            var original = SystemRegistrationManifest.Entries[0];
            var malformed = new SystemRegistrationEntry("", original.Property, original.Type,
                original.Dependencies, original.Policy, original.Source, original.OwnerToken,
                original.ProvidedTokens, original.FrameBindings, original.Enabled, original.IsRoot,
                original.FactoryStage, original.WireStage, original.BindStage,
                original.Factory, original.Wire, original.Bind);
            var malformedInstaller = new ProductionSystemInstaller();
            var malformedLogger = new CapturingLogger();
            var validate = typeof(ProductionSystemInstaller).GetMethod("ValidateManifest",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var invocation = Assert.Throws<TargetInvocationException>(() =>
                validate.Invoke(malformedInstaller, new object[] { new[] { malformed }, malformedLogger }));
            var error = Assert.IsType<SystemRegistrationGraphValidationException>(invocation.InnerException);
            SystemRegistrationDescriptor malformedRejected = Assert.Single(malformedInstaller.LastDescriptors);
            Assert.False(string.IsNullOrWhiteSpace(malformedInstaller.LastInstallationSessionId));
            Assert.Equal(malformedInstaller.LastInstallationSessionId, malformedRejected.SessionId);
            Assert.Contains("stage=Construction", malformedRejected.Reason, StringComparison.Ordinal);
            Assert.Contains("exceptionType=" + error.GetType().FullName, malformedRejected.Reason, StringComparison.Ordinal);
            Assert.Contains("reason=Invalid registration entry fields", malformedRejected.Reason, StringComparison.Ordinal);
            Assert.Contains(malformedLogger.Messages, message =>
                message.Contains("session=" + malformedInstaller.LastInstallationSessionId, StringComparison.Ordinal) &&
                message.Contains("stage=Construction", StringComparison.Ordinal) &&
                message.Contains("exceptionType=" + error.GetType().FullName, StringComparison.Ordinal));
        }

        [Fact]
        public void LegacyFacadeEnforcesOneCreateWireBindSessionWithoutDuplicateSubscriptions()
        {
            var store = new ComponentStore();
            var config = GameConfigLoader.LoadConfigStrict(new NullLogger());
            var registry = new SystemRegistry();
            var logger = new NullLogger();
            var stateMachine = new StateMachine();

            Assert.Throws<InvalidOperationException>(() => registry.WireDependencies(store, 0));
            Assert.Equal(0, SubscriptionCount(store, "OnEnemyKilled"));

            registry.CreateAll(store, config, logger, 0, stateMachine);
            var skill = registry.Skill;
            Assert.Throws<InvalidOperationException>(() =>
                registry.CreateAll(store, config, logger, 0, stateMachine));
            Assert.Same(skill, registry.Skill);

            registry.WireDependencies(store, 0);
            int deathSubscriptions = SubscriptionCount(store, "OnEnemyKilled");
            int waveSubscriptions = SubscriptionCount(registry.WaveSpawning!, "OnWaveStart");
            Assert.True(deathSubscriptions > 0);
            Assert.True(waveSubscriptions > 0);
            Assert.Throws<InvalidOperationException>(() => registry.WireDependencies(store, 0));
            Assert.Equal(deathSubscriptions, SubscriptionCount(store, "OnEnemyKilled"));
            Assert.Equal(waveSubscriptions, SubscriptionCount(registry.WaveSpawning!, "OnWaveStart"));

            var scheduler = new FrameScheduler(store, config);
            registry.AssignToGroups(scheduler);
            Assert.True(scheduler.IsCompositionSealed);
            int boundDeathSubscriptions = SubscriptionCount(store, "OnEnemyKilled");
            int boundWaveSubscriptions = SubscriptionCount(registry.WaveSpawning!, "OnWaveStart");
            Assert.Throws<InvalidOperationException>(() => registry.AssignToGroups(scheduler));
            Assert.Equal(boundDeathSubscriptions, SubscriptionCount(store, "OnEnemyKilled"));
            Assert.Equal(boundWaveSubscriptions, SubscriptionCount(registry.WaveSpawning!, "OnWaveStart"));
        }

        [Fact]
        public void ProductionManaAppliesUnlockedTechTreeBonuses()
        {
            var store = new ComponentStore();
            int playerId = store.CreateEntity();
            store.AddPlayer(playerId, 5f, 1f, 10f, 1);
            var config = GameConfigLoader.LoadConfigStrict(new NullLogger());
            var registry = new SystemRegistry();
            new ProductionSystemInstaller().Install(registry, store, config, new NullLogger(), playerId,
                new StateMachine(), new FrameScheduler(store, config));
            var tech = registry.TechTree!;
            tech.ReloadConfig(new TechTreeConfig
            {
                researchPointsPerWave = 0,
                branches = new List<TechBranchDef>
                {
                    new TechBranchDef
                    {
                        id = "mana",
                        name = "mana",
                        color = "blue",
                        nodes = new List<TechNodeDef>
                        {
                            new TechNodeDef
                            {
                                id = "mana-production",
                                name = "mana-production",
                                description = "production injection probe",
                                cost = 1,
                                prerequisites = new List<string>(),
                                effects = new List<TechEffect>
                                {
                                    new TechEffect { type = "max_mana_add", value = 17f },
                                    new TechEffect { type = "mana_regen_add", value = 3f },
                                    new TechEffect { type = "mana_cost_mult", value = 0.75f }
                                }
                            }
                        }
                    }
                }
            });
            store.AddResearchPoints(playerId, 1);

            Assert.True(tech.TryUnlock("mana-production"));
            registry.Mana!.SetTurn();

            Assert.Equal(config.Mana.MaxManaBase + 17f, store.PlayerMaxMana[playerId]);
            Assert.Equal(config.Mana.ManaRegenPerSec + 3f, store.PlayerManaRegen[playerId]);
            Assert.Equal(0.75f, store.PlayerManaCost[playerId]);
        }

        [Fact]
        public void GeneratedManifestHasCompleteTypedRegistrationSemantics()
        {
            var entries = SystemRegistrationManifest.Entries;
            var enabled = entries.Where(e => !string.Equals(e.Type, "disabled", StringComparison.Ordinal)).ToArray();
            var disabled = entries.Where(e => string.Equals(e.Type, "disabled", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(enabled);
            Assert.NotEmpty(disabled);
            Assert.All(entries, entry =>
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Id));
                Assert.False(string.IsNullOrWhiteSpace(entry.Source));
                Assert.False(string.IsNullOrWhiteSpace(entry.Policy));
                Assert.Equal(entry.Dependencies.Length == 0, entry.IsRoot);
                Assert.All(entry.Dependencies, dependency => Assert.Contains(entries, candidate => candidate.Id == dependency));
            });
            Assert.Contains(enabled, entry => !entry.IsRoot && entry.Dependencies.Length > 0);
            Assert.All(enabled, entry => Assert.StartsWith("production-", entry.Policy, StringComparison.Ordinal));
            Assert.All(disabled, entry => Assert.Equal("feature-disabled", entry.Policy));
            SystemRegistrationGraphValidator.Validate(entries);
        }

        [Fact]
        public void InvalidRecipeStageIsRejectedBeforeItsFirstMutation()
        {
            bool mutated = false;
            SystemFactory factory = (registry, store, config, logger, playerId, stateMachine, events) =>
                mutated = true;
            SystemWire wire = (registry, store, playerId) => { };
            SystemBind bind = (registry, scheduler) => { };
            var invalid = new SystemRegistrationEntry("invalid-stage", "InvalidStage", "probe",
                Array.Empty<string>(), "production-probe", "test", "registration.invalid-stage",
                new[] { "registration.invalid-stage" },
                Array.Empty<FrameBindingRegistration>(), true, true,
                RegistrationStage.Wiring, RegistrationStage.Construction, RegistrationStage.Binding,
                factory, wire, bind);

            Assert.Throws<SystemRegistrationGraphValidationException>(() =>
                SystemRegistrationGraphValidator.GetStableOrder(new[] { invalid }));
            Assert.False(mutated);
        }

        [Fact]
        public void DuplicateFrameBindingContractIsRejected()
        {
            var binding = Binding("node.same", "OwnerA");
            var error = Assert.Throws<SystemRegistrationGraphValidationException>(() =>
                SystemRegistrationGraphValidator.Validate(new[]
                {
                    Entry("OwnerA", binding), Entry("OwnerB", Binding("node.same", "OwnerB"))
                }));
            Assert.Contains("Duplicate manifest frame binding", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void OrphanProductionNodeIsRejected()
        {
            var node = Node("node.orphan", "registration.OwnerA");
            var error = Assert.Throws<FrameGraphValidationException>(() =>
                FrameRegistrationContractCatalog.ValidateContractSet(
                    new[] { Entry("OwnerA") }, new[] { node },
                    new[] { "ComponentStore", "registration.OwnerA" }, _ => true));
            Assert.Contains("Orphan production frame node", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ManifestBindingWithoutRealNodeIsRejected()
        {
            var error = Assert.Throws<FrameGraphValidationException>(() =>
                FrameRegistrationContractCatalog.ValidateContractSet(
                    new[] { Entry("OwnerA", Binding("node.missing", "OwnerA")) },
                    Array.Empty<FrameNodeAdapter>(), new[] { "ComponentStore", "registration.OwnerA" }, _ => true));
            Assert.Contains("no real production node", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void FrameBindingOwnerMismatchIsRejected()
        {
            FrameGraphValidationException error = ContractMismatch(Node("node.contract", "registration.Wrong"));
            Assert.Contains("owner mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FrameBindingPhaseMismatchIsRejected()
        {
            FrameGraphValidationException error = ContractMismatch(Node("node.contract", "registration.OwnerA", FramePhaseMask.Wave));
            Assert.Contains("phase mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FrameBindingExecutionPolicyMismatchIsRejected()
        {
            FrameGraphValidationException error = ContractMismatch(Node("node.contract", "registration.OwnerA", executionPolicy: FrameExecutionSemantics.SerialCommit));
            Assert.Contains("execution policy mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DisabledManifestOwnerCannotDeclareFrameBinding()
        {
            var disabled = Entry("Disabled", Binding("node.disabled", "Disabled"), enabled: false);
            var error = Assert.Throws<SystemRegistrationGraphValidationException>(() =>
                SystemRegistrationGraphValidator.Validate(new[] { disabled }));
            Assert.Contains("Disabled registration owns", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void UnexecutedOwnerBinderIsRejected()
        {
            var binding = Binding("node.contract", "OwnerA");
            var error = Assert.Throws<FrameGraphValidationException>(() =>
                FrameRegistrationContractCatalog.ValidateContractSet(
                    new[] { Entry("OwnerA", binding) },
                    new[] { Node("node.contract", "registration.OwnerA") },
                    new[] { "ComponentStore", "registration.OwnerA" }, _ => false));
            Assert.Contains("binder did not execute", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void GeneratedManifestPreservesMovementRuntimeDependencies()
        {
            var entries = SystemRegistrationManifest.Entries;
            var movement = Assert.Single(entries, entry => entry.Property == nameof(SystemRegistry.EnemyMovement));
            var dependencies = movement.Dependencies.ToHashSet(StringComparer.Ordinal);
            Assert.Contains(nameof(SystemRegistry.Pathfinding), dependencies);
            Assert.Contains(nameof(SystemRegistry.BossTrailAoe), dependencies);
            Assert.Contains(nameof(SystemRegistry.Weather), dependencies);
            Assert.Contains(nameof(SystemRegistry.DayNight), dependencies);
        }

        [Fact]
        public void ManifestDependenciesExactlyMatchTypedRecipeIl()
        {
            var propertyByGetter = typeof(SystemRegistry)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetMethod != null)
                .ToDictionary(property => property.GetMethod!, property => property.Name);
            var mismatches = new System.Collections.Generic.List<string>();

            foreach (var entry in SystemRegistrationManifest.Entries.Where(candidate => candidate.Enabled))
            {
                var actual = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (Delegate recipe in new Delegate[] { entry.Factory!, entry.Wire!, entry.Bind! })
                {
                    foreach (MethodBase called in ProductionIlWalker.CollectTransitiveCalls(
                        new[] { recipe.Method }, typeof(SystemRegistry).Assembly))
                    {
                        if (called is MethodInfo method && propertyByGetter.TryGetValue(method, out string? property) &&
                            !string.Equals(property, entry.Property, StringComparison.Ordinal))
                            actual.Add(property);
                    }
                }

                string[] expected = entry.Dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                string[] observed = actual.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!expected.SequenceEqual(observed, StringComparer.Ordinal))
                    mismatches.Add($"{entry.Id}: spec=[{string.Join(",", expected)}] recipe-il=[{string.Join(",", observed)}]");
            }

            Assert.True(mismatches.Count == 0,
                "Registration dependency mismatch:\n" + string.Join("\n", mismatches));
        }

        [Fact]
        public void RecipeDependencyWalkerFollowsCompilerGeneratedClosureBodies()
        {
            MethodInfo root = typeof(SystemInstallerProductionFlowTests).GetMethod(
                nameof(CreateClosureProbe), BindingFlags.Static | BindingFlags.NonPublic)!;
            MethodInfo getter = typeof(SystemRegistry).GetProperty(nameof(SystemRegistry.Skill))!.GetMethod!;

            Assert.DoesNotContain(getter, ProductionIlWalker.GetCalledMethods(root));
            Assert.Contains(getter, ProductionIlWalker.CollectTransitiveCalls(
                new[] { root }, typeof(SystemInstallerProductionFlowTests).Assembly,
                typeof(SystemRegistry).Assembly));
        }

        private static Delegate CreateClosureProbe(SystemRegistry registry) => () => _ = registry.Skill;

        [Fact]
        public void LedgerGeneratorIsDeterministicAndGeneratedArtifactsAreCurrent()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string script = Path.Combine(root, "tools", "generate-system-registry-ledger.ps1");
            string temp = Path.Combine(Path.GetTempPath(), "m7-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string ledger1 = Path.Combine(temp, "ledger-1.md");
                string manifest1 = Path.Combine(temp, "manifest-1.cs");
                string ledger2 = Path.Combine(temp, "ledger-2.md");
                string manifest2 = Path.Combine(temp, "manifest-2.cs");
                RunGenerator(script, ledger1, manifest1);
                RunGenerator(script, ledger2, manifest2);

                Assert.Equal(File.ReadAllText(ledger1), File.ReadAllText(ledger2));
                Assert.Equal(File.ReadAllText(manifest1), File.ReadAllText(manifest2));
                Assert.Equal(NormalizeNewlines(File.ReadAllText(Path.Combine(root, "docs", "ecs-gas-m7-nullable-ledger.md"))),
                    NormalizeNewlines(File.ReadAllText(ledger1)));
                Assert.Equal(NormalizeNewlines(File.ReadAllText(Path.Combine(root, "Core", "SystemRegistrationManifest.generated.cs"))),
                    NormalizeNewlines(File.ReadAllText(manifest1)));

                string rootA = Path.Combine(temp, "absolute-root-a");
                string rootB = Path.Combine(temp, "different-absolute-root-b");
                foreach (string copyRoot in new[] { rootA, rootB })
                {
                    Directory.CreateDirectory(Path.Combine(copyRoot, "Core"));
                    Directory.CreateDirectory(Path.Combine(copyRoot, "tools"));
                    File.Copy(Path.Combine(root, "Core", "SystemRegistry.cs"), Path.Combine(copyRoot, "Core", "SystemRegistry.cs"));
                    File.Copy(Path.Combine(root, "tools", "system-registration-spec.json"), Path.Combine(copyRoot, "tools", "system-registration-spec.json"));
                }
                string crossLedgerA = Path.Combine(rootA, "ledger.md");
                string crossManifestA = Path.Combine(rootA, "manifest.cs");
                string crossLedgerB = Path.Combine(rootB, "ledger.md");
                string crossManifestB = Path.Combine(rootB, "manifest.cs");
                RunGenerator(script, crossLedgerA, crossManifestA,
                    Path.Combine(rootA, "Core", "SystemRegistry.cs"), Path.Combine(rootA, "tools", "system-registration-spec.json"));
                RunGenerator(script, crossLedgerB, crossManifestB,
                    Path.Combine(rootB, "Core", "SystemRegistry.cs"), Path.Combine(rootB, "tools", "system-registration-spec.json"));
                string crossLedger = File.ReadAllText(crossLedgerA);
                string crossManifest = File.ReadAllText(crossManifestA);
                Assert.Equal(crossLedger, File.ReadAllText(crossLedgerB));
                Assert.Equal(crossManifest, File.ReadAllText(crossManifestB));
                Assert.DoesNotMatch(@"[A-Za-z]:\\", crossLedger + crossManifest);
                Assert.DoesNotContain(root, crossLedger + crossManifest, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(temp, crossLedger + crossManifest, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        [Fact]
        public void LedgerGeneratorRejectsLegacyFreeFormRecipeCode()
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string temp = Path.Combine(Path.GetTempPath(), "m7-legacy-recipe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string spec = Path.Combine(temp, "spec.json");
                string source = File.ReadAllText(Path.Combine(root, "tools", "system-registration-spec.json"));
                source = source.Replace("\"recipe\": {",
                    "\"factoryCode\": [\"Skill = null;\"], \"recipe\": {");
                File.WriteAllText(spec, source);
                string error = RunGeneratorExpectFailure(
                    Path.Combine(root, "tools", "generate-system-registry-ledger.ps1"),
                    Path.Combine(temp, "ledger.md"), Path.Combine(temp, "manifest.cs"),
                    Path.Combine(root, "Core", "SystemRegistry.cs"), spec);
                Assert.Contains("forbidden free-form recipe field", error, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        [Fact]
        public void InstallerDescriptorsPreserveManifestSourceAndPolicy()
        {
            var store = new ComponentStore();
            var config = GameConfigLoader.LoadConfigStrict(new NullLogger());
            var installer = new ProductionSystemInstaller();
            installer.Install(new SystemRegistry(), store, config, new NullLogger(), 0,
                new StateMachine(), new FrameScheduler(store, config));
            Assert.All(installer.LastDescriptors, descriptor =>
            {
                var entry = Assert.Single(SystemRegistrationManifest.Entries, item => item.Id == descriptor.Id);
                Assert.Equal(entry.Source, descriptor.Source);
                Assert.Equal(entry.Policy, descriptor.Reason);
            });
        }

        [Fact]
        public void GameManagerInitialize_ResetsMatchSeedOnce()
        {
            var manager = new GameManager(matchSeed: 99);
            manager.Initialize();
            Assert.Equal(99, manager.MatchSeed);
            Assert.Equal(99, manager.SchedulerDiagnostics.Store.Determinism.Seed);
        }

        [Fact]
        public void GameManagerProductionBootstrapDoesNotReachLegacyDamageAdapter()
        {
            var manager = new GameManager();
            manager.Initialize();
            Assert.Equal(0, manager.SchedulerDiagnostics.Store.DamageResolver.LegacyApplyCount);
        }

        [Fact]
        public void NullableLedgerManifestAndGeneratedDocumentMatchStrictPredicate()
        {
            var properties = typeof(SystemRegistry).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.PropertyType.Name.EndsWith("System", StringComparison.Ordinal) ||
                    property.PropertyType.Name.EndsWith("Handler", StringComparison.Ordinal) ||
                    property.PropertyType == typeof(EventBus))
                .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            var manifest = SystemRegistrationManifest.Entries
                .Where(entry => !string.Equals(entry.Type, "disabled", StringComparison.Ordinal) &&
                    !string.Equals(entry.Type, "composition", StringComparison.Ordinal))
                .Select(entry => entry.Property).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(properties, manifest);
            string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "ecs-gas-m7-nullable-ledger.md"));
            string document = File.ReadAllText(path);
            var documented = SystemRegistrationManifest.Entries
                .Where(entry => !string.Equals(entry.Type, "disabled", StringComparison.Ordinal))
                .Select(entry => entry.Property);
            Assert.All(documented, property => Assert.Contains("| " + property + " |", document, StringComparison.Ordinal));
        }

        private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

        private static void RunGenerator(string script, string ledger, string manifest,
            string? source = null, string? spec = null)
        {
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-Output");
            start.ArgumentList.Add(ledger);
            start.ArgumentList.Add("-ManifestOutput");
            start.ArgumentList.Add(manifest);
            if (source != null)
            {
                start.ArgumentList.Add("-Source");
                start.ArgumentList.Add(source);
            }
            if (spec != null)
            {
                start.ArgumentList.Add("-Spec");
                start.ArgumentList.Add(spec);
            }
            using Process process = Process.Start(start)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0,
                $"Ledger generator failed with exit {process.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        private static string RunGeneratorExpectFailure(string script, string ledger, string manifest,
            string source, string spec)
        {
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            foreach (string argument in new[]
            {
                "-NoProfile", "-File", script, "-Output", ledger, "-ManifestOutput", manifest,
                "-Source", source, "-Spec", spec
            }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.NotEqual(0, process.ExitCode);
            return stdout + stderr;
        }

        private static FrameGraphValidationException ContractMismatch(FrameNodeAdapter node)
        {
            var binding = Binding("node.contract", "OwnerA");
            return Assert.Throws<FrameGraphValidationException>(() =>
                FrameRegistrationContractCatalog.ValidateContractSet(
                    new[] { Entry("OwnerA", binding) }, new[] { node },
                    new[] { "ComponentStore", "registration.OwnerA" }, _ => true));
        }

        private static FrameBindingRegistration Binding(string nodeId, string owner,
            FramePhaseMask phase = FramePhaseMask.Build,
            FrameExecutionSemantics executionPolicy = FrameExecutionSemantics.SerialUpdate)
            => new FrameBindingRegistration(nodeId, owner, phase, executionPolicy,
                new[] { "ComponentStore", "registration." + owner }, Array.Empty<string>());

        private static SystemRegistrationEntry Entry(string id,
            FrameBindingRegistration? binding = null, bool enabled = true)
        {
            SystemFactory? factory = enabled ? (_, _, _, _, _, _, _) => { } : null;
            SystemWire? wire = enabled ? (_, _, _) => { } : null;
            SystemBind? bind = enabled ? (_, _) => { } : null;
            return new SystemRegistrationEntry(id, id, enabled ? "ProbeSystem" : "disabled",
                Array.Empty<string>(), enabled ? "production-probe" : "feature-disabled", "test",
                enabled ? "registration." + id : "registration." + id,
                enabled ? new[] { "registration." + id } : Array.Empty<string>(),
                binding.HasValue ? new[] { binding.Value } : Array.Empty<FrameBindingRegistration>(),
                enabled, true, RegistrationStage.Construction, RegistrationStage.Wiring,
                RegistrationStage.Binding, factory, wire, bind);
        }

        private static FrameNodeAdapter Node(string nodeId, string owner,
            FramePhaseMask phase = FramePhaseMask.Build,
            FrameExecutionSemantics executionPolicy = FrameExecutionSemantics.SerialUpdate)
        {
            var metadata = new FrameNodeMetadata(nodeId, phase, FrameTimeDomain.Build,
                executionPolicy, requiredDependencies: new[] { "ComponentStore", "registration.OwnerA" },
                bindingId: new FrameBindingId("test." + nodeId), owner: new FrameAccessOwner(owner),
                requiresSystemBinding: true);
            return new FrameNodeAdapter(metadata, new DelegateSystem(_ => { }));
        }

        private static int SubscriptionCount(object owner, string eventField)
        {
            if (owner is ComponentStore store)
            {
                if (eventField == "OnEnemyKilled") return store.EnemyKilledSubscriberCount;
                if (eventField == "OnTowerKill") return store.TowerKillSubscriberCount;
            }
            var field = owner.GetType().GetField(eventField,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (field!.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
        }

        private sealed class NullLogger : IRenderer
        {
            public void Log(string message) { }
            public void LogBattle(string message) { }
            public void LogDamage(string attacker, string defender, float damage, bool isCritical) { }
            public void LogDeath(string entity) { }
            public void LogWin(string winner) { }
            public void LogBattleStart(string battleName) { }
            public void LogTurn(int turn) { }
        }

        private sealed class CapturingLogger : IRenderer
        {
            public System.Collections.Generic.List<string> Messages { get; } = new System.Collections.Generic.List<string>();
            public void Log(string message) => Messages.Add(message);
            public void LogBattle(string message) { }
            public void LogDamage(string attacker, string defender, float damage, bool isCritical) { }
            public void LogDeath(string entity) { }
            public void LogWin(string winner) { }
            public void LogBattleStart(string battleName) { }
            public void LogTurn(int turn) { }
        }
    }
}

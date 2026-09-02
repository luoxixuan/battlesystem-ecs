#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BattleSystemECS.Config;

namespace BattleSystemECS.Core
{
    /// <summary>
    /// 显式的生产组装边界。安装器负责生命周期顺序，调用方无需了解 registry 的内部阶段。
    /// </summary>
    public interface ISystemInstaller
    {
        string Id { get; }
        void Install(SystemRegistry registry, ComponentStore store, GameConfig config,
            IRenderer logger, int playerId, StateMachine stateMachine,
            FrameScheduler scheduler, IBattleEventBus? battleEventBus = null);
    }

    public enum SystemRegistrationState { Registered, Disabled, Rejected }

    internal sealed class SystemRegistrationGraphValidationException : InvalidOperationException
    {
        public SystemRegistrationGraphValidationException(string message) : base(message) { }
    }

    internal static class SystemRegistrationGraphValidator
    {
        public static void Validate(IReadOnlyList<SystemRegistrationEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var byId = new Dictionary<string, SystemRegistrationEntry>(StringComparer.Ordinal);
            var ownerTokens = new HashSet<string>(StringComparer.Ordinal);
            var frameNodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Property) ||
                    string.IsNullOrWhiteSpace(entry.Type) || string.IsNullOrWhiteSpace(entry.Lifecycle) ||
                    string.IsNullOrWhiteSpace(entry.Group) || string.IsNullOrWhiteSpace(entry.Policy) ||
                    string.IsNullOrWhiteSpace(entry.Source) ||
                    entry.Dependencies == null ||
                    !Enum.IsDefined(typeof(RegistrationStage), entry.FactoryStage) ||
                    !Enum.IsDefined(typeof(RegistrationStage), entry.WireStage) ||
                    !Enum.IsDefined(typeof(RegistrationStage), entry.BindStage))
                    throw new SystemRegistrationGraphValidationException("Invalid registration entry fields: " + (entry.Id ?? "<empty>"));
                if (entry.FactoryStage >= entry.WireStage || entry.WireStage >= entry.BindStage)
                    throw new SystemRegistrationGraphValidationException(
                        "Registration recipe stages must be ordered factory < wire < bind: " + entry.Id);
                if (entry.Enabled && (entry.Factory == null || entry.Wire == null || entry.Bind == null))
                    throw new SystemRegistrationGraphValidationException(
                        "Enabled registration has an incomplete typed recipe: " + entry.Id);
                if (!entry.Enabled && (entry.Factory != null || entry.Wire != null || entry.Bind != null))
                    throw new SystemRegistrationGraphValidationException(
                        "Disabled registration has executable recipe delegates: " + entry.Id);
                if (entry.Enabled && string.IsNullOrWhiteSpace(entry.OwnerToken))
                    throw new SystemRegistrationGraphValidationException("Enabled registration has no owner token: " + entry.Id);
                if (entry.Enabled && (entry.ProvidedTokens.Length == 0 || Array.IndexOf(entry.ProvidedTokens, entry.OwnerToken) < 0))
                    throw new SystemRegistrationGraphValidationException("Enabled registration does not provide owner token: " + entry.Id);
                if (!entry.Enabled && (!string.IsNullOrEmpty(entry.OwnerToken) || entry.FrameBindings.Length != 0 || entry.ProvidedTokens.Length != 0))
                    throw new SystemRegistrationGraphValidationException("Disabled registration owns production frame bindings: " + entry.Id);
                if (entry.Enabled && !ownerTokens.Add(entry.OwnerToken))
                    throw new SystemRegistrationGraphValidationException("Duplicate registration owner token: " + entry.OwnerToken);
                foreach (var binding in entry.FrameBindings)
                {
                    if (!string.Equals(binding.RegistrationId, entry.Id, StringComparison.Ordinal))
                        throw new SystemRegistrationGraphValidationException($"Frame binding '{binding.NodeId}' owner mismatch: {binding.RegistrationId} != {entry.Id}.");
                    if (binding.Phase == FramePhaseMask.None || (binding.Phase & ~FramePhaseMask.All) != 0)
                        throw new SystemRegistrationGraphValidationException("Invalid frame binding phase: " + binding.NodeId);
                    if (!Enum.IsDefined(typeof(FrameExecutionSemantics), binding.ExecutionPolicy))
                        throw new SystemRegistrationGraphValidationException("Invalid frame binding execution policy: " + binding.NodeId);
                    if (binding.RequiredTokens == null || binding.ProvidedTokens == null ||
                        Array.IndexOf(binding.RequiredTokens, entry.OwnerToken) < 0)
                        throw new SystemRegistrationGraphValidationException("Invalid frame binding token contract: " + binding.NodeId);
                    if (!frameNodeIds.Add(binding.NodeId))
                        throw new SystemRegistrationGraphValidationException("Duplicate manifest frame binding: " + binding.NodeId);
                }
                if (!byId.TryAdd(entry.Id, entry))
                    throw new SystemRegistrationGraphValidationException("Duplicate registration id: " + entry.Id);
                if (entry.Dependencies.Length == 0 && !entry.IsRoot)
                    throw new SystemRegistrationGraphValidationException("Non-root registration has no dependencies: " + entry.Id);
                foreach (var dependency in entry.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(dependency) || !byId.ContainsKey(dependency) && !entries.Any(e => string.Equals(e.Id, dependency, StringComparison.Ordinal)))
                        throw new SystemRegistrationGraphValidationException($"Unknown registration dependency '{dependency}' for '{entry.Id}'.");
                }
            }

            var marks = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (var entry in entries) Visit(entry, byId, marks);
        }

        public static IReadOnlyList<SystemRegistrationEntry> GetStableOrder(
            IReadOnlyList<SystemRegistrationEntry> entries)
        {
            Validate(entries);
            var byId = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var remaining = entries.ToDictionary(entry => entry.Id,
                entry => entry.Dependencies.Length, StringComparer.Ordinal);
            var dependents = entries.ToDictionary(entry => entry.Id,
                _ => new List<string>(), StringComparer.Ordinal);
            foreach (var entry in entries)
                foreach (var dependency in entry.Dependencies)
                    dependents[dependency].Add(entry.Id);
            var ready = new SortedSet<string>(remaining.Where(pair => pair.Value == 0)
                .Select(pair => pair.Key), StringComparer.Ordinal);
            var result = new List<SystemRegistrationEntry>(entries.Count);
            while (ready.Count > 0)
            {
                string id = ready.Min!;
                ready.Remove(id);
                result.Add(byId[id]);
                foreach (string dependent in dependents[id])
                    if (--remaining[dependent] == 0) ready.Add(dependent);
            }
            if (result.Count != entries.Count)
                throw new SystemRegistrationGraphValidationException(
                    "Registration dependency graph contains a cycle.");
            return result;
        }

        private static void Visit(SystemRegistrationEntry entry, Dictionary<string, SystemRegistrationEntry> byId, Dictionary<string, byte> marks)
        {
            byte mark;
            if (marks.TryGetValue(entry.Id, out mark))
            {
                if (mark == 1) throw new SystemRegistrationGraphValidationException("Registration dependency cycle detected at: " + entry.Id);
                if (mark == 2) return;
            }
            marks[entry.Id] = 1;
            foreach (var dependencyId in entry.Dependencies)
            {
                Visit(byId[dependencyId], byId, marks);
            }
            marks[entry.Id] = 2;
        }
    }

    public sealed class SystemRegistrationDescriptor
    {
        public string Id { get; }
        public string SessionId { get; }
        public SystemRegistrationState State { get; }
        public string Reason { get; }
        public string Kind { get; }
        public string Source { get; }
        public SystemRegistrationDescriptor(string id, SystemRegistrationState state, string reason, string kind = "system", string source = "manifest", string? sessionId = null)
        {
            Id = id;
            SessionId = sessionId ?? string.Empty;
            State = state;
            Reason = reason;
            Kind = kind;
            Source = source;
        }
    }

    /// <summary>
    /// 现有 registry 实现的适配器。保持边界狭窄，使 registry 可以渐进收缩而不改变
    /// 系统行为，也不引入第二条组装路径。
    /// </summary>
    public sealed class ProductionSystemInstaller : ISystemInstaller
    {
        public const string InstallerId = "production.systems";
        public string Id => InstallerId;
        public string? LastInstallationSessionId { get; private set; }
        public IReadOnlyList<SystemRegistrationDescriptor> LastDescriptors { get; private set; } = Array.Empty<SystemRegistrationDescriptor>();

        public void Install(SystemRegistry registry, ComponentStore store, GameConfig config,
            IRenderer logger, int playerId, StateMachine stateMachine,
            FrameScheduler scheduler, IBattleEventBus? battleEventBus = null)
        {
            string installationSessionId = Guid.NewGuid().ToString("N");
            LastInstallationSessionId = installationSessionId;
            if (registry == null) throw PreflightNull(nameof(registry), logger, installationSessionId);
            if (store == null) throw PreflightNull(nameof(store), logger, installationSessionId);
            if (config == null) throw PreflightNull(nameof(config), logger, installationSessionId);
            if (logger == null) throw PreflightNull(nameof(logger), logger, installationSessionId);
            if (stateMachine == null) throw PreflightNull(nameof(stateMachine), logger, installationSessionId);
            if (scheduler == null) throw PreflightNull(nameof(scheduler), logger, installationSessionId);
            if (scheduler.IsCompositionSealed)
            {
                Reject(logger, installationSessionId, Id, RegistrationStage.Binding, new InvalidOperationException("scheduler composition already sealed"), "scheduler composition already sealed", "installer");
                throw new InvalidOperationException("System installer cannot run after graph composition is sealed.");
            }

            // 在修改 registry 或 scheduler 状态前校验完整注册图。
            ValidateManifest(SystemRegistrationManifest.Entries, logger);

            var plan = SystemRegistrationGraphValidator.GetStableOrder(
                SystemRegistrationManifest.Entries);
            var descriptors = new List<SystemRegistrationDescriptor>();
            foreach (var entry in plan)
            {
                descriptors.Add(new SystemRegistrationDescriptor(entry.Id,
                    entry.IsDisabled ? SystemRegistrationState.Disabled : SystemRegistrationState.Registered,
                    entry.Policy, "system", entry.Source, installationSessionId));
            }
            ExecuteStage(() => registry.CreateAll(store, config, logger, playerId, stateMachine, battleEventBus),
                registry, logger, RegistrationStage.Construction, installationSessionId);
            ExecuteStage(() => registry.WireDependencies(store, playerId),
                registry, logger, RegistrationStage.Wiring, installationSessionId);
            ExecuteStage(() => registry.AssignToGroups(scheduler),
                registry, logger, RegistrationStage.Binding, installationSessionId);
            LastDescriptors = descriptors.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            foreach (var descriptor in LastDescriptors)
                logger.Log($"[REGISTRY] {descriptor.State.ToString().ToLowerInvariant()} id={descriptor.Id} kind=system reason={descriptor.Reason} source={nameof(ProductionSystemInstaller)}");

            if (!scheduler.IsCompositionSealed)
            {
                var error = new InvalidOperationException("Production system installer completed without sealing graph composition.");
                Reject(logger, installationSessionId, "graph.seal", RegistrationStage.Binding, error, "post-install composition remained unsealed", "installer");
                throw error;
            }
        }

        private ArgumentNullException PreflightNull(string parameterName, IRenderer? logger, string sessionId)
        {
            var error = new ArgumentNullException(parameterName);
            Reject(logger, sessionId, parameterName, RegistrationStage.Construction, error,
                error.Message, "installer");
            return error;
        }

        private void Reject(IRenderer? logger, string sessionId, string registrationId, RegistrationStage stage,
            Exception exception, string reason, string kind)
        {
            string detail = $"session={sessionId}; stage={stage}; exceptionType={exception.GetType().FullName}; reason={reason}";
            LastDescriptors = new[] { new SystemRegistrationDescriptor(registrationId,
                SystemRegistrationState.Rejected, detail, kind, nameof(ProductionSystemInstaller), sessionId) };
            logger?.Log($"[REGISTRY] rejected id={registrationId} kind={kind} {detail} source={nameof(ProductionSystemInstaller)}");
        }

        private void ValidateManifest(IReadOnlyList<SystemRegistrationEntry> entries, IRenderer logger)
        {
            string sessionId = LastInstallationSessionId ?? (LastInstallationSessionId = Guid.NewGuid().ToString("N"));
            try
            {
                SystemRegistrationGraphValidator.Validate(entries);
            }
            catch (Exception ex)
            {
                Reject(logger, sessionId, Id, RegistrationStage.Construction, ex, ex.Message, "manifest");
                throw;
            }
        }

        private void ExecuteStage(Action action, SystemRegistry registry, IRenderer logger,
            RegistrationStage stage, string sessionId)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                string registrationId = registry.LastRegistrationFailureId ?? Id;
                RegistrationStage actualStage = registry.LastRegistrationFailureStage ?? stage;
                Reject(logger, sessionId, registrationId, actualStage, ex, ex.Message, "system");
                throw;
            }
        }
    }
}

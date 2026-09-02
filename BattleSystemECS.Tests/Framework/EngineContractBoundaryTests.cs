using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using BattleSystemECS.Core;
using BattleSystemECS.Systems;
using BattleSystemECS.Tests.Infrastructure;
using Xunit;

namespace BattleSystemECS.Tests.Framework
{
    public sealed class EngineContractBoundaryTests
    {
        [Fact]
        public void EngineAssemblyHasNoImplementationReferences()
        {
            var names = LoadEngineAssembly().GetReferencedAssemblies().Select(a => a.Name).ToArray();
            Assert.DoesNotContain(names, n => string.Equals(n, "BattleSystemECS.Core", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => string.Equals(n, "BattleSystemECS", StringComparison.Ordinal));
        }

        [Fact]
        public void PublicContractSurfaceContainsOnlyInterfacesAndValueHandles()
        {
            var types = LoadEngineAssembly().GetExportedTypes();
            Assert.All(types, t => Assert.True(t.IsInterface || t.IsValueType));
            Assert.DoesNotContain(types, t => t.FullName != null && t.FullName.Contains("Systems", StringComparison.Ordinal));
        }

        private static Assembly LoadEngineAssembly()
        {
            var reference = Assert.Single(typeof(FrameScheduler).Assembly.GetReferencedAssemblies(),
                candidate => string.Equals(candidate.Name, "BattleSystemECS.Engine", StringComparison.Ordinal));
            return Assembly.Load(reference);
        }

        [Fact]
        public void ProductionBootstrapUsesInstallerAsTheCompositionEdge()
        {
            var method = typeof(GameManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "Initialize" && m.GetParameters().Length == 0);
            Assert.NotNull(method);
            var body = method!.GetMethodBody();
            Assert.NotNull(body);
            var il = body!.GetILAsByteArray();
            Assert.NotNull(il);
            Assert.True(il!.Length > 0, "Production bootstrap has no executable IL.");

            var calls = ProductionIlWalker.GetCalledMethods(method);
            Assert.Contains(calls, called => called.Name == nameof(ProductionSystemInstaller.Install) &&
                called.DeclaringType == typeof(ProductionSystemInstaller));
            Assert.DoesNotContain(calls, called => called.Name == nameof(SystemRegistry.CreateAll) ||
                called.Name == nameof(SystemRegistry.WireDependencies) ||
                called.Name == nameof(SystemRegistry.AssignToGroups));
        }

        [Fact]
        public void BenchmarkCompositionUsesInstallerAsTheCompositionEdge()
        {
            var factory = typeof(ComponentStore).Assembly.GetType("BattleSystemECS.Systems.BenchmarkCompositionFactory", throwOnError: true)!;
            var method = factory.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var calls = ProductionIlWalker.GetCalledMethods(method!);
            Assert.Contains(calls, called =>
                called.Name == nameof(ProductionSystemInstaller.Install) &&
                called.DeclaringType == typeof(ProductionSystemInstaller));
            Assert.DoesNotContain(calls, called =>
                called.Name == nameof(SystemRegistry.CreateAll) ||
                called.Name == nameof(SystemRegistry.WireDependencies) ||
                called.Name == nameof(SystemRegistry.AssignToGroups));
        }

        [Fact]
        public void ProductionFrameGraphClosureHasNoConcreteContentReferences()
        {
            var graphType = typeof(FrameScheduler).Assembly.GetType("BattleSystemECS.Core.FrameSystemGraph", throwOnError: true)!;
            var offenders = new SortedSet<string>(StringComparer.Ordinal);
            var orchestrationTypes = new[] { graphType }.Concat(graphType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
            foreach (MethodBase method in orchestrationTypes.SelectMany(t =>
                t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>()
                    .Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))))
            {
                if (method.GetMethodBody() == null) continue;
                foreach (MemberInfo reference in ProductionIlWalker.GetMetadataReferences(method))
                    AddConcreteReference(reference, offenders);
            }
            Assert.True(offenders.Count == 0, "Concrete production FrameGraph references:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void FrameGraphTypeSignaturesHaveNoConcreteContentReferences()
        {
            var assembly = typeof(FrameScheduler).Assembly;
            var types = new[]
            {
                typeof(FrameGraph),
                assembly.GetType("BattleSystemECS.Core.FrameSystemGraph", throwOnError: true)!
            };
            var offenders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Type type in types)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    AddConcreteType(field.FieldType, type.FullName + "." + field.Name, offenders);
                foreach (MethodBase method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Cast<MethodBase>().Concat(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)))
                {
                    if (method is MethodInfo info) AddConcreteType(info.ReturnType, method.ToString()!, offenders);
                    foreach (ParameterInfo parameter in method.GetParameters()) AddConcreteType(parameter.ParameterType, method.ToString()!, offenders);
                }
            }
            Assert.True(offenders.Count == 0, "Concrete production signatures:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void FrameSchedulerTransitivePublicAndProtectedSurfaceHasNoConcreteSystemTypes()
        {
            var offenders = new SortedSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<Type>();
            Type[] roots = typeof(FrameScheduler).Assembly.GetTypes()
                .Where(type => type == typeof(FrameScheduler) ||
                    typeof(ISystemGroup).IsAssignableFrom(type) && type != typeof(ISystemGroup))
                .ToArray();
            Assert.All(roots.Where(type => typeof(ISystemGroup).IsAssignableFrom(type)),
                type => Assert.False(type.IsPublic || type.IsNestedPublic,
                    "Legacy group facade must remain assembly-internal: " + type.FullName));
            VisitPublicSurface(typeof(FrameScheduler), typeof(FrameScheduler).FullName!, visited, offenders);

            Assert.True(offenders.Count == 0,
                "Concrete FrameScheduler public/protected signatures:\n" + string.Join("\n", offenders));
        }

        private static void VisitPublicSurface(Type type, string source, ISet<Type> visited,
            ISet<string> offenders)
        {
            type = Unwrap(type);
            AddConcreteSystemType(type, source, offenders);
            if (type.Assembly != typeof(FrameScheduler).Assembly || !IsOrchestrationType(type) || !visited.Add(type)) return;

            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (FieldInfo field in type.GetFields(declared).Where(IsPublicOrProtected))
                VisitPublicSurface(field.FieldType, source + "." + field.Name, visited, offenders);
            foreach (PropertyInfo property in type.GetProperties(declared).Where(property =>
                property.GetAccessors(true).Any(IsPublicOrProtected)))
                VisitPublicSurface(property.PropertyType, source + "." + property.Name, visited, offenders);
            foreach (MethodInfo method in type.GetMethods(declared).Where(IsPublicOrProtected))
            {
                VisitPublicSurface(method.ReturnType, source + "." + method.Name + ":return", visited, offenders);
                foreach (ParameterInfo parameter in method.GetParameters())
                    VisitPublicSurface(parameter.ParameterType, source + "." + method.Name + ":" + parameter.Name, visited, offenders);
            }
            foreach (ConstructorInfo constructor in type.GetConstructors(declared).Where(IsPublicOrProtected))
                foreach (ParameterInfo parameter in constructor.GetParameters())
                    VisitPublicSurface(parameter.ParameterType, source + ".ctor:" + parameter.Name, visited, offenders);
        }

        private static bool IsOrchestrationType(Type type) =>
            type == typeof(FrameScheduler) ||
            typeof(ISystemGroup).IsAssignableFrom(type) ||
            type.Name.IndexOf("FrameGraph", StringComparison.Ordinal) >= 0 ||
            type.Name.IndexOf("FrameExecution", StringComparison.Ordinal) >= 0;

        [Fact]
        public void FrameSchedulerDeclaredImplementationHasNoConcreteContentReferences()
        {
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var offenders = new SortedSet<string>(StringComparer.Ordinal);
            Type type = typeof(FrameScheduler);
            foreach (FieldInfo field in type.GetFields(declared))
                AddConcreteSystemType(field.FieldType, type.FullName + "." + field.Name, offenders);
            foreach (MethodBase method in type.GetMethods(declared).Cast<MethodBase>()
                .Concat(type.GetConstructors(declared)))
            {
                if (method is MethodInfo info) AddConcreteSystemType(info.ReturnType, method.ToString()!, offenders);
                foreach (ParameterInfo parameter in method.GetParameters())
                    AddConcreteSystemType(parameter.ParameterType, method.ToString()!, offenders);
                if (method.GetMethodBody() == null) continue;
                foreach (MemberInfo reference in ProductionIlWalker.GetMetadataReferences(method))
                {
                    Type? referenceType = reference as Type ?? reference.DeclaringType;
                    if (referenceType != null)
                        AddConcreteSystemType(referenceType, reference.ToString() ?? reference.Name, offenders);
                }
            }
            Assert.True(offenders.Count == 0,
                "Concrete FrameScheduler implementation references:\n" + string.Join("\n", offenders));
        }

        [Fact]
        public void RegisteredContentSystemsHaveNoConcreteSystemEdges()
        {
            const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type[] owners = SystemRegistrationManifest.Entries
                .Where(entry => entry.Enabled)
                .Select(entry => typeof(SystemRegistry).GetProperty(entry.Property,
                    BindingFlags.Instance | BindingFlags.Public)?.PropertyType)
                .Where(type => type != null && type.Namespace?.StartsWith(
                    "BattleSystemECS.Systems", StringComparison.Ordinal) == true)
                .Select(type => type!)
                .Distinct()
                .ToArray();
            var ownerTypes = owners.ToHashSet();
            var offenders = new SortedSet<string>(StringComparer.Ordinal);

            foreach (Type owner in owners)
            {
                foreach (FieldInfo field in owner.GetFields(declared))
                    AddConcreteContentEdge(owner, field.FieldType, ownerTypes, owner.FullName + "." + field.Name, offenders);
                foreach (PropertyInfo property in owner.GetProperties(declared))
                    AddConcreteContentEdge(owner, property.PropertyType, ownerTypes, owner.FullName + "." + property.Name, offenders);
                foreach (MethodBase method in owner.GetMethods(declared).Cast<MethodBase>()
                    .Concat(owner.GetConstructors(declared)))
                {
                    if (method is MethodInfo info)
                        AddConcreteContentEdge(owner, info.ReturnType, ownerTypes, method.ToString()!, offenders);
                    foreach (ParameterInfo parameter in method.GetParameters())
                        AddConcreteContentEdge(owner, parameter.ParameterType, ownerTypes, method.ToString()!, offenders);
                    if (method.GetMethodBody() == null) continue;
                    foreach (MemberInfo reference in ProductionIlWalker.GetMetadataReferences(method))
                    {
                        Type? referenced = reference as Type ?? reference.DeclaringType;
                        if (referenced != null)
                            AddConcreteContentEdge(owner, referenced, ownerTypes,
                                method + " -> " + (reference.ToString() ?? reference.Name), offenders);
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "Registered concrete content edges:\n" + string.Join("\n", offenders));
        }

        private static void AddConcreteContentEdge(Type owner, Type candidate, ISet<Type> ownerTypes, string source,
            ISet<string> offenders)
        {
            if (candidate.IsByRef || candidate.IsArray || candidate.IsPointer)
            {
                AddConcreteContentEdge(owner, candidate.GetElementType()!, ownerTypes, source, offenders);
                return;
            }
            if (candidate.IsGenericType)
                foreach (Type argument in candidate.GetGenericArguments())
                    AddConcreteContentEdge(owner, argument, ownerTypes, source, offenders);
            Type? concrete = candidate;
            while (concrete != null && !ownerTypes.Contains(concrete)) concrete = concrete.DeclaringType;
            if (concrete != null && concrete != owner)
                offenders.Add(source + " -> " + concrete.FullName);
        }

        private static Type Unwrap(Type type)
        {
            while (type.IsByRef || type.IsArray || type.IsPointer) type = type.GetElementType()!;
            return type;
        }

        private static void AddConcreteReference(MemberInfo reference, ISet<string> offenders)
        {
            Type? type = reference as Type ?? reference.DeclaringType;
            if (type != null) AddConcreteType(type, reference.ToString() ?? reference.Name, offenders);
        }

        private static void AddConcreteType(Type type, string source, ISet<string> offenders)
        {
            if (type.IsByRef || type.IsArray || type.IsPointer) AddConcreteType(type.GetElementType()!, source, offenders);
            if (type.IsGenericType)
                foreach (Type argument in type.GetGenericArguments()) AddConcreteType(argument, source, offenders);
            string ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("BattleSystemECS.Systems", StringComparison.Ordinal) || ns.StartsWith("BattleSystemECS.Core.GAS", StringComparison.Ordinal))
                offenders.Add(source + " -> " + type.FullName);
        }

        private static bool IsPublicOrProtected(FieldInfo field) =>
            field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

        private static bool IsPublicOrProtected(MethodBase method) =>
            method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

        private static void AddConcreteSystemType(Type type, string source, ISet<string> offenders)
        {
            if (type.IsByRef || type.IsArray || type.IsPointer)
            {
                AddConcreteSystemType(type.GetElementType()!, source, offenders);
                return;
            }
            if (type.IsGenericType)
                foreach (Type argument in type.GetGenericArguments())
                    AddConcreteSystemType(argument, source, offenders);
            string ns = type.Namespace ?? string.Empty;
            if (ns.StartsWith("BattleSystemECS.Systems", StringComparison.Ordinal))
                offenders.Add(source + " -> " + type.FullName);
        }
    }
}

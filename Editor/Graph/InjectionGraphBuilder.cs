using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AceLand.Injection.Editor.Graph
{
    public static class InjectionGraphBuilder
    {
        // ------------------------------------------------------------ edit mode

        /// <summary>Opens the scene, maps it, restores the previous setup. Edit mode only.</summary>
        public static InjectionGraph FromScene(string scenePath)
        {
            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                return FromLoadedScenes(scenePath);
            }
            finally
            {
                if (originalSetup is { Length: > 0 })
                {
                    try { EditorSceneManager.RestoreSceneManagerSetup(originalSetup); }
                    catch { /* ignored */ }
                }
            }
        }

        // ------------------------------------------------------------ play mode

        /// <summary>
        /// Maps whatever is currently loaded — no scene reopening.
        /// Safe in Play mode; builds throwaway validation containers alongside the live ones.
        /// </summary>
        public static InjectionGraph FromLoadedScenes(string context = null)
        {
            var graph = new InjectionGraph { Context = context ?? "loaded scenes" };
            var containers = new List<IObjectResolver>();
            IObjectResolver global = null;

            try
            {
                global = DI.CreateValidationContainer();
                if (global is Container gc) gc.Label = "DI.Global";
                AddScope(graph, global, null);

                var roots = new List<GameObject>();
                for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.isLoaded) roots.AddRange(scene.GetRootGameObjects());
                }

                var scopes = roots
                    .SelectMany(r => r.GetComponentsInChildren<LifetimeScope>(true))
                    .Where(s => s != null)
                    .OrderBy(s => Depth(s.transform))
                    .ToList();

                var built = new Dictionary<LifetimeScope, IObjectResolver>();

                foreach (var scope in scopes)
                {
                    var parent = ParentResolverFor(scope, built, global);
                    try
                    {
                        var resolver = scope.BuildContainerOnly(parent);
                        if (resolver is Container c) c.Label = scope.name;
                        built[scope] = resolver;
                        containers.Add(resolver);
                        AddScope(graph, resolver, parent, scope);
                    }
                    catch (Exception e)
                    {
                        var group = graph.AddGroup(new GraphGroup
                        {
#if UNITY_6000_3_OR_NEWER
                            Id = "scope:broken:" + scope.GetEntityId(),
#else
                            Id = "scope:broken:" + scope.GetInstanceID(),                      
#endif
                            Title = scope.name,
                            Subtitle = "installer failed",
                            IsError = true,
                            Depth = Depth(scope.transform),
                            Target = scope,
                            ObjectPath = HierarchyPath(scope.transform),
                            ComponentTypeName = scope.GetType().FullName,
                        });
                        group.Notes.Add(e.Message);
                    }
                }

                var sceneRoot = scopes
                    .Where(s => s.transform.parent == null && built.ContainsKey(s))
                    .Select(s => built[s])
                    .FirstOrDefault() ?? global;
                
                foreach (var root in roots)
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is LifetimeScope) continue;
                    AddConsumer(graph, behaviour, OwningResolver(behaviour.transform, built, sceneRoot));
                }
            }
            finally
            {
                for (var i = containers.Count - 1; i >= 0; i--)
                {
                    try { containers[i].Dispose(); } catch { /* ignored */ }
                }
                global?.Dispose();
            }

            return graph;
        }

        /// <summary>Maps the LIVE containers. Shows which singletons are actually instantiated.</summary>
        public static InjectionGraph FromRuntime()
        {
            var graph = new InjectionGraph { Context = "Runtime" };
            if (!Application.isPlaying) return graph;

            if (DI.IsGlobalBuilt) AddScope(graph, DI.Global, null);

            var scopes = UnityFind.All(typeof(LifetimeScope), true)
                .Cast<LifetimeScope>()
                .Where(s => s != null && s.IsBuilt)
                .OrderBy(s => Depth(s.transform))
                .ToList();

            foreach (var scope in scopes)
            {
                var introspect = scope.Resolver as IContainerIntrospection;
                AddScope(graph, scope.Resolver, introspect?.ParentResolver, scope);
            }

            foreach (var behaviour in UnityFind.All(typeof(MonoBehaviour), true).Cast<MonoBehaviour>())
            {
                if (behaviour == null || behaviour is LifetimeScope) continue;
                if (!InjectionMetadata.HasAnyInjection(behaviour.GetType())) continue;
                AddConsumer(graph, behaviour, SafeResolverFor(behaviour.gameObject));
            }

            return graph;
        }

        // ------------------------------------------------------------ nodes

        private static void AddScope(InjectionGraph graph, IObjectResolver resolver, IObjectResolver parent,
                     LifetimeScope source = null)
        {
            if (resolver is not IContainerIntrospection introspect) return;

            var id = graph.ScopeIdFor(resolver, source);
            var depth = introspect.Depth;

            var group = graph.AddGroup(new GraphGroup
            {
                Id = id,
                Title = introspect.Label,
                Subtitle = source != null ? $"depth {depth} · scope" : $"depth {depth} · global",
                ColorIndex = depth,
                Depth = depth,
                Target = source,
                ScenePath = source != null ? source.gameObject.scene.path : null,
                ObjectPath = source != null ? HierarchyPath(source.transform) : null,
                ComponentTypeName = source?.GetType().FullName,
                ResolvedType = source?.GetType(),
                TypeFullName = source?.GetType().FullName,
                Origin = source != null ? GraphOrigin.For(source.GetType()) : ""
            });

            // parent scopes are added first (depth-ordered), so its id already exists
            var parentId = graph.ExistingScopeId(parent);
            if (parentId != null)
                graph.Connect(id, parentId, EdgeKind.ScopeParent);

            var used = new Dictionary<string, int>();

            foreach (var reg in introspect.LocalRegistrations)
            {
                if (reg.Kind == RegistrationKind.Container) continue;

                if (reg is { Kind: RegistrationKind.Instance, ImplementationType: not null } &&
                    typeof(LifetimeScope).IsAssignableFrom(reg.ImplementationType)) continue;

                var nodeId = graph.RegistrationId(id, reg, used);

                var node = graph.Add(new GraphNode
                {
                    Id = nodeId,
                    Kind = NodeKind.Registration,
                    Title = reg.DisplayName,
                    Namespace = reg.ImplementationType?.Namespace,
                    OwnerScopeId = id,
                    Depth = depth,
                    IsInstantiated = reg.IsInstantiated,
                    StateLabel = reg.IsInstantiated ? "live" : "declared",
                    ResolvedType = reg.ImplementationType,
                    TypeFullName = reg.ImplementationType?.FullName,
                    Origin = GraphOrigin.For(reg.ImplementationType)
                });

                node.Subtitle = $"{reg.Lifetime}{(reg.Id != null ? $" · #{reg.Id}" : "")}";

                foreach (var contract in reg.ContractTypes)
                    if (contract != reg.ImplementationType) node.Contracts.Add(contract.Name);

                switch (reg.Kind)
                {
                    case RegistrationKind.Factory:
                        node.Details.Add("factory (opaque)");
                        break;
                    case RegistrationKind.Instance:
                        node.Details.Add("pre-built instance");
                        break;
                }

                group.Nodes.Add(node);
                graph.Connect(id, nodeId, EdgeKind.Provides);
            }

            LinkServiceDependencies(graph, introspect, group);

            if (source != null && group.Nodes.Count == 0)
            {
                group.IsWarning = true;
                if (source.InjectionTargetMode == InjectionTarget.None)
                    group.Notes.Add("No registrations and Injection Target = None. " +
                                    "This scope does nothing — safe to delete.");
                else
                    group.Notes.Add($"No registrations. Only provides injection " +
                                    $"({source.InjectionTargetMode}) and a disposal boundary.");
                if (source.IsPersistent) group.Notes.Add("Persistent (DontDestroyOnLoad).");
            }
        }

        /// <summary>Registration → registration edges: the service dependency graph.</summary>
        private static void LinkServiceDependencies(InjectionGraph graph, IContainerIntrospection introspect,
            GraphGroup group)
        {
            foreach (var reg in introspect.LocalRegistrations)
            {
                if (reg.Kind != RegistrationKind.Type || reg.ImplementationType == null) continue;

                var nodeId = graph.RegistrationIdBySerial(reg.Serial);      // ← lookup, not construct
                if (nodeId == null || graph.Find(nodeId) == null) continue;

                IReadOnlyList<InjectDependency> dependencies;
                try { dependencies = InjectionMetadata.GetDependencies(reg.ImplementationType); }
                catch { continue; }

                foreach (var dependency in dependencies)
                {
                    if (dependency.Kind == DependencyKind.Component) continue;

                    var (contract, edgeKind) = Unwrap(dependency.ContractType);
                    if (contract == typeof(IObjectResolver) || contract == typeof(Container)) continue;

                    if (introspect.TryDescribeResolution(contract, dependency.Id, out var target, out _))
                    {
                        var targetId = graph.RegistrationIdBySerial(target.Serial);
                        if (targetId != null && targetId != nodeId && graph.Find(targetId) != null)
                            graph.Connect(nodeId, targetId, edgeKind, dependency.MemberName);
                    }
                    else if (!dependency.Optional)
                    {
                        var missingId = InjectionGraph.MissingId(contract, dependency.Id);
                        EnsureMissing(graph, missingId, contract);
                        graph.Find(nodeId).HasError = true;
                        graph.Connect(nodeId, missingId, EdgeKind.Missing, dependency.MemberName);
                    }
                }
            }
        }

        private static void EnsureMissing(InjectionGraph graph, string id, Type contract)
        {
            if (graph.Find(id) != null) return;

            var group = graph.AddGroup(new GraphGroup
            {
                Id = "group:unresolved",
                Title = "Unresolved",
                Subtitle = "no registration found",
                IsError = true,
                Depth = int.MaxValue - 1
            });

            var node = graph.Add(new GraphNode
            {
                Id = id,
                Kind = NodeKind.Unresolved,
                Title = contract.Name,
                Namespace = contract.Namespace,
                Subtitle = "NOT REGISTERED",
                OwnerScopeId = group.Id,
                HasError = true,
                ResolvedType = contract,
                TypeFullName = contract.FullName,
                Origin = GraphOrigin.For(contract)
            });

            group.Nodes.Add(node);
        }

        private static void AddConsumer(InjectionGraph graph, MonoBehaviour behaviour, IObjectResolver resolver)
        {
            var type = behaviour.GetType();

            IReadOnlyList<InjectDependency> dependencies;
            try { dependencies = InjectionMetadata.GetDependencies(type); }
            catch { return; }
            if (dependencies.Count == 0) return;

            var group = graph.AddGroup(new GraphGroup
            {
                Id = "group:consumers",
                Title = "Consumers",
                Subtitle = "scene MonoBehaviours",
                ColorIndex = 4,
                Depth = int.MaxValue,
                ResolvedType = type,
                TypeFullName = type.FullName,
                Origin = GraphOrigin.For(type)
            });

            var path = HierarchyPath(behaviour.transform);
            var consumerId = InjectionGraph.ConsumerId(type, path);

            var consumer = graph.Add(new GraphNode
            {
                Id = consumerId,
                Kind = NodeKind.Consumer,
                Title = type.Name,
                Namespace = type.Namespace,
                Subtitle = path,
                OwnerScopeId = group.Id,
                Target = behaviour,
                ScenePath = behaviour.gameObject.scene.path,
                ObjectPath = path,
                ComponentTypeName = type.FullName
            });
            group.Nodes.Add(consumer);

            var introspect = resolver as IContainerIntrospection;

            foreach (var dependency in dependencies)
            {
                if (dependency.Kind == DependencyKind.Component)
                {
                    consumer.Details.Add($"[{dependency.ComponentSource}] {Short(dependency.ContractType)} " +
                                         dependency.MemberName);
                    continue;
                }

                var (contract, edgeKind) = Unwrap(dependency.ContractType);

                if (introspect != null &&
                    introspect.TryDescribeResolution(contract, dependency.Id, out var info, out _))
                {
                    var targetId = graph.RegistrationIdBySerial(info.Serial);
                    if (targetId != null)
                        graph.Connect(consumerId, targetId, edgeKind, dependency.MemberName);
                    continue;
                }

                if (dependency.Optional)
                {
                    consumer.Details.Add($"optional {Short(contract)} (unbound)");
                    continue;
                }

                var missingId = InjectionGraph.MissingId(contract, dependency.Id);
                EnsureMissing(graph, missingId, contract);
                consumer.HasError = true;
                graph.Connect(consumerId, missingId, EdgeKind.Missing, dependency.MemberName);
            }
        }

        // ------------------------------------------------------------ helpers

        private static readonly Dictionary<IObjectResolver, string> scopeIds = new();

        static IObjectResolver SafeResolverFor(GameObject go)
        {
            var scope = LifetimeScope.OwningScopeOf(go);      // parents → scene root scope
            if (scope != null && scope.IsBuilt) return scope.Resolver;
            return DI.IsGlobalBuilt ? DI.Global : null;       // never force a rebuild
        }

        private static (Type, EdgeKind) Unwrap(Type type)
        {
            if (type.IsArray) return (type.GetElementType(), EdgeKind.Collection);
            if (!type.IsGenericType) return (type, EdgeKind.Resolves);

            var def = type.GetGenericTypeDefinition();
            var arg = type.GetGenericArguments()[0];

            if (def == typeof(Func<>) || def == typeof(Lazy<>)) return (arg, EdgeKind.Deferred);
            if (def == typeof(IEnumerable<>) || def == typeof(IList<>) || def == typeof(List<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>) ||
                def == typeof(IReadOnlyCollection<>))
                return (arg, EdgeKind.Collection);

            return (type, EdgeKind.Resolves);
        }

        private static string Short(Type t) => t.IsGenericType
            ? t.Name.Substring(0, t.Name.IndexOf('`')) + "<" +
              string.Join(",", t.GetGenericArguments().Select(a => a.Name)) + ">"
            : t.Name;

        private static int Depth(Transform t)
        {
            var d = 0;
            for (var c = t.parent; c != null; c = c.parent) d++;
            return d;
        }

        private static string HierarchyPath(Transform t)
        {
            var path = t.name;
            for (var c = t.parent; c != null; c = c.parent) path = c.name + "/" + path;
            return path;
        }

        private static IObjectResolver ParentResolverFor(LifetimeScope scope,
                                                 Dictionary<LifetimeScope, IObjectResolver> built,
                                                 IObjectResolver fallback)
        {
            for (var t = scope.transform.parent; t != null; t = t.parent)
            {
                var candidate = t.GetComponent<LifetimeScope>();
                if (candidate != null && built.TryGetValue(candidate, out var r)) return r;
            }
            return fallback;
        }

        private static IObjectResolver OwningResolver(Transform transform,
                                              Dictionary<LifetimeScope, IObjectResolver> built,
                                              IObjectResolver sceneRoot)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                var scope = t.GetComponent<LifetimeScope>();
                if (scope != null && built.TryGetValue(scope, out var r)) return r;
            }
            return sceneRoot;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AceLand.Injection.Editor.Graph
{
    internal static class InjectionGraphBuilder
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
                        
                        group.Notes.Add(GraphNote.Error(e.Message));
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

            FinalizeScopeNotes(graph);
            return graph;
        }

        /// <summary>Maps the LIVE containers. Shows which singletons are actually instantiated.</summary>
        public static InjectionGraph FromRuntime()
        {
            var graph = new InjectionGraph { Context = "Runtime" };
            if (!Application.isPlaying) return graph;

            if (DI.IsGlobalBuilt) AddScope(graph, DI.Global, null);

            var scopes = FindAllObjects(typeof(LifetimeScope), true)
                .Cast<LifetimeScope>()
                .Where(s => s != null && s.IsBuilt)
                .OrderBy(s => Depth(s.transform))
                .ToList();

            foreach (var scope in scopes)
            {
                var introspect = scope.Resolver as IContainerIntrospection;
                AddScope(graph, scope.Resolver, introspect?.ParentResolver, scope);
            }

            foreach (var behaviour in FindAllObjects(typeof(MonoBehaviour), true).Cast<MonoBehaviour>())
            {
                if (behaviour == null || behaviour is LifetimeScope) continue;
                if (!InjectionMetadata.HasAnyInjection(behaviour.GetType())) continue;
                AddConsumer(graph, behaviour, SafeResolverFor(behaviour.gameObject));
            }

            FinalizeScopeNotes(graph);
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
            
            var installerIds = new string[introspect.Installers.Count];

            foreach (var info in introspect.Installers)
            {
                var nodeId = InjectionGraph.InstallerId(id, info.Key);
                var asset = info.Asset as Object;
                var component = asset as Component;

                var node = graph.Add(new GraphNode
                {
                    Id = nodeId,
                    Kind = NodeKind.Installer,
                    Title = info.Name,
                    Subtitle = "installer",
                    Namespace = info.Type?.Namespace,
                    OwnerScopeId = id,
                    Depth = depth,
                    ResolvedType = info.Type,
                    TypeFullName = info.Type?.FullName,
                    Origin = GraphOrigin.For(info.Type),
                    Target = asset,
                    ScenePath = component != null ? component.gameObject.scene.path : null,
                    ObjectPath = component != null ? HierarchyPath(component.transform) : null,
                    ComponentTypeName = info.Type?.FullName
                });

                group.Nodes.Add(node);
                graph.Connect(id, nodeId, EdgeKind.Provides);
            }

            foreach (var reg in introspect.LocalRegistrations)
            {
                if (reg.Kind == RegistrationKind.Container) continue;
                if (reg.Kind == RegistrationKind.Instance && reg.ImplementationType != null &&
                    typeof(LifetimeScope).IsAssignableFrom(reg.ImplementationType)) continue;

                var nodeId = graph.RegistrationKey(id, reg);
                graph.MapSerial(reg.Serial, nodeId);

                var installerId = reg.Source.HasValue
                    ? InjectionGraph.InstallerId(id, reg.Source.Value.Key)
                    : null;
                var installerNode = installerId != null ? graph.Find(installerId) : null;

                var existing = graph.Find(nodeId);
                if (existing != null)
                {
                    existing.MergeCount++;
                    if (reg.IsInstantiated) existing.InstantiatedCount++;
                    existing.IsInstantiated = existing.InstantiatedCount > 0;
                    existing.StateLabel = $"{existing.InstantiatedCount}/{existing.MergeCount} live";

                    if (installerNode != null)
                    {
                        installerNode.ProvidedCount++;
                        if (!existing.InstallerNodeIds.Contains(installerId))     // merged node → many installers
                            existing.InstallerNodeIds.Add(installerId);
                    }
                    continue;
                }

                var node = graph.Add(new GraphNode
                {
                    Id = nodeId,
                    Kind = NodeKind.Registration,
                    Title = reg.DisplayName,
                    Namespace = reg.ImplementationType?.Namespace,
                    OwnerScopeId = id,
                    Depth = depth,
                    MergeCount = 1,
                    InstantiatedCount = reg.IsInstantiated ? 1 : 0,
                    IsInstantiated = reg.IsInstantiated,
                    StateLabel = reg.IsInstantiated ? "live" : "declared",
                    ResolvedType = reg.ImplementationType,
                    TypeFullName = reg.ImplementationType?.FullName,
                    Origin = GraphOrigin.For(reg.ImplementationType)
                });

                node.Subtitle = $"{reg.Lifetime}{(reg.Id != null ? $" · #{reg.Id}" : "")}";

                if (installerNode != null)
                {
                    installerNode.ProvidedCount++;
                    node.InstallerNodeIds.Add(installerId);
                    graph.Connect(installerId, nodeId, EdgeKind.Installs);
                }

                foreach (var contract in reg.ContractTypes)
                    if (contract != reg.ImplementationType) node.Contracts.Add(TypeNames.Short(contract));

                if (reg.Kind == RegistrationKind.Factory) node.Details.Add("factory (opaque)");
                if (reg.Kind == RegistrationKind.Instance) node.Details.Add("pre-built instance");

                group.Nodes.Add(node);
                graph.Connect(id, nodeId, EdgeKind.Provides);
            }

            LinkServiceDependencies(graph, introspect);
        }
        
        /// <summary>
        /// Runs after every scope and consumer is added — an empty scope is only pointless
        /// if nothing depends on it for injection.
        /// </summary>
        static void FinalizeScopeNotes(InjectionGraph graph)
        {
            foreach (var group in graph.Groups)
            {
                if (!group.IsScope) continue;
                if (group.IsError) continue;
                if (group.Target is not LifetimeScope scope) continue;

                group.Notes.Clear();
                group.IsWarning = false;

                var injected = 0;
                foreach (var node in graph.Nodes)
                    if (node.Kind == NodeKind.Consumer && node.InjectorScopeId == group.Id) injected++;
                group.InjectedCount = injected;

                var registrations = group.Nodes.Count(n => n.Kind == NodeKind.Registration);
                var deadInstallers = group.Nodes.Count(n => n.Kind == NodeKind.Installer && n.ProvidedCount == 0);

                if (scope.IsPersistent)
                    group.Notes.Add(GraphNote.Info("Persistent (DontDestroyOnLoad)."));

                if (deadInstallers > 0)
                    group.Notes.Add(GraphNote.Warning(
                        $"{deadInstallers} installer(s) registered nothing — dead code, or an early return."));

                if (registrations > 0) continue;                    // ← was group.Nodes.Count

                var mode = scope.InjectionTargetMode;

                if (mode == InjectionTarget.None)
                {
                    group.IsWarning = true;
                    group.Notes.Add(GraphNote.Warning(
                        "No registrations and Injection Target = None. This scope does nothing — safe to delete."));
                }
                else if (injected == 0)
                {
                    group.IsWarning = true;
                    group.Notes.Add(GraphNote.Warning(
                        $"No registrations and no objects injected ({mode}). Nothing depends on this scope."));
                }
                else
                {
                    group.Notes.Add(GraphNote.Info(
                        $"Injects {injected} object(s) ({mode}) and provides a disposal boundary."));
                }
            }
        }

        /// <summary>Registration → registration edges: the service dependency graph.</summary>
        private static void LinkServiceDependencies(InjectionGraph graph, IContainerIntrospection introspect)
        {
            foreach (var reg in introspect.LocalRegistrations)
            {
                if (reg.Kind != RegistrationKind.Type || reg.ImplementationType == null) continue;

                var nodeId = graph.RegistrationIdBySerial(reg.Serial);      // ← lookup, not construct
                if (nodeId == null) continue;
                
                var owner = graph.Find(nodeId);
                if (owner == null) continue;

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
                        owner.HasError = true;
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
                Title = TypeNames.Short(contract),
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
            consumer.InjectorScopeId = graph.ExistingScopeId(resolver);
            
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

        private static IObjectResolver SafeResolverFor(GameObject go)
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
        
        private static FindObjectsInactive Inactive(bool includeInactive)
            => includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;

        private static Component[] FindAllObjects(Type type, bool includeInactive)
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            var objects = Object.FindObjectsByType(type, Inactive(includeInactive));
#elif UNITY_2020_1_OR_NEWER
            var objects = Object.FindObjectsOfType(type, includeInactive);
#else
            var objects = Object.FindObjectsOfType(type);
#endif
            var result = new Component[objects.Length];
            for (int i = 0; i < objects.Length; i++) result[i] = (Component)objects[i];
            return result;
        }
    }
}
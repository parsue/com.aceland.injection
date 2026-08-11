using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AceLand.Injection.Editor.Validation
{
    public enum IssueSeverity { Info, Warning, Error }

    public struct ValidationIssue
    {
        public IssueSeverity Severity;
        public string Context;       // scene / prefab asset path
        public string ObjectPath;    // hierarchy path
        public string TypeName;
        public string MemberName;
        public string ContractName;
        public string Message;
        public UnityEngine.Object Target;

        public override string ToString()
        {
            var site = string.IsNullOrEmpty(MemberName) ? TypeName : $"{TypeName}.{MemberName}";
            return $"[{Severity}] {Context} :: {ObjectPath} :: {site} -> {Message}";
        }
    }

    public sealed class ValidationReport
    {
        public readonly List<ValidationIssue> Issues = new();
        public int ScenesChecked, ScopesBuilt, ObjectsChecked, DependenciesChecked;
        public double DurationSeconds;

        public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
        public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
    }

    public static class InjectionValidator
    {
        // ------------------------------------------------------------------ entry points

        public static ValidationReport ValidateAll()
        {
            var settings = InjectionValidationSettings.instance;
            var report = new ValidationReport();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // remember what the user had open so we can put it back
            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            IObjectResolver global = null;

            try
            {
                try
                {
                    global = DI.CreateValidationContainer();
                }
                catch (Exception e)
                {
                    report.Issues.Add(Issue(IssueSeverity.Error, "Global", "", "Global container", "", "",
                        "Failed to build: " + e.Message, null));
                    return report;                          // nothing else can be checked
                }

                foreach (var path in GetScenePaths(settings))
                    ValidateScene(path, global, report, settings);

                if (settings.validatePrefabs)
                    ValidatePrefabs(global, report, settings);
            }
            finally
            {
                global?.Dispose();

                if (originalSetup is { Length: > 0 })
                {
                    try { EditorSceneManager.RestoreSceneManagerSetup(originalSetup); }
                    catch (Exception e) { Debug.LogWarning("[Injection] could not restore scenes: " + e.Message); }
                }

                stopwatch.Stop();
                report.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            }

            return report;
        }

        /// <summary>
        /// CLI entry point. -executeMethod requires a static void method.
        /// Unity -batchmode -quit -executeMethod AceLand.Injection.Editor.InjectionValidator.ValidateFromCommandLine
        /// </summary>
        public static void ValidateFromCommandLine()
        {
            var report = ValidateAll();

            foreach (var issue in report.Issues)
            {
                if (issue.Severity == IssueSeverity.Error) Debug.LogError("[Injection] " + issue);
                else if (issue.Severity == IssueSeverity.Warning) Debug.LogWarning("[Injection] " + issue);
                else Debug.Log("[Injection] " + issue);
            }

            Debug.Log($"[Injection] {report.ErrorCount} error(s), {report.WarningCount} warning(s) in " +
                      $"{report.ScenesChecked} scene(s), {report.DependenciesChecked} check(s), " +
                      $"{report.DurationSeconds:0.00}s");

            if (report.ErrorCount > 0) EditorApplication.Exit(1);
        }

        // ------------------------------------------------------------------ scenes

        private static IEnumerable<string> GetScenePaths(InjectionValidationSettings settings)
        {
            var buildScenes = EditorBuildSettings.scenes
                .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
                .Select(s => s.path);

            IEnumerable<string> candidates;

            if (settings.includeScenesNotInBuildSettings)
            {
                candidates = AssetDatabase.FindAssets("t:Scene")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => !string.IsNullOrEmpty(p) && p.StartsWith("Assets/"));
            }
            else
            {
                var forced = AssetDatabase.FindAssets("t:Scene")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => !string.IsNullOrEmpty(p) && p.StartsWith("Assets/"))
                    .Where(p => Matches(p, settings.alwaysIncludeScenePathFilters));

                candidates = buildScenes.Concat(forced);
            }

            return candidates
                .Where(p => !Matches(p, settings.ignoredScenePathFilters))
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        private static bool Matches(string path, string[] filters)
        {
            if (filters == null || filters.Length == 0) return false;
            var normalised = path.Replace('\\', '/');
            foreach (var filter in filters)
            {
                if (string.IsNullOrWhiteSpace(filter)) continue;
                if (normalised.IndexOf(filter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void ValidateScene(string path, IObjectResolver global, ValidationReport report,
                                  InjectionValidationSettings settings)
        {
            var containers = new List<IObjectResolver>();

            try
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                report.ScenesChecked++;

                var roots = scene.GetRootGameObjects();

                var scopes = roots
                    .SelectMany(r => r.GetComponentsInChildren<LifetimeScope>(true))
                    .Where(s => s != null)
                    .OrderBy(s => Depth(s.transform))
                    .ToList();

                // scope -> its validation container
                var built = new Dictionary<LifetimeScope, IObjectResolver>();

                foreach (var scope in scopes)
                {
                    var parent = ParentResolverFor(scope, built, global);

                    try
                    {
                        var resolver = scope.BuildContainerOnly(parent);
                        built[scope] = resolver;
                        containers.Add(resolver);
                        report.ScopesBuilt++;
                    }
                    catch (Exception e)
                    {
                        report.Issues.Add(Issue(IssueSeverity.Error, path, HierarchyPath(scope.transform),
                            scope.GetType().Name, "", "", "Installer threw: " + e.Message, scope));
                    }
                }

                // shallowest built scope owns anything without a scope above it
                var sceneRoot = scopes.Where(built.ContainsKey)
                                      .Select(s => built[s])
                                      .FirstOrDefault() ?? global;

                foreach (var root in roots)
                foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is LifetimeScope) continue;
                    var resolver = OwningResolver(behaviour.transform, built, sceneRoot);
                    ValidateObject(behaviour, resolver, path, report, settings);
                }
            }
            catch (Exception e)
            {
                report.Issues.Add(Issue(IssueSeverity.Error, path, "", "Scene", "", "",
                    "Could not validate: " + e.Message, null));
            }
            finally
            {
                for (int i = containers.Count - 1; i >= 0; i--)
                {
                    try { containers[i].Dispose(); }
                    catch (Exception e) { Debug.LogWarning("[Injection] dispose failed: " + e.Message); }
                }
            }
        }

        // ------------------------------------------------------------------ scope chain helpers

        /// <summary>Resolver of the nearest already-built ancestor scope, or the fallback.</summary>
        private static IObjectResolver ParentResolverFor(LifetimeScope scope,
                                                 Dictionary<LifetimeScope, IObjectResolver> built,
                                                 IObjectResolver fallback)
        {
            for (var t = scope.transform.parent; t != null; t = t.parent)
            {
                var candidate = t.GetComponent<LifetimeScope>();
                if (candidate != null && built.TryGetValue(candidate, out var resolver))
                    return resolver;
            }
            return fallback;
        }

        /// <summary>Resolver that would inject the object at runtime.</summary>
        private static IObjectResolver OwningResolver(Transform transform,
                                              Dictionary<LifetimeScope, IObjectResolver> built,
                                              IObjectResolver sceneRoot)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                var scope = t.GetComponent<LifetimeScope>();
                if (scope != null && built.TryGetValue(scope, out var resolver))
                    return resolver;
            }
            return sceneRoot;
        }

        private static int Depth(Transform t)
        {
            int depth = 0;
            for (var c = t.parent; c != null; c = c.parent) depth++;
            return depth;
        }

        // ------------------------------------------------------------------ prefabs

        private static void ValidatePrefabs(IObjectResolver global, ValidationReport report,
                                    InjectionValidationSettings settings)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                foreach (var behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null || behaviour is LifetimeScope) continue;
                    ValidateObject(behaviour, global, path, report, settings, isPrefab: true);
                }
            }
        }

        // ------------------------------------------------------------------ one object

        private static void ValidateObject(MonoBehaviour behaviour, IObjectResolver resolver, string context,
                                   ValidationReport report, InjectionValidationSettings settings,
                                   bool isPrefab = false)
        {
            var type = behaviour.GetType();

            IReadOnlyList<InjectDependency> dependencies;
            try { dependencies = InjectionMetadata.GetDependencies(type); }
            catch (Exception e)
            {
                report.Issues.Add(Issue(IssueSeverity.Warning, context, HierarchyPath(behaviour.transform),
                    type.Name, "", "", "Could not read injection points: " + e.Message, behaviour));
                return;
            }

            if (dependencies.Count == 0) return;

            report.ObjectsChecked++;
            var objectPath = HierarchyPath(behaviour.transform);

            foreach (var dependency in dependencies)
            {
                report.DependenciesChecked++;
                if (dependency.Optional) continue;

                if (dependency.Kind == DependencyKind.Component)
                {
                    // runtime-only or always-succeeds sources cannot be judged here
                    if (dependency.ComponentSource == ComponentSource.AddComponent) continue;
                    if (dependency.ComponentSource == ComponentSource.Scene) continue;

                    if (!ComponentPresent(behaviour, dependency))
                    {
                        report.Issues.Add(Issue(
                            settings.treatMissingComponentAsError ? IssueSeverity.Error : IssueSeverity.Warning,
                            context, objectPath, type.Name, dependency.MemberName,
                            dependency.ContractType.Name,
                            $"[{dependency.ComponentSource}] found no {ElementName(dependency.ContractType)}",
                            behaviour));
                    }
                    continue;
                }

                if (resolver == null || !resolver.CanResolve(dependency.ContractType, dependency.Id))
                {
                    var id = dependency.Id != null ? $" #{dependency.Id}" : "";
                    var note = isPrefab ? " (prefabs are checked against the global container)" : "";
                    report.Issues.Add(Issue(IssueSeverity.Error, context, objectPath, type.Name,
                        dependency.MemberName, dependency.ContractType.Name,
                        $"No registration for {dependency.ContractType.Name}{id}{note}", behaviour));
                }
            }
        }

        private static bool ComponentPresent(MonoBehaviour behaviour, InjectDependency dependency)
        {
            var type = ElementType(dependency.ContractType);
            if (type == null) return true;

            switch (dependency.ComponentSource)
            {
                case ComponentSource.Self:   return behaviour.GetComponent(type) != null;
                case ComponentSource.Parent: return behaviour.GetComponentInParent(type) != null;
                case ComponentSource.Child:  return behaviour.GetComponentInChildren(type, true) != null;
                default: return true;
            }
        }

        private static Type ElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType) return type.GetGenericArguments()[0];
            return type;
        }

        private static string ElementName(Type type) => ElementType(type)?.Name ?? type.Name;

        private static string HierarchyPath(Transform t)
        {
            var path = t.name;
            for (var c = t.parent; c != null; c = c.parent) path = c.name + "/" + path;
            return path;
        }

        private static ValidationIssue Issue(IssueSeverity severity, string context, string objectPath, string typeName,
                                     string memberName, string contractName, string message,
                                     UnityEngine.Object target)
        => new()
        {
            Severity = severity, Context = context, ObjectPath = objectPath, TypeName = typeName,
            MemberName = memberName, ContractName = contractName, Message = message, Target = target
        };
    }
}
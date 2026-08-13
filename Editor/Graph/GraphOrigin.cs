using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
using Object = UnityEngine.Object;

namespace AceLand.Injection.Editor.Graph
{
    internal readonly struct ScriptTarget
    {
        public readonly Type Type;
        public readonly MonoScript Script;
        public readonly bool FromPackage;

        public ScriptTarget(Type type, MonoScript script, bool fromPackage)
        {
            Type = type; Script = script; FromPackage = fromPackage;
        }

        public string Label => Type != null ? TypeNames.Short(Type) : Script?.name ?? "?";
    }

    
    /// <summary>Resolves where a type comes from (package / assembly) and where its script lives.</summary>
    internal static class GraphOrigin
    {
        // ── origin ──

        private static readonly Dictionary<Assembly, string> originCache = new();

        /// <summary>"com.aceland.library 2.2.3", "Assembly-CSharp", "Unity", or "" when unknown.</summary>
        public static string For(Type type)
        {
            if (type == null) return "";

            var assembly = type.Assembly;
            if (originCache.TryGetValue(assembly, out var cached)) return cached;

            var origin = Resolve(assembly);
            originCache[assembly] = origin;
            return origin;
        }

        private static string Resolve(Assembly assembly)
        {
            var name = assembly.GetName().Name;

            if (name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                name.StartsWith("UnityEditor", StringComparison.Ordinal))
                return "Unity";

            if (name.StartsWith("System", StringComparison.Ordinal) ||
                name == "mscorlib" || name == "netstandard")
                return ".NET";

            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(assembly);
                if (package != null)
                    return $"{package.name} {package.version}";
            }
            catch { /* older Unity, or an assembly outside the package graph */ }

            // predefined assemblies live in Assets/
            if (name == "Assembly-CSharp" || name == "Assembly-CSharp-Editor" ||
                name == "Assembly-CSharp-firstpass" || name == "Assembly-CSharp-Editor-firstpass")
                return name;

            try
            {
                var asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(name);
                if (!string.IsNullOrEmpty(asmdefPath))
                    return asmdefPath.StartsWith("Packages/", StringComparison.Ordinal)
                        ? $"{name} (package)"
                        : name;
            }
            catch { /* ignored */ }

            return name;
        }

        public static bool IsPackage(string origin)
            => !string.IsNullOrEmpty(origin) &&
               (origin.StartsWith("com.", StringComparison.Ordinal) || origin.EndsWith("(package)"));

        // ── scripts ──

        private static readonly string[] searchFolders = { "Assets", "Packages" };
        private static readonly Dictionary<string, ScriptTarget[]> targetCache = new();

        /// <summary>
        /// Every script a type could reasonably open: its own definition, plus each generic argument.
        /// IObjectPool&lt;PlayerInfoCard&gt; → [IObjectPool, PlayerInfoCard]
        /// </summary>
        public static ScriptTarget[] FindScripts(Type type, string typeFullName, Object sceneTarget)
        {
            // exact match for scene objects
            MonoScript exact = sceneTarget switch
            {
                MonoBehaviour mb => MonoScript.FromMonoBehaviour(mb),
                ScriptableObject so => MonoScript.FromScriptableObject(so),
                _ => null
            };
            if (exact != null)
                return new[] { new ScriptTarget(type, exact, IsPackagePath(AssetDatabase.GetAssetPath(exact))) };

            var key = type?.AssemblyQualifiedName ?? typeFullName;
            if (string.IsNullOrEmpty(key)) return Array.Empty<ScriptTarget>();
            if (targetCache.TryGetValue(key, out var cached)) return cached;

            var results = new List<ScriptTarget>();

            if (type != null)
            {
                foreach (var candidate in Candidates(type))
                {
                    var script = Lookup(candidate, TypeNames.Short(candidate));
                    if (script != null)
                        results.Add(new ScriptTarget(candidate, script,
                            IsPackagePath(AssetDatabase.GetAssetPath(script))));
                }
            }
            else
            {
                var script = Lookup(null, ShortName(typeFullName));
                if (script != null)
                    results.Add(new ScriptTarget(null, script,
                        IsPackagePath(AssetDatabase.GetAssetPath(script))));
            }

            var array = results.ToArray();
            targetCache[key] = array;
            return array;
        }

        /// <summary>Best single script: user code beats package code, definition beats arguments.</summary>
        public static MonoScript FindScript(Type type, string typeFullName, Object sceneTarget)
        {
            var targets = FindScripts(type, typeFullName, sceneTarget);
            if (targets.Length == 0) return null;

            foreach (var t in targets)
                if (!t.FromPackage) return t.Script;      // editable wins

            return targets[0].Script;
        }

        private static IEnumerable<Type> Candidates(Type type)
        {
            if (type == null) yield break;

            if (type.IsArray)
            {
                foreach (var t in Candidates(type.GetElementType())) yield return t;
                yield break;
            }

            yield return type.IsGenericType && !type.IsGenericTypeDefinition
                ? type.GetGenericTypeDefinition()
                : type;

            if (!type.IsGenericType) yield break;

            foreach (var argument in type.GetGenericArguments())
            foreach (var t in Candidates(argument))
                yield return t;
        }

        private static MonoScript Lookup(Type type, string shortName)
        {
            if (string.IsNullOrEmpty(shortName)) return null;

            MonoScript fallback = null;
            foreach (var guid in AssetDatabase.FindAssets($"{shortName} t:MonoScript", searchFolders))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                if (script == null || script.name != shortName) continue;

                if (type != null && script.GetClass() == type) return script;   // exact
                fallback ??= script;
            }
            return fallback;
        }

        private static bool IsPackagePath(string path)
            => !string.IsNullOrEmpty(path) && path.StartsWith("Packages/", StringComparison.Ordinal);

        /// <summary>
        /// Script-file name for a type. Handles generics, nested types and assembly-qualified strings.
        /// IObjectPool&lt;Bullet&gt; → "IObjectPool"
        /// </summary>
        public static string ShortName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "";

            var name = typeName;

            // 1. drop generic arguments:  Foo`1[[Bar, Asm, Version=...]]  →  Foo`1
            var bracket = name.IndexOf('[');
            if (bracket >= 0) name = name.Substring(0, bracket);

            // 2. drop assembly qualification:  Foo, Asm, Version=...  →  Foo
            var comma = name.IndexOf(',');
            if (comma >= 0) name = name.Substring(0, comma);

            name = name.Trim();

            // 3. namespace
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);

            // 4. nested types
            var plus = name.LastIndexOf('+');
            if (plus >= 0) name = name.Substring(plus + 1);

            // 5. generic arity
            var tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);

            return name;
        }

        public static void ClearCaches()
        {
            originCache.Clear();
            targetCache.Clear();
        }
    }
}
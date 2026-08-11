using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;
using Object = UnityEngine.Object;

namespace AceLand.Injection.Editor.Graph
{
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

        private static readonly Dictionary<string, MonoScript> scriptCache = new();

        /// <summary>Best-effort MonoScript lookup. Exact for MonoBehaviours, name-matched otherwise.</summary>
        public static MonoScript FindScript(Type type, string typeFullName, Object sceneTarget)
        {
            if (sceneTarget is MonoBehaviour behaviour)
            {
                var exact = MonoScript.FromMonoBehaviour(behaviour);
                if (exact != null) return exact;
            }
            if (sceneTarget is ScriptableObject so)
            {
                var exact = MonoScript.FromScriptableObject(so);
                if (exact != null) return exact;
            }

            var key = type?.FullName ?? typeFullName;
            if (string.IsNullOrEmpty(key)) return null;
            if (scriptCache.TryGetValue(key, out var cached)) return cached;

            var shortName = ShortName(key);
            MonoScript best = null;

            foreach (var guid in AssetDatabase.FindAssets($"{shortName} t:MonoScript"))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                if (script == null || script.name != shortName) continue;

                var scriptClass = script.GetClass();
                if (type != null && scriptClass == type) { best = script; break; }   // exact
                best ??= script;                                                     // name fallback
            }

            scriptCache[key] = best;
            return best;
        }

        public static string ShortName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return "";

            var name = typeName;
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);

            var plus = name.LastIndexOf('+');                 // nested types
            if (plus >= 0) name = name.Substring(plus + 1);

            var tick = name.IndexOf('`');                     // generic arity
            if (tick > 0) name = name.Substring(0, tick);

            return name;
        }

        public static void ClearCaches()
        {
            originCache.Clear();
            scriptCache.Clear();
        }
    }
}
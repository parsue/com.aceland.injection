// GraphOrigin.cs
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

        // ── scripts (delegated to ScriptLocator) ──

        /// <summary>
        /// Every script a type could reasonably open: its own declaration, plus each generic argument.
        /// IObjectPool&lt;PlayerInfoCard&gt; → [IObjectPool, PlayerInfoCard]
        /// </summary>
        public static ScriptTarget[] FindScripts(Type type, string typeFullName, Object sceneTarget)
            => ScriptLocator.Find(type, typeFullName, sceneTarget);

        /// <summary>Best single script: user code beats package code, definition beats arguments.</summary>
        public static MonoScript FindScript(Type type, string typeFullName, Object sceneTarget)
            => FindScript(type, typeFullName, sceneTarget, out _);

        /// <summary>As above, plus the line the declaration starts on (0 when unknown).</summary>
        public static MonoScript FindScript(Type type, string typeFullName, Object sceneTarget, out int line)
        {
            if (ScriptLocator.TryFindBest(type, typeFullName, sceneTarget, out var best))
            {
                line = best.Line;
                return best.Script;
            }

            line = 0;
            return null;
        }

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

        /// <summary>Per-scan reset. Script lookups survive — they self-invalidate on project change.</summary>
        public static void ClearCaches() => originCache.Clear();

        /// <summary>Full reset, for script reloads.</summary>
        public static void ClearAllCaches()
        {
            originCache.Clear();
            ScriptLocator.ClearCaches();
        }
    }
}
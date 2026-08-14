// ScriptLocator.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AceLand.Injection.Editor.Graph
{
    /// <summary>How a script was matched. Lower is more trustworthy.</summary>
    internal enum ScriptMatch
    {
        SceneObject = 0,   // MonoScript.FromMonoBehaviour — cannot be wrong
        ClassName   = 1,   // file named after the type AND GetClass() == type
        Declaration = 2,   // declaration found by scanning source text
        FileName    = 3,   // file named after the type, class unverified
    }

    internal readonly struct ScriptTarget
    {
        public readonly Type Type;
        public readonly MonoScript Script;
        public readonly bool FromPackage;
        public readonly bool Generated;
        public readonly int Line;
        public readonly ScriptMatch Match;

        private readonly string typeName;      // display name when Type is null

        public ScriptTarget(Type type, string typeName, MonoScript script, int line, ScriptMatch match)
        {
            Type = type;
            this.typeName = typeName;
            Script = script;
            Line = line;
            Match = match;

            var path = script != null ? AssetDatabase.GetAssetPath(script) : "";
            FromPackage = path.StartsWith("Packages/", StringComparison.Ordinal);
            Generated = ScriptLocator.IsGenerated(path);
        }

        public bool IsValid => Script != null;

        public string Name => Type != null ? TypeNames.Short(Type)
                            : !string.IsNullOrEmpty(typeName) ? typeName
                            : Script != null ? Script.name : "?";

        /// <summary>"ScoreService" or "ScoreService  ·  Services.cs" when the file is named differently.</summary>
        public string Label
        {
            get
            {
                var name = Name;
                return Script != null && Script.name != name ? $"{name}  ·  {Script.name}.cs" : name;
            }
        }

        /// <summary>Lower is better. Match dominates, then editability, then generated-ness.</summary>
        public int Rank => (int)Match * 4 + (FromPackage ? 2 : 0) + (Generated ? 1 : 0);
    }

    /// <summary>
    /// Finds the source file that declares a type — even when the file is not named after it.
    /// Three tiers: live scene object → file-name match → source scan for the declaration.
    /// </summary>
    internal static class ScriptLocator
    {
        // A full unscoped scan is refused beyond this many scripts; a scoped one is always allowed.
        private const int MAX_UNSCOPED_SCAN = 4000;

        private static readonly string[] DefaultScope = { "Assets", "Packages" };

        private static readonly Dictionary<string, ScriptTarget[]> resultCache = new();
        private static readonly Dictionary<string, ScriptTarget> locateCache = new();
        private static readonly Dictionary<string, string[]> scopeCache = new();
        private static readonly Dictionary<string, Regex> regexCache = new();

        [InitializeOnLoadMethod]
        private static void Hook() => EditorApplication.projectChanged += ClearCaches;

        // ══════════════════════════════════════════════════════ public

        /// <summary>
        /// Every script a type could reasonably open: its own declaration, plus each generic argument.
        /// IObjectPool&lt;PlayerInfoCard&gt; → [IObjectPool, PlayerInfoCard]
        /// </summary>
        public static ScriptTarget[] Find(Type type, string typeFullName, Object sceneTarget)
        {
            // ── tier 1 · a live instance names its own script; no guessing needed ──
            var exact = sceneTarget switch
            {
                MonoBehaviour mb => MonoScript.FromMonoBehaviour(mb),
                ScriptableObject so => MonoScript.FromScriptableObject(so),
                _ => null
            };
            if (exact != null)
                return new[]
                {
                    new ScriptTarget(type, GraphOrigin.ShortName(typeFullName), exact, 0, ScriptMatch.SceneObject)
                };

            var key = type?.AssemblyQualifiedName ?? typeFullName;
            if (string.IsNullOrEmpty(key)) return Array.Empty<ScriptTarget>();
            if (resultCache.TryGetValue(key, out var cached)) return cached;

            var results = new List<ScriptTarget>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var candidate in Candidates(type, typeFullName))
            {
                if (!seen.Add(candidate.Key)) continue;
                if (TryLocate(candidate, out var hit)) results.Add(hit);
            }

            var array = results.ToArray();
            resultCache[key] = array;
            return array;
        }

        /// <summary>
        /// Best single script. Editable code wins over package code — for a generic like
        /// IObjectPool&lt;PlayerInfoCard&gt; you almost always want YOUR type, not the package
        /// interface. Match quality only breaks ties between equally editable candidates.
        /// </summary>
        public static bool TryFindBest(Type type, string typeFullName, Object sceneTarget, out ScriptTarget best)
        {
            best = default;

            var targets = Find(type, typeFullName, sceneTarget);
            if (targets.Length == 0) return false;

            best = targets[0];
            foreach (var t in targets)
            {
                var better = (!t.FromPackage && best.FromPackage) ||
                             (t.FromPackage == best.FromPackage && t.Match < best.Match);
                if (better) best = t;      // strict improvement only → declaration order breaks ties
            }
            return true;
        }

        public static void ClearCaches()
        {
            resultCache.Clear();
            locateCache.Clear();
            scopeCache.Clear();
        }

        // ══════════════════════════════════════════════════════ candidates

        private readonly struct Candidate
        {
            public readonly Type Type;
            public readonly string Namespace;
            public readonly string Name;       // declaration name: no namespace, no arity
            public readonly string FileName;   // outermost name — the file it *probably* lives in

            public Candidate(Type type, string ns, string name, string fileName)
            {
                Type = type; Namespace = ns; Name = name; FileName = fileName;
            }

            public string Key => $"{Namespace}|{Name}";
        }

        private static IEnumerable<Candidate> Candidates(Type type, string typeFullName)
        {
            if (type == null)
            {
                var parsed = FromString(typeFullName);
                if (parsed.HasValue) yield return parsed.Value;
                yield break;
            }

            foreach (var t in Expand(type))
            {
                if (!HasProjectSource(t)) continue;
                yield return FromType(t);
            }
        }

        private static IEnumerable<Type> Expand(Type type)
        {
            if (type == null) yield break;

            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                foreach (var t in Expand(type.GetElementType())) yield return t;
                yield break;
            }

            yield return type.IsGenericType && !type.IsGenericTypeDefinition
                ? type.GetGenericTypeDefinition()
                : type;

            if (!type.IsGenericType) yield break;

            foreach (var argument in type.GetGenericArguments())
                foreach (var t in Expand(argument))
                    yield return t;
        }

        private static Candidate FromType(Type type)
        {
            var outer = type;
            while (outer.IsNested && outer.DeclaringType != null) outer = outer.DeclaringType;

            return new Candidate(type, outer.Namespace, StripArity(type.Name), StripArity(outer.Name));
        }

        /// <summary>Parses "Ns.Outer+Inner`1[[...]], Asm, Version=..." without reflection.</summary>
        private static Candidate? FromString(string typeFullName)
        {
            if (string.IsNullOrEmpty(typeFullName)) return null;

            var s = typeFullName;

            var bracket = s.IndexOf('[');                       // generic arguments
            if (bracket >= 0) s = s.Substring(0, bracket);
            var comma = s.IndexOf(',');                         // assembly qualification
            if (comma >= 0) s = s.Substring(0, comma);
            s = s.Trim();
            if (s.Length == 0) return null;

            var plus = s.IndexOf('+');
            var outerPart = plus >= 0 ? s.Substring(0, plus) : s;
            var declPart = plus >= 0 ? s.Substring(plus + 1) : s;

            var dot = outerPart.LastIndexOf('.');
            var ns = dot >= 0 ? outerPart.Substring(0, dot) : null;
            var fileName = StripArity(outerPart.Substring(dot + 1));

            var lastDot = declPart.LastIndexOf('.');
            var name = lastDot >= 0 ? declPart.Substring(lastDot + 1) : declPart;
            var lastPlus = name.LastIndexOf('+');               // Inner+Deeper
            if (lastPlus >= 0) name = name.Substring(lastPlus + 1);
            name = StripArity(name);

            return string.IsNullOrEmpty(name) ? null : new Candidate(null, ns, name, fileName);
        }

        private static string StripArity(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var tick = name.IndexOf('`');
            return tick > 0 ? name.Substring(0, tick) : name;
        }

        private static bool HasProjectSource(Type type)
        {
            if (type == null || type.IsGenericParameter || type.IsPrimitive) return false;
            if (type == typeof(string) || type == typeof(object) || type == typeof(decimal)) return false;

            var asm = type.Assembly.GetName().Name;
            if (asm.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                asm.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                asm.StartsWith("System", StringComparison.Ordinal) ||
                asm == "mscorlib" || asm == "netstandard")
                return false;

            return true;
        }

        // ══════════════════════════════════════════════════════ locating

        private static bool TryLocate(Candidate candidate, out ScriptTarget target)
        {
            target = default;
            if (string.IsNullOrEmpty(candidate.Name)) return false;

            if (locateCache.TryGetValue(candidate.Key, out var cached))
            {
                target = cached;
                return cached.IsValid;
            }

            var best = default(ScriptTarget);

            // ── tier 2 · the file is named after the type (the common case) ──
            if (!string.IsNullOrEmpty(candidate.FileName))
            {
                foreach (var guid in AssetDatabase.FindAssets($"{candidate.FileName} t:MonoScript", DefaultScope))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileNameWithoutExtension(path) != candidate.FileName) continue;

                    var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    if (script == null) continue;

                    // verified: Unity itself agrees this file declares the type
                    if (candidate.Type != null && script.GetClass() == candidate.Type)
                    {
                        target = new ScriptTarget(candidate.Type, candidate.Name, script,
                            LineOf(script.text, candidate.Name), ScriptMatch.ClassName);
                        locateCache[candidate.Key] = target;
                        return true;
                    }

                    // unverified: right name, but GetClass() disagreed (nested type, partial,
                    // or a string-only lookup). Keep only if the namespace lines up.
                    if (!NamespaceMatches(script.text, candidate.Namespace)) continue;

                    var weak = new ScriptTarget(candidate.Type, candidate.Name, script,
                        LineOf(script.text, candidate.Name), ScriptMatch.FileName);
                    if (!best.IsValid || weak.Rank < best.Rank) best = weak;
                }
            }

            // ── tier 3 · declared in a differently named file ──
            var scanned = ScanForDeclaration(candidate);
            if (scanned.IsValid && (!best.IsValid || scanned.Rank < best.Rank)) best = scanned;

            locateCache[candidate.Key] = best;
            target = best;
            return best.IsValid;
        }

        /// <summary>Text-scans the owning assembly's folder for "class|struct|interface|record|enum Name".</summary>
        private static ScriptTarget ScanForDeclaration(Candidate candidate)
        {
            var scope = ScopeFor(candidate.Type);
            var unscoped = ReferenceEquals(scope, DefaultScope);

            var guids = AssetDatabase.FindAssets("t:MonoScript", scope);
            if (unscoped && guids.Length > MAX_UNSCOPED_SCAN) return default;   // don't stall the editor

            var regex = DeclarationRegex(candidate.Name);

            string bestPath = null;
            var bestScore = int.MaxValue;
            var bestLine = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                var text = ReadText(path);
                if (string.IsNullOrEmpty(text)) continue;
                if (text.IndexOf(candidate.Name, StringComparison.Ordinal) < 0) continue;   // cheap reject
                if (!NamespaceMatches(text, candidate.Namespace)) continue;

                var match = regex.Match(text);
                if (!match.Success) continue;

                var score = Score(path, candidate.FileName);
                if (score >= bestScore) continue;

                bestScore = score;
                bestPath = path;
                bestLine = LineAt(text, match.Index);
                if (score == 0) break;
            }

            if (bestPath == null) return default;

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(bestPath);
            return script == null
                ? default
                : new ScriptTarget(candidate.Type, candidate.Name, script, bestLine, ScriptMatch.Declaration);
        }

        /// <summary>Folder of the asmdef that produced the type, so a scan never walks the whole project.</summary>
        private static string[] ScopeFor(Type type)
        {
            if (type == null) return DefaultScope;

            var asm = type.Assembly.GetName().Name;
            if (scopeCache.TryGetValue(asm, out var cached)) return cached;

            var scope = DefaultScope;

            if (asm.StartsWith("Assembly-CSharp", StringComparison.Ordinal))
            {
                scope = new[] { "Assets" };
            }
            else
            {
                var folder = AsmdefFolder(asm) ?? PackageFolder(type);
                if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
                    scope = new[] { folder };
            }

            scopeCache[asm] = scope;
            return scope;
        }

        private static string AsmdefFolder(string assemblyName)
        {
            try
            {
                var asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
                if (string.IsNullOrEmpty(asmdef)) return null;
                return Path.GetDirectoryName(asmdef)?.Replace('\\', '/');
            }
            catch { return null; }
        }

        private static string PackageFolder(Type type)
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(type.Assembly);
                return package?.assetPath;
            }
            catch { return null; }
        }

        // ══════════════════════════════════════════════════════ text helpers

        /// <summary>Disk read where possible; falls back to MonoScript.text for virtual package paths.</summary>
        private static string ReadText(string path)
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch { /* locked, encoding, whatever — fall through */ }

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            return script != null ? script.text : null;
        }

        private static Regex DeclarationRegex(string name)
        {
            if (regexCache.TryGetValue(name, out var cached)) return cached;

            var regex = new Regex($@"\b(?:class|struct|interface|record|enum)\s+{Regex.Escape(name)}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

            regexCache[name] = regex;
            return regex;
        }

        private static bool NamespaceMatches(string text, string ns)
        {
            if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(text)) return true;
            if (text.IndexOf("namespace " + ns, StringComparison.Ordinal) >= 0) return true;

            // nested declaration:  namespace A { namespace B { ... } }
            var dot = ns.LastIndexOf('.');
            if (dot < 0) return false;
            return text.IndexOf("namespace " + ns.Substring(dot + 1), StringComparison.Ordinal) >= 0;
        }

        private static int LineOf(string text, string name)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var match = DeclarationRegex(name).Match(text);
            return match.Success ? LineAt(text, match.Index) : 0;
        }

        private static int LineAt(string text, int index)
        {
            var line = 1;
            for (var i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        private static int Score(string path, string preferredFileName)
        {
            var score = 0;
            if (path.StartsWith("Packages/", StringComparison.Ordinal)) score += 4;
            if (IsGenerated(path)) score += 2;
            if (!string.IsNullOrEmpty(preferredFileName) &&
                Path.GetFileNameWithoutExtension(path) != preferredFileName) score += 1;
            return score;
        }

        internal static bool IsGenerated(string path)
            => !string.IsNullOrEmpty(path) &&
               (path.EndsWith(".g.cs", StringComparison.Ordinal) ||
                path.EndsWith(".generated.cs", StringComparison.Ordinal) ||
                path.IndexOf("/Generated/", StringComparison.Ordinal) >= 0);
    }
}
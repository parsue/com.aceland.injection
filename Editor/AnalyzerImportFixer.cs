using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    internal static class AnalyzerImportFixer
    {
        const string AnalyzerName = "AceLand.Injection.SourceGenerator.dll";
        const string Label = "RoslynAnalyzer";

        [MenuItem("Tools/AceLand/Injection/Fix Analyzer Import Settings")]
        private static void Fix()
        {
            var path = FindAnalyzer();
            if (path == null)
            {
                Debug.LogError($"[Injection] {AnalyzerName} not found. " +
                               "Build it: SourceGenerator~ → dotnet build -c Release");
                return;
            }

            var importer = AssetImporter.GetAtPath(path) as PluginImporter;
            if (importer == null)
            {
                Debug.LogError($"[Injection] {path} is not imported as a plugin. " +
                               "Is it inside an immutable package? Fix the .meta in the repo instead.");
                return;
            }

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(false);
            foreach (var target in (BuildTarget[])System.Enum.GetValues(typeof(BuildTarget)))
            {
                if ((int)target < 0) continue;                        // obsolete entries
                try { importer.SetCompatibleWithPlatform(target, false); }
                catch { /* unsupported target on this install */ }
            }
            
            if (PluginImporterCompat.TrySet(importer, false, out var how))
                Debug.Log($"[Injection] validateReferences = false (via {how})");
            else
                Debug.LogWarning(
                    "[Injection] Could not set validateReferences from script on this Unity version.\n" +
                    "The RoslynAnalyzer label alone is usually enough. If the 'Unable to resolve reference' " +
                    "error persists, edit the .meta manually — see the package README.");

            var labels = AssetDatabase.GetLabels(importer).ToList();
            if (!labels.Contains(Label))
            {
                labels.Add(Label);
                AssetDatabase.SetLabels(importer, labels.ToArray());
            }

            importer.SaveAndReimport();

            Debug.Log($"[Injection] Fixed import settings for {path}.\n" +
                      "→ Restart Unity, then run Tools ▸ AceLand ▸ Injection ▸ Diagnostics");
        }

        private static string FindAnalyzer()
        {
            var candidates = new[]
            {
                "Packages/com.aceland.injection/Analyzers/" + AnalyzerName,
                "Assets/Plugins/AceLand/" + AnalyzerName,
            };

            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return AssetDatabase.FindAssets("AceLand.Injection.SourceGenerator")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith(AnalyzerName, System.StringComparison.Ordinal));
        }
    }
}
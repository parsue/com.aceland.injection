using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    internal static class InjectionDiagnosticsMenu
    {
        [MenuItem("Tools/AceLand/Injection/Diagnostics")]
        private static void Dump()
        {
            var plans = InjectorPlanRegistry.All().ToList();
            var sb = new StringBuilder();

            sb.AppendLine($"<b>AceLand Injection — {plans.Count} generated injector plan(s)</b>");
            if (plans.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("No plans found — everything uses reflection (works, just slower).");

                var path = AssetDatabase.FindAssets("AceLand.Injection.SourceGenerator")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => p.EndsWith(".dll", System.StringComparison.Ordinal));

                if (path == null)
                {
                    sb.AppendLine("  ✗ analyzer DLL not found → build SourceGenerator~");
                }
                else if (AssetImporter.GetAtPath(path) is PluginImporter importer)
                {
                    var labels = AssetDatabase.GetLabels(importer);
                    var hasLabel = labels.Contains("RoslynAnalyzer");
                    var anyPlatform = importer.GetCompatibleWithAnyPlatform();
                    var editor = importer.GetCompatibleWithEditor();
                    var knowsRefs    = PluginImporterCompat.TryGet(importer, out var validateRefs);

                    sb.AppendLine($"  DLL          : {path}");
                    sb.AppendLine($"  label        : {(hasLabel ? "✓ RoslynAnalyzer" : "✗ MISSING")}");
                    sb.AppendLine($"  any platform : {(anyPlatform ? "✗ ENABLED — must be off" : "✓ off")}");
                    sb.AppendLine($"  editor       : {(editor ? "✗ ENABLED — must be off" : "✓ off")}");
                    sb.AppendLine($"  validate refs: {(!knowsRefs ? "? (internal on this version)" : validateRefs ? "✗ ON — must be off" : "✓ off")}");

                    if (!hasLabel || anyPlatform || editor || (knowsRefs && validateRefs))
                        sb.AppendLine("  → run Tools ▸ AceLand ▸ Injection ▸ Fix Analyzer Import Settings, then restart Unity");
                }
            }

            Debug.Log(sb.ToString());
        }

        [MenuItem("Tools/AceLand/Injection/Open Generated Code Folder")]
        private static void OpenGenerated()
            => EditorUtility.RevealInFinder(System.IO.Path.GetFullPath("Temp/GeneratedCode"));
    }
}
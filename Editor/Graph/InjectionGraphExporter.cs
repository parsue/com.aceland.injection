using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AceLand.Injection.Editor.Graph
{
    public static class InjectionGraphExporter
    {
        // ------------------------------------------------------------ Mermaid

        /// <summary>Mermaid flowchart — renders natively in GitBook, GitHub and Notion.</summary>
        public static string ToMermaid(InjectionGraph graph, bool includeConsumers = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("```mermaid");
            sb.AppendLine("graph RL");
            sb.AppendLine($"  %% {graph.Context}");

            // one subgraph per scope, holding its registrations
            foreach (var scope in graph.OfKind(NodeKind.Scope).OrderBy(s => s.Depth))
            {
                sb.AppendLine($"  subgraph {Safe(scope.Id)}[\"{Escape(scope.Title)}\"]");
                foreach (var reg in graph.OfKind(NodeKind.Registration)
                                         .Where(r => r.OwnerScopeId == scope.Id))
                {
                    var contracts = reg.Details.Where(d => d.StartsWith("as ")).ToArray();
                    var label = Escape(reg.Title) + "<br/>" + Escape(reg.Subtitle);
                    if (contracts.Length > 0)
                        label += "<br/><i>" + Escape(string.Join(", ", contracts)) + "</i>";
                    sb.AppendLine($"    {Safe(reg.Id)}[\"{label}\"]");
                }
                sb.AppendLine("  end");
            }

            foreach (var node in graph.OfKind(NodeKind.Unresolved))
                sb.AppendLine($"  {Safe(node.Id)}{{{{\"❌ {Escape(node.Title)}\"}}}}");

            if (includeConsumers)
                foreach (var node in graph.OfKind(NodeKind.Consumer))
                    sb.AppendLine($"  {Safe(node.Id)}([\"{Escape(node.Title)}\"])");

            foreach (var edge in graph.Edges)
            {
                if (edge.Kind == EdgeKind.Provides) continue;                       // implied by subgraph
                if (!includeConsumers && edge.Kind != EdgeKind.ScopeParent) continue;

                var from = graph.Find(edge.FromId);
                var to = graph.Find(edge.ToId);
                if (from == null || to == null) continue;

                var arrow = edge.Kind switch
                {
                    EdgeKind.ScopeParent => "-.->|parent|",
                    EdgeKind.Deferred    => "-. deferred .->",
                    EdgeKind.Collection  => "==>",
                    EdgeKind.Missing     => "-->|MISSING|",
                    _                    => "-->"
                };

                sb.AppendLine($"  {Safe(edge.FromId)} {arrow} {Safe(edge.ToId)}");
            }

            sb.AppendLine("  classDef err fill:#5a1e1e,stroke:#ff6b6b,color:#fff;");
            var errors = graph.Nodes.Where(n => n.HasError).Select(n => Safe(n.Id)).ToArray();
            if (errors.Length > 0) sb.AppendLine($"  class {string.Join(",", errors)} err;");

            sb.AppendLine("```");
            return sb.ToString();
        }

        // ------------------------------------------------------------ Graphviz

        public static string ToDot(InjectionGraph graph)
        {
            var sb = new StringBuilder();
            sb.AppendLine("digraph Injection {");
            sb.AppendLine("  rankdir=RL;");
            sb.AppendLine("  node [shape=box style=rounded fontname=\"Helvetica\" fontsize=10];");
            sb.AppendLine("  graph [fontname=\"Helvetica\" fontsize=11];");

            var cluster = 0;
            foreach (var scope in graph.OfKind(NodeKind.Scope).OrderBy(s => s.Depth))
            {
                sb.AppendLine($"  subgraph cluster_{cluster++} {{");
                sb.AppendLine($"    label=\"{Escape(scope.Title)}\"; style=filled; fillcolor=\"#f0f0f0\";");
                foreach (var reg in graph.OfKind(NodeKind.Registration).Where(r => r.OwnerScopeId == scope.Id))
                    sb.AppendLine($"    \"{reg.Id}\" [label=\"{Escape(reg.Title)}\\n{Escape(reg.Subtitle)}\"];");
                sb.AppendLine("  }");
            }

            foreach (var node in graph.OfKind(NodeKind.Unresolved))
                sb.AppendLine($"  \"{node.Id}\" [label=\"{Escape(node.Title)}\\nNOT REGISTERED\" " +
                              "shape=octagon color=red fontcolor=red];");

            foreach (var node in graph.OfKind(NodeKind.Consumer))
                sb.AppendLine($"  \"{node.Id}\" [label=\"{Escape(node.Title)}\" shape=ellipse" +
                              (node.HasError ? " color=red" : "") + "];");

            foreach (var edge in graph.Edges)
            {
                if (edge.Kind == EdgeKind.Provides) continue;
                var style = edge.Kind switch
                {
                    EdgeKind.ScopeParent => " [style=dashed label=\"parent\" color=gray]",
                    EdgeKind.Deferred    => " [style=dotted label=\"deferred\"]",
                    EdgeKind.Collection  => " [penwidth=2]",
                    EdgeKind.Missing     => " [color=red label=\"missing\"]",
                    _                    => ""
                };
                sb.AppendLine($"  \"{edge.FromId}\" -> \"{edge.ToId}\"{style};");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        // ------------------------------------------------------------ menus

        [MenuItem("Tools/AceLand/Injection/Export Graph (Mermaid)")]
        private static void ExportMermaid()
        {
            var path = EditorUtility.SaveFilePanel("Export injection graph",
                Application.dataPath, "injection-graph", "md");
            if (string.IsNullOrEmpty(path)) return;

            var sb = new StringBuilder();
            sb.AppendLine("# Injection Graph");
            sb.AppendLine();
            foreach (var scene in EditorBuildSettings.scenes.Where(s => s.enabled))
            {
                sb.AppendLine($"## {System.IO.Path.GetFileNameWithoutExtension(scene.path)}");
                sb.AppendLine();
                sb.AppendLine(ToMermaid(InjectionGraphBuilder.FromScene(scene.path)));
                sb.AppendLine();
            }

            System.IO.File.WriteAllText(path, sb.ToString());
            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("Tools/AceLand/Injection/Copy Current Scene Graph (Mermaid)")]
        private static void CopyCurrent()
        {
            var path = SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning("[Injection] save the scene first.");
                return;
            }
            EditorGUIUtility.systemCopyBuffer = ToMermaid(InjectionGraphBuilder.FromScene(path));
            Debug.Log("[Injection] Mermaid graph copied to clipboard.");
        }

        private static string Safe(string id) => id.Replace(":", "_").Replace(".", "_")
                                          .Replace("/", "_").Replace("#", "_").Replace("+", "_");

        private static string Escape(string s) => (s ?? "").Replace("\"", "'");
    }
}
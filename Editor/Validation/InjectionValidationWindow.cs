using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    public sealed class InjectionValidationWindow : EditorWindow
    {
        ValidationReport _report;
        Vector2 _scroll;
        bool _showErrors = true, _showWarnings = true, _showInfo;
        string _filter = "";

        [MenuItem("Window/AceLand/Injection/Validation %#i")]
        public static void Open()
        {
            var w = GetWindow<InjectionValidationWindow>("Injection Validation");
            w.minSize = new Vector2(720, 320);
            w.Show();
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Validate", EditorStyles.toolbarButton, GUILayout.Width(80)))
                    Run();
                GUILayout.Space(8);
                _showErrors   = GUILayout.Toggle(_showErrors,   "Errors",   EditorStyles.toolbarButton, GUILayout.Width(60));
                _showWarnings = GUILayout.Toggle(_showWarnings, "Warnings", EditorStyles.toolbarButton, GUILayout.Width(70));
                _showInfo     = GUILayout.Toggle(_showInfo,     "Info",     EditorStyles.toolbarButton, GUILayout.Width(50));
                GUILayout.FlexibleSpace();
                _filter = GUILayout.TextField(_filter, EditorStyles.toolbarSearchField, GUILayout.Width(220));
                if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    SettingsService.OpenProjectSettings("Project/AceLand/Injection");
            }

            if (_report == null)
            {
                EditorGUILayout.HelpBox(
                    "Press Validate.\n\n" +
                    "• Opens each scene additively, builds every LifetimeScope container (no entry points, " +
                    "no injection, no side effects)\n" +
                    "• Checks every [Inject] / [Self] / [Parent] / [Child] site against the resolved scope chain\n" +
                    "• Prefabs are checked against the global container\n\n" +
                    "Save your scenes first — open scenes are reloaded.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(
                $"{_report.ErrorCount} error(s), {_report.WarningCount} warning(s) · " +
                $"{_report.ScopesBuilt} scope(s), {_report.ObjectsChecked} object(s), " +
                $"{_report.DependenciesChecked} dependency check(s) in {_report.DurationSeconds:0.00}s · " +
                $"{InjectorPlanRegistry.Count} generated injector(s)",
                _report.ErrorCount > 0 ? MessageType.Error :
                _report.WarningCount > 0 ? MessageType.Warning : MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var issue in _report.Issues)
            {
                if (issue.Severity == IssueSeverity.Error && !_showErrors) continue;
                if (issue.Severity == IssueSeverity.Warning && !_showWarnings) continue;
                if (issue.Severity == IssueSeverity.Info && !_showInfo) continue;
                if (!string.IsNullOrEmpty(_filter) &&
                    !issue.ToString().ToLowerInvariant().Contains(_filter.ToLowerInvariant())) continue;

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var icon = issue.Severity == IssueSeverity.Error ? "console.erroricon"
                                 : issue.Severity == IssueSeverity.Warning ? "console.warnicon"
                                 : "console.infoicon";
                        GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(22), GUILayout.Height(18));
                        EditorGUILayout.LabelField($"{issue.TypeName}.{issue.MemberName}", EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        if (issue.Target != null && GUILayout.Button("Select", GUILayout.Width(60)))
                        {
                            Selection.activeObject = issue.Target;
                            EditorGUIUtility.PingObject(issue.Target);
                        }
                        if (!string.IsNullOrEmpty(issue.Context) && GUILayout.Button("Open", GUILayout.Width(52)))
                        {
                            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(issue.Context);
                            if (asset != null) AssetDatabase.OpenAsset(asset);
                        }
                    }
                    EditorGUILayout.LabelField(issue.Message);
                    EditorGUILayout.LabelField($"{issue.Context}  ›  {issue.ObjectPath}", EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void Run()
        {
            if (!UnityEditor.SceneManagement.EditorSceneManager
                    .SaveCurrentModifiedScenesIfUserWantsTo()) return;
            try
            {
                EditorUtility.DisplayProgressBar("AceLand Injection", "Validating…", 0.5f);
                _report = InjectionValidator.ValidateAll();
            }
            finally { EditorUtility.ClearProgressBar(); }
            Repaint();
        }
    }
}
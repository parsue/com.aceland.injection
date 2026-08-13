using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor.Validation
{
    [FilePath("ProjectSettings/AceLandInjectionValidation.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class InjectionValidationSettings : ScriptableSingleton<InjectionValidationSettings>
    {
        public bool validateOnBuild = true;
        public bool failBuildOnError = true;
        public bool includeScenesNotInBuildSettings;
        public bool validatePrefabs = true;
        public bool treatMissingComponentAsError = true;

        [Tooltip("Skip scenes whose path contains any of these. One per line. Case-insensitive.")]
        public string[] ignoredScenePathFilters = { "/Tests/", "/Sandbox/", "/_Scratch/" };

        [Tooltip("Always validate scenes matching these, even if not in Build Settings. " +
                 "Use for Addressables / async-loaded scenes.")]
        public string[] alwaysIncludeScenePathFilters = { "/Addressables/" };

        public void Save() => Save(true);
    }

    static class InjectionSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider Create() =>
        new("Project/AceLand Packages/Injection", SettingsScope.Project)
        {
            label = "Injection",
            guiHandler = _ =>
            {
                var s = InjectionValidationSettings.instance;
                EditorGUI.BeginChangeCheck();

                EditorGUILayout.LabelField("Build Gate", EditorStyles.boldLabel);
                s.validateOnBuild = EditorGUILayout.Toggle(
                    new GUIContent("Validate On Build",
                        "Run validation in IPreprocessBuildWithReport before every build."),
                    s.validateOnBuild);
                using (new EditorGUI.DisabledScope(!s.validateOnBuild))
                    s.failBuildOnError = EditorGUILayout.Toggle(
                        new GUIContent("Fail Build On Error",
                            "Throw BuildFailedException when unresolvable bindings are found."),
                        s.failBuildOnError);

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Scope", EditorStyles.boldLabel);

                s.includeScenesNotInBuildSettings = EditorGUILayout.Toggle(
                    new GUIContent("Include All Scenes",
                        "ON  — every .unity under Assets/ (catches Addressables and additive scenes).\n" +
                        "OFF — enabled Build Settings entries, plus anything matching Always Include."),
                    s.includeScenesNotInBuildSettings);

                var count = 0;
                try { count = GetScenePathsPublic(s).Count(); } catch { /* ignored */ }
                EditorGUILayout.LabelField(" ", $"{count} scene(s) will be validated", EditorStyles.miniLabel);

                s.validatePrefabs = EditorGUILayout.Toggle(
                    new GUIContent("Validate Prefabs",
                        "Check prefab assets against DI.Global. Their runtime scope is unknown, so only " +
                        "globally-registered contracts can be verified."),
                    s.validatePrefabs);

                s.treatMissingComponentAsError = EditorGUILayout.Toggle(
                    new GUIContent("Component Miss = Error",
                        "A missing [Self]/[Parent]/[Child] target fails the build. Turn off for warnings."),
                    s.treatMissingComponentAsError);

                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Filters", EditorStyles.boldLabel);
                DrawStringList("Ignore Paths", ref s.ignoredScenePathFilters,
                    "Skip scenes whose path contains any of these.");
                DrawStringList("Always Include", ref s.alwaysIncludeScenePathFilters,
                    "Validate these even when Include All Scenes is off.");

                if (EditorGUI.EndChangeCheck()) s.Save();
            },
            keywords = new[] { "aceland", "injection", "di", "validation" }
        };

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
                .OrderBy(p => p, System.StringComparer.Ordinal);
        }

        private static bool Matches(string path, string[] filters)
        {
            if (filters == null || filters.Length == 0) return false;
            var normalised = path.Replace('\\', '/');
            foreach (var filter in filters)
            {
                if (string.IsNullOrWhiteSpace(filter)) continue;
                if (normalised.IndexOf(filter.Trim(), System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void DrawStringList(string label, ref string[] values, string tooltip)
        {
            values ??= System.Array.Empty<string>();
            EditorGUILayout.LabelField(new GUIContent(label, tooltip));

            using (new EditorGUI.IndentLevelScope())
            {
                for (var i = 0; i < values.Length; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        values[i] = EditorGUILayout.TextField(values[i]);
                        if (GUILayout.Button("−", GUILayout.Width(24)))
                        {
                            values = values.Where((_, index) => index != i).ToArray();
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                if (GUILayout.Button("+ Add", GUILayout.Width(70)))
                    values = values.Append("/Folder/").ToArray();
            }
        }
        
        /// <summary>Editor UI preview of the scenes that will be validated.</summary>
        private static IEnumerable<string> GetScenePathsPublic(InjectionValidationSettings settings)
            => GetScenePaths(settings);
    }
}
using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    [FilePath("ProjectSettings/AceLandInjectionValidation.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class InjectionValidationSettings : ScriptableSingleton<InjectionValidationSettings>
    {
        public bool validateOnBuild = true;
        public bool failBuildOnError = true;
        public bool includeScenesNotInBuildSettings;
        public bool validatePrefabs = true;
        public bool treatMissingComponentAsError = true;

        public void Save() => Save(true);
    }

    static class InjectionSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider Create() =>
            new SettingsProvider("Project/AceLand/Injection", SettingsScope.Project)
            {
                label = "Injection",
                guiHandler = _ =>
                {
                    var s = InjectionValidationSettings.instance;
                    EditorGUI.BeginChangeCheck();
                    s.validateOnBuild = EditorGUILayout.Toggle("Validate On Build", s.validateOnBuild);
                    using (new EditorGUI.DisabledScope(!s.validateOnBuild))
                        s.failBuildOnError = EditorGUILayout.Toggle("Fail Build On Error", s.failBuildOnError);
                    s.includeScenesNotInBuildSettings =
                        EditorGUILayout.Toggle("Include All Scenes", s.includeScenesNotInBuildSettings);
                    s.validatePrefabs = EditorGUILayout.Toggle("Validate Prefabs", s.validatePrefabs);
                    s.treatMissingComponentAsError =
                        EditorGUILayout.Toggle("Component Miss = Error", s.treatMissingComponentAsError);
                    if (EditorGUI.EndChangeCheck()) s.Save();
                },
                keywords = new[] { "aceland", "injection", "di", "validation" }
            };
    }
}
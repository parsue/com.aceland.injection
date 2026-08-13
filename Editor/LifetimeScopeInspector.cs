using AceLand.Injection.Editor.Validation;
using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    [CustomEditor(typeof(LifetimeScope), true)]
    internal class LifetimeScopeInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var scope = (LifetimeScope)target;
            EditorGUILayout.Space();

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Built in Awake (order -5000): injection completes before other MonoBehaviours' Awake.\n" +
                    "Parent defaults to the nearest LifetimeScope above, then DI.Global.", MessageType.Info);
                if (GUILayout.Button("Validate Project")) InjectionValidationWindow.Open();
                return;
            }

            EditorGUILayout.LabelField("Runtime", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Built", scope.IsBuilt ? "Yes" : "No");
            EditorGUILayout.LabelField("Async startup",
                scope.StartupTask.IsCompleted ? "completed" : "running…");
        }
    }
}
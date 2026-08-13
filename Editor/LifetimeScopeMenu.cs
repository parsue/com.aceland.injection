using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    internal static class LifetimeScopeMenu
    {
        [MenuItem("GameObject/AceLand/Lifetime Scope", false, 10)]
        private static void Create(MenuCommand cmd)
        {
            var go = new GameObject("LifetimeScope", typeof(LifetimeScope));
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create LifetimeScope");
            Selection.activeObject = go;
        }
    }
}
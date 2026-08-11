using UnityEditor;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    static class LifetimeScopeMenu
    {
        [MenuItem("GameObject/AceLand/Lifetime Scope", false, 10)]
        static void Create(MenuCommand cmd)
        {
            var go = new GameObject("LifetimeScope", typeof(LifetimeScope));
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create LifetimeScope");
            Selection.activeObject = go;
        }
    }
}
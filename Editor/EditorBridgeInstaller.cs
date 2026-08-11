using UnityEditor;

namespace AceLand.Injection.Editor
{
    [InitializeOnLoad]
    internal static class EditorBridgeInstaller
    {
        static EditorBridgeInstaller()
            => InjectionBridge.SetGlobalProvider(() => DI.IsGlobalBuilt ? DI.Global : null);
    }
}
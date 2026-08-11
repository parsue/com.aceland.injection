using System.Reflection;
using UnityEditor;

namespace AceLand.Injection.Editor
{
    /// <summary>
    /// PluginImporter.ValidateReferences is public on some Unity versions and internal on others.
    /// Reach it through reflection, then through SerializedObject, then give up gracefully.
    /// </summary>
    internal static class PluginImporterCompat
    {
        private const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] serializedNames =
        {
            "m_ValidateReferences",
            "validateReferences",
        };

        public static bool TryGet(PluginImporter importer, out bool value)
        {
            value = false;
            if (importer == null) return false;

            var property = typeof(PluginImporter).GetProperty("ValidateReferences", FLAGS);
            if (property != null && property.CanRead)
            {
                try
                {
                    value = (bool)property.GetValue(importer);
                    return true;
                }
                catch { /* fall through */ }
            }

            var serialized = new SerializedObject(importer);
            foreach (var name in serializedNames)
            {
                var prop = serialized.FindProperty(name);
                if (prop == null) continue;
                value = prop.boolValue;
                return true;
            }

            return false;
        }

        public static bool TrySet(PluginImporter importer, bool value, out string how)
        {
            how = null;
            if (importer == null) return false;

            var property = typeof(PluginImporter).GetProperty("ValidateReferences", FLAGS);
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(importer, value);
                    how = "reflection";
                    return true;
                }
                catch { /* fall through */ }
            }

            var serialized = new SerializedObject(importer);
            foreach (var name in serializedNames)
            {
                var prop = serialized.FindProperty(name);
                if (prop == null) continue;
                prop.boolValue = value;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                how = $"SerializedObject.{name}";
                return true;
            }

            return false;
        }
    }
}
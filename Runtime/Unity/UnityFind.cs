using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AceLand.Injection
{
    /// <summary>Version-safe FindObject* wrappers (the plural overloads exist on every supported version).</summary>
    internal static class UnityFind
    {
        public static FindObjectsInactive Inactive(bool includeInactive)
            => includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
        
        public static T First<T>(bool includeInactive) where T : Component
        {
    
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            return Object.FindAnyObjectByType<T>(Inactive(includeInactive));
#elif UNITY_2020_1_OR_NEWER
            var all = Object.FindObjectsOfType<T>(includeInactive);
            return all.Length > 0 ? all[0] : null;
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        public static Component First(Type type, bool includeInactive)
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            return (Component)Object.FindAnyObjectByType(type, Inactive(includeInactive));
#else
            var all = All(type, includeInactive);
            return all.Length > 0 ? all[0] : null;
#endif
        }

        public static Component[] All(Type type, bool includeInactive)
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
            var objects = Object.FindObjectsByType(type, Inactive(includeInactive));
#elif UNITY_2020_1_OR_NEWER
            var objects = Object.FindObjectsOfType(type, includeInactive);
#else
            var objects = Object.FindObjectsOfType(type);
#endif
            var result = new Component[objects.Length];
            for (int i = 0; i < objects.Length; i++) result[i] = (Component)objects[i];
            return result;
        }
    }
}
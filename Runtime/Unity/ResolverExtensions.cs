// ResolverExtensions.cs
using UnityEngine;
using Object = UnityEngine.Object;

namespace AceLand.Injection
{
    public static class ResolverExtensions
    {
        public static void InjectGameObject(this IObjectResolver resolver, GameObject go)
        {
            if (go == null) return;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) resolver.Inject(mb);
        }

        public static T Instantiate<T>(this IObjectResolver r, T prefab, Transform parent = null,
                                       bool worldPositionStays = false) where T : Object
            => InstantiationStage.Instantiate(r, prefab, parent, worldPositionStays);

        public static T Instantiate<T>(this IObjectResolver r, T prefab, Vector3 position,
                                       Quaternion rotation, Transform parent = null) where T : Component
        {
            var i = InstantiationStage.Instantiate(r, prefab, parent, false);
            i.transform.SetPositionAndRotation(position, rotation);
            return i;
        }

        public static GameObject Instantiate(this IObjectResolver r, GameObject prefab, Vector3 position,
                                             Quaternion rotation, Transform parent = null)
        {
            var i = InstantiationStage.Instantiate(r, prefab, parent, false);
            i.transform.SetPositionAndRotation(position, rotation);
            return i;
        }
    }
}
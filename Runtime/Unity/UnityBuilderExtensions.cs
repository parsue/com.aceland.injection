// UnityBuilderExtensions.cs
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AceLand.Injection
{
    public static class UnityBuilderExtensions
    {
        public static IRegistrationBuilder RegisterComponent<T>(this IContainerBuilder b, T c) where T : Component
            => b.RegisterInstance(typeof(T), c);

        public static IRegistrationBuilder RegisterScriptableObject<T>(this IContainerBuilder b, T a)
            where T : ScriptableObject => b.RegisterInstance(typeof(T), a);

        public static IRegistrationBuilder RegisterComponentInHierarchy<T>(this IContainerBuilder b) where T : Component
        {
            var scene = (b as ContainerBuilder)?.ContextScene;
            return b.RegisterFactory(typeof(T), _ =>
                FindInScene<T>(scene) ??
                throw new InjectionException($"RegisterComponentInHierarchy<{typeof(T).Name}>: not found."),
                Lifetime.Singleton);
        }

        public static IRegistrationBuilder RegisterComponentInNewPrefab<T>(this IContainerBuilder b, T prefab,
                                                                           Lifetime lifetime, Transform parent = null)
            where T : Component
        {
            var p = parent ?? (b as ContainerBuilder)?.ContextTransform;
            return b.RegisterFactory(typeof(T), r => r.Instantiate(prefab, p), lifetime);
        }

        public static IRegistrationBuilder RegisterComponentOnNewGameObject<T>(this IContainerBuilder b,
            Lifetime lifetime, string name = null, Transform parent = null) where T : Component
        {
            var p = parent ?? (b as ContainerBuilder)?.ContextTransform;
            return b.RegisterFactory(typeof(T), r =>
            {
                var go = new GameObject(name ?? typeof(T).Name);
                if (p != null) go.transform.SetParent(p, false);
                var c = go.AddComponent<T>();
                r.Inject(c);
                return c;
            }, lifetime);
        }

        static T FindInScene<T>(Scene? scene) where T : Component
        {
            if (scene.HasValue && scene.Value.IsValid())
            {
                foreach (var root in scene.Value.GetRootGameObjects())
                {
                    var c = root.GetComponentInChildren<T>(true);
                    if (c != null) return c;
                }
                return null;
            }
            return UnityFind.First<T>(true);
        }
    }
}
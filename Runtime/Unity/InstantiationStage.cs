// InstantiationStage.cs
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AceLand.Injection
{
    internal static class InstantiationStage
    {
        static GameObject _stage;

        internal static Transform Stage
        {
            get
            {
                if (_stage == null)
                {
                    _stage = new GameObject("[Injection Instantiation Stage]");
                    _stage.SetActive(false);                    // children never receive Awake here
                    _stage.hideFlags = HideFlags.HideAndDontSave;
                    if (Application.isPlaying) Object.DontDestroyOnLoad(_stage);
                }
                return _stage.transform;
            }
        }

        public static T Instantiate<T>(IObjectResolver resolver, T prefab, Transform parent,
                                       bool worldPositionStays) where T : Object
        {
            if (prefab == null) throw new InjectionException("Cannot instantiate a null prefab.");

            var instance = Object.Instantiate(prefab, Stage);
            var go = instance as GameObject ?? (instance as Component)?.gameObject;

            if (go != null)
            {
                resolver.InjectGameObject(go);                  // injection BEFORE Awake
                go.transform.SetParent(parent, worldPositionStays);
                if (parent == null && Application.isPlaying)
                    SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
            }
            else resolver.Inject(instance);

            return instance;
        }
    }
}
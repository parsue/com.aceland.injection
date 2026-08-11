using UnityEngine;
using Object = UnityEngine.Object;

namespace AceLand.Injection
{
    /// <summary>Prefab pool. Instances are injected once at creation (before Awake) and reused.</summary>
    public sealed class ComponentPool<T> : ObjectPool<T> where T : Component
    {
        public ComponentPool(IObjectResolver resolver, T prefab, Transform parent = null,
                             int prewarm = 0, int maxSize = 0)
            : base(
                create: () =>
                {
                    var instance = resolver.Instantiate(prefab, parent);
                    instance.gameObject.SetActive(false);
                    return instance;
                },
                onRent: item => item.gameObject.SetActive(true),
                onReturn: item =>
                {
                    if (item == null) return;
                    item.gameObject.SetActive(false);
                    if (parent != null) item.transform.SetParent(parent, false);
                },
                onDestroy: item => { if (item != null) Object.Destroy(item.gameObject); },
                prewarm: prewarm, maxSize: maxSize)
        { }
    }
}
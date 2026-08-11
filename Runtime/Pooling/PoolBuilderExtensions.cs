using System;
using UnityEngine;

namespace AceLand.Injection
{
    public static class PoolBuilderExtensions
    {
        /// <summary>Registers IObjectPool&lt;T&gt; (singleton, disposed with the scope).</summary>
        public static IRegistrationBuilder RegisterPool<T>(this IContainerBuilder b,
            Func<IObjectResolver, T> factory, Action<T> onRent = null, Action<T> onReturn = null,
            Action<T> onDestroy = null, int prewarm = 0, int maxSize = 0)
        {
            return b.RegisterFactory(typeof(IObjectPool<T>),
                r => new ObjectPool<T>(() => factory(r), onRent, onReturn, onDestroy, prewarm, maxSize),
                Lifetime.Singleton);
        }

        /// <summary>Registers IObjectPool&lt;T&gt; for a prefab; instances are DI-injected before Awake.</summary>
        public static IRegistrationBuilder RegisterPrefabPool<T>(this IContainerBuilder b, T prefab,
            Transform parent = null, int prewarm = 0, int maxSize = 0) where T : Component
        {
            var ctx = parent ?? (b as ContainerBuilder)?.ContextTransform;
            return b.RegisterFactory(typeof(IObjectPool<T>),
                r => new ComponentPool<T>(r, prefab, ctx, prewarm, maxSize),
                Lifetime.Singleton);
        }

        /// <summary>Pools instances of a registered/constructible type T.</summary>
        public static IRegistrationBuilder RegisterPool<T>(this IContainerBuilder b, int prewarm = 0,
                                                           int maxSize = 0) where T : class
            => b.RegisterPool(r => r.CanResolve(typeof(T)) ? r.Resolve<T>() : r.CreateInstance<T>(),
                              prewarm: prewarm, maxSize: maxSize);
    }
}
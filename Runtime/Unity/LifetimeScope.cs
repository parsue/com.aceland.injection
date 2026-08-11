using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AceLand.Injection
{
    public enum InjectionTarget { None, Children, Scene }

    [DefaultExecutionOrder(-5000)]
    [DisallowMultipleComponent]
    [AddComponentMenu("AceLand/Injection/Lifetime Scope")]
    public class LifetimeScope : MonoBehaviour
    {
        [SerializeField] LifetimeScope parentScope;
        [SerializeField] List<MonoBehaviour> installers = new List<MonoBehaviour>();
        [SerializeField] List<ScriptableObject> assetInstallers = new List<ScriptableObject>();
        [SerializeField] InjectionTarget injectionTarget = InjectionTarget.Scene;
        [SerializeField] bool dontDestroyOnLoad;

        public IObjectResolver Resolver { get; private set; }
        public bool IsBuilt => Resolver != null;

        EntryPointRunner _runner;
        /// <summary>Completes when this scope's IAsyncStartable entry points are done.</summary>
        public Task StartupTask => _runner != null ? _runner.StartupTask : Task.CompletedTask;

        protected virtual void Awake()
        {
            if (dontDestroyOnLoad) { transform.SetParent(null, true); DontDestroyOnLoad(gameObject); }
            if (Resolver == null) Build();
        }

        protected virtual void OnDestroy()
        {
            Resolver?.Dispose();
            Resolver = null;
        }

        public IObjectResolver Build()
        {
            if (Resolver != null) return Resolver;

            var builder = CreateBuilder(ResolveParentResolver(), false);
            Resolver = builder.Build();

            if (builder.EntryPointTypes.Count > 0)
            {
                var instances = new List<object>(builder.EntryPointTypes.Count);
                foreach (var t in builder.EntryPointTypes) instances.Add(Resolver.Resolve(t));
                _runner = EntryPointRunner.Create(transform, instances, name);
            }

            PerformInjection();
            return Resolver;
        }

        /// <summary>Editor/validation: build the container only — no entry points, no injection, no side effects.</summary>
        public IObjectResolver BuildContainerOnly(IObjectResolver parentOverride = null)
            => CreateBuilder(parentOverride ?? ResolveParentResolver(), true).Build();

        ContainerBuilder CreateBuilder(IObjectResolver parent, bool validationOnly)
        {
            var builder = new ContainerBuilder(parent)
            {
                ContextScene = gameObject.scene,
                ContextTransform = transform,
                SkipEntryPointActivation = validationOnly
            };
            builder.RegisterInstance(this);
            foreach (var mi in installers) (mi as IInstaller)?.Install(builder);
            foreach (var si in assetInstallers) (si as IInstaller)?.Install(builder);
            Configure(builder);
            return builder;
        }

        /// <summary>Override to register bindings in code.</summary>
        protected virtual void Configure(IContainerBuilder builder) { }

        IObjectResolver ResolveParentResolver()
        {
            if (parentScope != null) return parentScope.IsBuilt ? parentScope.Resolver : parentScope.Build();
            for (var t = transform.parent; t != null; t = t.parent)
            {
                var s = t.GetComponent<LifetimeScope>();
                if (s != null) return s.IsBuilt ? s.Resolver : s.Build();
            }
            return DI.Global;
        }

        void PerformInjection()
        {
            switch (injectionTarget)
            {
                case InjectionTarget.None: return;
                case InjectionTarget.Children: InjectHierarchy(gameObject); return;
                case InjectionTarget.Scene:
                {
                    var scene = gameObject.scene;
                    if (!scene.IsValid()) { InjectHierarchy(gameObject); return; }
                    foreach (var root in scene.GetRootGameObjects()) InjectHierarchy(root);
                    return;
                }
            }
        }

        void InjectHierarchy(GameObject root)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || mb is LifetimeScope) continue;
                if (NearestScope(mb.transform) != this) continue;
                try { Resolver.Inject(mb); }
                catch (Exception e)
                { Debug.LogError($"[Injection] failed on '{mb.GetType().Name}': {e.Message}", mb); }
            }
        }

        internal static LifetimeScope NearestScope(Transform t)
        {
            for (var c = t; c != null; c = c.parent)
            {
                var s = c.GetComponent<LifetimeScope>();
                if (s != null && s.IsBuilt) return s;
            }
            return FindSceneRootScope(t.gameObject.scene);
        }

        static LifetimeScope FindSceneRootScope(Scene scene)
        {
            if (!scene.IsValid()) return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var s = root.GetComponent<LifetimeScope>();
                if (s != null && s.IsBuilt) return s;
            }
            return null;
        }

        public static IObjectResolver ResolverFor(GameObject go)
            => (go != null ? NearestScope(go.transform)?.Resolver : null) ?? DI.Global;
    }
}
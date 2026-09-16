using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AceLand.Injection
{
    public enum InjectionTarget { None, Children, Scene }

    [DefaultExecutionOrder(-5000)]
    [DisallowMultipleComponent]
    [AddComponentMenu("AceLand/Injection/Injection Scope")]
    public class InjectionScope : MonoBehaviour
    {
        [SerializeField] private InjectionScope parentScope;
        [SerializeField] private List<MonoBehaviour> installers = new();
        [SerializeField] private List<ScriptableObject> assetInstallers = new();
        [SerializeField] private InjectionTarget injectionTarget = InjectionTarget.Scene;
        [SerializeField] private bool dontDestroyOnLoad;
        [Tooltip("When no scope is found in parents, fall back to the persistent (DontDestroyOnLoad) scope.")]
        [SerializeField]
        private bool autoParentToPersistentScope = true;

        public IResolver Resolver { get; private set; }
        public bool IsBuilt => Resolver != null;

        private EntryPointRunner _runner;
        private Scene _originScene;
        private static InjectionScope _persistent;
        static readonly Dictionary<Type, bool> ConfigureOverrides = new();
        
        /// <summary>True when a subclass actually implements Configure.</summary>
        bool OverridesConfigure()
        {
            var type = GetType();
            if (ConfigureOverrides.TryGetValue(type, out var cached)) return cached;

            var method = type.GetMethod(nameof(Configure),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            var overridden = method != null && method.DeclaringType != typeof(InjectionScope);
            ConfigureOverrides[type] = overridden;
            return overridden;
        }

        private Scene TargetScene => _originScene.IsValid() ? _originScene : gameObject.scene;
        
        /// <summary>Inspector setting, exposed for tooling.</summary>
        public InjectionTarget InjectionTargetMode => injectionTarget;

        /// <summary>True when this scope survives scene loads.</summary>
        public bool IsPersistent => dontDestroyOnLoad;

        /// <summary>The DontDestroyOnLoad scope, if one exists.</summary>
        public static InjectionScope Persistent => _persistent ? _persistent : null;
        
        /// <summary>Completes when this scope's IAsyncEntryPoint entry points are done.</summary>
        public Task StartupTask => _runner ? _runner.StartupTask : Task.CompletedTask;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _persistent = null;    // domain-reload-off safety

        protected virtual void Awake()
        {
            _originScene = gameObject.scene;
            
            if (dontDestroyOnLoad)
            {
                transform.SetParent(null, true);
                DontDestroyOnLoad(gameObject);

                if (_persistent && _persistent != this)
                    Debug.LogWarning($"[Injection] '{name}' replaces '{_persistent.name}' as the " +
                                     "persistent scope. Only one DontDestroyOnLoad scope is supported.", this);
                _persistent = this;
            }
            
            if (Resolver == null)
                Build();
        }

        protected virtual void OnDestroy()
        {
            if (_persistent == this) _persistent = null;
            Resolver?.Dispose();
            Resolver = null;
        }

        private IResolver Build()
        {
            if (Resolver != null) return Resolver;

            var builder = CreateBuilder(ResolveParentResolver(), false);
            Resolver = builder.Build();
            if (Resolver is Container c) c.Label = $"{name} ({TargetScene.name})";
    
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
        public IResolver BuildContainerOnly(IResolver parentOverride = null)
            => CreateBuilder(parentOverride ?? ResolveParentResolver(), true).Build();

        ContainerBuilder CreateBuilder(IResolver parent, bool validationOnly)
        {
            var builder = new ContainerBuilder(parent)
            {
                ContextScene = TargetScene,
                ContextTransform = transform,
                SkipEntryPointActivation = validationOnly
            };

            builder.RegisterInstance(this);

            foreach (var mi in installers)
            {
                if (mi is not IInstaller installer) continue;
                using (builder.Source(mi)) installer.Install(builder);
            }

            foreach (var si in assetInstallers)
            {
                if (si is not IInstaller installer) continue;
                using (builder.Source(si)) installer.Install(builder);
            }

            // only attribute Configure when a subclass implements it — the base is an empty hook
            if (OverridesConfigure())
            {
                using (builder.Source(this, $"{GetType().Name}.Configure"))
                    Configure(builder);
            }
            else
            {
                Configure(builder);
            }

            return builder;
        }

        /// <summary>Override to register bindings in code.</summary>
        protected virtual void Configure(IContainerBuilder builder) { }

        private IResolver ResolveParentResolver()
        {
            if (parentScope)
                return parentScope.IsBuilt ? parentScope.Resolver : parentScope.Build();

            for (var t = transform.parent; t; t = t.parent)
            {
                var s = t.GetComponent<InjectionScope>();
                if (s) return s.IsBuilt ? s.Resolver : s.Build();
            }

            // ← cross-scene fallback
            if (autoParentToPersistentScope && _persistent && _persistent != this)
                return _persistent.IsBuilt ? _persistent.Resolver : _persistent.Build();

            return DI.Global;
        }

        private void PerformInjection()
        {
            switch (injectionTarget)
            {
                case InjectionTarget.None: return;
                case InjectionTarget.Children: InjectHierarchy(gameObject); return;
                case InjectionTarget.Scene:
                {
                    var scene = TargetScene;                         // ← was gameObject.scene
                    if (!scene.IsValid()) { InjectHierarchy(gameObject); return; }
                    foreach (var root in scene.GetRootGameObjects()) InjectHierarchy(root);
                    return;
                }
            }
        }

        private void InjectHierarchy(GameObject root)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!mb || mb is InjectionScope) continue;
                if (NearestScope(mb.transform) != this) continue;
                try { Resolver.Inject(mb); }
                catch (Exception e)
                { Debug.LogError($"[Injection] failed on '{mb.GetType().Name}': {e.Message}", mb); }
            }
        }

        private static InjectionScope NearestScope(Transform t)
        {
            for (var c = t; c; c = c.parent)
            {
                var s = c.GetComponent<InjectionScope>();
                if (s && s.IsBuilt) return s;
            }
            return FindSceneRootScope(t.gameObject.scene);
        }

        private static InjectionScope FindSceneRootScope(Scene scene)
        {
            if (!scene.IsValid()) return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var s = root.GetComponent<InjectionScope>();
                if (s && s.IsBuilt) return s;
            }
            return null;
        }

        /// <summary>
        /// Scope that would inject this object, or null if none would.
        /// Unlike ResolverFor, this never triggers DI.Global's lazy build.
        /// </summary>
        public static InjectionScope OwningScopeOf(GameObject go)
            => go ? NearestScope(go.transform) : null;

        /// <summary>
        /// The scope that will ACTUALLY inject this object at runtime, honouring each scope's
        /// <see cref="InjectionTarget"/> mode — or null when nothing injects it.
        /// <para>
        /// Unlike <see cref="OwningScopeOf"/> (which only reports the nearest scope), this answers
        /// the real question: the nearest built scope in the parent chain injects its own subtree
        /// (Children or Scene), unless its mode is <see cref="InjectionTarget.None"/>. When there is
        /// no scope in the parents, only a scene-root scope set to <see cref="InjectionTarget.Scene"/>
        /// reaches the object; a Children-mode root does not.
        /// </para>
        /// DI.Global never auto-injects scene MonoBehaviours, so a null result means the object's
        /// <c>[Inject]</c> members stay unassigned at runtime.
        /// </summary>
        public static InjectionScope InjectingScopeOf(GameObject go)
        {
            if (!go) return null;

            // Nearest built scope in the parent chain always covers its own subtree.
            for (var t = go.transform; t; t = t.parent)
            {
                var s = t.GetComponent<InjectionScope>();
                if (s && s.IsBuilt)
                    return s.injectionTarget == InjectionTarget.None ? null : s;
            }

            // No scope ancestor: only a Scene-mode root scope reaches this object.
            var root = FindSceneRootScope(go.scene);
            return root && root.injectionTarget == InjectionTarget.Scene ? root : null;
        }
    }
}
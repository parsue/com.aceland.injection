// DI.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace AceLand.Injection
{
    /// <summary>Process-wide container. Packages publish services here; anything can consume them.</summary>
    // ReSharper disable once InconsistentNaming
    public static class DI
    {
        private static Container _global;
        private static EntryPointRunner _runner;
        private static readonly List<(int order, Action<IContainerBuilder> configure)> pendingList = new();

        public static bool IsGlobalBuilt => _global is { IsDisposed: false };

        public static IObjectResolver Global
        {
            get
            {
                if (_global == null || _global.IsDisposed) BuildGlobal();
                return _global;
            }
        }

        /// <summary>Async startup of global entry points.</summary>
        public static Task StartupTask => _runner != null ? _runner.StartupTask : Task.CompletedTask;

        public static void ConfigureGlobal(Action<IContainerBuilder> configure, int order = 0)
        {
            if (configure == null) return;
            if (IsGlobalBuilt)
                throw new InjectionException(
                    "Global container already built. Configure earlier (RuntimeInitializeLoadType." +
                    "AfterAssembliesLoaded), use an IGlobalInstaller, or DI.Global.CreateScope(...).");
            pendingList.Add((order, configure));
        }

        public static T Resolve<T>(object id = null) => Global.Resolve<T>(id);
        public static bool TryResolve<T>(out T i, object id = null) => Global.TryResolve(out i, id);
        public static void Inject(object target) => Global.Inject(target);
        public static T CreateInstance<T>(params object[] extraArgs) => Global.CreateInstance<T>(extraArgs);
        public static IObjectResolver CreateScope(Action<IContainerBuilder> configure = null)
            => Global.CreateScope(configure);

        public static void DisposeGlobal()
        {
            if (_runner != null) UnityEngine.Object.Destroy(_runner.gameObject);
            _runner = null;
            _global?.Dispose();
            _global = null;
        }

        // ------------------------------------------------------------------

        private static ContainerBuilder CreateGlobalBuilder(bool validationOnly)
        {
            var builder = new ContainerBuilder { SkipEntryPointActivation = validationOnly };
            foreach (var installer in GlobalInstallerScanner.Discover())
            {
                try { installer.Install(builder); }
                catch (Exception e)
                { Debug.LogError($"[Injection] Global installer '{installer.GetType().Name}' failed: {e}"); }
            }
            var pending = new List<(int, Action<IContainerBuilder>)>(pendingList);
            pending.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            foreach (var (_, cfg) in pending) cfg(builder);
            return builder;
        }

        private static void BuildGlobal()
        {
            var builder = CreateGlobalBuilder(false);
            pendingList.Clear();
            _global = (Container)builder.Build();
            _global.Label = "DI.Global";

            if (Application.isPlaying && builder.EntryPointTypes.Count > 0)
            {
                var instances = new List<object>(builder.EntryPointTypes.Count);
                foreach (var t in builder.EntryPointTypes) instances.Add(_global.Resolve(t));
                _runner = EntryPointRunner.Create(null, instances, "Global");
                UnityEngine.Object.DontDestroyOnLoad(_runner.gameObject);
            }
        }

        /// <summary>Editor-only: a throwaway global container for validation. Caller disposes it.</summary>
        public static IObjectResolver CreateValidationContainer() => CreateGlobalBuilder(true).Build();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()             // domain-reload-off safety
        {
            _global?.Dispose();
            _global = null;
            _runner = null;
            pendingList.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void InstallBridge() => InjectionBridge.SetGlobalProvider(() => _global);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (!IsGlobalBuilt) BuildGlobal();
            Application.quitting -= DisposeGlobal;
            Application.quitting += DisposeGlobal;
        }
    }
}
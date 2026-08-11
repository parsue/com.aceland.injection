using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AceLand.Injection
{
    [DefaultExecutionOrder(-4000)]
    public sealed class EntryPointRunner : MonoBehaviour
    {
        readonly List<IInitializable> _init = new List<IInitializable>();
        readonly List<IAsyncStartable> _async = new List<IAsyncStartable>();
        readonly List<ITickable> _tick = new List<ITickable>();
        readonly List<IFixedTickable> _fixed = new List<IFixedTickable>();
        readonly List<ILateTickable> _late = new List<ILateTickable>();

        CancellationTokenSource _cts;
        bool _started;

        /// <summary>Completes when every IAsyncStartable of this scope finished (or faulted).</summary>
        public Task StartupTask { get; private set; } = Task.CompletedTask;

        public CancellationToken CancellationToken => _cts?.Token ?? CancellationToken.None;

        internal static EntryPointRunner Create(Transform parent, IEnumerable<object> entryPoints, string label)
        {
            var go = new GameObject($"[Injection EntryPoints{(label != null ? " - " + label : "")}]");
            if (parent != null) go.transform.SetParent(parent, false);
            var runner = go.AddComponent<EntryPointRunner>();
            foreach (var ep in Order(entryPoints)) runner.Add(ep);
            return runner;
        }

        static IEnumerable<object> Order(IEnumerable<object> src)
        {
            var list = new List<object>(src);
            list.Sort((a, b) =>
                ((a as IOrderedEntryPoint)?.Order ?? 0).CompareTo((b as IOrderedEntryPoint)?.Order ?? 0));
            return list;
        }

        public void Add(object ep)
        {
            if (ep is IInitializable i) _init.Add(i);
            if (ep is IAsyncStartable a) _async.Add(a);
            if (ep is ITickable t) _tick.Add(t);
            if (ep is IFixedTickable f) _fixed.Add(f);
            if (ep is ILateTickable l) _late.Add(l);
        }

        void Start()
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();

            for (int i = 0; i < _init.Count; i++) { var x = _init[i]; Safe(x.Initialize); }
            if (_async.Count > 0) StartupTask = RunAsyncStartablesAsync(_cts.Token);
        }

        async Task RunAsyncStartablesAsync(CancellationToken ct)
        {
            // sequential: deterministic ordering; use Task.WhenAll below if you prefer parallel
            for (int i = 0; i < _async.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                var startable = _async[i];
                try
                {
                    var task = startable.StartAsync(ct);
                    if (task != null) await task;
                }
                catch (OperationCanceledException) { /* scope disposed */ }
                catch (Exception e)
                {
                    Debug.LogError($"[Injection] {startable.GetType().Name}.StartAsync failed:\n{e}");
                }
            }
        }

        void Update()      { for (int i = 0; i < _tick.Count; i++)  { var x = _tick[i];  Safe(x.Tick); } }
        void FixedUpdate() { for (int i = 0; i < _fixed.Count; i++) { var x = _fixed[i]; Safe(x.FixedTick); } }
        void LateUpdate()  { for (int i = 0; i < _late.Count; i++)  { var x = _late[i];  Safe(x.LateTick); } }

        void OnDestroy()
        {
            try { _cts?.Cancel(); } catch { /* ignored */ }
            _cts?.Dispose();
            _cts = null;
        }

        static void Safe(Action a) { try { a(); } catch (Exception e) { Debug.LogException(e); } }
    }
}
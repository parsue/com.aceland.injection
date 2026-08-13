using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AceLand.Injection
{
    public sealed class Container : IObjectResolver, IAsyncDisposable, IContainerIntrospection
    {
        private readonly Container _parent;

        private readonly Dictionary<RegistrationKey, List<Registration>> _registry = new();
        private readonly Dictionary<Registration, object> _instances = new();
        private readonly List<object> _disposables = new();      // IDisposable / IAsyncDisposable
        private readonly List<Container> _children = new();
        private readonly List<IExternalResolver> _fallbacks;
        private readonly object _sync = new();

        [ThreadStatic] private static Stack<Type> _resolveStack;

        public bool IsDisposed { get; private set; }
        internal Container Parent => _parent;
        internal IReadOnlyList<Type> EntryPointTypes { get; }

        internal Container(ContainerBuilder builder, Container parent)
        {
            _parent = parent;
            _fallbacks = builder.Fallbacks;
            EntryPointTypes = builder.EntryPointTypes;
            parent?._children.Add(this);

            var self = new Registration
            {
                ImplementationType = typeof(Container),
                Lifetime = Lifetime.Singleton,
                Instance = this,
                OwnsInstance = false,
                Owner = this
            };
            self.ContractTypes.Add(typeof(IObjectResolver));
            self.ContractTypes.Add(typeof(Container));
            AddToRegistry(self);
            _instances[self] = this;

            foreach (var reg in builder.Registrations)
            {
                if (reg.ContractTypes.Count == 0 && reg.ImplementationType != null)
                    reg.ContractTypes.Add(reg.ImplementationType);
                reg.Owner = this;
                AddToRegistry(reg);
            }

            foreach (var cb in builder.BuildCallbacks) cb(this);

            if (!builder.SkipEntryPointActivation)
                foreach (var t in builder.EntryPointTypes) Resolve(t);
        }

        private void AddToRegistry(Registration reg)
        {
            foreach (var contract in reg.ContractTypes)
            {
                var key = new RegistrationKey(contract, reg.Id);
                if (!_registry.TryGetValue(key, out var list)) _registry[key] = list = new List<Registration>(1);
                list.Add(reg);
            }
        }

        // ---------------------------------------------------------------- resolve

        public T Resolve<T>(object id = null) => (T)Resolve(typeof(T), id);

        public object Resolve(Type contract, object id = null)
        {
            if (TryResolve(contract, out var instance, id)) return instance;
            throw new InjectionException($"No registration for {contract.FullName}" +
                                         (id != null ? $" (id: {id})" : "") + "." + DescribeChain());
        }

        public bool TryResolve<T>(out T instance, object id = null)
        {
            if (TryResolve(typeof(T), out var o, id)) { instance = (T)o; return true; }
            instance = default; return false;
        }

        public bool TryResolve(Type contract, out object instance, object id = null)
        {
            ThrowIfDisposed();

            var reg = FindRegistration(contract, id);
            if (reg != null) { instance = GetOrCreate(reg, this); return true; }

            if (TryResolveCollection(contract, id, out instance)) return true;

            if (contract.IsGenericType)
            {
                var def = contract.GetGenericTypeDefinition();
                if (def == typeof(Func<>) || def == typeof(Lazy<>))
                {
                    var t = contract.GetGenericArguments()[0];
                    var name = def == typeof(Func<>) ? nameof(MakeFactory) : nameof(MakeLazy);
                    instance = typeof(Container)
                        .GetMethod(name, System.Reflection.BindingFlags.NonPublic |
                                         System.Reflection.BindingFlags.Instance)!
                        .MakeGenericMethod(t).Invoke(this, null);
                    return true;
                }
            }

            for (var c = this; c != null; c = c._parent)
                foreach (var f in c._fallbacks)
                    if (f.TryResolve(contract, id, out instance)) return true;

            instance = null;
            return false;
        }

        public bool CanResolve(Type contract, object id = null)
        {
            if (IsDisposed) return false;
            if (FindRegistration(contract, id) != null) return true;
            if (contract.IsGenericType)
            {
                var def = contract.GetGenericTypeDefinition();
                if (def == typeof(Func<>) || def == typeof(Lazy<>))
                    return CanResolve(contract.GetGenericArguments()[0], id);
            }
            var element = ElementTypeOf(contract);
            if (element != null && FindRegistration(element, id) != null) return true;
            for (var c = this; c != null; c = c._parent)
                foreach (var f in c._fallbacks)
                    if (f.TryResolve(contract, id, out _)) return true;
            return false;
        }

        private Func<T> MakeFactory<T>() => () => Resolve<T>();
        private Lazy<T> MakeLazy<T>() => new(() => Resolve<T>());

        private Registration FindRegistration(Type contract, object id)
        {
            var key = new RegistrationKey(contract, id);
            for (var c = this; c != null; c = c._parent)
                if (c._registry.TryGetValue(key, out var list) && list.Count > 0)
                    return list[^1];      // last wins
            return null;
        }

        private static Type ElementTypeOf(Type contract)
        {
            if (contract.IsArray) return contract.GetElementType();
            if (!contract.IsGenericType) return null;
            var def = contract.GetGenericTypeDefinition();
            return def == typeof(IEnumerable<>) || def == typeof(IList<>) || def == typeof(List<>) ||
                   def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>) ||
                   def == typeof(IReadOnlyCollection<>)
                ? contract.GetGenericArguments()[0] : null;
        }

        private bool TryResolveCollection(Type contract, object id, out object result)
        {
            var element = ElementTypeOf(contract);
            if (element == null) { result = null; return false; }

            var key = new RegistrationKey(element, id);
            var all = new List<Registration>();
            for (var c = this; c != null; c = c._parent)
                if (c._registry.TryGetValue(key, out var list)) all.AddRange(list);

            if (all.Count == 0) { result = null; return false; }

            var list2 = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
            foreach (var t in all)
                list2.Add(GetOrCreate(t, this));

            if (contract.IsArray)
            {
                var arr = Array.CreateInstance(element, list2.Count);
                list2.CopyTo(arr, 0);
                result = arr;
            }
            else result = list2;
            return true;
        }

        private object GetOrCreate(Registration reg, Container requestScope)
        {
            switch (reg.Lifetime)
            {
                case Lifetime.Singleton:
                {
                    var owner = reg.Owner ?? this;
                    lock (owner._sync)
                    {
                        if (owner._instances.TryGetValue(reg, out var existing)) return existing;
                        var created = Create(reg, owner);
                        owner._instances[reg] = created;
                        owner.Track(reg, created);
                        return created;
                    }
                }
                case Lifetime.Scoped:
                {
                    lock (requestScope._sync)
                    {
                        if (requestScope._instances.TryGetValue(reg, out var existing)) return existing;
                        var created = Create(reg, requestScope);
                        requestScope._instances[reg] = created;
                        requestScope.Track(reg, created);
                        return created;
                    }
                }
                default:
                {
                    var created = Create(reg, requestScope);
                    requestScope.Track(reg, created);
                    return created;
                }
            }
        }

        private void Track(Registration reg, object instance)
        {
            if (!reg.OwnsInstance || ReferenceEquals(instance, this)) return;
            if (instance is IDisposable || instance is IAsyncDisposable)
                lock (_sync) _disposables.Add(instance);
        }

        private object Create(Registration reg, Container scope)
        {
            if (reg.Instance != null)
            {
                Injector.InjectInto(reg.Instance, scope, reg);
                reg.OnActivated?.Invoke(scope, reg.Instance);
                return reg.Instance;
            }

            _resolveStack ??= new Stack<Type>();
            var type = reg.ImplementationType;

            if (_resolveStack.Contains(type))
                throw new InjectionException("Circular dependency: " +
                    string.Join(" -> ", _resolveStack.Reverse().Select(t => t.Name)) + " -> " + type.Name);

            _resolveStack.Push(type);
            try
            {
                object instance;
                if (reg.Factory != null)
                {
                    instance = reg.Factory(scope) ??
                               throw new InjectionException($"Factory for {type.Name} returned null.");
                    Injector.InjectInto(instance, scope, reg);
                }
                else
                {
                    if (typeof(Component).IsAssignableFrom(type))
                        throw new InjectionException(
                            $"{type.Name} is a Component and cannot be created with 'new'. Use " +
                            "RegisterComponent / RegisterComponentInHierarchy / RegisterComponentInNewPrefab.");
                    if (typeof(ScriptableObject).IsAssignableFrom(type))
                        throw new InjectionException(
                            $"{type.Name} is a ScriptableObject. Use RegisterScriptableObject(asset) or " +
                            "RegisterFactory(_ => ScriptableObject.CreateInstance<T>()).");

                    instance = Injector.CreateInstance(type, scope, reg, null);
                }

                reg.OnActivated?.Invoke(scope, instance);
                return instance;
            }
            finally { _resolveStack.Pop(); }
        }

        // ---------------------------------------------------------------- injection

        public void Inject(object instance)
        {
            ThrowIfDisposed();
            if (instance == null) return;
            Injector.InjectInto(instance, this, null);
        }

        public object CreateInstance(Type type, params object[] extraArgs)
        {
            ThrowIfDisposed();
            return Injector.CreateInstance(type, this, null, extraArgs);
        }

        public T CreateInstance<T>(params object[] extraArgs) => (T)CreateInstance(typeof(T), extraArgs);

        // ---------------------------------------------------------------- scopes / lifetime

        public IObjectResolver CreateScope(Action<IContainerBuilder> configure = null)
        {
            ThrowIfDisposed();
            var builder = new ContainerBuilder { ParentContainer = this };
            configure?.Invoke(builder);
            return builder.Build();
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            for (int i = _children.Count - 1; i >= 0; i--) _children[i].Dispose();
            _children.Clear();

            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                var d = _disposables[i];
                try
                {
                    if (d is IDisposable sync) sync.Dispose();
                    else if (d is IAsyncDisposable a) FireAndForget(a.DisposeAsync());
                }
                catch (Exception e) { Debug.LogException(e); }
            }
            Cleanup();
        }

        /// <summary>Awaitable disposal — use when your services implement IAsyncDisposable.</summary>
        public async ValueTask DisposeAsync()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            for (int i = _children.Count - 1; i >= 0; i--) await _children[i].DisposeAsync();
            _children.Clear();

            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                var d = _disposables[i];
                try
                {
                    if (d is IAsyncDisposable a) await a.DisposeAsync();
                    else if (d is IDisposable sync) sync.Dispose();
                }
                catch (Exception e) { Debug.LogException(e); }
            }
            Cleanup();
        }

        private void Cleanup()
        {
            _disposables.Clear();
            _instances.Clear();
            _registry.Clear();
            _parent?._children.Remove(this);
        }

        private static async void FireAndForget(ValueTask task)
        {
            try { await task; } catch (Exception e) { Debug.LogException(e); }
        }

        private void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(Container));
        }

        internal IEnumerable<Type> RegisteredContracts()
        {
            for (var c = this; c != null; c = c._parent)
                foreach (var k in c._registry.Keys) yield return k.Type;
        }

        private string DescribeChain()
        {
            var sb = new StringBuilder().AppendLine();
            var depth = 0;
            for (var c = this; c != null; c = c._parent, depth++)
            {
                sb.AppendLine($"[scope {depth}] {c._registry.Count} contracts:");
                foreach (var kv in c._registry.Take(40))
                    sb.AppendLine($"   - {kv.Key.Type.Name}{(kv.Key.Id != null ? $"#{kv.Key.Id}" : "")}");
            }
            return sb.ToString();
        }

    public string Label { get; set; } = "Container";

    public int Depth
    {
        get { int d = 0; for (var c = _parent; c != null; c = c._parent) d++; return d; }
    }

    IObjectResolver IContainerIntrospection.ParentResolver => _parent;

    public IReadOnlyList<RegistrationInfo> LocalRegistrations
    {
        get
        {
            var seen = new HashSet<Registration>();
            var list = new List<RegistrationInfo>();

            lock (_sync)
            {
                list.AddRange(
                    from pair in _registry
                    from reg in pair.Value
                    where seen.Add(reg)
                    select Describe(reg)
                );
            }
            
            list.Sort((a, b) =>
            {
                var byName = string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
                return byName != 0
                    ? byName
                    : a.Serial.CompareTo(b.Serial); // declaration order — deterministic per build
            });
            return list;
        }
    }

    public bool TryDescribeResolution(Type contract, object id,
                                      out RegistrationInfo info, out IObjectResolver owner)
    {
        for (var c = this; c != null; c = c._parent)
        {
            if (!c._registry.TryGetValue(new RegistrationKey(contract, id), out var list) || list.Count == 0)
                continue;

            var reg = list[^1];          // last wins, same as resolution
            info = c.Describe(reg);
            owner = c;
            return true;
        }
        info = default;
        owner = null;
        return false;
    }

    private RegistrationInfo Describe(Registration reg)
    {
        var kind = ReferenceEquals(reg.Instance, this) ? RegistrationKind.Container
                 : reg.Instance != null ? RegistrationKind.Instance
                 : reg.Factory != null ? RegistrationKind.Factory
                 : RegistrationKind.Type;

        return new RegistrationInfo(
            reg.Serial,
            reg.ContractTypes.ToArray(),
            reg.ImplementationType,
            reg.Lifetime,
            reg.Id,
            kind,
            reg.Instance != null || _instances.ContainsKey(reg));
    }
    }
}
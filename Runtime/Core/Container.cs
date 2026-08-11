using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AceLand.Injection
{
    public sealed class Container : IObjectResolver, IAsyncDisposable
    {
        readonly Container _parent;
        readonly Dictionary<RegistrationKey, List<Registration>> _registry =
            new Dictionary<RegistrationKey, List<Registration>>();
        readonly Dictionary<Registration, object> _instances = new Dictionary<Registration, object>();
        readonly List<object> _disposables = new List<object>();      // IDisposable / IAsyncDisposable
        readonly List<Container> _children = new List<Container>();
        readonly List<IExternalResolver> _fallbacks;
        readonly object _sync = new object();

        [ThreadStatic] static Stack<Type> _resolveStack;

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

        void AddToRegistry(Registration reg)
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
                                         System.Reflection.BindingFlags.Instance)
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

        Func<T> MakeFactory<T>() => () => Resolve<T>();
        Lazy<T> MakeLazy<T>() => new Lazy<T>(() => Resolve<T>());

        Registration FindRegistration(Type contract, object id)
        {
            var key = new RegistrationKey(contract, id);
            for (var c = this; c != null; c = c._parent)
                if (c._registry.TryGetValue(key, out var list) && list.Count > 0)
                    return list[list.Count - 1];      // last wins
            return null;
        }

        static Type ElementTypeOf(Type contract)
        {
            if (contract.IsArray) return contract.GetElementType();
            if (!contract.IsGenericType) return null;
            var def = contract.GetGenericTypeDefinition();
            return def == typeof(IEnumerable<>) || def == typeof(IList<>) || def == typeof(List<>) ||
                   def == typeof(IReadOnlyList<>) || def == typeof(ICollection<>) ||
                   def == typeof(IReadOnlyCollection<>)
                ? contract.GetGenericArguments()[0] : null;
        }

        bool TryResolveCollection(Type contract, object id, out object result)
        {
            var element = ElementTypeOf(contract);
            if (element == null) { result = null; return false; }

            var key = new RegistrationKey(element, id);
            var all = new List<Registration>();
            for (var c = this; c != null; c = c._parent)
                if (c._registry.TryGetValue(key, out var list)) all.AddRange(list);

            if (all.Count == 0) { result = null; return false; }

            var list2 = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
            for (int i = 0; i < all.Count; i++) list2.Add(GetOrCreate(all[i], this));

            if (contract.IsArray)
            {
                var arr = Array.CreateInstance(element, list2.Count);
                list2.CopyTo(arr, 0);
                result = arr;
            }
            else result = list2;
            return true;
        }

        object GetOrCreate(Registration reg, Container requestScope)
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

        void Track(Registration reg, object instance)
        {
            if (!reg.OwnsInstance || ReferenceEquals(instance, this)) return;
            if (instance is IDisposable || instance is IAsyncDisposable)
                lock (_sync) _disposables.Add(instance);
        }

        object Create(Registration reg, Container scope)
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

        void Cleanup()
        {
            _disposables.Clear();
            _instances.Clear();
            _registry.Clear();
            _parent?._children.Remove(this);
        }

        static async void FireAndForget(ValueTask task)
        {
            try { await task; } catch (Exception e) { Debug.LogException(e); }
        }

        void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(Container));
        }

        internal IEnumerable<Type> RegisteredContracts()
        {
            for (var c = this; c != null; c = c._parent)
                foreach (var k in c._registry.Keys) yield return k.Type;
        }

        internal string DescribeChain()
        {
            var sb = new StringBuilder().AppendLine();
            int depth = 0;
            for (var c = this; c != null; c = c._parent, depth++)
            {
                sb.AppendLine($"[scope {depth}] {c._registry.Count} contracts:");
                foreach (var kv in c._registry.Take(40))
                    sb.AppendLine($"   - {kv.Key.Type.Name}{(kv.Key.Id != null ? $"#{kv.Key.Id}" : "")}");
            }
            return sb.ToString();
        }
    }
}
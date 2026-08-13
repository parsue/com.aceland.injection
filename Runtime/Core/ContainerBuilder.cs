using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AceLand.Injection
{
    public sealed class ContainerBuilder : IContainerBuilder
    {
        internal readonly List<Registration> Registrations = new();
        internal readonly List<Action<IObjectResolver>> BuildCallbacks = new();
        internal readonly List<Type> EntryPointTypes = new();
        internal readonly List<IExternalResolver> Fallbacks = new();

        internal Container ParentContainer;

        /// <summary>Editor validation builds containers without activating entry points.</summary>
        public bool SkipEntryPointActivation { get; set; }

        public Scene? ContextScene { get; set; }
        public Transform ContextTransform { get; set; }

        public ContainerBuilder() { }
        public ContainerBuilder(IObjectResolver parent) => ParentContainer = parent as Container;
        
        internal readonly List<InstallerInfo> Sources = new();
        private InstallerInfo? _currentSource;

        /// <summary>
        /// Attributes every registration made inside the scope to one installer.
        /// <code>using (builder.Source(installer)) installer.Install(builder);</code>
        /// </summary>
        public IDisposable Source(object installer, string label = null)
        {
            var type = installer?.GetType();
            var asset = installer as UnityEngine.Object;

            // asset name disambiguates four PlayerProfile assets
            var name = label
                       ?? (asset != null && !string.IsNullOrEmpty(asset.name)
                           ? $"{type?.Name} ({asset.name})"
                           : type?.Name)
                       ?? "unknown";

            var info = new InstallerInfo(name, type, asset, Sources.Count);
            Sources.Add(info);
            _currentSource = info;
            return new SourceScope(this);
        }

        private sealed class SourceScope : IDisposable
        {
            readonly ContainerBuilder _builder;
            public SourceScope(ContainerBuilder builder) => _builder = builder;
            public void Dispose() => _builder._currentSource = null;
        }

        public IRegistrationBuilder Register(Type implementationType, Lifetime lifetime)
        {
            if (implementationType == null) throw new ArgumentNullException(nameof(implementationType));
            var reg = new Registration
            {
                ImplementationType = implementationType,
                Lifetime = lifetime,
                Source = _currentSource
            };
            Registrations.Add(reg);
            return new RegistrationBuilder(reg);
        }

        public IRegistrationBuilder RegisterInstance(Type contractType, object instance, bool ownsInstance = false)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            var reg = new Registration
            {
                ImplementationType = instance.GetType(),
                Lifetime = Lifetime.Singleton,
                Instance = instance,
                OwnsInstance = ownsInstance,
                Source = _currentSource
            };
            reg.ContractTypes.Add(contractType ?? instance.GetType());
            Registrations.Add(reg);
            return new RegistrationBuilder(reg);
        }

        public IRegistrationBuilder RegisterFactory(Type contractType, Func<IObjectResolver, object> factory,
                                                    Lifetime lifetime)
        {
            var reg = new Registration
            {
                ImplementationType = contractType,
                Lifetime = lifetime,
                Factory = factory ?? throw new ArgumentNullException(nameof(factory)),
                Source = _currentSource
            };
            reg.ContractTypes.Add(contractType);
            Registrations.Add(reg);
            return new RegistrationBuilder(reg);
        }

        public void RegisterEntryPoint(Type type) => EntryPointTypes.Add(type);
        public void RegisterBuildCallback(Action<IObjectResolver> cb) => BuildCallbacks.Add(cb);
        public void AddFallbackResolver(IExternalResolver r) => Fallbacks.Add(r);

        public bool Contains(Type contract, object id = null, bool includeParent = true)
        {
            foreach (var r in Registrations)
                if (Equals(r.Id, id) && r.ContractTypes.Contains(contract)) return true;
            return includeParent && ParentContainer != null && ParentContainer.CanResolve(contract, id);
        }

        public IObjectResolver Build() => new Container(this, ParentContainer);
    }
}
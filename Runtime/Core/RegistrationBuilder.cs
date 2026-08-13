// RegistrationBuilder.cs
using System;
using System.Collections.Generic;

namespace AceLand.Injection
{
    internal sealed class RegistrationBuilder : IRegistrationBuilder
    {
        private readonly Registration _r;
        internal RegistrationBuilder(Registration r) => _r = r;

        public Type ImplementationType => _r.ImplementationType;

        public IRegistrationBuilder As(Type contract)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (_r.ImplementationType != null && !contract.IsAssignableFrom(_r.ImplementationType))
                throw new InjectionException($"{_r.ImplementationType.Name} is not assignable to {contract.Name}.");
            if (!_r.ContractTypes.Contains(contract)) _r.ContractTypes.Add(contract);
            return this;
        }

        public IRegistrationBuilder AsSelf() => As(_r.ImplementationType);

        public IRegistrationBuilder AsImplementedInterfaces()
        {
            foreach (var i in _r.ImplementationType.GetInterfaces())
            {
                if (i == typeof(IDisposable) || i == typeof(IAsyncDisposable)) continue;
                As(i);
            }
            return this;
        }

        public IRegistrationBuilder WithId(object id) { _r.Id = id; return this; }

        public IRegistrationBuilder UsingConstructor(params Type[] p)
        { _r.ConstructorSignature = p ?? Type.EmptyTypes; return this; }

        public IRegistrationBuilder WithParameter(string name, object value) => WithParameter(name, _ => value);
        public IRegistrationBuilder WithParameter(string name, Func<IObjectResolver, object> f)
        { Params().Add(new ParameterOverride { Name = name, Value = f }); return this; }
        public IRegistrationBuilder WithParameter(Type t, object value) => WithParameter(t, _ => value);
        public IRegistrationBuilder WithParameter(Type t, Func<IObjectResolver, object> f)
        { Params().Add(new ParameterOverride { Type = t, Value = f }); return this; }

        public IRegistrationBuilder InjectMember(string name, object value = null, bool optional = false)
            => InjectMember(name, value == null ? null : _ => value, optional);

        public IRegistrationBuilder InjectMember(string name, Func<IObjectResolver, object> f, bool optional = false)
        {
            (_r.Members ??= new List<MemberOverride>())
                .Add(new MemberOverride { Name = name, Value = f, Optional = optional });
            return this;
        }

        public IRegistrationBuilder InvokeMethod(string name, params object[] args)
        {
            (_r.Methods ??= new List<MethodOverride>()).Add(new MethodOverride { Name = name, ExplicitArgs = args });
            return this;
        }

        public IRegistrationBuilder IgnoreAttributes() { _r.IgnoreAttributes = true; return this; }
        public IRegistrationBuilder OnActivated(Action<IObjectResolver, object> cb) { _r.OnActivated += cb; return this; }

        private List<ParameterOverride> Params() => _r.Parameters ??= new List<ParameterOverride>();
    }
}
// Registration.cs
using System;
using System.Collections.Generic;
using System.Reflection;

namespace AceLand.Injection
{
    public sealed class Registration
    {
        public Type ImplementationType;
        public Lifetime Lifetime;
        public object Id;
        public readonly List<Type> ContractTypes = new List<Type>();
        public Func<IObjectResolver, object> Factory;
        public object Instance;
        public bool OwnsInstance = true;
        public Action<IObjectResolver, object> OnActivated;

        // explicit plan
        public bool IgnoreAttributes;
        public Type[] ConstructorSignature;
        public List<ParameterOverride> Parameters;
        public List<MemberOverride> Members;
        public List<MethodOverride> Methods;

        internal Container Owner;
        internal ConstructorInfo CachedConstructor;

        public bool HasExplicitPlan =>
            IgnoreAttributes || ConstructorSignature != null ||
            Parameters != null || Members != null || Methods != null;

        public override string ToString() =>
            $"{ImplementationType?.Name ?? "instance"} ({Lifetime}){(Id != null ? $" #{Id}" : "")}";
    }

    public sealed class ParameterOverride
    {
        public string Name; public Type Type; public Func<IObjectResolver, object> Value;
    }
    public sealed class MemberOverride
    {
        public string Name; public Func<IObjectResolver, object> Value; public bool Optional;
    }
    public sealed class MethodOverride
    {
        public string Name; public object[] ExplicitArgs;
    }

    public readonly struct RegistrationKey : IEquatable<RegistrationKey>
    {
        public readonly Type Type; public readonly object Id;
        public RegistrationKey(Type type, object id) { Type = type; Id = id; }
        public bool Equals(RegistrationKey o) => Type == o.Type && Equals(Id, o.Id);
        public override bool Equals(object o) => o is RegistrationKey k && Equals(k);
        public override int GetHashCode()
        { unchecked { return ((Type?.GetHashCode() ?? 0) * 397) ^ (Id?.GetHashCode() ?? 0); } }
    }
}
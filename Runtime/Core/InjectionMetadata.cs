using System;
using System.Collections.Generic;
using System.Reflection;

namespace AceLand.Injection
{
    /// <summary>Read-only description of a type's injection points (generated plan if any, else reflection).</summary>
    public static class InjectionMetadata
    {
        public static IReadOnlyList<InjectDependency> GetDependencies(Type type)
        {
            if (InjectorPlanRegistry.TryGet(type, out var plan)) return plan.Dependencies;

            var info = InjectTypeInfo.Get(type);
            var list = new List<InjectDependency>();

            if (info.AttributedConstructor != null)
                foreach (var p in info.AttributedConstructor.GetParameters())
                    list.Add(new InjectDependency(p.ParameterType, DependencyKind.Constructor, p.Name,
                        p.HasDefaultValue || (p.GetCustomAttribute<InjectAttribute>()?.Optional ?? false),
                        p.GetCustomAttribute<InjectAttribute>()?.Id));

            foreach (var m in info.Members)
            {
                if (m.Component != null)
                    list.Add(new InjectDependency(m.MemberType, DependencyKind.Component, m.Member.Name,
                        m.Component.Optional, null, m.Component.Source));
                else
                    list.Add(new InjectDependency(m.MemberType,
                        m.Member is FieldInfo ? DependencyKind.Field : DependencyKind.Property,
                        m.Member.Name, m.Inject.Optional, m.Inject.Id));
            }

            foreach (var mt in info.Methods)
                foreach (var p in mt.Parameters)
                    list.Add(new InjectDependency(p.ParameterType, DependencyKind.MethodParameter,
                        $"{mt.Method.Name}({p.Name})", mt.Inject.Optional || p.HasDefaultValue, mt.Inject.Id));

            return list;
        }

        public static bool HasAnyInjection(Type type) => GetDependencies(type).Count > 0;
    }
}
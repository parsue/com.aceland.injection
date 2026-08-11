using System;
using System.Reflection;
using UnityEngine;

namespace AceLand.Injection
{
    internal static class Injector
    {
        static Injector() => ComponentResolver.Hook();
        
        /// <summary>Set false to force the reflection path (debugging).</summary>
        public static bool UseGeneratedPlans = true;

        // ------------------------------------------------------------------ create

        public static object CreateInstance(Type type, Container scope, Registration reg, object[] extraArgs)
        {
            if (TryGetPlan(type, reg, out var plan) && plan.CanCreateInstance && CtorSatisfied(plan, scope, extraArgs))
            {
                var fast = plan.CreateInstance(scope, extraArgs);
                plan.Inject(fast, scope);
                ApplyOverrides(fast, scope, reg, InjectTypeInfo.Get(type));
                return fast;
            }

            var info = InjectTypeInfo.Get(type);
            var ctor = SelectConstructor(info, scope, reg, extraArgs)
                       ?? throw new InjectionException($"{type.Name}: no usable constructor found.");

            var ps = ctor.GetParameters();
            var args = ps.Length == 0 ? Array.Empty<object>() : new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = ResolveParameter(ps[i], scope, reg, extraArgs, type);

            object instance;
            try { instance = ctor.Invoke(args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }

            InjectInto(instance, scope, reg, info);
            return instance;
        }

        // ------------------------------------------------------------------ inject

        public static void InjectInto(object instance, Container scope, Registration reg,
                                      InjectTypeInfo info = null)
        {
            if (instance == null) return;
            var type = instance.GetType();

            if (TryGetPlan(type, reg, out var plan))
            {
                plan.Inject(instance, scope);
                if (reg != null && (reg.Members != null || reg.Methods != null))
                    ApplyOverrides(instance, scope, reg, info ?? InjectTypeInfo.Get(type));
                return;
            }

            info ??= InjectTypeInfo.Get(type);
            var component = instance as Component;
            var ignoreAttributes = reg is { IgnoreAttributes: true };

            if (!ignoreAttributes)
            {
                foreach (var m in info.Members)
                {
                    if (m.Component != null)
                    {
                        if (component == null)
                            throw new InjectionException(
                                $"{info.Type.Name}.{m.Member.Name}: component attributes require a Component.");
                        var value = ComponentResolver.Resolve(component, m.Component.Source, m.MemberType,
                                                              m.Component.Optional, m.Component.IncludeInactive,
                                                              m.Member.Name);
                        if (value != null) m.SetValue(instance, value);
                        continue;
                    }

                    var attr = m.Inject;
                    if (scope.TryResolve(m.MemberType, out var resolved, attr.Id)) m.SetValue(instance, resolved);
                    else if (!attr.Optional)
                        throw new InjectionException(
                            $"Cannot inject '{info.Type.Name}.{m.Member.Name}': " +
                            $"{m.MemberType.Name}{(attr.Id != null ? $" #{attr.Id}" : "")} is not registered.");
                }

                foreach (var mt in info.Methods)
                {
                    var ps = mt.Parameters;
                    var args = ps.Length == 0 ? Array.Empty<object>() : new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (scope.TryResolve(ps[i].ParameterType, out var v, mt.Inject.Id)) args[i] = v;
                        else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                        else if (mt.Inject.Optional) args[i] = null;
                        else throw new InjectionException(
                            $"Cannot invoke {info.Type.Name}.{mt.Method.Name}: missing {ps[i].ParameterType.Name}.");
                    }
                    Invoke(mt.Method, instance, args);
                }
            }

            ApplyOverrides(instance, scope, reg, info);
        }

        // ------------------------------------------------------------------ explicit plan overrides

        static void ApplyOverrides(object instance, Container scope, Registration reg, InjectTypeInfo info)
        {
            if (reg?.Members != null)
            {
                foreach (var mo in reg.Members)
                {
                    var member = info.FindMember(mo.Name)
                                 ?? throw new InjectionException(
                                     $"{info.Type.Name}: member '{mo.Name}' not found (InjectMember).");
                    var memberType = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;

                    object value;
                    if (mo.Value != null) value = mo.Value(scope);
                    else if (!scope.TryResolve(memberType, out value))
                    {
                        if (mo.Optional) continue;
                        throw new InjectionException($"{info.Type.Name}.{mo.Name}: {memberType.Name} is not registered.");
                    }

                    if (member is FieldInfo fi) fi.SetValue(instance, value);
                    else ((PropertyInfo)member).SetValue(instance, value);
                }
            }

            if (reg?.Methods == null) return;
            foreach (var mo in reg.Methods)
            {
                var explicitArgs = mo.ExplicitArgs ?? Array.Empty<object>();
                var method = info.FindMethod(mo.Name, explicitArgs.Length)
                             ?? throw new InjectionException(
                                 $"{info.Type.Name}: method '{mo.Name}' not found (InvokeMethod).");
                var ps = method.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i < explicitArgs.Length && explicitArgs[i] != null) { args[i] = explicitArgs[i]; continue; }
                    if (scope.TryResolve(ps[i].ParameterType, out var v)) args[i] = v;
                    else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else throw new InjectionException(
                        $"Cannot invoke {info.Type.Name}.{mo.Name}: missing {ps[i].ParameterType.Name}.");
                }
                Invoke(method, instance, args);
            }
        }

        // ------------------------------------------------------------------ plan helpers

        static bool TryGetPlan(Type type, Registration reg, out IInjectorPlan plan)
        {
            plan = null;
            if (!UseGeneratedPlans) return false;
            if (reg != null && (reg.IgnoreAttributes || reg.ConstructorSignature != null || reg.Parameters != null))
                return false;                                   // explicit plan → reflection
            return InjectorPlanRegistry.TryGet(type, out plan);
        }

        static bool CtorSatisfied(IInjectorPlan plan, Container scope, object[] extraArgs)
        {
            foreach (var d in plan.Dependencies)
            {
                if (d.Kind != DependencyKind.Constructor || d.Optional) continue;
                if (InjectorPlanUtil.PickExtra(extraArgs, d.ContractType) != null) continue;
                if (!scope.CanResolve(d.ContractType, d.Id))
                    return !plan.HasMultipleConstructors; // single ctor → let it throw nicely
            }
            return true;
        }

        // ------------------------------------------------------------------ reflection ctor selection

        static ConstructorInfo SelectConstructor(InjectTypeInfo info, Container scope,
                                                 Registration reg, object[] extraArgs)
        {
            if (reg?.CachedConstructor != null) return reg.CachedConstructor;

            ConstructorInfo chosen;
            if (reg?.ConstructorSignature != null)
            {
                chosen = info.Type.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, reg.ConstructorSignature, null);
                if (chosen == null)
                    throw new InjectionException(
                        $"{info.Type.Name}: no constructor matching " +
                        $"({string.Join(", ", Array.ConvertAll(reg.ConstructorSignature, t => t.Name))}).");
            }
            else if (info.AttributedConstructor != null) chosen = info.AttributedConstructor;
            else
            {
                chosen = null;
                foreach (var c in info.Constructors)
                    if (CanSatisfy(c, scope, reg, extraArgs)) { chosen = c; break; }
                chosen ??= info.Constructors.Length > 0 ? info.Constructors[^1] : null;
            }

            if (reg != null) reg.CachedConstructor = chosen;
            return chosen;
        }

        static bool CanSatisfy(ConstructorInfo ctor, Container scope, Registration reg, object[] extraArgs)
        {
            foreach (var p in ctor.GetParameters())
            {
                if (FindOverride(p, reg) != null) continue;
                if (InjectorPlanUtil.PickExtra(extraArgs, p.ParameterType) != null) continue;
                if (scope.CanResolve(p.ParameterType, p.GetCustomAttribute<InjectAttribute>()?.Id)) continue;
                if (p.HasDefaultValue) continue;
                return false;
            }
            return true;
        }

        static object ResolveParameter(ParameterInfo p, Container scope, Registration reg,
                                       object[] extraArgs, Type owner)
        {
            var ov = FindOverride(p, reg);
            if (ov != null) return ov.Value(scope);

            var extra = InjectorPlanUtil.PickExtra(extraArgs, p.ParameterType);
            if (extra != null) return extra;

            var attr = p.GetCustomAttribute<InjectAttribute>();
            if (scope.TryResolve(p.ParameterType, out var v, attr?.Id)) return v;
            if (p.HasDefaultValue) return p.DefaultValue;
            if (attr is { Optional: true }) return null;

            throw new InjectionException(
                $"Cannot construct {owner.Name}: parameter '{p.Name}' ({p.ParameterType.Name}) is not registered. " +
                "Register it, pass WithParameter(...) / CreateInstance(args), or give it a default value.");
        }

        static ParameterOverride FindOverride(ParameterInfo p, Registration reg)
        {
            if (reg?.Parameters == null) return null;
            foreach (var o in reg.Parameters) if (o.Name == p.Name) return o;
            foreach (var o in reg.Parameters) if (o.Type != null && p.ParameterType.IsAssignableFrom(o.Type)) return o;
            return null;
        }

        static void Invoke(MethodBase m, object instance, object[] args)
        {
            try { m.Invoke(instance, args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AceLand.Injection
{
    internal sealed class InjectTypeInfo
    {
        private const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, InjectTypeInfo> cache = new Dictionary<Type, InjectTypeInfo>();
        private static readonly object cacheLock = new object();

        public readonly Type Type;
        public readonly ConstructorInfo[] Constructors;
        public readonly ConstructorInfo AttributedConstructor;
        public readonly List<MemberTarget> Members = new List<MemberTarget>();
        public readonly List<MethodTarget> Methods = new List<MethodTarget>();

        public struct MemberTarget
        {
            public MemberInfo Member; public Type MemberType;
            public InjectAttribute Inject; public ComponentInjectAttribute Component;
            public void SetValue(object t, object v)
            {
                if (Member is FieldInfo f) f.SetValue(t, v); else ((PropertyInfo)Member).SetValue(t, v);
            }
        }

        public struct MethodTarget
        {
            public MethodInfo Method; public ParameterInfo[] Parameters; public InjectAttribute Inject;
        }

        public static InjectTypeInfo Get(Type type)
        {
            lock (cacheLock)
            {
                if (cache.TryGetValue(type, out var i)) return i;
                cache[type] = i = new InjectTypeInfo(type);
                return i;
            }
        }

        private InjectTypeInfo(Type type)
        {
            Type = type;

            if (!typeof(UnityEngine.Object).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
            {
                Constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic)
                                   .Where(c => !c.IsStatic)
                                   .OrderByDescending(c => c.GetParameters().Length).ToArray();
                AttributedConstructor = Constructors.FirstOrDefault(c => c.IsDefined(typeof(InjectAttribute), true));
            }
            else Constructors = Array.Empty<ConstructorInfo>();

            var chain = new List<Type>();
            for (var t = type; t != null && t != typeof(object) &&
                 t != typeof(UnityEngine.MonoBehaviour) && t != typeof(UnityEngine.Component) &&
                 t != typeof(UnityEngine.ScriptableObject); t = t.BaseType)
                chain.Add(t);
            chain.Reverse();

            foreach (var t in chain)
            {
                foreach (var f in t.GetFields(FLAGS)) TryAdd(f, f.FieldType);
                foreach (var p in t.GetProperties(FLAGS)) if (p.CanWrite) TryAdd(p, p.PropertyType);
                foreach (var m in t.GetMethods(FLAGS))
                {
                    var a = m.GetCustomAttribute<InjectAttribute>(true);
                    if (a == null) continue;
                    Methods.Add(new MethodTarget { Method = m, Parameters = m.GetParameters(), Inject = a });
                }
            }
        }

        private void TryAdd(MemberInfo member, Type memberType)
        {
            var inject = member.GetCustomAttribute<InjectAttribute>(true);
            var comp = member.GetCustomAttribute<ComponentInjectAttribute>(true);
            if (inject == null && comp == null) return;
            Members.Add(new MemberTarget
            { Member = member, MemberType = memberType, Inject = inject, Component = comp });
        }

        public MemberInfo FindMember(string name)
        {
            for (var t = Type; t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField(name, FLAGS); if (f != null) return f;
                var p = t.GetProperty(name, FLAGS); if (p != null && p.CanWrite) return p;
            }
            return null;
        }

        public MethodInfo FindMethod(string name, int argCount)
        {
            for (var t = Type; t != null && t != typeof(object); t = t.BaseType)
            {
                var c = t.GetMethods(FLAGS).Where(m => m.Name == name).ToArray();
                if (c.Length == 0) continue;
                return c.FirstOrDefault(m => m.GetParameters().Length >= argCount) ?? c[0];
            }
            return null;
        }
    }
}
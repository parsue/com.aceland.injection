using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AceLand.Injection
{
    internal static class ComponentResolver
    {
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        internal static void Hook() => ComponentInjection.Resolver = ResolveBoxed;

        private static object ResolveBoxed(object owner, ComponentSource source, Type memberType,
                                   bool optional, bool includeInactive, bool includeSelf, string memberName)
        {
            if (owner is not Component c)
                throw new InjectionException(
                    $"{owner?.GetType().Name}.{memberName}: component attributes require a Component.");
            return Resolve(c, source, memberType, optional, includeInactive, includeSelf, memberName);
        }

        public static object Resolve(Component owner, ComponentSource source, Type memberType,
                                     bool optional, bool includeInactive, bool includeSelf, string memberName)
        {
            var (element, kind) = Unwrap(memberType);
            if (!typeof(Component).IsAssignableFrom(element) && !element.IsInterface)
                throw new InjectionException($"{owner.GetType().Name}.{memberName}: {element.Name} is not a Component.");

            if (kind == Kind.Single)
            {
                var single = FindSingle(owner, source, element, includeInactive, includeSelf);
                if (!single && !optional)
                    throw new InjectionException(
                        $"{owner.GetType().Name}.{memberName}: no {element.Name} found via [{source}] " +
                        $"on '{owner.gameObject.name}'.");
                return single;
            }

            var all = FindMany(owner, source, element, includeInactive, includeSelf);
            if (all.Count == 0 && !optional)
                throw new InjectionException($"{owner.GetType().Name}.{memberName}: no {element.Name} found.");

            if (kind == Kind.Array)
            {
                var arr = Array.CreateInstance(element, all.Count);
                for (int i = 0; i < all.Count; i++) arr.SetValue(all[i], i);
                return arr;
            }
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
            foreach (var c in all) list.Add(c);
            return list;
        }

        private enum Kind { Single, Array, List }

        private static (Type, Kind) Unwrap(Type t)
        {
            if (t.IsArray) return (t.GetElementType(), Kind.Array);
            if (t.IsGenericType)
            {
                var d = t.GetGenericTypeDefinition();
                if (d == typeof(List<>) || d == typeof(IList<>) ||
                    d == typeof(IReadOnlyList<>) || d == typeof(IEnumerable<>))
                    return (t.GetGenericArguments()[0], Kind.List);
            }
            return (t, Kind.Single);
        }

        private static Component FindSingle(Component o, ComponentSource s, Type type,
                                            bool includeInactive, bool includeSelf)
        {
            switch (s)
            {
                case ComponentSource.Self:   return o.GetComponent(type);
                case ComponentSource.Parent:
                    if (includeSelf) return o.GetComponentInParent(type, includeInactive);
                    return FirstExcludingSelf(o, o.GetComponentsInParent(type, includeInactive));
                case ComponentSource.Child:
                    if (includeSelf) return o.GetComponentInChildren(type, includeInactive);
                    return FirstExcludingSelf(o, o.GetComponentsInChildren(type, includeInactive));
                case ComponentSource.AddComponent:
                    return o.GetComponent(type) ?? o.gameObject.AddComponent(type);
                case ComponentSource.Scene:
                    return UnityFind.First(type, includeInactive);
                default: return null;
            }
        }

        private static IList<Component> FindMany(Component o, ComponentSource s, Type type,
                                                 bool includeInactive, bool includeSelf)
        {
            switch (s)
            {
                case ComponentSource.Self:   return o.GetComponents(type);
                case ComponentSource.Parent:
                    return ExcludeSelf(o, o.GetComponentsInParent(type, includeInactive), includeSelf);
                case ComponentSource.Child:
                    return ExcludeSelf(o, o.GetComponentsInChildren(type, includeInactive), includeSelf);
                case ComponentSource.Scene:  return UnityFind.All(type, includeInactive);
                default: return Array.Empty<Component>();
            }
        }

        private static Component FirstExcludingSelf(Component o, Component[] found)
        {
            var self = o.transform;
            foreach (var c in found)
                if (c && c.transform != self) return c;
            return null;
        }

        private static IList<Component> ExcludeSelf(Component o, Component[] found, bool includeSelf)
        {
            if (includeSelf) return found;
            var self = o.transform;
            var list = new List<Component>(found.Length);
            foreach (var c in found)
                if (c && c.transform != self) list.Add(c);
            return list;
        }
    }
}
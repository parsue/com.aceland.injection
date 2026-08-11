// GlobalInstallerScanner.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace AceLand.Injection
{
    internal static class GlobalInstallerScanner
    {
        private const string RESOURCES_FOLDER = "AceLandInjection";

        private static readonly string[] ignored =
        {
            "System", "mscorlib", "netstandard", "Mono.", "Unity.", "UnityEngine", "UnityEditor",
            "nunit.", "Bee.", "ExCSS", "Newtonsoft", "log4net", "JetBrains", "Microsoft."
        };

        public static IEnumerable<IInstaller> Discover()
        {
            var found = new List<(int order, IInstaller installer)>();

#if ACELAND_INJECTION_NO_AUTO_SCAN
            return Array.Empty<IInstaller>();
#else
#if UNITY_EDITOR
            foreach (var t in UnityEditor.TypeCache.GetTypesDerivedFrom<IGlobalInstaller>()) TryAddType(t, found);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) AddAssemblyAttributes(asm, found);
#else
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (Skip(asm)) continue;
                AddAssemblyAttributes(asm, found);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (var t in types) if (typeof(IGlobalInstaller).IsAssignableFrom(t)) TryAddType(t, found);
            }
#endif
            found.Sort((a, b) => a.order.CompareTo(b.order));

            foreach (var so in Resources.LoadAll<ScriptableObject>(RESOURCES_FOLDER))
                if (so is IInstaller inst) found.Add((int.MaxValue, inst));

            return found.Select(f => f.installer);
#endif
        }

        static void AddAssemblyAttributes(Assembly asm, List<(int, IInstaller)> found)
        {
            object[] attrs;
            try { attrs = asm.GetCustomAttributes(typeof(InjectionInstallerAttribute), false); }
            catch { return; }
            foreach (InjectionInstallerAttribute a in attrs)
            {
                var i = Instantiate(a.InstallerType);
                if (i != null) found.Add((a.Order, i));
            }
        }

        static void TryAddType(Type t, List<(int, IInstaller)> found)
        {
            if (t.IsAbstract || t.IsInterface) return;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return;
            var auto = t.GetCustomAttribute<AutoInstallAttribute>();
            if (auto == null) return;
            var i = Instantiate(t);
            if (i != null) found.Add((auto.Order, i));
        }

        static IInstaller Instantiate(Type t)
        {
            try { return (IInstaller)Activator.CreateInstance(t, true); }
            catch (Exception e)
            {
                Debug.LogError($"[Injection] Cannot instantiate installer '{t?.FullName}': {e.Message}");
                return null;
            }
        }

        private static bool Skip(Assembly asm)
        {
            var n = asm.GetName().Name;
            foreach (var p in ignored) if (n.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
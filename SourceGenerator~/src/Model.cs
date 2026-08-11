using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AceLand.Injection.SourceGenerator
{
    internal sealed class Symbols
    {
        public readonly INamedTypeSymbol Inject, Injectable, NoInjector, GenerateFor;
        public readonly Dictionary<string, string> ComponentAttrToSource;

        public Symbols(Compilation c, INamedTypeSymbol inject)
        {
            Inject = inject;
            Injectable = c.GetTypeByMetadataName("AceLand.Injection.InjectableAttribute");
            NoInjector = c.GetTypeByMetadataName("AceLand.Injection.NoInjectorAttribute");
            GenerateFor = c.GetTypeByMetadataName("AceLand.Injection.GenerateInjectorForAttribute");
            ComponentAttrToSource = new Dictionary<string, string>
            {
                { "AceLand.Injection.SelfAttribute",         "Self" },
                { "AceLand.Injection.ParentAttribute",       "Parent" },
                { "AceLand.Injection.ChildAttribute",        "Child" },
                { "AceLand.Injection.FromSceneAttribute",    "Scene" },
                { "AceLand.Injection.AddComponentAttribute", "AddComponent" },
            };
        }
    }

    internal sealed class TypeModel
    {
        public readonly INamedTypeSymbol Symbol;
        public IMethodSymbol Constructor;
        public bool HasMultipleConstructors, IsComponent, IsPartial, Nested;
        public readonly List<MemberModel> Members = new List<MemberModel>();
        public readonly List<MethodModel> Methods = new List<MethodModel>();

        public TypeModel(INamedTypeSymbol s) => Symbol = s;

        public string FullName => Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        public string DisplayName => Symbol.ToDisplayString();
        public string HintName => DisplayName.Replace('.', '_').Replace('+', '_') + ".Injector.g.cs";
    }

    internal sealed class MemberModel
    {
        public string Name;
        public string TypeFullName;
        public bool IsComponent, Optional, IncludeInactive, IsPrivateOrProtected, IsProperty;
        public string ComponentSource;
        public string IdLiteral;

        public static MemberModel From(ISymbol member, ITypeSymbol type, AttributeData inject,
                                       (AttributeData attr, string source)? comp, Symbols s)
        {
            var m = new MemberModel
            {
                Name = member.Name,
                TypeFullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsProperty = member is IPropertySymbol,
                IsPrivateOrProtected = member.DeclaredAccessibility < Accessibility.Internal
            };
            if (comp.HasValue)
            {
                m.IsComponent = true;
                m.ComponentSource = comp.Value.source;
                m.Optional = comp.Value.attr.GetNamedBool("Optional", false);
                m.IncludeInactive = comp.Value.attr.GetNamedBool("IncludeInactive", true);
            }
            else
            {
                m.Optional = inject.GetNamedBool("Optional", false);
                m.IdLiteral = inject.GetNamedLiteral("Id");
            }
            return m;
        }
    }

    internal sealed class MethodModel
    {
        public string Name;
        public bool IsPrivateOrProtected, Optional;
        public string IdLiteral;
        public readonly List<(string type, string name)> Parameters = new List<(string, string)>();

        public static MethodModel From(IMethodSymbol m, AttributeData inject)
        {
            var model = new MethodModel
            {
                Name = m.Name,
                IsPrivateOrProtected = m.DeclaredAccessibility < Accessibility.Internal,
                Optional = inject.GetNamedBool("Optional", false),
                IdLiteral = inject.GetNamedLiteral("Id")
            };
            foreach (var p in m.Parameters)
                model.Parameters.Add((p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), p.Name));
            return model;
        }
    }

    internal static class SymbolExtensions
    {
        public static bool HasAttr(this ISymbol s, INamedTypeSymbol attr)
            => attr != null && s.GetAttributes()
                .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attr));

        public static AttributeData GetAttr(this ISymbol s, INamedTypeSymbol attr)
            => s.GetAttributes()
                .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attr));

        public static (AttributeData, string)? GetComponentAttr(this ISymbol s, Symbols symbols)
        {
            foreach (var a in s.GetAttributes())
            {
                var name = a.AttributeClass?.ToDisplayString();
                if (name != null && symbols.ComponentAttrToSource.TryGetValue(name, out var src))
                    return (a, src);
            }
            return null;
        }

        public static bool GetNamedBool(this AttributeData a, string name, bool fallback)
        {
            foreach (var kv in a.NamedArguments)
                if (kv.Key == name && kv.Value.Value is bool b) return b;
            return fallback;
        }

        /// <summary>Returns a C# literal for the Id named argument, or null.</summary>
        public static string GetNamedLiteral(this AttributeData a, string name)
        {
            foreach (var kv in a.NamedArguments)
            {
                if (kv.Key != name) continue;
                var v = kv.Value.Value;
                if (v == null) return null;
                if (v is string str) return "\"" + str.Replace("\"", "\\\"") + "\"";
                if (v is bool bo) return bo ? "true" : "false";
                if (kv.Value.Type?.TypeKind == TypeKind.Enum)
                    return $"({kv.Value.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){v}";
                return v.ToString();
            }
            return null;
        }

        public static bool InheritsFrom(this INamedTypeSymbol type, string metadataName)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
                if (t.ToDisplayString() == metadataName) return true;
            return false;
        }

        public static bool IsDeclaredPartial(this INamedTypeSymbol type)
            => type.DeclaringSyntaxReferences
                   .Select(r => r.GetSyntax())
                   .OfType<TypeDeclarationSyntax>()
                   .Any(d => d.Modifiers.Any(m => m.ValueText == "partial"));

        public static IEnumerable<ISymbol> EnumerateMembersWithBases(this INamedTypeSymbol type)
        {
            var chain = new List<INamedTypeSymbol>();
            for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            {
                var n = t.ToDisplayString();
                if (n == "UnityEngine.MonoBehaviour" || n == "UnityEngine.Component" ||
                    n == "UnityEngine.ScriptableObject" || n == "UnityEngine.Object") break;
                chain.Add(t);
            }
            chain.Reverse();                                   // base first
            foreach (var t in chain)
                foreach (var m in t.GetMembers())
                    yield return m;
        }
    }
}
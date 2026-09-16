using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AceLand.Injection.SourceGenerator
{
    [Generator]
    public sealed class InjectorGenerator : IIncrementalGenerator
    {
        const string Version = "1.0.0";
        const string Ns = "AceLand.Injection";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Candidate type declarations: those carrying attributes on the type or any member.
            // NOTE: staying on Roslyn 4.1 (Unity 2022.3), so we use CreateSyntaxProvider and
            // deliberately avoid ForAttributeWithMetadataName (requires Roslyn 4.3.1+).
            var candidates = context.SyntaxProvider
                .CreateSyntaxProvider(IsCandidate, Transform)
                .Collect();

            var input = context.CompilationProvider.Combine(candidates);

            context.RegisterSourceOutput(input, (spc, pair) =>
            {
                try { Produce(spc, pair.Left, pair.Right); }
                catch (Exception e)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(Diags.GeneratorCrashed, Location.None, e.ToString()));
                }
            });
        }

        // ------------------------------------------------------------------ syntax filter

        static bool IsCandidate(SyntaxNode node, CancellationToken _)
        {
            if (!(node is TypeDeclarationSyntax t)) return false;
            if (t is InterfaceDeclarationSyntax) return false;

            if (t.AttributeLists.Count > 0) return true;
            foreach (var m in t.Members)
                if (m.AttributeLists.Count > 0) return true;
            return false;
        }

        static TypeDeclarationSyntax Transform(GeneratorSyntaxContext ctx, CancellationToken _)
            => (TypeDeclarationSyntax)ctx.Node;

        // ------------------------------------------------------------------ production

        static void Produce(SourceProductionContext context, Compilation c,
                            ImmutableArray<TypeDeclarationSyntax> candidates)
        {
            var injectAttr = c.GetTypeByMetadataName($"{Ns}.InjectAttribute");
            if (injectAttr == null) return;                       // Abstractions not referenced

            var symbols = new Symbols(c, injectAttr);
            var models = new List<TypeModel>();
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            // before generating the module file
            var moduleInitAttr = c.GetTypeByMetadataName("System.Runtime.CompilerServices.ModuleInitializerAttribute");
            var needsPolyfill  = moduleInitAttr == null
                                 || !c.IsSymbolAccessibleWithin(moduleInitAttr, c.Assembly);

            // UnityEngine is absent in engine-free assemblies (Abstractions, tools)
            var hasUnityEngine = c.GetTypeByMetadataName("UnityEngine.Object") != null;

            // 1) syntax candidates
            foreach (var decl in candidates)
            {
                if (decl == null) continue;
                var model = c.GetSemanticModel(decl.SyntaxTree);
                if (!(model.GetDeclaredSymbol(decl) is INamedTypeSymbol sym)) continue;
                if (!seen.Add(sym)) continue;
                Analyze(sym, symbols, context, models);
            }

            // 2) [assembly: GenerateInjectorFor(typeof(X))]
            foreach (var a in c.Assembly.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbols.GenerateFor)) continue;
                if (a.ConstructorArguments.Length == 0) continue;
                if (!(a.ConstructorArguments[0].Value is INamedTypeSymbol sym)) continue;
                if (!seen.Add(sym)) continue;
                Analyze(sym, symbols, context, models);
            }

            // 3) global installers → compile-time registry (avoids Player cold-start reflection scan)
            var installers = CollectInstallers(c, symbols, candidates);

            if (models.Count == 0 && installers.Count == 0) return;

            foreach (var m in models)
                context.AddSource(m.HintName, SourceText.From(Emitter.EmitPlan(m, Version), Encoding.UTF8));

            context.AddSource("__AceLandInjectorModule.g.cs",
                SourceText.From(
                    Emitter.EmitModule(c.AssemblyName, models, installers, Version, needsPolyfill, hasUnityEngine),
                    Encoding.UTF8));
        }

        // ------------------------------------------------------------------ installers

        static List<InstallerModel> CollectInstallers(Compilation c, Symbols s,
                                                      ImmutableArray<TypeDeclarationSyntax> candidates)
        {
            var result = new List<InstallerModel>();
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            // [AutoInstall] on a concrete IGlobalInstaller implementation
            if (s.GlobalInstaller != null && s.AutoInstall != null)
            {
                foreach (var decl in candidates)
                {
                    if (decl == null) continue;
                    var model = c.GetSemanticModel(decl.SyntaxTree);
                    if (!(model.GetDeclaredSymbol(decl) is INamedTypeSymbol sym)) continue;
                    if (sym.IsAbstract || sym.IsStatic || sym.TypeKind == TypeKind.Interface) continue;
                    if (sym.InheritsFrom("UnityEngine.Object")) continue;
                    var auto = sym.GetAttr(s.AutoInstall);
                    if (auto == null) continue;
                    if (!sym.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, s.GlobalInstaller))) continue;
                    if (!c.IsSymbolAccessibleWithin(sym, c.Assembly)) continue;   // let reflection handle non-accessible
                    if (!seen.Add(sym)) continue;
                    result.Add(new InstallerModel
                    {
                        TypeFullName = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        Order = CtorInt(auto, 0, 0)
                    });
                }
            }

            // [assembly: InjectionInstaller(typeof(X), order)]
            if (s.InjectionInstaller != null)
            {
                foreach (var a in c.Assembly.GetAttributes())
                {
                    if (!SymbolEqualityComparer.Default.Equals(a.AttributeClass, s.InjectionInstaller)) continue;
                    if (a.ConstructorArguments.Length == 0) continue;
                    if (!(a.ConstructorArguments[0].Value is INamedTypeSymbol sym)) continue;
                    if (sym.IsAbstract || sym.TypeKind == TypeKind.Interface) continue;
                    if (!c.IsSymbolAccessibleWithin(sym, c.Assembly)) continue;
                    if (!seen.Add(sym)) continue;
                    result.Add(new InstallerModel
                    {
                        TypeFullName = sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        Order = CtorInt(a, 1, 0)
                    });
                }
            }

            return result;
        }

        static int CtorInt(AttributeData a, int index, int fallback)
            => a.ConstructorArguments.Length > index && a.ConstructorArguments[index].Value is int v ? v : fallback;

        // ------------------------------------------------------------------ analysis

        static void Analyze(INamedTypeSymbol type, Symbols s, SourceProductionContext ctx, List<TypeModel> output)
        {
            if (type.IsStatic || type.IsAbstract || type.TypeKind == TypeKind.Interface) return;
            if (type.HasAttr(s.NoInjector)) return;
            if (type.IsGenericType)
            {
                Report(ctx, Diags.GenericUnsupported, type, type.Name);
                return;
            }

            var model = new TypeModel(type);
            var isUnityObject = type.InheritsFrom("UnityEngine.Object");
            model.IsComponent = type.InheritsFrom("UnityEngine.Component");

            // ---- constructor ----
            if (!isUnityObject)
            {
                var ctors = type.InstanceConstructors.Where(k => !k.IsStatic).ToArray();
                var attributed = ctors.Where(k => k.HasAttr(s.Inject)).ToArray();
                if (attributed.Length > 1)
                {
                    Report(ctx, Diags.MultipleInjectCtors, type, type.Name);
                    return;
                }
                var chosen = attributed.FirstOrDefault()
                             ?? ctors.OrderByDescending(k => k.Parameters.Length).FirstOrDefault();
                if (chosen != null && !chosen.IsImplicitlyDeclared || chosen?.Parameters.Length > 0)
                {
                    model.Constructor = chosen;
                    model.HasMultipleConstructors = ctors.Length > 1;
                }
                else model.Constructor = chosen;   // parameterless
            }

            // ---- members ----
            foreach (var member in type.EnumerateMembersWithBases())
            {
                switch (member)
                {
                    case IFieldSymbol f when !f.IsStatic && !f.IsImplicitlyDeclared:
                    {
                        var inj = f.GetAttr(s.Inject);
                        var comp = f.GetComponentAttr(s);
                        if (inj == null && comp == null) break;
                        if (f.IsReadOnly || f.IsConst)
                        { Report(ctx, Diags.ReadOnlyMember, f, $"{type.Name}.{f.Name}"); return; }
                        model.Members.Add(MemberModel.From(f, f.Type, inj, comp, s));
                        break;
                    }
                    case IPropertySymbol p when !p.IsStatic:
                    {
                        var inj = p.GetAttr(s.Inject);
                        var comp = p.GetComponentAttr(s);
                        if (inj == null && comp == null) break;
                        if (p.SetMethod == null)
                        { Report(ctx, Diags.NoSetter, p, $"{type.Name}.{p.Name}"); return; }
                        model.Members.Add(MemberModel.From(p, p.Type, inj, comp, s));
                        break;
                    }
                    case IMethodSymbol m when !m.IsStatic && m.MethodKind == MethodKind.Ordinary:
                    {
                        var inj = m.GetAttr(s.Inject);
                        if (inj == null) break;
                        model.Methods.Add(MethodModel.From(m, inj));
                        break;
                    }
                }
            }

            var forced = type.HasAttr(s.Injectable);
            if (!forced && model.Members.Count == 0 && model.Methods.Count == 0 &&
                (model.Constructor == null || model.Constructor.Parameters.Length == 0))
                return;                                        // nothing to generate

            // ---- component attributes only valid on Components ----
            if (!model.IsComponent && model.Members.Any(m => m.IsComponent))
            {
                var bad = model.Members.First(m => m.IsComponent);
                Report(ctx, Diags.ComponentOnNonComponent, type, $"{type.Name}.{bad.Name}");
                return;
            }

            // ---- accessibility strategy ----
            model.IsPartial = type.IsDeclaredPartial();
            var needsPrivateAccess =
                model.Members.Any(m => m.IsPrivateOrProtected) ||
                model.Methods.Any(m => m.IsPrivateOrProtected) ||
                (model.Constructor != null &&
                 model.Constructor.DeclaredAccessibility < Accessibility.Internal);

            if (needsPrivateAccess && !model.IsPartial)
            {
                Report(ctx, Diags.MakePartial, type, type.Name);      // info: reflection fallback
                return;
            }

            model.Nested = model.IsPartial;
            output.Add(model);
        }

        static void Report(SourceProductionContext ctx, DiagnosticDescriptor d, ISymbol s, params object[] args)
            => ctx.ReportDiagnostic(Diagnostic.Create(d, s.Locations.FirstOrDefault(), args));
    }
}

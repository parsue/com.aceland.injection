using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AceLand.Injection.SourceGenerator
{
    [Generator]
    public sealed class InjectorGenerator : ISourceGenerator
    {
        const string Version = "1.0.0";
        const string Ns = "AceLand.Injection";

        public void Initialize(GeneratorInitializationContext context)
            => context.RegisterForSyntaxNotifications(() => new CandidateReceiver());

        public void Execute(GeneratorExecutionContext context)
        {
            try { ExecuteCore(context); }
            catch (Exception e)
            {
                context.ReportDiagnostic(Diagnostic.Create(Diags.GeneratorCrashed, Location.None, e.ToString()));
            }
        }

        void ExecuteCore(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxReceiver is CandidateReceiver receiver)) return;

            var c = context.Compilation;
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
            
            context.AddSource("__AceLandInjectorModule.g.cs",
                SourceText.From(
                    Emitter.EmitModule(c.AssemblyName, models, Version, needsPolyfill, hasUnityEngine),
                    Encoding.UTF8));

            // 1) syntax candidates
            foreach (var decl in receiver.Candidates)
            {
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

            if (models.Count == 0) return;

            foreach (var m in models)
                context.AddSource(m.HintName, SourceText.From(Emitter.EmitPlan(m, Version), Encoding.UTF8));
            
            context.AddSource("__AceLandInjectorModule.g.cs",
                SourceText.From(
                    Emitter.EmitModule(c.AssemblyName, models, Version, needsPolyfill, hasUnityEngine),
                    Encoding.UTF8));
        }

        // ------------------------------------------------------------------ analysis

        static void Analyze(INamedTypeSymbol type, Symbols s, GeneratorExecutionContext ctx, List<TypeModel> output)
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

        static void Report(GeneratorExecutionContext ctx, DiagnosticDescriptor d, ISymbol s, params object[] args)
            => ctx.ReportDiagnostic(Diagnostic.Create(d, s.Locations.FirstOrDefault(), args));

        // ------------------------------------------------------------------

        sealed class CandidateReceiver : ISyntaxReceiver
        {
            public readonly List<TypeDeclarationSyntax> Candidates = new List<TypeDeclarationSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode node)
            {
                if (!(node is TypeDeclarationSyntax t)) return;
                if (t is InterfaceDeclarationSyntax) return;

                if (t.AttributeLists.Count > 0) { Candidates.Add(t); return; }
                foreach (var m in t.Members)
                    if (m.AttributeLists.Count > 0) { Candidates.Add(t); return; }
            }
        }
    }
}
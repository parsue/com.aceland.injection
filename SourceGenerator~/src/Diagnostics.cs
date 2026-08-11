using Microsoft.CodeAnalysis;

namespace AceLand.Injection.SourceGenerator
{
    internal static class Diags
    {
        const string Cat = "AceLand.Injection";
        
        public static readonly DiagnosticDescriptor GeneratorCrashed = new DiagnosticDescriptor(
            "ACEDI000", "Injector generator crashed",
            "AceLand.Injection.SourceGenerator threw; falling back to reflection. {0}",
            Cat, DiagnosticSeverity.Warning, true);
        
        public static readonly DiagnosticDescriptor ReadOnlyMember = new DiagnosticDescriptor(
            "ACEDI001", "[Inject] member cannot be readonly",
            "'{0}' is readonly/const and cannot be injected", Cat, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor NoSetter = new DiagnosticDescriptor(
            "ACEDI002", "[Inject] property needs a setter",
            "'{0}' has no setter and cannot be injected", Cat, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor ComponentOnNonComponent = new DiagnosticDescriptor(
            "ACEDI003", "Component attribute on a non-Component type",
            "'{0}' uses [Self]/[Parent]/[Child]/[FromScene]/[AddComponent] but the type is not a UnityEngine.Component",
            Cat, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor MultipleInjectCtors = new DiagnosticDescriptor(
            "ACEDI004", "Multiple [Inject] constructors",
            "'{0}' declares more than one [Inject] constructor", Cat, DiagnosticSeverity.Error, true);

        public static readonly DiagnosticDescriptor MakePartial = new DiagnosticDescriptor(
            "ACEDI005", "Mark the type 'partial' for a generated injector",
            "'{0}' injects private/protected members; mark it 'partial' to get a generated injector " +
            "(currently falls back to reflection)", Cat, DiagnosticSeverity.Info, true);

        public static readonly DiagnosticDescriptor GenericUnsupported = new DiagnosticDescriptor(
            "ACEDI006", "Generic types are not code-generated",
            "'{0}' is generic; injection falls back to reflection", Cat, DiagnosticSeverity.Info, true);
    }
}
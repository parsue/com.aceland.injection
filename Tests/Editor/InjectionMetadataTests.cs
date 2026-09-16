using System.Linq;
using NUnit.Framework;

namespace AceLand.Injection.Tests.Editor
{
    /// <summary>
    /// InjectionMetadata.GetDependencies 的迴歸測試。
    /// 重點守門 P0-2：含 [Inject] constructor 的型別不得重複列舉其參數依賴。
    /// </summary>
    public class InjectionMetadataTests
    {
        // 帶 [Inject] 的型別會被 Source Generator 產生 plan（位於外部組件），
        // 因此這些 fixture 及其契約型別需為 public，避免 CS0122。
        public interface IDepOne { }
        public interface IDepTwo { }

        // 含 [Inject] 標記的 constructor，兩個參數
        public sealed class AttributedCtor
        {
            [Inject] public AttributedCtor(IDepOne a, IDepTwo b) { }
        }

        // 無標記，單一 constructor
        public sealed class PlainCtor
        {
            public PlainCtor(IDepOne a) { }
        }

        public sealed class FieldAndProperty
        {
            [Inject] public IDepOne Field;
            [Inject] public IDepTwo Property { get; set; }
        }

        public sealed class MethodInject
        {
            [Inject] public void Init(IDepOne a, IDepTwo b) { }
        }

        [SetUp]
        public void SetUp() => Injector.UseGeneratedPlans = false;

        [TearDown]
        public void TearDown() => Injector.UseGeneratedPlans = true;

        [Test]
        public void Attributed_Constructor_Dependencies_Are_Not_Duplicated()
        {
            var deps = InjectionMetadata.GetDependencies(typeof(AttributedCtor));
            var ctorDeps = deps.Where(d => d.Kind == DependencyKind.Constructor).ToList();

            // P0-2 迴歸：修正前 attributed ctor 參數會被列舉兩次（4 筆）。
            Assert.AreEqual(2, ctorDeps.Count, "attributed constructor 參數不應重複列舉");

            var contracts = ctorDeps.Select(d => d.ContractType).ToList();
            CollectionAssert.Contains(contracts, typeof(IDepOne));
            CollectionAssert.Contains(contracts, typeof(IDepTwo));
        }

        [Test]
        public void Plain_Constructor_Dependencies_Counted_Once()
        {
            var deps = InjectionMetadata.GetDependencies(typeof(PlainCtor));
            var ctorDeps = deps.Where(d => d.Kind == DependencyKind.Constructor).ToList();

            Assert.AreEqual(1, ctorDeps.Count);
            Assert.AreEqual(typeof(IDepOne), ctorDeps[0].ContractType);
        }

        [Test]
        public void Field_And_Property_Dependencies_Detected()
        {
            var deps = InjectionMetadata.GetDependencies(typeof(FieldAndProperty));

            Assert.AreEqual(1, deps.Count(d => d.Kind == DependencyKind.Field));
            Assert.AreEqual(1, deps.Count(d => d.Kind == DependencyKind.Property));
        }

        [Test]
        public void Method_Parameter_Dependencies_Detected()
        {
            var deps = InjectionMetadata.GetDependencies(typeof(MethodInject));
            var methodDeps = deps.Where(d => d.Kind == DependencyKind.MethodParameter).ToList();

            Assert.AreEqual(2, methodDeps.Count);
        }

        [Test]
        public void HasAnyInjection_Reflects_Dependency_Presence()
        {
            Assert.IsTrue(InjectionMetadata.HasAnyInjection(typeof(AttributedCtor)));
            Assert.IsTrue(InjectionMetadata.HasAnyInjection(typeof(FieldAndProperty)));
        }
    }
}

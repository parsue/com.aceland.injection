using NUnit.Framework;

namespace AceLand.Injection.Tests.Editor
{
    /// <summary>
    /// Injector 快路徑（generated plan）與後備（反射）兩條路徑的一致性測試。
    /// 作為 P0-1（generator 重複 AddSource 崩潰）與 P0-2（依賴重複列舉）的迴歸守門：
    /// 無論走哪條路徑，建構與注入的結果都必須相同。
    /// </summary>
    public class InjectorTests
    {
        public interface IGreeter { string Greet(); }

        public sealed class Greeter : IGreeter
        {
            public string Greet() => "hi";
        }

        // ctor 注入
        public sealed class CtorTarget
        {
            public readonly IGreeter Greeter;
            [Inject] public CtorTarget(IGreeter greeter) => Greeter = greeter;
        }

        // 成員注入
        public sealed class MemberTarget
        {
            [Inject] public IGreeter Field;
            [Inject] public IGreeter Property { get; set; }
        }

        // 方法注入
        public sealed class MethodTarget
        {
            public IGreeter Received;
            [Inject] public void Init(IGreeter greeter) => Received = greeter;
        }

        static IResolver Build()
        {
            var builder = new ContainerBuilder();
            builder.Register<Greeter>(Lifetime.Singleton).As<IGreeter>();
            builder.Register<CtorTarget>(Lifetime.Transient);
            builder.Register<MemberTarget>(Lifetime.Transient);
            builder.Register<MethodTarget>(Lifetime.Transient);
            return builder.Build();
        }

        [TearDown]
        public void TearDown() => Injector.UseGeneratedPlans = true;

        [Test]
        public void Constructor_Injection_Consistent_Across_Paths([Values(true, false)] bool useGeneratedPlans)
        {
            Injector.UseGeneratedPlans = useGeneratedPlans;
            using var root = Build();

            var target = root.Resolve<CtorTarget>();

            Assert.IsNotNull(target.Greeter, $"useGeneratedPlans={useGeneratedPlans}");
            Assert.AreEqual("hi", target.Greeter.Greet());
        }

        [Test]
        public void Member_Injection_Consistent_Across_Paths([Values(true, false)] bool useGeneratedPlans)
        {
            Injector.UseGeneratedPlans = useGeneratedPlans;
            using var root = Build();

            var target = root.Resolve<MemberTarget>();

            Assert.IsNotNull(target.Field, $"field, useGeneratedPlans={useGeneratedPlans}");
            Assert.IsNotNull(target.Property, $"property, useGeneratedPlans={useGeneratedPlans}");
        }

        [Test]
        public void Method_Injection_Consistent_Across_Paths([Values(true, false)] bool useGeneratedPlans)
        {
            Injector.UseGeneratedPlans = useGeneratedPlans;
            using var root = Build();

            var target = root.Resolve<MethodTarget>();

            Assert.IsNotNull(target.Received, $"useGeneratedPlans={useGeneratedPlans}");
        }

        [Test]
        public void Inject_Into_Existing_Instance_Consistent_Across_Paths([Values(true, false)] bool useGeneratedPlans)
        {
            Injector.UseGeneratedPlans = useGeneratedPlans;
            using var root = Build();

            var target = new MemberTarget();
            root.Inject(target);

            Assert.IsNotNull(target.Field, $"field, useGeneratedPlans={useGeneratedPlans}");
            Assert.IsNotNull(target.Property, $"property, useGeneratedPlans={useGeneratedPlans}");
        }
    }
}

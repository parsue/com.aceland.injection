using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AceLand.Injection.Tests.Editor
{
    /// <summary>
    /// 核心容器行為的迴歸測試：三種 Lifetime、parent chain、集合解析、
    /// 循環偵測、以及同步/非同步釋放。
    /// </summary>
    public class ContainerTests
    {
        // ---------------------------------------------------------------- fixtures

        // 注意：帶 [Inject] 的型別會被 Source Generator 產生 plan，
        // 而生成程式碼位於外部組件，故這些 fixture 必須為 public（含其契約型別），
        // 否則生成的 plan 會因 protection level 而無法存取（CS0122）。
        public interface IService { }
        public sealed class ServiceA : IService { }
        public sealed class ServiceB : IService { }

        public sealed class Counter
        {
            public static int Instances;
            public Counter() => Instances++;
        }

        public sealed class Consumer
        {
            public readonly IService Service;
            [Inject] public Consumer(IService service) => Service = service;
        }

        public sealed class FieldConsumer
        {
            [Inject] public IService Service;
        }

        // circular: NodeA -> NodeB -> NodeA (constructor injection)
        public sealed class NodeA { [Inject] public NodeA(NodeB b) { } }
        public sealed class NodeB { [Inject] public NodeB(NodeA a) { } }

        public sealed class SyncDisposable : IDisposable
        {
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        public sealed class AsyncOnlyDisposable : IAsyncDisposable
        {
            public bool Disposed;
            public ValueTask DisposeAsync() { Disposed = true; return default; }
        }

        [SetUp]
        public void SetUp()
        {
            Counter.Instances = 0;
            // 讓行為在測試中不依賴 generated plan 的存在與否，統一走反射路徑。
            Injector.UseGeneratedPlans = false;
        }

        [TearDown]
        public void TearDown() => Injector.UseGeneratedPlans = true;

        static IResolver Build(Action<IContainerBuilder> configure)
        {
            var builder = new ContainerBuilder();
            configure(builder);
            return builder.Build();
        }

        // ---------------------------------------------------------------- lifetimes

        [Test]
        public void Transient_Returns_New_Instance_Each_Resolve()
        {
            using var root = Build(b => b.Register<Counter>(Lifetime.Transient));

            var first = root.Resolve<Counter>();
            var second = root.Resolve<Counter>();

            Assert.AreNotSame(first, second);
            Assert.AreEqual(2, Counter.Instances);
        }

        [Test]
        public void Singleton_Returns_Same_Instance()
        {
            using var root = Build(b => b.Register<Counter>(Lifetime.Singleton));

            var first = root.Resolve<Counter>();
            var second = root.Resolve<Counter>();

            Assert.AreSame(first, second);
            Assert.AreEqual(1, Counter.Instances);
        }

        [Test]
        public void Scoped_Same_Within_Scope_But_Different_Across_Scopes()
        {
            using var root = Build(b => b.Register<Counter>(Lifetime.Scoped));

            var rootInstance = root.Resolve<Counter>();
            Assert.AreSame(rootInstance, root.Resolve<Counter>(), "同一 scope 內應為同一實例");

            using var child = root.CreateScope();
            var childInstance = child.Resolve<Counter>();

            Assert.AreNotSame(rootInstance, childInstance, "不同 scope 應為不同實例");
            Assert.AreEqual(2, Counter.Instances);
        }

        // ---------------------------------------------------------------- resolution

        [Test]
        public void Constructor_Injection_Resolves_Dependency()
        {
            using var root = Build(b =>
            {
                b.Register<ServiceA>(Lifetime.Singleton).As<IService>();
                b.Register<Consumer>(Lifetime.Transient);
            });

            var consumer = root.Resolve<Consumer>();

            Assert.IsNotNull(consumer.Service);
            Assert.IsInstanceOf<ServiceA>(consumer.Service);
        }

        [Test]
        public void Field_Injection_Into_Existing_Instance()
        {
            using var root = Build(b => b.Register<ServiceA>(Lifetime.Singleton).As<IService>());

            var target = new FieldConsumer();
            root.Inject(target);

            Assert.IsNotNull(target.Service);
            Assert.IsInstanceOf<ServiceA>(target.Service);
        }

        [Test]
        public void Parent_Chain_Resolves_From_Parent()
        {
            using var root = Build(b => b.Register<ServiceA>(Lifetime.Singleton).As<IService>());
            using var child = root.CreateScope();

            var fromChild = child.Resolve<IService>();

            Assert.IsNotNull(fromChild);
            Assert.AreSame(root.Resolve<IService>(), fromChild, "singleton 應由 parent 擁有並共用");
        }

        [Test]
        public void Collection_Resolves_All_Registrations()
        {
            using var root = Build(b =>
            {
                b.Register<ServiceA>(Lifetime.Singleton).As<IService>();
                b.Register<ServiceB>(Lifetime.Singleton).As<IService>();
            });

            var all = root.Resolve<IEnumerable<IService>>();
            var list = new List<IService>(all);

            Assert.AreEqual(2, list.Count);
        }

        [Test]
        public void Unregistered_Contract_Throws_InjectionException()
        {
            using var root = Build(_ => { });

            Assert.Throws<InjectionException>(() => root.Resolve<IService>());
        }

        // ---------------------------------------------------------------- circular

        [Test]
        public void Circular_Dependency_Throws_InjectionException()
        {
            using var root = Build(b =>
            {
                b.Register<NodeA>(Lifetime.Transient);
                b.Register<NodeB>(Lifetime.Transient);
            });

            var ex = Assert.Throws<InjectionException>(() => root.Resolve<NodeA>());
            StringAssert.Contains("Circular", ex.Message);
        }

        // ---------------------------------------------------------------- disposal

        [Test]
        public void Dispose_Disposes_Owned_Disposables()
        {
            SyncDisposable instance;
            using (var root = Build(b => b.Register<SyncDisposable>(Lifetime.Singleton)))
            {
                instance = root.Resolve<SyncDisposable>();
                Assert.IsFalse(instance.Disposed);
            }

            Assert.IsTrue(instance.Disposed, "容器釋放時應釋放其擁有的 IDisposable");
        }

        [Test]
        public void DisposeAsync_Disposes_AsyncDisposables()
        {
            var root = (Container)Build(b => b.Register<AsyncOnlyDisposable>(Lifetime.Singleton));
            var instance = root.Resolve<AsyncOnlyDisposable>();
            Assert.IsFalse(instance.Disposed);

            root.DisposeAsync().GetAwaiter().GetResult();

            Assert.IsTrue(instance.Disposed, "DisposeAsync 應釋放 IAsyncDisposable");
        }

        [Test]
        public void Dispose_Cascades_To_Child_Scopes()
        {
            var root = Build(b => b.Register<SyncDisposable>(Lifetime.Scoped));
            var child = root.CreateScope();
            var scopedInstance = child.Resolve<SyncDisposable>();

            root.Dispose();

            Assert.IsTrue(scopedInstance.Disposed, "釋放 parent 應連帶釋放 child scope 及其實例");
            Assert.IsTrue(root.IsDisposed);
            Assert.IsTrue(child.IsDisposed);
        }

        [Test]
        public void Resolve_After_Dispose_Throws()
        {
            var root = Build(b => b.Register<ServiceA>(Lifetime.Singleton).As<IService>());
            root.Dispose();

            Assert.Throws<ObjectDisposedException>(() => root.Resolve<IService>());
        }
    }
}

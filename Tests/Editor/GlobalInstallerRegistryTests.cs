using System.Linq;
using NUnit.Framework;

// 觸發本測試組件的 [assembly: InjectionInstaller] 產碼路徑。
[assembly: AceLand.Injection.InjectionInstaller(
    typeof(AceLand.Injection.Tests.Editor.GlobalInstallerRegistryTests.AssemblyInstaller), -50)]

namespace AceLand.Injection.Tests.Editor
{
    /// <summary>
    /// P1-4：驗證 Source Generator 於編譯期把全域 Installer 寫入
    /// <see cref="GlobalInstallerRegistry"/>，讓 Player 冷啟動可跳過反射掃描。
    /// 注意：Installer 型別必須為 public（含巢狀），生成的 module initializer
    /// 位於外部組件，會以 typeof(...) 參照，否則會因 protection level 失敗（CS0122）。
    /// </summary>
    public class GlobalInstallerRegistryTests
    {
        // 走 [AutoInstall] + IGlobalInstaller 產碼路徑
        [AutoInstall(10)]
        public sealed class AutoInstaller : IGlobalInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        // 走 [assembly: InjectionInstaller(...)] 產碼路徑（可不是 IGlobalInstaller）
        public sealed class AssemblyInstaller : IInstaller
        {
            public void Install(IContainerBuilder builder) { }
        }

        [Test]
        public void AutoInstall_Type_Is_Registered_At_CompileTime()
        {
            var all = GlobalInstallerRegistry.All().ToArray();
            Assert.IsTrue(all.Any(e => e.InstallerType == typeof(AutoInstaller)),
                "[AutoInstall] 的 IGlobalInstaller 應由產碼寫入 GlobalInstallerRegistry。");
        }

        [Test]
        public void AutoInstall_Order_Is_Preserved()
        {
            var entry = GlobalInstallerRegistry.All()
                .First(e => e.InstallerType == typeof(AutoInstaller));
            Assert.AreEqual(10, entry.Order);
        }

        [Test]
        public void Assembly_Attribute_Installer_Is_Registered_With_Order()
        {
            var all = GlobalInstallerRegistry.All().ToArray();
            var entry = all.FirstOrDefault(e => e.InstallerType == typeof(AssemblyInstaller));
            Assert.IsNotNull(entry.InstallerType,
                "[assembly: InjectionInstaller] 指定的 Installer 應由產碼寫入 registry。");
            Assert.AreEqual(-50, entry.Order);
        }

        [Test]
        public void Registry_Register_Is_Idempotent()
        {
            var before = GlobalInstallerRegistry.Count;
            GlobalInstallerRegistry.Register(typeof(AutoInstaller), 10);   // 重複註冊
            Assert.AreEqual(before, GlobalInstallerRegistry.Count,
                "同一型別重複註冊不應增加項目數（去重）。");
        }
    }
}

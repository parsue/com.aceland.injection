using System.Runtime.CompilerServices;

// 讓 EditMode 測試 assembly 能存取 internal 型別（例如 Injector.UseGeneratedPlans），
// 以驗證 generated plan 與反射兩條路徑的行為一致。
[assembly: InternalsVisibleTo("AceLand.Injection.EditorTests")]

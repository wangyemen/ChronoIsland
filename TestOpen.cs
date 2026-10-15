using System;

namespace ChronoIsland;

/// <summary>打开测试：dotnet run -- testopen chrome
/// 用完整的降级链尝试打开指定 AUMID</summary>
public static class TestOpen
{
    public static void Run(string aumid)
    {
        Console.WriteLine("=== 打开测试 ===");
        Console.WriteLine("目标 AUMID: " + aumid);
        Console.WriteLine();

        Console.WriteLine("① CustomActivator ...");
        bool r1 = Native.AppLauncher.ActivateViaCustomActivator(aumid);
        Console.WriteLine("   " + r1);
        if (r1) return;

        Console.WriteLine("② AppsFolder 枚举 ...");
        bool r2 = Native.AppLauncher.ActivateViaShellEnumeration(aumid);
        Console.WriteLine("   " + r2);
        if (r2) return;

        Console.WriteLine("③ shell:AppsFolder ...");
        bool r3 = Native.AppLauncher.ActivateViaShell(aumid);
        Console.WriteLine("   " + r3);
    }
}

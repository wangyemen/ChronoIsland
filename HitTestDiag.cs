using System;
using System.Runtime.InteropServices;

namespace ChronoIsland;

/// <summary>穿透状态自检：dotnet run -- hittest
/// 检查岛窗口的 WS_EX_TRANSPARENT 样式位</summary>
public static class HitTestDiag
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
    delegate bool EnumProc(IntPtr h, IntPtr p);

    public static void Run()
    {
        uint myPid = (uint)Environment.ProcessId;
        Console.WriteLine("=== 岛窗口样式自检 ===");
        Console.WriteLine("本进程 PID: " + myPid);
        Console.WriteLine();

        bool found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != myPid) return true;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(h, sb, 256);
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if (ex == 0) return true;
            found = true;
            Console.WriteLine($"窗口: '{sb}'  句柄=0x{h:X}");
            Console.WriteLine($"  WS_EX_TRANSPARENT : {((ex & WS_EX_TRANSPARENT) != 0 ? "✓ 已开启（穿透）" : "✗ 未开启（拦截）")}");
            Console.WriteLine($"  WS_EX_LAYERED     : {((ex & WS_EX_LAYERED) != 0 ? "✓" : "✗")}");
            Console.WriteLine($"  WS_EX_TOOLWINDOW  : {((ex & WS_EX_TOOLWINDOW) != 0 ? "✓（不在 Alt+Tab）" : "✗")}");
            Console.WriteLine();
            return true;
        }, IntPtr.Zero);

        if (!found) Console.WriteLine("未找到窗口（需要先启动主程序）");
        Console.WriteLine("说明：");
        Console.WriteLine("  WS_EX_TRANSPARENT 开启 = 窗口穿透，鼠标点击会落到后面的窗口");
        Console.WriteLine("  本程序在鼠标未停留达标时保持开启 → 岛不挡任何东西");
    }
}

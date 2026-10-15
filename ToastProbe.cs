using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ChronoIsland;

/// <summary>横幅探测：dotnet run -- probe [秒]
/// 全量枚举窗口（不做类名过滤），对比发送通知前后的窗口变化，找出横幅真实身份</summary>
public static class ToastProbe
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    static Dictionary<IntPtr, string> Snapshot()
    {
        var d = new Dictionary<IntPtr, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetClassName(h, sb, 512);
            var cls = sb.ToString();
            GetWindowThreadProcessId(h, out uint pid);
            string proc = "?";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            GetWindowRect(h, out var r);
            var tsb = new StringBuilder(256);
            GetWindowText(h, tsb, 256);
            var owner = GetWindow(h, 4);   // GW_OWNER
            d[h] = $"类={cls} | 进程={proc} | {r.R - r.L}x{r.B - r.T} @({r.L},{r.T}) | 标题='{tsb}' | owner={(owner == IntPtr.Zero ? "无" : "有")}";
            return true;
        }, IntPtr.Zero);
        return d;
    }

    public static void Run(int seconds = 15)
    {
        Console.WriteLine("=== 横幅窗口探测器 ===");
        Console.WriteLine("对比发送通知前后的窗口变化\n");

        var before = Snapshot();
        Console.WriteLine($"通知前窗口数: {before.Count}");
        Console.WriteLine("现在请发送一条测试通知（或等待下面的脚本自动发送）...\n");

        var found = new List<string>();
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline)
        {
            var now = Snapshot();
            foreach (var kv in now)
            {
                if (before.ContainsKey(kv.Key)) continue;
                // 新出现的窗口 —— 很可能就是横幅
                var desc = kv.Value;
                if (!found.Contains(desc))
                {
                    found.Add(desc);
                    Console.WriteLine($"[新窗口] {desc}");
                }
            }
            System.Threading.Thread.Sleep(100);
        }

        Console.WriteLine();
        if (found.Count == 0) Console.WriteLine("未检测到任何新窗口（横幅可能不是 Win32 窗口）");
        else Console.WriteLine($"共发现 {found.Count} 个新窗口");
    }
}

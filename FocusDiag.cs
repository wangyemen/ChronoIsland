using System;
using System.Linq;

namespace ChronoIsland;

/// <summary>专注助手 API 可用性测试</summary>
public static class FocusDiag
{
    public static void Run()
    {
        Console.WriteLine("=== 专注助手 API 测试 ===");
        try
        {
            var t = Type.GetType("Windows.UI.Shell.FocusSessionManager, Microsoft.Windows.SDK.NET");
            Console.WriteLine("FocusSessionManager 类型: " + (t == null ? "未找到" : "OK"));
            if (t != null)
            {
                foreach (var m in t.GetMethods().Where(x => x.DeclaringType == t).Take(20))
                    Console.WriteLine("   " + m.ReturnType.Name + " " + m.Name);
                var isSupported = t.GetProperty("IsSupported");
                if (isSupported != null)
                {
                    var def = t.GetMethod("GetDefault")?.Invoke(null, null);
                    if (def != null)
                        Console.WriteLine("IsSupported = " + isSupported.GetValue(def));
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("异常: " + ex.Message); }

        Console.WriteLine();
        Console.WriteLine("=== 当前通知横幅窗口枚举 ===");
        Console.WriteLine("（等待 8 秒，请现在发一条通知）");
        var deadline = DateTime.Now.AddSeconds(8);
        var seen = new System.Collections.Generic.HashSet<string>();
        while (DateTime.Now < deadline)
        {
            foreach (var w in WindowList())
            {
                if (seen.Add(w)) Console.WriteLine("   " + w);
            }
            System.Threading.Thread.Sleep(150);
        }
        Console.WriteLine("枚举结束");
    }

    private static System.Collections.Generic.List<string> WindowList()
    {
        var list = new System.Collections.Generic.List<string>();
        NativeMethods.EnumWindows((h, _) =>
        {
            if (!NativeMethods.IsWindowVisible(h)) return true;
            var sb = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(h, sb, 256);
            var cls = sb.ToString();
            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
            string proc = "?";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            NativeMethods.GetWindowRect(h, out var r);
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            // 只关心小窗口（横幅尺寸）
            if (w is > 200 and < 900 && hh is > 60 and < 400)
                list.Add($"类={cls,-42} 进程={proc,-24} {w}x{hh}");
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static class NativeMethods
    {
        public delegate bool EnumProc(IntPtr h, IntPtr p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ChronoIsland.UI;

/// <summary>
/// 窗口样式：从 Alt+Tab 移除、不抢焦点、可穿透（docs §6）。
/// </summary>
public static class WindowChromeHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

    /// <summary>应用工具窗口样式：不进任务栏、不进 Alt+Tab、不激活</summary>
    public static void MakeToolWindow(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>切换鼠标穿透</summary>
    public static void SetClickThrough(Window w, bool through)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (through) ex |= WS_EX_TRANSPARENT;
        else ex &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    /// <summary>置顶但不抢焦点</summary>
    public static void SetTopmostNoActivate(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")] private static extern bool GetCursorPosNative(out POINT p);
    [DllImport("user32.dll", EntryPoint = "GetKeyState")] private static extern short GetKeyStateNative(int vk);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRectNative(IntPtr hwnd, out RECT r);

    /// <summary>获取窗口矩形（物理像素）——用于与 GetCursorPos 做同坐标系比较</summary>
    public static bool GetWindowRect(IntPtr hwnd, out RECT r) => GetWindowRectNative(hwnd, out r);

    /// <summary>读取窗口扩展样式（诊断用）</summary>
    public static int GetExStyle(IntPtr hwnd) => GetWindowLong(hwnd, GWL_EXSTYLE);

    /// <summary>获取鼠标屏幕坐标</summary>
    public static bool GetCursorPos(out POINT p) => GetCursorPosNative(out p);

    /// <summary>获取按键状态（高位为 1 表示按下）</summary>
    public static short GetKeyState(int vk) => GetKeyStateNative(vk);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int cmd);

    /// <summary>把已运行的应用窗口带到前台</summary>
    public static void BringToForeground(IntPtr hwnd)
    {
        try
        {
            ShowWindowAsync(hwnd, 9);          // SW_RESTORE
            SetForegroundWindow(hwnd);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// 把窗口移到"主显示器"顶部并铺满其宽度（物理像素定位）。
    ///
    /// 为什么需要：多显示器（尤其含虚拟显示器/串流屏）时，
    /// WPF 的 PrimaryScreenWidth 与实际主显示器可能不一致，
    /// 导致 GetCursorPos（物理像素）与窗口坐标错位，悬停判定失效。
    /// </summary>
    public static void MoveToPrimaryMonitor(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            var pt = new POINT { X = 0, Y = 0 };
            var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);
            if (mon == IntPtr.Zero) return;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;

            // 用 SetWindowPos 按物理像素定位（左边界对齐主显示器）
            SetWindowPos(hwnd, new IntPtr(-1),
                mi.rcMonitor.Left, mi.rcMonitor.Top,
                mi.rcMonitor.Right - mi.rcMonitor.Left,
                Math.Max(420, mi.rcMonitor.Bottom - mi.rcMonitor.Top),
                SWP_NOACTIVATE);

            App.LogInfo("HitTest",
                $"岛窗口定位到主显示器：{mi.rcMonitor.Left},{mi.rcMonitor.Top} " +
                $"{mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top}");
        }
        catch (Exception ex) { App.Log("HitTest", ex); }
    }

    /// <summary>窗口居中于屏幕顶部</summary>
    public static void CenterTop(Window w)
    {
        var area = SystemParameters.WorkArea;
        w.Left = area.Left;
        w.Top = area.Top;
        w.Width = area.Width;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ChronoIsland.Native;

/// <summary>
/// 通知横幅抑制器。
///
/// 实现：定时轮询查找"通知横幅"窗口并隐藏它。
/// 通知本体仍在操作中心累积，只是不显示横幅。
///
/// 为何不用 SetWinEventHook：跨进程 OUTOFCONTEXT 钩子需要消息循环，
/// 且 UWP 横幅的 SHOW 事件不一定会派发到桌面应用，实测无回调。
/// 轮询虽然多几次调用，但可靠、可控。
/// </summary>
public static class ToastWatcher
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr param);

    private const int SW_HIDE = 0;

    public static bool Enabled { get; set; }
    public static bool Diagnose { get; set; }
    public static int SuppressedCount { get; private set; }

    private static System.Threading.Timer? _poll;

    /// <summary>通知横幅的窗口类名（不同 Windows 版本）</summary>
    private static readonly string[] BannerClasses =
    {
        "XamlExplorerHostIslandWindow",       // Win11 22H2+ 通知横幅（explorer 托管）
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "Xaml_WindowedPopupClass",
        "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow"
    };

    /// <summary>允许的宿主进程（防止误伤普通 UWP 应用）</summary>
    private static readonly string[] HostProcesses =
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SystemSettings",
        "Widgets", "WidgetService", "ShellHost"
    };

    public static void Start()
    {
        if (_poll != null) return;
        _poll = new System.Threading.Timer(_ => Scan(), null,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(120));
        App.LogInfo("ToastWatcher", "横幅抑制器已启动（轮询模式）");
    }

    public static void Stop()
    {
        _poll?.Dispose();
        _poll = null;
    }

    private static void Scan()
    {
        if (!Enabled && !Diagnose) return;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    var sb = new StringBuilder(256);
                    GetClassName(hwnd, sb, sb.Capacity);
                    var cls = sb.ToString();

                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0) return true;
                    string proc;
                    try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }

                    GetWindowRect(hwnd, out var rc0);
                    int w0 = rc0.Right - rc0.Left, h0 = rc0.Bottom - rc0.Top;

                    if (Diagnose)
                    {
                        // 诊断：只报可能相关的（Shell/Xaml/小窗口），避免噪音
                        bool interesting = proc.Contains("ShellExperience", StringComparison.OrdinalIgnoreCase)
                            || proc.Contains("explorer", StringComparison.OrdinalIgnoreCase)
                            || cls.Contains("Xaml", StringComparison.OrdinalIgnoreCase)
                            || cls.Contains("Island", StringComparison.OrdinalIgnoreCase)
                            || cls.Contains("Notify", StringComparison.OrdinalIgnoreCase)
                            || cls.Contains("Flyout", StringComparison.OrdinalIgnoreCase)
                            || (w0 is > 200 and < 900 && h0 is > 60 and < 400);   // 横幅尺寸范围
                        if (interesting)
                            App.LogInfo("ToastDiag", $"窗口: 类={cls} 进程={proc} 尺寸={w0}x{h0} 标题长={GetWindowTextLength(hwnd)}");
                    }

                    bool classMatch = false;
                    foreach (var c in BannerClasses)
                        if (cls.Contains(c, StringComparison.OrdinalIgnoreCase)) { classMatch = true; break; }
                    if (!classMatch) return true;

                    bool hostMatch = false;
                    foreach (var h in HostProcesses)
                        if (proc.Contains(h, StringComparison.OrdinalIgnoreCase)) { hostMatch = true; break; }
                    if (!hostMatch) return true;      // 非系统宿主的窗口不处理

                    // 横幅特征：无标题 + 位于屏幕右下/右上区域
                    if (GetWindowTextLength(hwnd) > 0) return true;

                    if (Enabled)
                    {
                        ShowWindow(hwnd, SW_HIDE);
                        SuppressedCount++;
                        App.LogInfo("ToastWatcher", $"已隐藏通知横幅（{proc}/{cls}），累计 {SuppressedCount}");
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }
}

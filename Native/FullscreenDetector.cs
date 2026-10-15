using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChronoIsland.Native;

/// <summary>
/// 游戏 / 全屏检测（docs §8）。
///
/// 关键：必须区分「真全屏」与「最大化窗口」。
/// Windows 上最大化窗口的 GetWindowRect 会超出屏幕边界（含不可见边框），
/// 若只用矩形比较会把浏览器/资源管理器误判为全屏，导致岛被错误隐藏。
/// 因此这里额外要求：窗口未被最大化(IsZoomed=false) 且 无标题栏(WS_CAPTION 缺失)。
/// </summary>
public static class FullscreenDetector
{
    // ---- DllImport ----
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // QUERY_USER_NOTIFICATION_STATE
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;

    /// <summary>内置游戏进程表</summary>
    private static readonly HashSet<string> GameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "gameoverlayui", "EADesktop", "EpicGamesLauncher",
        "RiotClientUx", "LeagueClient", "VALORANT", "Overwatch", "csgo", "cs2",
        "dota2", "PUBG", "GTA5", "RDR2", "Cyberpunk2077", "EldenRing", "Minecraft",
        "Wow", "ffxiv", "GenshinImpact", "Houkai", "StarRail", "Wuthering Waves"
    };

    /// <summary>检测结果变化（true = 进入全屏/游戏）</summary>
    public static event Action<bool>? FullscreenChanged;

    private static bool _last;
    private static Timer? _timer;

    public static bool IsFullscreenNow() => Detect();

    /// <summary>
    /// 检测是否处于真全屏 / 游戏。任一手段命中即为真。
    /// </summary>
    private static bool Detect()
    {
        try
        {
            // 1) Shell 状态：只认 D3D 独占全屏 / 演示模式 —— 最可靠。
            //    【重要】不再接受 QUNS_BUSY：截图工具（微信/QQ 截图）、录屏软件的
            //    透明全屏覆盖层会让系统报告 BUSY，导致岛被错误隐藏。
            if (SHQueryUserNotificationState(out int st) == 0)
            {
                if (st == QUNS_RUNNING_D3D_FULL_SCREEN || st == QUNS_PRESENTATION_MODE)
                    return true;
            }

            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == GetShellWindow() || fg == GetDesktopWindow()) return false;

            // 排除自身进程：岛窗口置顶时会成为前台，否则会自我误判为"全屏"
            if (GetWindowThreadProcessId(fg, out uint selfPid) != 0 &&
                selfPid == (uint)Environment.ProcessId)
                return false;

            // 最小化窗口不可能全屏
            if (IsIconic(fg)) return false;

            // 【关键】最大化窗口不是全屏 —— 排除它，避免点浏览器/资源管理器就误隐藏
            bool maximized = IsZoomed(fg);

            // 3) 进程白名单（与窗口特征无关，独立判断）
            if (GetWindowThreadProcessId(fg, out uint pid) != 0 && pid != 0)
            {
                try
                {
                    var name = Process.GetProcessById((int)pid).ProcessName;
                    if (GameProcesses.Contains(name)) return true;
                }
                catch { }
            }

            if (maximized) return false;      // 最大化 → 不是全屏，到此为止

            // 2) 前台窗口边界覆盖其所在显示器，且无标题栏 → 真全屏
            var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            if (!GetWindowRect(fg, out var r)) return false;

            bool coversMonitor =
                r.Left <= mi.rcMonitor.Left + 2 && r.Top <= mi.rcMonitor.Top + 2 &&
                r.Right >= mi.rcMonitor.Right - 2 && r.Bottom >= mi.rcMonitor.Bottom - 2;
            if (!coversMonitor) return false;

            // 有标题栏的窗口即使铺满屏幕也不算全屏（那是被拖到最大或特殊布局）
            int style = GetWindowLong(fg, GWL_STYLE);
            bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
            if (hasCaption) return false;

            // 无标题栏 + 覆盖整屏：视为真全屏（QUNS_BUSY 不作为必要条件，
            // 因为正常应用也可能让系统报告 BUSY）
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Fullscreen", ex);
            return false;
        }
    }

    /// <summary>开始轮询（2s 兜底），仅在结果变化时触发事件</summary>
    public static void Start()
    {
        _timer ??= new Timer(_ =>
        {
            var cur = Detect();
            if (cur == _last) return;
            _last = cur;
            App.LogInfo("Fullscreen", cur ? "进入全屏/游戏 → 隐藏灵动岛" : "退出全屏 → 恢复灵动岛");
            FullscreenChanged?.Invoke(cur);
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public static void Stop() { _timer?.Dispose(); _timer = null; }

    /// <summary>手动复位（托盘"暂停/恢复"时调用，避免状态卡死）</summary>
    public static void Reset() { _last = Detect(); }
}

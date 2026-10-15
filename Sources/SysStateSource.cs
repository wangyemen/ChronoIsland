using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ChronoIsland.Core;

namespace ChronoIsland.Sources;

/// <summary>键位状态快照（docs §5.5）。Signal: capslock | numlock | scrolllock</summary>
public sealed record SysSnapshot(string Signal, bool On);

/// <summary>解锁事件载荷</summary>
public sealed record UnlockSnapshot(int AwaySeconds, DateTime At, IntPtr LastForegroundHwnd);

/// <summary>摄像头占用快照（docs §5.6）</summary>
public sealed record CameraSnapshot(bool Busy, string DeviceName);

/// <summary>
/// 系统状态源（docs §5.5）：
///   · CapsLock / NumLock / ScrollLock 翻转 → 发 Kind="sys" 事件
///   · 锁屏 / 休眠唤醒 → 发 Kind="unlock" 事件（离开时长 + 锁屏前焦点窗口）
///
/// 键位检测：优先 WH_KEYBOARD_LL 低级钩子（仅 3 键回调，&lt;1ms 返回，不阻塞输入线程）；
/// 钩子装失败自动降级 250ms 轮询 GetKeyState。
/// 解锁检测：1s 轮询 WTSGetActiveConsoleSessionId + WTSLock，无窗口依赖。
///
/// 值级忽略（关键语义）：忽略表键为 (signal, value)，过期自清 ——
/// 忽略"CapsLock=开"后，切到"关"会再次弹出（关是另一个值）；
/// 过期后按回"开"也重新弹出。
/// </summary>
public sealed class SysStateSource : IEventSource
{
    public string Key => "sys";
    /// <summary>优先级 2：高于时钟/时长(1)、低于通知(3) —— 状态翻转值得看见，但不抢通知。</summary>
    public int Priority => 2;

    private EventBus? _bus;
    private Settings _settings = new();

    // ---- 键位 ----
    private const int WH_KEYBOARD_LL = 13;
    private const int VK_CAPITAL = 0x14, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91;
    private IntPtr _hook = IntPtr.Zero;
    private bool _hookOk;
    private System.Threading.Timer? _keyTimer;
    private readonly object _keyLock = new();
    private bool _caps, _num, _scroll;

    // 低级钩子委托必须保持强引用（GC 回收会导致钩子崩溃）
    private LowLevelKeyboardProc _procRef;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint GetKeyboardType();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- 会话 ----
    private System.Threading.Timer? _sessTimer;
    private uint _lastSession = 0;
    private bool _lockedSinceLastCheck;
    private DateTime _lockStart;
    private IntPtr _lastFgHwnd = IntPtr.Zero;
    // 注意：WTSGetActiveConsoleSessionId 由 kernel32.dll 导出，不在 wtsapi32.dll
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GetDesktopWindow();

    // ---- 值级忽略表 ----
    private static readonly Dictionary<(string Sig, bool Val), DateTime> Ignored = new();
    private static readonly object IgnoredLock = new();

    public SysStateSource()
    {
        // 委托引用必须作为字段保存 —— 这是低级钩子的经典坑。
        _procRef = HookProc;
    }

    public void Start(EventBus bus)
    {
        _bus = bus;

        // 1. 键位：先试装 LL 钩子，失败则 250ms 轮询
        try
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _procRef, IntPtr.Zero, 0);
            _hookOk = _hook != IntPtr.Zero;
        }
        catch { _hookOk = false; }

        RefreshKeyState();
        if (_hookOk)
        {
            App.LogInfo("SysState", "LL 键盘钩子已安装（仅监听 3 键）");
        }
        else
        {
            App.LogInfo("SysState", "LL 钩子安装失败，降级 250ms 轮询 GetKeyState");
            _keyTimer = new System.Threading.Timer(_ => PollKeys(), null, 250, 250);
        }

        // 2. 会话：1s 轮询
        _lastSession = WTSGetActiveConsoleSessionId();
        _lockStart = DateTime.Now;
        _sessTimer = new System.Threading.Timer(_ => PollSession(), null, 1000, 1000);

        // 3. 摄像头：2s 轮询（共享探测，无需 MF/WMI，开销极低）
        _camTimer = new System.Threading.Timer(_ => PollCamera(), null, 1500, 2000);
    }

    public void Configure(Settings s)
    {
        _settings = s ?? new Settings();
    }

    // ================= 摄像头（docs §5.6） =================
    //
    // Windows 没有公开的"摄像头是否正在使用"API（硬件指示灯由驱动私有协议控制）。
    //
    // 0.0.1 的实现：Windows.Devices.Enumeration（WinRT）拿到每个摄像头的
    // DeviceInformation，再对每个设备做"只读共享探测"——
    // 以 0 访问权限 CreateFileW 打开设备的 \.\CxDevice 符号链接：
    //   · 成功 → 无人独占 → 立即关闭（不打断任何正在进行的采集）
    //   · ERROR_SHARING_VIOLATION(32) / ERROR_ACCESS_DENIED(5) → 有进程独占 → 使用中
    // 对外只暴露"开 / 关"两个值，复用同一套值级忽略与堆叠机制。

    private System.Threading.Timer? _camTimer;
    private readonly object _camLock = new();
    private bool _camBusy;
    private string _camDeviceName = "";
    private DateTime _camBusySince = DateTime.Now;
    private bool _camDiagDone;

    // 静态镜像：UI（球体 / 详情卡）在没有源实例引用时也要读实时状态
    private static readonly object CamStaticLock = new();
    private static bool CamBusyStatic;

    private const uint FILE_SHARE_READ = 0x00000001, FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3, FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const int ERROR_SHARING_VIOLATION = 32, ERROR_ACCESS_DENIED = 5;
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    /// <summary>枚举摄像头 → (显示名, 设备路径)</summary>
    private static List<(string Name, string Path)> EnumerateCameraDevices()
        => Native.CameraEnumerator.List();

    private static bool ProbeCameraFree(string devicePath, out string deviceName)
    {
        deviceName = "";
        try
        {
            var h = CreateFileW(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            deviceName = System.IO.Path.GetFileName(devicePath);
            if (h != InvalidHandle && h != IntPtr.Zero)
            {
                CloseHandle(h);
                return true;
            }
            int err = Marshal.GetLastWin32Error();
            return err == ERROR_SHARING_VIOLATION || err == ERROR_ACCESS_DENIED ? false : true;
        }
        catch { deviceName = System.IO.Path.GetFileName(devicePath); return true; }
    }

    private void PollCamera()
    {
        try
        {
            if (!_settings.Sys.Enabled || !_settings.Sys.Camera)
            {
                lock (_camLock) _camBusy = false;
                lock (CamStaticLock) CamBusyStatic = false;
                return;
            }

            var devices = EnumerateCameraDevices();
            if (!_camDiagDone)
            {
                _camDiagDone = true;
                App.LogInfo("SysState", devices.Count > 0
                    ? $"已接入 {devices.Count} 个摄像头：{string.Join("、", devices.Select(d => d.Name))}"
                    : "未检测到摄像头（该功能保持静默）");
            }

            if (devices.Count == 0) { SetCameraBusy(false, "未检测到摄像头"); return; }

            bool anyBusy = false;
            string firstName = "";
            foreach (var d in devices)
            {
                if (string.IsNullOrWhiteSpace(firstName)) firstName = d.Name;
                if (!ProbeCameraFree(d.Path, out _))
                {
                    anyBusy = true;
                    if (!string.IsNullOrWhiteSpace(d.Name)) firstName = d.Name;
                    break;
                }
            }
            SetCameraBusy(anyBusy, string.IsNullOrWhiteSpace(firstName) ? "摄像头" : firstName);
        }
        catch (Exception ex) { App.Log("SysState", ex); }
    }

    private void SetCameraBusy(bool busy, string deviceName)
    {
        lock (_camLock)
        {
            if (busy == _camBusy) return;
            _camBusy = busy;
            _camDeviceName = deviceName;
            if (busy) _camBusySince = DateTime.Now;
        }
        lock (CamStaticLock) CamBusyStatic = busy;

        if (!_settings.Sys.Enabled || !_settings.Sys.Camera) return;
        if (IsSnoozed("camera", busy) || IsIgnored("camera", busy)) return;

        App.LogInfo("SysState", busy ? $"摄像头被启用：{deviceName}" : "摄像头已释放");

        _bus?.Publish(new IslandEvent(
            Id: "camera",
            SourceKey: "sys",
            Kind: "camera",
            Priority: Priority + 1,
            Title: busy ? "摄像头已启用" : "摄像头已关闭",
            Subtitle: busy ? deviceName : "已不再录制",
            Payload: new CameraSnapshot(busy, deviceName),
            Policy: new IslandPolicy(
                TimeSpan.FromSeconds(Math.Max(busy ? 4 : 3, _settings.Sys.TtlSeconds)),
                Coalesce: true, Expandable: true, HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "sys", StealFocus = busy });
    }

    /// <summary>当前摄像头是否被占用（UI 读实时值）</summary>
    public static bool IsCameraBusy
    {
        get { lock (CamStaticLock) return CamBusyStatic; }
    }

    // ================= 值级忽略（UI 调用） =================

    /// <summary>忽略"信号=值"。该值 60s 内再变化不弹；其他值照常弹。</summary>
    public static void IgnoreValue(string signal, bool value)
    {
        lock (IgnoredLock) Ignored[(signal, value)] = DateTime.Now;
        App.LogInfo("SysState", $"值级忽略：{signal}={value}");
    }

    /// <summary>该"信号=值"是否处于忽略中（未过期）</summary>
    public static bool IsIgnored(string signal, bool value)
    {
        lock (IgnoredLock)
        {
            if (!Ignored.TryGetValue((signal, value), out var at)) return false;
            int expiry = App.CurrentSettings.Sys.IgnoreValueExpirySeconds;
            if (DateTime.Now - at < TimeSpan.FromSeconds(expiry)) return true;
            Ignored.Remove((signal, value));
            return false;
        }
    }

    // ================= 供 UI 用：实时键状态 / snooze =================

    /// <summary>当前某键的开/关状态（实时读 GetKeyState）</summary>
    public static bool IsOn(string signal)
    {
        int vk = signal switch
        {
            "capslock" => 0x14,
            "numlock" => 0x90,
            "scrolllock" => 0x91,
            _ => 0
        };
        if (vk == 0) return false;
        try { return (GetKeyStateProbe(vk) & 1) != 0; }
        catch { return false; }
    }

    [DllImport("user32.dll", EntryPoint = "GetKeyState")] private static extern short GetKeyStateProbe(int vk);

    private static readonly Dictionary<(string Sig, bool Val), DateTime> Snoozed = new();
    private static readonly object SnoozedLock = new();

    /// <summary>"稍后提醒"：120s 内同值不再弹。与 IgnoreValue 区别：ignore 60s 过期，snooze 120s 过期。</summary>
    public static void SnoozeValue(string signal, bool value)
    {
        lock (SnoozedLock) Snoozed[(signal, value)] = DateTime.Now;
        App.LogInfo("SysState", $"稍后提醒：{signal}={value}（120s）");
    }

    public static bool IsSnoozed(string signal, bool value)
    {
        lock (SnoozedLock)
        {
            if (!Snoozed.TryGetValue((signal, value), out var at)) return false;
            if (DateTime.Now - at < TimeSpan.FromSeconds(120)) return true;
            Snoozed.Remove((signal, value));
            return false;
        }
    }

    // ================= 键位 =================

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            int msg = (int)wParam;
            bool keyEvent = msg == 0x0100 || msg == 0x0101 || msg == 0x0104 || msg == 0x0105;
            if (keyEvent && (vk == VK_CAPITAL || vk == VK_NUMLOCK || vk == VK_SCROLL))
                RefreshKeyState();
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void PollKeys()
    {
        try { RefreshKeyState(); }
        catch (Exception ex) { App.Log("SysState", ex); }
    }

    private void RefreshKeyState()
    {
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        bool num = (GetKeyState(VK_NUMLOCK) & 1) != 0;
        bool scroll = (GetKeyState(VK_SCROLL) & 1) != 0;

        lock (_keyLock)
        {
            bool dcaps = caps != _caps, dnum = num != _num, dscroll = scroll != _scroll;
            _caps = caps; _num = num; _scroll = scroll;

            if (dcaps) EmitKey("capslock", caps, "大写锁定", "CapsLock");
            if (dnum) EmitKey("numlock", num, "数字键盘", "NumLock");
            if (dscroll) EmitKey("scrolllock", scroll, "滚动锁定", "ScrollLock");
        }
    }

    private void EmitKey(string signal, bool on, string zh, string en)
    {
        // 设置开关
        if (!_settings.Sys.Enabled) return;
        if (signal == "capslock" && !_settings.Sys.CapsLock) return;
        if (signal == "numlock" && !_settings.Sys.NumLock) return;
        if (signal == "scrolllock" && !_settings.Sys.ScrollLock) return;

        // 笔记本无小键盘 → 自动隐藏 NumLock
        if (signal == "numlock" && _settings.Sys.AutoHideNumlockNoNumpad && GetKeyboardType() == 10)
            return;

        // 稍后提醒（120s 内同值不再弹）
        if (IsSnoozed(signal, on))
        {
            App.LogInfo("SysState", $"{signal}={on} 处于稍后提醒期，不弹");
            return;
        }

        // 值级忽略（60s 过期自清）
        if (IsIgnored(signal, on))
        {
            App.LogInfo("SysState", $"{signal}={on} 处于忽略期，不弹");
            return;
        }

        int ttl = Math.Max(2, _settings.Sys.TtlSeconds);
        App.LogInfo("SysState", $"状态翻转：{en}={on} → 生成岛（{ttl}s）");
        _bus?.Publish(new IslandEvent(
            Id: signal,
            SourceKey: "sys",
            Kind: "sys",
            Priority: Priority,
            Title: zh,
            Subtitle: on ? "开" : "关",
            Payload: new SysSnapshot(signal, on),
            Policy: new IslandPolicy(TimeSpan.FromSeconds(ttl), Coalesce: true, Expandable: true,
                HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "sys", StealFocus = true });
    }

    // ================= 会话 =================

    private void PollSession()
    {
        try
        {
            uint sid = WTSGetActiveConsoleSessionId();
            // 锁屏期间 GetDesktopWindow 的会话不可用；用 WTS 查询判断
            bool locked = IsConsoleLocked();

            if (locked && !_lockedSinceLastCheck)
            {
                // 进入锁屏：记录时刻 + 锁屏前焦点
                _lockedSinceLastCheck = true;
                _lockStart = DateTime.Now;
                _lastFgHwnd = GetForegroundWindow();
                App.LogInfo("SysState", "检测到锁屏/休眠");
            }
            else if (!locked && _lockedSinceLastCheck)
            {
                _lockedSinceLastCheck = false;
                int away = (int)Math.Max(0, (DateTime.Now - _lockStart).TotalSeconds);
                _lockedSinceLastCheck = false;
                EmitUnlock(away);
            }
            _lastSession = sid;
        }
        catch (Exception ex) { App.Log("SysState", ex); }
    }

    /// <summary>控制台会话是否处于"断开"（锁屏/休眠唤醒前）状态。
    /// Win10/11 锁屏时会话 WTSConnectState = Disconnected(1)，解锁后 = Active(0)。</summary>
    private static bool IsConsoleLocked()
    {
        try
        {
            IntPtr data = IntPtr.Zero;
            uint bytes = 0;
            uint sid = WTSGetActiveConsoleSessionId();
            if (!WTSQuerySessionInformation(sid, 1 /*WTSConnectState*/, out data, out bytes)
                || bytes == 0) return false;
            int state = Marshal.ReadInt32(data);
            WTSFreeMemory(data);
            return state == 1;   // Disconnected
        }
        catch { return false; }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(uint sessionId, int infoClass, out IntPtr data, out uint bytes);
    private void EmitUnlock(int awaySeconds)
    {
        if (!_settings.Sys.Enabled || !_settings.Sys.Unlock) return;
        App.LogInfo("SysState", $"解锁进入桌面（离开 {awaySeconds}s）");
        _bus?.Publish(new IslandEvent(
            Id: "unlock",
            SourceKey: "sys",
            Kind: "unlock",
            Priority: Priority,
            Title: "已解锁",
            Subtitle: "进入桌面",
            Payload: new UnlockSnapshot(awaySeconds, DateTime.Now, _lastFgHwnd),
            Policy: new IslandPolicy(TimeSpan.FromSeconds(Math.Max(3, _settings.Sys.TtlSeconds)),
                Coalesce: true, Expandable: true, HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "sys", StealFocus = true });
    }

    // ================= 供 UI 用 =================

    /// <summary>解锁卡"快速聚焦"：把锁屏前焦点窗口带到前台（若还活着）</summary>
    public static bool FocusLastWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return SetForegroundWindow(hwnd); }
        catch { return false; }
    }

    public void Dispose()
    {
        _keyTimer?.Dispose();
        _sessTimer?.Dispose();
        _camTimer?.Dispose();
        if (_hookOk && _hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _procRef = null!;   // 解除引用（钩子已卸载）
    }
}

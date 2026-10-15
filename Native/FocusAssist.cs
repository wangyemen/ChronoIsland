using System;
using System.Runtime.InteropServices;

namespace ChronoIsland.Native;

/// <summary>
/// 专注助手（勿扰）控制 —— 通过 WNF（Windows Notification Facility）状态写入。
///
/// 这是社区验证可行的方法（AutoDarkMode、ElevenClock 等项目采用）：
/// Windows 的专注助手开关对应一个 WNF 状态变量
///   WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
/// 写入 0 = 关闭、1 = 仅优先通知、2 = 仅闹钟。
///
/// 为什么不用其它方案（均已实测无效）：
///   · NOC_GLOBAL_SETTING_TOASTS_ENABLED 注册表 —— Win11 22H2+ 已不控制横幅
///   · EnumWindows + ShowWindow —— 横幅不是 Win32 窗口，探测期间 0 匹配
///   · FocusSessionManager（官方 API）—— 需要 MSIX 包标识，桌面应用不可用
///
/// 设为"仅优先通知(1)"后：普通通知不弹横幅、但仍进入操作中心，
/// 这正是本项目需要的效果。
/// </summary>
public static class FocusAssist
{
    // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
    private static readonly ulong WNF_QUIETHOURS =
        0x0D83063EA3810465UL;

    private const int PROFILE_OFF = 0;
    private const int PROFILE_PRIORITY_ONLY = 1;
    private const int PROFILE_ALARMS_ONLY = 2;

    [DllImport("ntdll.dll")]
    private static extern int NtUpdateWnfStateData(
        ref ulong stateName,
        IntPtr buffer,
        int length,
        IntPtr typeId,
        IntPtr explicitScope,
        int matchingChangeStamp,
        int checkStamp);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(
        ref ulong stateName,
        IntPtr typeId,
        IntPtr explicitScope,
        out int changeStamp,
        IntPtr buffer,
        ref int bufferSize);

    private static bool _applied;

    /// <summary>写入专注助手配置</summary>
    public static bool SetProfile(int profile)
    {
        try
        {
            var name = WNF_QUIETHOURS;
            var buf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buf, profile);
                int status = NtUpdateWnfStateData(ref name, buf, sizeof(int),
                    IntPtr.Zero, IntPtr.Zero, 0, 0);
                if (status == 0)
                {
                    App.LogInfo("FocusAssist", $"专注助手已设为 {(profile == 0 ? "关闭" : profile == 1 ? "仅优先通知" : "仅闹钟")}（WNF 写入成功）");
                    return true;
                }
                App.LogInfo("FocusAssist", $"WNF 写入失败，NTSTATUS = 0x{status:X8}");
                return false;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { App.Log("FocusAssist", ex); return false; }
    }

    /// <summary>读取当前专注助手配置（-1 表示读取失败）</summary>
    public static int GetProfile()
    {
        try
        {
            var name = WNF_QUIETHOURS;
            var buf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                int size = sizeof(int);
                int status = NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero,
                    out _, buf, ref size);
                if (status == 0 && size >= sizeof(int)) return Marshal.ReadInt32(buf);
                return -1;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return -1; }
    }

    /// <summary>开启"仅优先通知"：不弹普通横幅，通知仍进操作中心</summary>
    public static bool Enable()
    {
        bool ok = SetProfile(PROFILE_PRIORITY_ONLY);
        if (ok) _applied = true;
        return ok;
    }

    /// <summary>关闭专注助手</summary>
    public static bool Disable()
    {
        bool ok = SetProfile(PROFILE_OFF);
        if (ok) _applied = false;
        return ok;
    }

    /// <summary>还原（进程退出时调用，仅当本程序开启过）</summary>
    public static void Restore()
    {
        if (!_applied) return;
        SetProfile(PROFILE_OFF);
        App.LogInfo("FocusAssist", "已还原专注助手为关闭");
        _applied = false;
    }

    /// <summary>是否可用（能读到状态即说明支持）</summary>
    public static bool IsSupported() => GetProfile() >= 0;
}

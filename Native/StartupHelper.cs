using System;
using Microsoft.Win32;

namespace ChronoIsland.Native;

/// <summary>开机自启（HKCU Run）。0.0.1 默认关闭，由用户在设置界面开启。</summary>
public static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "ChronoIsland";

    public static void SetLaunchOnStartup(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
        {
            var exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("无法获取程序路径");
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            try { key.DeleteValue(AppName); } catch { }
        }
    }

    public static bool IsLaunchOnStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }
}

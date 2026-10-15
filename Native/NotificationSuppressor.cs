using System;
using Microsoft.Win32;

namespace ChronoIsland.Native;

/// <summary>
/// 岛活跃期间抑制 Windows 原生通知框（docs §5.1）。
/// 【必须】写入前备份原值；退出/异常一律还原。提供 --restore-notifications 命令行入口。
/// </summary>
public static class NotificationSuppressor
{
    private const string KeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ValueName = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";
    private const string BackupFile = "notifications-backup.json";

    /// <summary>焦点助手（Quiet Hours）—— 现代 Windows 11 上真正抑制弹窗的开关</summary>
    private const string QuietHoursKey =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.notifications.quiethourssettings";

    private static string BackupPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChronoIsland", BackupFile);

    /// <summary>抑制原生通知（幂等；已有备份则不重复写）</summary>
    public static bool Suppress()
    {
        try
        {
            if (System.IO.File.Exists(BackupPath)) return true;   // 已抑制
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            object? prev = key.GetValue(ValueName);
            var backup = new { previous = prev?.ToString(), existed = prev != null, at = DateTime.Now };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BackupPath)!);
            System.IO.File.WriteAllText(BackupPath,
                System.Text.Json.JsonSerializer.Serialize(backup));
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) { App.Log("Suppressor", ex); return false; }
    }

    /// <summary>还原为原始状态（退出时调用）</summary>
    public static bool Restore()
    {
        try
        {
            if (!System.IO.File.Exists(BackupPath)) return true;   // 没抑制过
            var json = System.IO.File.ReadAllText(BackupPath);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool existed = root.GetProperty("existed").GetBoolean();

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (existed)
            {
                var prev = root.GetProperty("previous").GetString();
                key.SetValue(ValueName, Convert.ToInt32(prev), RegistryValueKind.DWord);
            }
            else
            {
                try { key.DeleteValue(ValueName); } catch { }
            }
            System.IO.File.Delete(BackupPath);
            return true;
        }
        catch (Exception ex) { App.Log("Suppressor", ex); return false; }
    }

    /// <summary>命令行还原入口</summary>
    public static void RestoreFromCli()
    {
        var ok = Restore();
        Console.WriteLine(ok ? "已还原 Windows 原生通知设置。" : "还原失败，请查看日志。");
    }
}

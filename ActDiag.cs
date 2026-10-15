using System;
using System.Linq;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland;

public static class ActDiag
{
    public static void Run()
    {
        Console.WriteLine("=== 通知来源的激活方式诊断 ===\n");
        var l = UserNotificationListener.Current;
        var list = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        var seen = new System.Collections.Generic.HashSet<string>();

        foreach (var n in list)
        {
            string aumid = "", name = "";
            try { aumid = n.AppInfo?.AppUserModelId ?? ""; } catch { }
            try { name = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
            if (string.IsNullOrEmpty(aumid) || !seen.Add(aumid)) continue;

            Console.WriteLine($"[{name}]  AUMID = {aumid}");

            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{aumid}");
            if (key == null)
            {
                Console.WriteLine("  未在 HKCU 注册 → 无激活信息");
            }
            else
            {
                var act = key.GetValue("CustomActivator")?.ToString();
                var dn = key.GetValue("DisplayName")?.ToString();
                Console.WriteLine($"  DisplayName     = {dn ?? "(无)"}");
                Console.WriteLine($"  CustomActivator = {act ?? "(无) → 无法用此机制打开"}");
            }
            Console.WriteLine();
        }

        // 也列出所有已注册的 AUMID
        Console.WriteLine("=== HKCU 已注册的全部 AUMID ===");
        using var all = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        if (all != null)
            foreach (var n in all.GetSubKeyNames())
                Console.WriteLine("  " + n);
    }
}

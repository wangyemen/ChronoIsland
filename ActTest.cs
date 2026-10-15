using System;
using System.Linq;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland;

/// <summary>激活测试：dotnet run -- acttest
/// 用 IApplicationActivationManager 逐个尝试通知来源的 AUMID</summary>
public static class ActTest
{
    public static void Run()
    {
        var l = UserNotificationListener.Current;
        var list = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();

        var seen = new System.Collections.Generic.HashSet<string>();
        Console.WriteLine("=== AUMID 激活测试 ===");
        Console.WriteLine("（只测试不弹窗的应用，避免干扰）\n");

        foreach (var n in list)
        {
            string aumid = "", name = "";
            try { aumid = n.AppInfo?.AppUserModelId ?? ""; } catch { }
            try { name = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
            if (string.IsNullOrEmpty(aumid) || !seen.Add(aumid)) continue;

            Console.WriteLine($"[{name}]");
            Console.WriteLine($"  AUMID: {aumid}");

            // 真的调用 ActivateApplication 验证
            bool ok = Native.AppLauncher.ActivateByAumid(aumid, out uint pid);
            Console.WriteLine("  ActivateApplication: " + (ok ? $"成功 (PID {pid}) ✓" : "失败 ✗"));
            if (!ok)
            {
                bool shellOk = Native.AppLauncher.ActivateViaShell(aumid);
                Console.WriteLine("  shell:AppsFolder  : " + (shellOk ? "已尝试 ✓" : "失败 ✗"));
            }
            Console.WriteLine();
        }
    }
}

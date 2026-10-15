using System;
using System.Linq;
using System.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland;

/// <summary>接管测试：dotnet run -- takeover [秒]
/// 1. 记录通知中心条数
/// 2. 你发一条通知
/// 3. 检查是否被移除，并打印岛收到的内容</summary>
public static class TakeoverTest
{
    public static void Run(int seconds = 25)
    {
        var l = UserNotificationListener.Current;
        var st = l.RequestAccessAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("权限: " + st);
        if (st != UserNotificationListenerAccessStatus.Allowed) return;

        var before = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        var beforeIds = before.Select(x => x.Id).ToHashSet();
        Console.WriteLine($"起始通知数: {before.Count}");
        Console.WriteLine();
        Console.WriteLine($"请在 {seconds} 秒内发送通知（QQ/微信/任意）...");
        Console.WriteLine();

        var start = DateTimeOffset.Now;
        var deadline = DateTime.Now.AddSeconds(seconds);
        int captured = 0, removed = 0;

        while (DateTime.Now < deadline)
        {
            var list = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
            foreach (var n in list.OrderBy(x => x.CreationTime))
            {
                if (beforeIds.Contains(n.Id) || n.CreationTime < start) continue;
                beforeIds.Add(n.Id);
                captured++;

                string app = "?", txt = "";
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { }
                try
                {
                    var b = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                    txt = string.Join(" / ", b?.GetTextElements()?.Select(e => e.Text)
                        .Where(e => !string.IsNullOrWhiteSpace(e)) ?? Array.Empty<string>());
                }
                catch { }

                var now = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
                bool stillThere = now.Any(x => x.Id == n.Id);
                if (!stillThere) removed++;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {app} | {txt}");
                Console.WriteLine($"            系统通知中心: {(stillThere ? "仍存在 ✗" : "已被移除 ✓")}");
            }
            Thread.Sleep(300);
        }

        var after = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"捕获到: {captured} 条");
        Console.WriteLine($"已被移除: {removed} 条");
        Console.WriteLine($"通知中心: {before.Count} → {after.Count} 条");
    }
}

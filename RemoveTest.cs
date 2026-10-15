using System;
using System.Linq;
using System.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland;

/// <summary>验证 RemoveNotification API：捕获新通知后立即从通知中心移除</summary>
public static class RemoveTest
{
    public static void Run(int seconds = 25)
    {
        Console.WriteLine("=== RemoveNotification 测试 ===");
        var listener = UserNotificationListener.Current;
        var status = listener.RequestAccessAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("权限: " + status);
        if (status != UserNotificationListenerAccessStatus.Allowed) return;

        // 检查 RemoveNotification 方法是否存在
        var mi = typeof(UserNotificationListener).GetMethod("RemoveNotification");
        Console.WriteLine("RemoveNotification 方法: " + (mi == null ? "不存在 ✗" : "存在 ✓ " + mi.GetParameters()[0].ParameterType.Name));
        Console.WriteLine();

        var before = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"当前通知中心有 {before.Count} 条");

        // 直接测试：移除通知中心里最新的一条，验证 API 真的管用
        var newest = before.OrderByDescending(n => n.CreationTime).FirstOrDefault();
        if (newest != null)
        {
            string a = "?", ti = "";
            try { a = newest.AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { }
            try
            {
                var bd = newest.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                ti = bd?.GetTextElements()?.FirstOrDefault()?.Text ?? "";
            }
            catch { }
            Console.WriteLine($"尝试移除最新一条: {a} | {ti} (id={newest.Id})");
            try
            {
                listener.RemoveNotification(newest.Id);
                Console.WriteLine("  → RemoveNotification 调用成功");
            }
            catch (Exception ex) { Console.WriteLine("  → 失败: " + ex.GetType().Name + ": " + ex.Message); }

            System.Threading.Thread.Sleep(800);
            var afterRemove = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"  移除后剩余: {afterRemove.Count} 条（移除前 {before.Count} 条）");
            Console.WriteLine(afterRemove.Count < before.Count ? "→ 移除生效 ✓" : "→ 移除未生效 ✗");
        }

        Console.WriteLine();
        var seen = new System.Collections.Generic.HashSet<uint>(before.Select(n => n.Id));
        Console.WriteLine($"继续监控新通知 {seconds} 秒 —— 请现在发一条 QQ 消息\n");

        var start = DateTimeOffset.Now;
        var deadline = DateTime.Now.AddSeconds(seconds);
        int removed = 0;

        while (DateTime.Now < deadline)
        {
            try
            {
                var list = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
                foreach (var n in list.OrderBy(x => x.CreationTime))
                {
                    if (!seen.Add(n.Id)) continue;
                    if (n.CreationTime < start) continue;

                    string app = "?", title = "";
                    try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { }
                    try
                    {
                        var b = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                        title = b?.GetTextElements()?.FirstOrDefault()?.Text ?? "";
                    }
                    catch { }

                    Console.WriteLine($"[捕获] {DateTime.Now:HH:mm:ss} {app} | {title}");

                    // 立即从通知中心移除
                    try
                    {
                        listener.RemoveNotification(n.Id);
                        removed++;
                        Console.WriteLine($"         → 已从通知中心移除 (id={n.Id})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"         → 移除失败: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("轮询异常: " + ex.Message); }
            Thread.Sleep(600);
        }

        var after = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine($"共捕获并移除: {removed} 条");
        Console.WriteLine($"通知中心剩余: {after.Count} 条（捕获前 {before.Count} 条）");
        Console.WriteLine(removed > 0 ? "→ 即时移除方案可用 ✓" : "→ 未捕获到通知");
    }
}

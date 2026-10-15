using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland;

/// <summary>通知诊断：dotnet run -- notifdiag [秒数]
/// 实时轮询通知列表，打印新增项 —— 用于验证"收到消息能否读到"</summary>
public static class NotifyDiag
{
    public static void Run(int seconds = 30)
    {
        Console.WriteLine("=== 通知实时监控 ===");
        var listener = UserNotificationListener.Current;
        var status = listener.RequestAccessAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("权限: " + status);
        if (status != UserNotificationListenerAccessStatus.Allowed)
        {
            Console.WriteLine("未授权，中止");
            return;
        }

        var seen = new System.Collections.Generic.HashSet<uint>();
        // 先记录已有通知（不推送）
        var initial = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        foreach (var n in initial) seen.Add(n.Id);
        Console.WriteLine($"已有通知 {initial.Count} 条（已跳过）");
        Console.WriteLine($"开始监控 {seconds} 秒，请现在发一条 QQ/微信消息...");
        Console.WriteLine();

        var start = DateTimeOffset.Now;
        var deadline = DateTime.Now.AddSeconds(seconds);
        int found = 0;

        while (DateTime.Now < deadline)
        {
            try
            {
                var list = listener.GetNotificationsAsync(NotificationKinds.Toast)
                    .AsTask().GetAwaiter().GetResult();
                foreach (var n in list.OrderBy(x => x.CreationTime))
                {
                    if (!seen.Add(n.Id)) continue;
                    if (n.CreationTime < start) continue;

                    found++;
                    string app = "?", title = "", body = "";
                    try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { }
                    try
                    {
                        var binding = n.Notification?.Visual
                            ?.GetBinding(KnownNotificationBindings.ToastGeneric);
                        var texts = binding?.GetTextElements()?.Select(e => e.Text)
                            .Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? Array.Empty<string>();
                        title = texts.ElementAtOrDefault(0) ?? "";
                        body = texts.Length > 1 ? string.Join(" / ", texts.Skip(1)) : "";
                    }
                    catch { }
                    Console.WriteLine($"  [新 #{found}] {DateTime.Now:HH:mm:ss}  {app} | {title} | {body}");
                }
            }
            catch (Exception ex) { Console.WriteLine("  轮询异常: " + ex.Message); }
            Thread.Sleep(800);
        }
        Console.WriteLine();
        Console.WriteLine($"监控结束，共捕获 {found} 条新通知");
        Console.WriteLine(found > 0 ? "→ 通知链路完全正常 ✓" : "→ 未捕获到（请确认发消息时程序在运行）");
    }
}

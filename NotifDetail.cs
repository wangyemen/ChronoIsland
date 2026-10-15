using System;
using System.Linq;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland;

/// <summary>通知详情诊断：dotnet run -- nfdetail
/// 打印每条通知的 AppInfo 全字段，用于确定"打开"该启动什么</summary>
public static class NotifDetail
{
    public static void Run()
    {
        var l = UserNotificationListener.Current;
        var list = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"通知总数: {list.Count}\n");

        int i = 0;
        foreach (var n in list.OrderByDescending(x => x.CreationTime))
        {
            if (++i > 8) break;
            Console.WriteLine($"--- [{i}] ---");
            try
            {
                Console.WriteLine("  Id          : " + n.Id);
                Console.WriteLine("  CreationTime: " + n.CreationTime.LocalDateTime);
                var ai = n.AppInfo;
                Console.WriteLine("  AppInfo     : " + (ai == null ? "null" : "OK"));
                if (ai != null)
                {
                    try { Console.WriteLine("    AppUserModelId : " + ai.AppUserModelId); } catch (Exception e) { Console.WriteLine("    AppUserModelId : 读取失败 " + e.GetType().Name); }
                    try { Console.WriteLine("    PackageFamilyName: " + ai.PackageFamilyName); } catch { }
                    try
                    {
                        var di = ai.DisplayInfo;
                        Console.WriteLine("    DisplayName : " + di?.DisplayName);
                        Console.WriteLine("    Description : " + di?.Description);
                    }
                    catch { }
                    try
                    {
                        var ico = ai.DisplayInfo?.GetLogo(new Windows.Foundation.Size(32, 32));
                        Console.WriteLine("    图标        : " + (ico == null ? "无" : "有"));
                    }
                    catch (Exception e) { Console.WriteLine("    图标        : " + e.GetType().Name); }
                }
                // 通知内容
                var b = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = b?.GetTextElements()?.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                Console.WriteLine("  文本        : " + string.Join(" | ", texts ?? new System.Collections.Generic.List<string>()));
            }
            catch (Exception ex) { Console.WriteLine("  异常: " + ex.Message); }
            Console.WriteLine();
        }
    }
}

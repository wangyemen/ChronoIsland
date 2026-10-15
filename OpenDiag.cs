using System;
using System.Linq;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ChronoIsland;

/// <summary>打开应用诊断：dotnet run -- opendbg
/// 列出每条通知的 AUMID 与推测的启动目标</summary>
public static class OpenDiag
{
    public static void Run()
    {
        var l = UserNotificationListener.Current;
        var list = l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"通知总数: {list.Count}\n");

        var groups = list.GroupBy(n =>
        {
            try { return n.AppInfo?.AppUserModelId ?? "(无 AUMID)"; } catch { return "(读取失败)"; }
        });

        foreach (var g in groups)
        {
            string name = "?";
            try { name = list.First(n => (n.AppInfo?.AppUserModelId ?? "") == g.Key).AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { }
            Console.WriteLine($"显示名: {name}");
            Console.WriteLine($"  AUMID: {g.Key}");
            Console.WriteLine($"  通知数: {g.Count()}");

            // 推测启动目标
            var aumid = g.Key;
            var exe = ExtractExe(aumid);
            Console.WriteLine($"  解析 exe: {exe ?? "(无)"}");
            if (exe != null)
            {
                Console.WriteLine($"    存在: {System.IO.File.Exists(exe)}");
                var stem = System.IO.Path.GetFileNameWithoutExtension(exe);
                var running = System.Diagnostics.Process.GetProcessesByName(stem);
                Console.WriteLine($"    进程名: {stem}  已运行实例: {running.Length}");
                foreach (var p in running)
                    Console.WriteLine($"      PID {p.Id}  有主窗口: {p.MainWindowHandle != IntPtr.Zero}");
            }
            Console.WriteLine();
        }
    }

    private static string? ExtractExe(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;
        var idx = aumid.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return null;
        var raw = aumid[..(idx + 4)];
        if (raw.StartsWith("{"))
        {
            var close = raw.IndexOf('}');
            if (close > 0 && close + 1 < raw.Length) raw = raw[(close + 1)..].TrimStart('\\');
        }
        if (!System.IO.Path.IsPathRooted(raw))
        {
            var full = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), raw);
            if (System.IO.File.Exists(full)) return full;
        }
        return raw;
    }
}

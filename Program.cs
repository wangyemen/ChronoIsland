using System;
using System.Threading;

namespace ChronoIsland;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "smoke")
        {
            bool settingsOnly = args.Length > 1 && args[1] == "settings";
            Smoke.Run(settingsOnly);
            Native.NotificationSuppressor.Restore();
            return;
        }
        if (args.Length > 0 && args[0] == "media")
        {
            MediaDiag.Run().GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0] == "lyrics")
        {
            if (args.Length > 1 && args[1] == "rotate")
            {
                LyricsDiag.RunRotation();
                return;
            }
            if (args.Length > 2)
            {
                // 可选第 4 个参数：时长（毫秒）。真实播放链路是带时长的，
                // 而时长参与匹配打分，不带它测出的结果会偏悲观。
                int durMs = 0;
                if (args.Length > 3) int.TryParse(args[3], out durMs);
                LyricsDiag.RunOne(args[1], args[2], durMs);
            }
            else LyricsDiag.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "hittest")
        {
            HitTestDiag.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "centertest")
        {
            CenterTest.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "testopen")
        {
            TestOpen.Run(args.Length > 1 ? args[1] : "Chrome");
            return;
        }
        if (args.Length > 0 && args[0] == "actdiag")
        {
            ActDiag.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "acttest")
        {
            ActTest.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "opendbg")
        {
            OpenDiag.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "takeover")
        {
            int sec = args.Length > 1 && int.TryParse(args[1], out var ts) ? ts : 25;
            TakeoverTest.Run(sec);
            return;
        }
        if (args.Length > 0 && args[0] == "nfdetail")
        {
            NotifDetail.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "rmtest")
        {
            int sec = args.Length > 1 && int.TryParse(args[1], out var rs) ? rs : 25;
            RemoveTest.Run(sec);
            return;
        }
        if (args.Length > 0 && args[0] == "wnf")
        {
            Console.WriteLine("=== WNF 专注助手测试 ===");
            Console.WriteLine("当前配置: " + Native.FocusAssist.GetProfile() + "  (0=关闭 1=仅优先 2=仅响铃)");
            Console.WriteLine("是否支持: " + Native.FocusAssist.IsSupported());
            Console.WriteLine();
            Console.WriteLine("开启专注辅助（仅优先通知）...");
            bool ok = Native.FocusAssist.Enable();
            Console.WriteLine("缁撴灉: " + ok);
            Console.WriteLine("寮€鍚悗閰嶇疆: " + Native.FocusAssist.GetProfile());
            if (ok)
            {
                Console.WriteLine();
                Console.WriteLine("请现在发送一条通知，观察是否还有提示音...");
                Console.WriteLine("30 秒后自动还原...");
                System.Threading.Thread.Sleep(30000);
                Native.FocusAssist.Restore();
                Console.WriteLine("已还原为: " + Native.FocusAssist.GetProfile());
            }
            return;
        }
        if (args.Length > 0 && args[0] == "probe")
        {
            int sec = args.Length > 1 && int.TryParse(args[1], out var ps) ? ps : 15;
            ToastProbe.Run(sec);
            return;
        }
        if (args.Length > 0 && args[0] == "focusdiag")
        {
            FocusDiag.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "e2e")
        {
            int sec = args.Length > 1 && int.TryParse(args[1], out var es) ? es : 25;
            E2E.Run(sec);
            return;
        }
        if (args.Length > 0 && args[0] == "notifdiag")
        {
            int sec = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 30;
            NotifyDiag.Run(sec);
            return;
        }
        if (args.Length > 0 && args[0] == "notify")
        {
            Console.WriteLine("=== 通知读取测试（强类型 API）===");
            try
            {
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
                var st = listener.RequestAccessAsync().AsTask().GetAwaiter().GetResult();
                Console.WriteLine("权限状态: " + st);

                var list = listener.GetNotificationsAsync(
                    Windows.UI.Notifications.NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
                Console.WriteLine("通知总数: " + list.Count);
                int i = 0;
                foreach (var n in list)
                {
                    if (++i > 6) break;
                    string appName = "?", title = "?";
                    try { appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "?"; } catch { }
                    try
                    {
                        var v = n.Notification?.Visual;
                        if (v != null)
                            foreach (var b in v.Bindings)
                            {
                                var t = b.GetTextElements()?.Select(x => x.Text)
                                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                                if (t is { Count: > 0 }) { title = t[0]!; break; }
                            }
                    }
                    catch { }
                    Console.WriteLine($"  [{i}] {appName} — {title}");
                }
                Console.WriteLine(list.Count > 0 ? "✓ 读取链路正常 ✔" : "✗ 无通知，请发一条测试消息");
            }
            catch (Exception ex)
            {
                Console.WriteLine("失败: " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("  内部: " + ex.InnerException.Message);
            }
            return;
        }
        if (args.Length > 0 && args[0] == "dsh")
        {
            int sec = 0;
            if (args.Length > 1 && args[1] == "watch")
                sec = args.Length > 2 && int.TryParse(args[2], out var ws) ? ws : 20;
            DshDiag.Run(sec);
            return;
        }
        if (args.Length > 0 && args[0] == "dshrender")
        {
            DshRenderDiag.Run(args.Length > 1 ? args[1] : "");
            return;
        }
        if (args.Length > 0 && (args[0] == "pillwidth" || args[0] == "widthdiag"))
        {
            PillWidthDiag.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "geom")
        {
            var s = Core.Settings.Load();
            Core.IslandMetrics.Apply(s);
            Console.WriteLine("=== 几何设置自检 ===");
            Console.WriteLine($"settings.json : Top={s.Island.Top}  MaxWidth={s.Island.MaxWidth}  Scale={s.Island.Scale}");
            Console.WriteLine($"metrics 实际值: Top={Core.IslandMetrics.TopMargin}  MaxWidth={Core.IslandMetrics.MaxWidth}  Scale={Core.IslandMetrics.Scale}");
            bool ok = Math.Abs(Core.IslandMetrics.TopMargin - s.Island.Top) < 0.01
                   && Math.Abs(Core.IslandMetrics.MaxWidth - s.Island.MaxWidth) < 0.01
                   && Math.Abs(Core.IslandMetrics.Scale - s.Island.Scale) < 0.01;
            Console.WriteLine(ok ? "同步: ✔ 生效" : "同步: ✗ 未生效");
            Console.WriteLine($"球体尺寸={Core.IslandMetrics.SphereSize}  胶囊高={Core.IslandMetrics.IslandHeight}  侧列上限={Core.IslandMetrics.MaxSideSpheres}");
            return;
        }
        if (args.Length > 0 && args[0] == "icon")
        {
            IconDiag.Run();
            return;
        }
        if (args.Length > 0 && args[0] == "--restore-notifications")
        {
            Native.NotificationSuppressor.RestoreFromCli();
            return;
        }
        // ===== 单实例守护 =====
        using var guard = new Core.SingleInstance();
        if (guard.IsAnotherRunning)
        {
            // 已有实例在运行：把它的设置窗口叫到前台，然后自己退出
            Core.SingleInstance.SignalExistingInstance();
            return;
        }

        var app = new App();
        app.SingleInstance = guard;

        // 任何退出路径都要还原原生通知设置
        app.Exit += (_, _) => Native.NotificationSuppressor.Restore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Native.NotificationSuppressor.Restore();
        app.InitializeComponent();
        app.Run();
    }
}


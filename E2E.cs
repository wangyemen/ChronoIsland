using System;
using System.Linq;
using System.Threading;
using ChronoIsland.Core;

namespace ChronoIsland;

/// <summary>端到端测试：dotnet run -- e2e [秒数]
/// 启动完整程序（岛 + 所有模块），统计收到的通知事件</summary>
public static class E2E
{
    public static int ReceivedNotifications;
    public static int ReceivedMedia;
    public static readonly System.Collections.Generic.List<string> Log = new();

    public static void Run(int seconds = 25)
    {
        var thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();
            var win = new UI.IslandWindow();
            win.Show();
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Console.WriteLine($"E2E 启动，监控 {seconds} 秒...");
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline) Thread.Sleep(500);

        Console.WriteLine();

        // 从日志文件统计实际捕获
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChronoIsland", "logs", "app.log");
            if (System.IO.File.Exists(logPath))
            {
                var lines = System.IO.File.ReadAllLines(logPath);
                var captured = lines.Count(l => l.Contains("捕获通知"));
                var suppressed = lines.Count(l => l.Contains("已隐藏通知横幅"));
                Console.WriteLine($"捕获通知: {captured} 次");
                Console.WriteLine($"隐藏横幅: {suppressed} 次");
                Console.WriteLine(captured > 0 ? "→ 通知接管链路正常 ✓" : "→ 未捕获到通知 ✗");
            }
        }
        catch { }

        Environment.Exit(0);
    }
}

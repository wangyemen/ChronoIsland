using System;
using System.Threading;
using System.Windows;
using ChronoIsland.UI;

namespace ChronoIsland;

/// <summary>冒烟入口：dotnet run -- smoke [settings]</summary>
public static class Smoke
{
    public static void Run(bool settingsOnly = false)
    {
        var thread = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();

            if (settingsOnly)
            {
                var s = new SettingsWindow(Core.Settings.Load());
                s.Show();
            }
            else
            {
                var win = new IslandWindow();
                win.Show();
                for (int i = 1; i <= 6; i++)
                    win.DebugPush("App" + i, "消息标题 " + i, "这是第 " + i + " 条副文本");

                var s = new SettingsWindow(Core.Settings.Load());
                s.Show();
            }
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Thread.Sleep(settingsOnly ? 5000 : 8000);
        Console.WriteLine("SMOKE_DONE");
        Environment.Exit(0);
    }
}

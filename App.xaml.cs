using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ChronoIsland.UI;
using System.Linq;

namespace ChronoIsland;

public partial class App : System.Windows.Application
{
    private IslandWindow? _window;
    private UI.TrayIcon? _tray;
    public static Core.Settings CurrentSettings { get; private set; } = new();

    /// <summary>单实例守卫（由 Program 注入）</summary>
    public Core.SingleInstance? SingleInstance { get; set; }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        CurrentSettings = Core.Settings.Load();
        ApplyTheme(CurrentSettings.Theme.Mode);

        // 全局异常：写日志并保持运行（单模块崩溃不拖垮全局）
        DispatcherUnhandledException += (_, ex) =>
        {
            Log("UI", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exc) Log("AppDomain", exc);
        };

        _window = new IslandWindow();
        _window.Show();

        // 托盘（开发版可见，docs §10）
        var icon = Native.IconHelper.LoadWindowIcon();
        if (icon != null) _window.Icon = icon;
        _tray = new UI.TrayIcon(_window, CurrentSettings);

        // 第二个实例被启动时 → 本实例打开设置窗口
        SingleInstance?.ShowSettingsRequested += () =>
            Dispatcher.InvokeAsync(() => _tray?.OpenSettings(CurrentSettings));

        // 启动后打开设置窗口（让用户能立刻配置；托盘菜单也可随时打开）
        if (CurrentSettings.General.OpenSettingsOnStart)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try { _tray.OpenSettings(CurrentSettings); }
                catch (Exception ex) { Log("Startup", ex); }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _tray?.Dispose();
        Native.NotificationSuppressor.Restore();
        base.OnExit(e);
    }

    /// <summary>应用主题：System / Dark / Light（同时刷新灵动岛与所有窗口）</summary>
    public static void ApplyTheme(string mode)
    {
        try
        {
            AppTheme.Apply(mode);
            var t = AppTheme.Current;

            // 更新全局资源，XAML 里用 DynamicResource 的控件会自动刷新
            var res = Current.Resources;
            void Set(string key, object val) { res[key] = val; }
            Set("WindowSurface", t.WindowSurface);
            Set("PanelSurface", t.PanelSurface);
            Set("RowSurface", t.RowSurface);
            Set("Track", t.Track);
            Set("Stroke", t.Stroke);
            Set("StrokeStrong", t.Stroke);
            Set("Chip", t.RowSurface);
            Set("Separator", t.Separator);
            Set("TextPrimary", t.TextPrimary);
            Set("TextSecondary", t.TextSecondary);
            Set("TextTertiary", t.TextTertiary);
            Set("Accent", t.Accent);
            Set("AccentGradient", t.AccentGradient);
            Set("Success", t.Success);
            Set("Warning", t.Warning);
            Set("Danger", t.Danger);
            // 兼容旧 key
            Set("Surface", t.WindowSurface);
            Set("SurfaceElevated", t.PanelSurface);

            LogInfo("Theme", $"主题已切换为 {(t.IsDark ? "深色" : "浅色")}（模式 {mode}）");
        }
        catch (Exception ex) { Log("Theme", ex); }
    }

    /// <summary>普通信息日志（诊断用）</summary>
    public static void LogInfo(string scope, string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChronoIsland", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{scope}] {message}\n",
                System.Text.Encoding.UTF8);
        }
        catch { }
    }

    internal static void Log(string scope, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChronoIsland", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "app.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{scope}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n",
                System.Text.Encoding.UTF8);
        }
        catch { /* 日志失败不影响运行 */ }
    }
}

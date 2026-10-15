using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ChronoIsland.Core;
using ChronoIsland.Native;

namespace ChronoIsland.UI;

/// <summary>
/// 系统托盘图标（docs §10）。右键：打开设置 / 预览 / 暂停 / 退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _ni;
    private readonly IslandWindow _island;
    private SettingsWindow? _settingsWindow;
    private bool _paused;

    public TrayIcon(IslandWindow island, Settings settings)
    {
        _island = island;

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => OpenSettings(settings));
        menu.Items.Add("预览灵动岛", null, (_, _) =>
            island.Dispatcher.Invoke(() => island.DebugPush("WeCom", "张伟", "这是一条预览通知")));
        menu.Items.Add(new ToolStripSeparator());
        var pauseItem = menu.Items.Add("暂停显示", null, (_, _) => TogglePause());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogs());
        menu.Items.Add("图标信息", null, (_, _) => ShowIconInfo());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());

        _ni = new NotifyIcon
        {
            Icon = IconHelper.LoadTrayIcon(),
            Text = "ChronoIsland 时序岛",
            Visible = true,
            ContextMenuStrip = menu
        };
        _ni.DoubleClick += (_, _) => OpenSettings(settings);
        _pauseItem = pauseItem;
    }

    private readonly ToolStripItem _pauseItem;

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Text = _paused ? "恢复显示" : "暂停显示";
        _island.Dispatcher.Invoke(() => { if (_paused) _island.Hide(); });
    }

    public void OpenSettings(Settings settings)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_settingsWindow is { IsLoaded: true })
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        });
    }

    /// <summary>显示当前图标来源（排查"图标不显示"）</summary>
    private static void ShowIconInfo()
    {
        var file = IconHelper.FindIconFile();
        var msg = file == null
            ? "未找到 icon.ico，正在使用内置矢量图标。\n\n期望位置：\n" +
              System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico") +
              "\n或\nE:\\ChronoIsland\\assets\\icon.ico"
            : "图标来源：\n" + file;
        System.Windows.MessageBox.Show(msg, "ChronoIsland 图标",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private static void OpenLogs()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChronoIsland", "logs");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex) { App.Log("Tray", ex); }
    }

    private void Exit()
    {
        // 退出前必须还原原生通知设置
        NotificationSuppressor.Restore();
        _ni.Visible = false;
        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    public void Dispose()
    {
        _ni.Visible = false;
        _ni.Dispose();
    }
}

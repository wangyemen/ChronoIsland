using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChronoIsland.Core;
using ChronoIsland.Native;
using ChronoIsland.Sources;

namespace ChronoIsland.UI;

/// <summary>
/// 设置窗口：左侧导航 + 右侧内容（与样式原型一致）。
/// 与灵动岛共用同一套视觉 token（AppTheme）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer? _dataTimer;
    private bool _loading = true;
    private string _page = "general";

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); ShowStatus("已保存"); };

        Loaded += (_, _) =>
        {
            AnimateIn();
            ApplyIcon();
            Nav.SelectedIndex = 0;      // 触发页面渲染
        };

        // 数据页每 5 秒刷新
        _dataTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _dataTimer.Tick += (_, _) => { if (_page == "usage") RenderPage(); };
        _dataTimer.Start();
        Closed += (_, _) => _dataTimer.Stop();

        // 主题变化时重绘
        AppTheme.Changed += _ => Dispatcher.InvokeAsync(() => { RefreshChrome(); RenderPage(); });

        Closing += OnClosing;
    }

    // ================= 页面路由 =================
    private void Nav_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.SelectedItem is not ListBoxItem item) return;
        var tag = item.Tag?.ToString() ?? "general";

        // 三个 ListBox 互斥选中
        foreach (var other in new[] { Nav, NavFunc, NavLook })
            if (!ReferenceEquals(other, lb)) other.SelectedItem = null;

        if (_page == "theme" && tag != "theme") StopPreviewIfActive();
        _page = tag;
        RenderPage();
    }

    private void RenderPage()
    {
        _loading = true;                 // 重建期间禁止回写
        try { RenderPageCore(); }
        finally { _loading = false; }
    }

    private void RenderPageCore()
    {
        var (title, sub, content) = _page switch
        {
            "clock" => ("时间日期", "浮现时机与显示格式", BuildClockPage()),
            "usage" => ("使用时长", "数据仅存本地", BuildUsagePage()),
            "media" => ("媒体控制", "会话选择与显示行为", BuildMediaPage()),
            "notify" => ("通知接管", "在灵动岛显示系统通知", BuildNotifyPageWithQuery()),
            "sys" => ("系统状态", "键位翻转 · 解锁进入桌面 · 摄像头占用", BuildSysPage()),
            "theme" => ("主题与材质", "深浅色与玻璃效果", BuildThemePage()),
            "anim" => ("动画", "弹簧物理参数", BuildAnimPage()),
            "about" => ("关于", "版本与资源", BuildAboutPage()),
            _ => ("常规", "开机自启与基础行为", BuildGeneralPage())
        };
        PageTitle.Text = title;
        PageSub.Text = sub;
        PageHost.Content = content;
        UpdateNotifyBadge();
    }

    private void UpdateNotifyBadge()
    {
        // 只有"已启用接管"且"确实没有权限"时才显示角标
        if (!_settings.Notification.Enabled)
        {
            NotifyBadge.Visibility = Visibility.Collapsed;
            return;
        }
        bool granted = Sources.NotificationSource.LastKnownAccess ?? false;
        NotifyBadge.Visibility = granted ? Visibility.Collapsed : Visibility.Visible;
        NotifyBadgeText.Text = "!";
    }

    // ================= 通用构建块 =================
    private Border Group(string title, params UIElement[] children)
    {
        var sp = new StackPanel();
        if (!string.IsNullOrEmpty(title))
        {
            var h = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            h.Children.Add(new Border
            {
                Width = 3, Height = 13, CornerRadius = new CornerRadius(2),
                Background = AppTheme.Current.AccentGradient, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            h.Children.Add(new TextBlock
            {
                Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Current.TextPrimary, VerticalAlignment = VerticalAlignment.Center
            });
            sp.Children.Add(h);
        }
        foreach (var c in children) sp.Children.Add(c);
        return new Border
        {
            Style = (Style)FindResource("Group"), Child = sp
        };
    }

    /// <summary>一行设置：左侧标题+说明，右侧控件</summary>
    private Border Row(string label, string hint, FrameworkElement? control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        { Text = label, FontSize = 12.5, Foreground = AppTheme.Current.TextPrimary });
        if (!string.IsNullOrEmpty(hint))
            left.Children.Add(new TextBlock
            {
                Text = hint, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
                Foreground = AppTheme.Current.TextTertiary, TextWrapping = TextWrapping.Wrap
            });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        return new Border { Style = (Style)FindResource("Row"), Child = grid };
    }

    private CheckBox Toggle(bool value, Action<bool> changed)
    {
        var cb = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = value };
        cb.Checked += (_, _) => { if (_loading) return; changed(true); _settings.Save(); };
        cb.Unchecked += (_, _) => { if (_loading) return; changed(false); _settings.Save(); };
        return cb;
    }

    private StackPanel SliderRow(string label, double val, double min, double max,
        string unit, Action<double> changed)
    {
        var valText = new TextBlock
        {
            Text = (max - min <= 1.5) ? $"{val:0.00} {unit}" : $"{val:0} {unit}", FontSize = 11,
            Foreground = AppTheme.Current.TextTertiary, Margin = new Thickness(0, 3, 0, 0)
        };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        { Text = label, FontSize = 12.5, Foreground = AppTheme.Current.TextPrimary });
        left.Children.Add(valText);

        bool fractional = max - min <= 1.5;          // 缩放这类小范围用小数步进
        var sl = new Slider
        {
            Minimum = min, Maximum = max, Value = val, Width = 150,
            IsSnapToTickEnabled = true,
            TickFrequency = fractional ? 0.05 : 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        sl.ValueChanged += (_, e) =>
        {
            valText.Text = fractional ? $"{e.NewValue:0.00} {unit}" : $"{e.NewValue:0} {unit}";
            if (_loading) return;
            changed(e.NewValue);
            // 立即落盘：不依赖防抖定时器，避免窗口关闭时丢改动
            _settings.Save();
            App.LogInfo("Settings", $"{label} = {e.NewValue}");
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0); grid.Children.Add(left);
        Grid.SetColumn(sl, 1); grid.Children.Add(sl);

        var wrap = new StackPanel();
        wrap.Children.Add(new Border { Style = (Style)FindResource("Row"), Child = grid });
        return wrap;
    }

    /// <summary>分段选择（主题、小时制等）</summary>
    private Border Segmented(string[] options, string current, Action<string> changed)
    {
        var host = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(3),
            Background = AppTheme.Current.RowSurface, VerticalAlignment = VerticalAlignment.Center
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var opt in options)
        {
            bool on = opt == current;
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5),
                Background = on ? AppTheme.Current.RowSurface : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = opt, FontSize = 12,
                    Foreground = on ? AppTheme.Current.TextPrimary : AppTheme.Current.TextTertiary,
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal
                }
            };
            b.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var child in sp.Children.OfType<Border>())
                {
                    if (child.Child is TextBlock tb)
                    {
                        tb.Foreground = AppTheme.Current.TextTertiary;
                        tb.FontWeight = FontWeights.Normal;
                    }
                    child.Background = Brushes.Transparent;
                }
                b.Background = AppTheme.Current.RowSurface;
                if (b.Child is TextBlock me)
                {
                    me.Foreground = AppTheme.Current.TextPrimary;
                    me.FontWeight = FontWeights.SemiBold;
                }
                changed(opt);
                _settings.Save();
            };
            sp.Children.Add(b);
        }
        host.Child = sp;
        return host;
    }

    private TextBlock Desc(string text) => new()
    {
        Text = text, FontSize = 11.5, Foreground = AppTheme.Current.TextTertiary,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
    };

    private TextBlock H4(string text) => new()
    {
        Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
        Foreground = AppTheme.Current.TextPrimary, Margin = new Thickness(0, 0, 0, 4)
    };

    // ================= 各页面 =================

    /// <summary>系统状态页（docs §5.5 / §5.6）</summary>
    private UIElement BuildSysPage()
    {
        var sp = new StackPanel();
        var sys = _settings.Sys;

        sp.Children.Add(Group("总开关",
            Row("启用系统状态提醒", "关闭后不监控任何键位 / 解锁 / 摄像头",
                Toggle(sys.Enabled, v => sys.Enabled = v))));

        sp.Children.Add(Group("键位状态", Desc("按键时在顶部弹一条胶囊，展开可看全部三个键的实时状态。忽略针对 \"信号 + 值\"：忽略 \"大写=开\" 后，切到 \"关\" 仍会再次弹出。"),
            Row("大写锁定 CapsLock", "翻转时提醒（CapsLock）",
                Toggle(sys.CapsLock, v => sys.CapsLock = v)),
            Row("数字键盘 NumLock", "翻转时提醒",
                Toggle(sys.NumLock, v => sys.NumLock = v)),
            Row("滚动锁定 ScrollLock", "极少使用，默认关闭",
                Toggle(sys.ScrollLock, v => sys.ScrollLock = v)),
            Row("无小键盘时隐藏 NumLock", "笔记本（GetKeyboardType()==10）不提醒 NumLock",
                Toggle(sys.AutoHideNumlockNoNumpad, v => sys.AutoHideNumlockNoNumpad = v)),
            SliderRow("状态胶囊停留时长", sys.TtlSeconds, 2, 10, "秒",
                v => sys.TtlSeconds = (int)v),
            SliderRow("忽略有效期", sys.IgnoreValueExpirySeconds, 15, 300, "秒",
                v => sys.IgnoreValueExpirySeconds = (int)v)));

        sp.Children.Add(Group("会话与隐私",
            Row("解锁进入桌面", "锁屏 / 休眠后解锁时显示欢迎卡",
                Toggle(sys.Unlock, v => sys.Unlock = v)),
            Row("摄像头占用提醒", "有程序开始 / 停止使用摄像头时提醒（隐私保护）",
                Toggle(sys.Camera, v => sys.Camera = v))));

        sp.Children.Add(Group("预览", Desc("立即在灵动岛弹一条示例，方便确认样式与交互（4 条各点一次）。"),
            PreviewRow("大写锁定", "capslock"),
            PreviewRow("数字键盘", "numlock"),
            PreviewRow("解锁进入桌面", "unlock"),
            PreviewRow("摄像头占用", "camera")));

        return sp;
    }

    /// <summary>预览行：点一下就在灵动岛弹一条示例岛</summary>
    private UIElement PreviewRow(string label, string signal)
    {
        var btn = new Button
        {
            Content = "预览",
            Height = 26,
            Padding = new Thickness(14, 0, 14, 0),
            MinWidth = 60,
            Style = (Style)FindResource("Pill"),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        btn.Click += (_, _) =>
        {
            var main = Application.Current.Windows.OfType<IslandWindow>().FirstOrDefault();
            main?.PreviewSysState(signal);
            ShowStatus("已弹出示例");
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        { Text = label, FontSize = 12.5, Foreground = AppTheme.Current.TextPrimary });
        sp.Children.Add(left);
        sp.Children.Add(btn);

        return new Border
        {
            Style = (Style)FindResource("Row"),
            Child = sp
        };
    }

    private UIElement BuildGeneralPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(Group("启动",
            Row("开机自启", "登录 Windows 后自动在托盘运行", Toggle(_settings.General.LaunchOnStartup, v =>
            {
                _settings.General.LaunchOnStartup = v;
                try { StartupHelper.SetLaunchOnStartup(v); ShowStatus(v ? "已设置开机自启" : "已取消"); }
                catch (Exception ex) { App.Log("Startup", ex); ShowStatus("设置失败"); }
            })),
            Row("启动时打开设置", "程序启动后自动显示本窗口", Toggle(_settings.General.OpenSettingsOnStart,
                v => _settings.General.OpenSettingsOnStart = v))));
        sp.Children.Add(Group("显示行为",
            Row("自动已读收起", "通知被查看后立即消失", Toggle(true, _ => { })),
            Row("悬停暂停计时", "指针在岛上时不进入倒计时", Toggle(true, _ => { })),
            SliderRow("默认显示时长", _settings.Notification.TtlSeconds, 2, 15, "秒",
                v => _settings.Notification.TtlSeconds = (int)v)));
        return sp;
    }

    private UIElement BuildClockPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(Group("显示格式",
            Row("小时制", "跟随系统区域设置", Segmented(new[] { "24 小时", "12 小时" },
                _settings.Clock.Hour24 ? "24 小时" : "12 小时", v => _settings.Clock.Hour24 = v == "24 小时"))));
        sp.Children.Add(Group("浮现时机", Desc("默认不常驻，只在特定时刻短暂出现"),
            Row("跨小时时浮现", "停留 3 秒后自动收起", Toggle(true, _ => { })),
            Row("跨天时浮现", "显示完整日期与星期", Toggle(true, _ => { })),
            Row("锁屏唤醒后浮现", "解锁时快速确认时间", Toggle(true, _ => { })),
            Row("常驻显示", "始终占据顶部一行", Toggle(_settings.Clock.Persistent,
                v => _settings.Clock.Persistent = v))));
        return sp;
    }

    private UIElement BuildUsagePage()
    {
        var sp = new StackPanel();
        var agg = UsageTracker.Load(DateTime.Today);
        var total = agg.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);

        // 统计卡
        var stats = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        for (int i = 0; i < 3; i++)
            stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cards = new (string K, string V, string? D, Brush? C)[]
        {
            ("今日总计", total > TimeSpan.Zero ? $"{(int)total.TotalHours}h {total.Minutes}m" : "0h 0m", null, AppTheme.Current.Success),
            ("本周平均", "—", null, null),
            ("收录应用", agg.Count.ToString(), null, null)
        };
        for (int i = 0; i < cards.Length; i++)
        {
            var (k, v, d, c) = cards[i];
            var box = new StackPanel();
            box.Children.Add(new TextBlock
            { Text = k, FontSize = 11, Foreground = AppTheme.Current.TextTertiary });
            box.Children.Add(new TextBlock
            {
                Text = v, FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0),
                Foreground = c ?? AppTheme.Current.TextPrimary
            });
            if (d != null) box.Children.Add(new TextBlock
            { Text = d, FontSize = 10.5, Foreground = AppTheme.Current.Success });
            var card = new Border
            {
                Background = AppTheme.Current.RowSurface, BorderBrush = AppTheme.Current.Stroke,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(15, 13, 15, 13), Child = box,
                Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0)
            };
            Grid.SetColumn(card, i);
            stats.Children.Add(card);
        }
        sp.Children.Add(stats);

        // 排行
        sp.Children.Add(H4("今日应用排行"));
        sp.Children.Add(Desc("按前台时长排序，空闲超过 5 分钟不计入"));
        if (agg.Count == 0)
        {
            sp.Children.Add(Desc("暂无数据 — 使用一段时间后自动记录"));
        }
        else
        {
            var max = agg.Values.Max().TotalSeconds;
            foreach (var kv in agg.OrderByDescending(k => k.Value).Take(8))
            {
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

                // 应用图标 + 名称
                var nameCell = new StackPanel { Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center };
                var iconHost = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) };
                var ic = AppIconExtractor.GetIcon(kv.Key);
                if (ic != null)
                    iconHost.Children.Add(new Image
                    {
                        Source = ic, Width = 18, Height = 18, Stretch = Stretch.UniformToFill,
                        Clip = new RectangleGeometry(new Rect(0, 0, 18, 18), 5, 5)
                    });
                else
                    iconHost.Children.Add(new Border
                    { CornerRadius = new CornerRadius(5), Background = AppTheme.Current.AccentGradient });
                nameCell.Children.Add(iconHost);
                nameCell.Children.Add(new TextBlock
                {
                    Text = kv.Key, FontSize = 12,
                    Foreground = AppTheme.Current.TextSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(nameCell, 0); g.Children.Add(nameCell);

                var track = new Border
                {
                    Height = 8, CornerRadius = new CornerRadius(4), Background = AppTheme.Current.RowSurface,
                    VerticalAlignment = VerticalAlignment.Center
                };
                track.Child = new Border
                {
                    CornerRadius = new CornerRadius(4), Background = AppTheme.Current.AccentGradient,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(4, 300 * (kv.Value.TotalSeconds / max))
                };
                Grid.SetColumn(track, 1); g.Children.Add(track);

                var vl = new TextBlock
                {
                    Text = $"{(int)kv.Value.TotalHours}h {kv.Value.Minutes}m", FontSize = 12,
                    TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = AppTheme.Current.TextPrimary
                };
                Grid.SetColumn(vl, 2); g.Children.Add(vl);
                sp.Children.Add(g);
            }
        }

        sp.Children.Add(new Border { Height = 16 });
        sp.Children.Add(Group("提醒",
            Row("启用休息提醒", "同应用每 60 分钟最多一次", Toggle(true, _ => { })),
            SliderRow("连续时长阈值", _settings.Usage.BreakMinutes, 20, 120, "分钟",
                v => _settings.Usage.BreakMinutes = (int)v),
            Row("记录窗口标题", "默认关闭以保护隐私", Toggle(_settings.Usage.CaptureTitle,
                v => _settings.Usage.CaptureTitle = v))));

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var openBtn = new Button { Content = "打开数据目录", Style = (Style)FindResource("Pill") };
        openBtn.Click += (_, _) => OpenDataFolder();
        acts.Children.Add(openBtn);
        sp.Children.Add(acts);
        return sp;
    }

    private void OpenDataFolder()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChronoIsland");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex) { App.Log("Settings", ex); }
    }

    private UIElement BuildMediaPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(H4("当前会话"));
        sp.Children.Add(Desc("按优先级排序：音乐应用 > 浏览器 > 其他"));

        try
        {
            var mgr = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync().GetAwaiter().GetResult();
            var sessions = mgr?.GetSessions();
            if (sessions == null || sessions.Count == 0)
                sp.Children.Add(Desc("未检测到正在播放的媒体"));
            else
                foreach (var s in sessions)
                {
                    var id = s.SourceAppUserModelId ?? "";
                    var pb = s.GetPlaybackInfo();
                    var props = s.TryGetMediaPropertiesAsync().GetAwaiter().GetResult();
                    sp.Children.Add(SessionCard(id,
                        $"{MediaSourcePolicy.DisplayName(id)}",
                        $"{pb?.PlaybackStatus} · 优先级 {MediaSourcePolicy.GetSourcePriority(id)}" +
                        (MediaSourcePolicy.IsBrowserSource(id) ? " · 浏览器降级" : ""),
                        props?.Title, props?.Artist));
                }
        }
        catch (Exception ex) { sp.Children.Add(Desc("读取失败：" + ex.Message)); }

        sp.Children.Add(new Border { Height = 16 });
        sp.Children.Add(Group("显示行为", Desc("控制岛在播放状态变化时的表现"),
            Row("暂停后自动收起", "关闭则暂停时保留暂停态", Toggle(_settings.Media.HideWhenPaused,
                v => _settings.Media.HideWhenPaused = v)),
            SliderRow("暂停后收起延时", _settings.Media.PausedHideSeconds, 1, 60, "秒",
                v => _settings.Media.PausedHideSeconds = (int)v),
            Row("浏览器会话降级", "避免浏览器抢占本地播放器", Toggle(_settings.Media.BrowserAsFallback,
                v => _settings.Media.BrowserAsFallback = v)),
            Row("启用媒体模块", "关闭后不读取任何媒体信息", Toggle(_settings.Media.Enabled,
                v => _settings.Media.Enabled = v))));

        // ---- 歌词 ----
        sp.Children.Add(new Border { Height = 16 });
        sp.Children.Add(Group("歌词", Desc("来源：QQ音乐 → 网易云 → LRCLIB，命中即缓存；仅本地展示，不分发歌词数据"),
            Row("显示歌词", "胶囊态在标题下方显示当前行；开启后胶囊会增高一行",
                Toggle(_settings.Media.ShowLyrics, v => _settings.Media.ShowLyrics = v)),
            Row("歌词放不下时自动滚动", "关闭则直接截断",
                Toggle(_settings.Media.LyricAutoScroll, v => _settings.Media.LyricAutoScroll = v)),
            Row("显示翻译行", "部分来源提供歌词翻译",
                Toggle(_settings.Media.ShowLyricTranslation, v => _settings.Media.ShowLyricTranslation = v)),
            SliderRow("歌词时间轴偏移", _settings.Media.LyricOffsetMs, -3000, 3000, "毫秒",
                v => _settings.Media.LyricOffsetMs = (int)v)));
        return sp;
    }

    private Border SessionCard(string appKey, string name, string hint, string? title, string? artist)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var iconHost = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) };
        var ic = AppIconExtractor.GetIcon(appKey);
        if (ic != null)
            iconHost.Children.Add(new Image
            {
                Source = ic, Width = 22, Height = 22, Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, 22, 22), 6, 6)
            });
        else
            iconHost.Children.Add(new Border
            { CornerRadius = new CornerRadius(6), Background = AppTheme.Current.AccentGradient });
        sp.Children.Add(iconHost);

        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        txt.Children.Add(new TextBlock
        { Text = name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Current.TextPrimary });
        txt.Children.Add(new TextBlock
        { Text = hint, FontSize = 11, Foreground = AppTheme.Current.TextTertiary });
        if (!string.IsNullOrWhiteSpace(title))
            txt.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} — {artist}",
                FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
                Foreground = AppTheme.Current.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis
            });
        sp.Children.Add(txt);

        return new Border
        {
            Background = AppTheme.Current.RowSurface, BorderBrush = AppTheme.Current.Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 6), Child = sp
        };
    }

    /// <summary>进入通知页时主动查询一次权限（同步等待，最多 3 秒）</summary>
    private UIElement BuildNotifyPageWithQuery()
    {
        try
        {
            bool granted = Sources.NotificationSource.QueryAccess(3000);
            App.LogInfo("Notification", granted ? "通知访问权限：已授予" : "通知访问权限：未授予");
            UpdateNotifyBadge();
        }
        catch { }
        return BuildNotifyPage();
    }

    private UIElement BuildNotifyPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(Group("接管方式", Desc(
            "「鸠占鹊巢」：检测到新通知时，先把内容取到灵动岛，随即调用 " +
            "RemoveNotification 把系统那条从通知中心移除。\n\n" +
            "效果：\n" +
            "  · 通知显示在灵动岛\n" +
            "  · 系统通知中心不留副本，只能通过岛上的「打开 / 忽略」处理\n" +
            "  · 原生横幅仍可能短暂出现 —— Win11 的横幅由系统 UWP 组件直接渲染，" +
            "不是普通窗口，第三方程序无法拦截（已实测确认）"),
            Row("接管系统通知", "取到岛后即从通知中心移除系统副本", Toggle(_settings.Notification.Enabled,
                v => _settings.Notification.Enabled = v)),
            Row("移除系统副本", "关闭后系统通知会保留，与岛各存一份", Toggle(_settings.Notification.TakeOver,
                v => _settings.Notification.TakeOver = v))));
        sp.Children.Add(Group("合并与留存", Desc("同来源通知在窗口内合并为 ×N"),
            SliderRow("合并窗口", 120, 30, 600, "秒", _ => { }),
            SliderRow("显示时长", _settings.Notification.TtlSeconds, 2, 15, "秒",
                v => _settings.Notification.TtlSeconds = (int)v),
            Row("重要通知延长", "系统重要通知停留 10 秒", Toggle(true, _ => { }))));
        bool? access = Sources.NotificationSource.LastKnownAccess;
        string statusText = access switch
        {
            true => "✓ 已取得通知访问权限 —— 可以抑制系统原生弹窗。",
            false => "✗ 未取得通知访问权限。\n" +
                     "开启路径：设置 → 隐私和安全性 → 通知 → 允许应用访问通知。\n" +
                     "未取得权限时不影响岛显示通知，只是不抑制系统原生弹窗。",
            null => "尚未检测。点击下方「重新检测」查询当前权限状态。"
        };
        var statusCard = Group("权限状态", Desc(statusText));
        var recheck = new Button
        {
            Content = "重新检测", Style = (Style)FindResource("Pill"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0)
        };
        recheck.Click += (_, _) =>
        {
            bool ok = Sources.NotificationSource.QueryAccess(3000);
            UpdateNotifyBadge();
            RenderPage();
            ShowStatus(ok ? "已授予通知访问权限" : "未授予通知访问权限");
        };
        if (statusCard.Child is StackPanel sp2) sp2.Children.Add(recheck);
        sp.Children.Add(statusCard);

        // 如实说明横幅抑制的能力边界（Win11 系统限制）
        sp.Children.Add(Group("关于「不弹原生横幅」", Desc(
            "Windows 11 的通知横幅由系统 UWP 组件直接渲染，不是普通窗口：" +
            "第三方程序既无法枚举它、也无法隐藏它（已验证）。\n\n" +
            "官方提供的专注助手 API（FocusSessionManager）需要 MSIX 包标识，普通桌面程序不可用。\n\n" +
            "因此本程序的行为是：\n" +
            "  · 通知会同步显示在灵动岛（已验证可用）\n" +
            "  · 系统横幅仍会出现（Windows 平台限制，无法绕过）\n\n" +
            "若你希望完全静音通知横幅，请在系统设置中开启专注助手：" +
            "设置 → 系统 → 专注 → 开启，然后把本程序加入「优先通知」以便岛仍能收到。")));
        return sp;
    }

    private UIElement BuildThemePage()
    {
        var sp = new StackPanel();
        sp.Children.Add(Group("主题", Desc("跟随系统，或固定为深色 / 浅色"),
            Row("外观模式", "实时预览", Segmented(new[] { "跟随", "深色", "浅色" },
                _settings.Theme.Mode switch { "Dark" => "深色", "Light" => "浅色", _ => "跟随" },
                v =>
                {
                    _settings.Theme.Mode = v switch { "深色" => "Dark", "浅色" => "Light", _ => "System" };
                    App.ApplyTheme(_settings.Theme.Mode);
                    RefreshChrome();
                    RenderPage();
                })),
            Row("玻璃材质", "Acrylic 半透明，不支持时降级 Mica", Toggle(_settings.Theme.Acrylic,
                v => _settings.Theme.Acrylic = v))));
        // ===== 悬停穿透行为（方案一）=====
        sp.Children.Add(Group("悬停与穿透", Desc(
            "岛默认是【穿透】的 —— 鼠标点它会直接点到后面的窗口，绝不会挡住按钮。\n\n" +
            "操作方式：\n" +
            "  · 鼠标移入岛 → 岛变半透明并显示高亮描边，此时仍可穿透点击后面的内容\n" +
            "  · 在岛上停留超过设定时间 → 岛变为不透明并展开详情，此时可以操作岛\n" +
            "  · 鼠标离开 → 立即收回胶囊并恢复穿透\n" +
            "  · 按住 Alt → 强制穿透（应急保险，随时点到底下的东西）"),
            Row("启用悬停激活", "关闭后鼠标悬停即可交互（反应更快，但容易误触）",
                Toggle(_settings.Island.HoverActivate, v =>
                {
                    _settings.Island.HoverActivate = v;
                    ApplyGeometryToIsland();
                })),
            SliderRow("停留多久可交互", _settings.Island.HoverActivateMs, 100, 1500, "毫秒", v =>
            {
                _settings.Island.HoverActivateMs = (int)v;
                ApplyGeometryToIsland();
            }),
            Row("Alt 强制穿透", "按住 Alt 时无视停留时间，始终穿透",
                Toggle(_settings.Island.AltForceThrough, v =>
                {
                    _settings.Island.AltForceThrough = v;
                    ApplyGeometryToIsland();
                }))));

        sp.Children.Add(Group("详情卡展开",
            Desc("两种方式都会生成同一张详情卡，卡片右上角都有关闭按钮。"),
            SliderRow("悬浮多久自动展开", _settings.Island.HoverExpandMs, 0, 2000, "毫秒", v =>
            {
                _settings.Island.HoverExpandMs = (int)v;
                ShowStatus(v <= 0 ? "悬浮自动展开已关闭（仅点击展开）"
                                  : $"悬浮 {(int)v} ms 自动展开");
            }),
            Row("点击展开", "点击当前胶囊展开 / 收起详情卡", Toggle(true, _ => { }))));

        sp.Children.Add(Group("灵动岛", Desc("岛自身的几何 —— 拖动即时预览"),
            SliderRow("顶部距离", _settings.Island.Top, 4, 24, "px", v =>
            {
                _settings.Island.Top = v;
                LivePreviewGeometry();
            }),
            SliderRow("宽度上限", _settings.Island.MaxWidth, 200, 560, "px", v =>
            {
                _settings.Island.MaxWidth = v;
                LivePreviewGeometry();
            }),
            SliderRow("整体缩放", _settings.Island.Scale, 0.5, 3.0, "×", v =>
            {
                _settings.Island.Scale = Math.Round(v, 2);
                LivePreviewGeometry();
            })));
        var hint = new TextBlock
        {
            Text = "提示：调整后会自动在屏幕顶部显示一条示例岛，方便对照效果。",
            FontSize = 11, Foreground = AppTheme.Current.TextTertiary,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
        };
        sp.Children.Add(hint);
        return sp;
    }

    private UIElement BuildAnimPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(Desc("半隐式欧拉，固定步长 1/120s，收敛阈值 0.05"));
        var items = new (string N, int K, int C, string Use)[]
        {
            ("Gentle", 170, 22, "宽度变化 / 内容替换"),
            ("Snappy", 300, 26, "折叠成点（阶段 A）"),
            ("Gooey", 220, 18, "水珠拉伸 / 吸附（阶段 B）"),
            ("Float", 140, 20, "详情卡展开"),
            ("Push", 200, 24, "集群推挤 / 补位"),
            ("Dismiss", 260, 30, "手动忽略 / 滑出")
        };
        foreach (var (n, k, c, use) in items)
        {
            var val = new TextBlock
            {
                Text = $"k={k}   c={c}", FontSize = 11,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = AppTheme.Current.TextTertiary, VerticalAlignment = VerticalAlignment.Center
            };
            sp.Children.Add(Row(n, use, val));
        }
        sp.Children.Add(new Border { Height = 8 });
        sp.Children.Add(Group("可访问性",
            Row("减少动效", "退化为 200ms 线性淡入淡出，几何瞬变",
                Toggle(_settings.General.ReduceMotion == "On", v =>
                    _settings.General.ReduceMotion = v ? "On" : "System"))));
        return sp;
    }

    private UIElement BuildAboutPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(Group("ChronoIsland 时序岛",
            Desc("Windows 桌面灵动岛 · v0.0.1 开发版"),
            Row("运行环境", ".NET 10 · WPF · Windows 10 19041+", null),
            Row("参考项目", "FluidBar（EventBus 架构与媒体实现）", null),
            Row("图标", "assets/icon.ico · 7 个尺寸", null)));
        var openBtn = new Button { Content = "打开数据目录", Style = (Style)FindResource("Pill"),
            HorizontalAlignment = HorizontalAlignment.Left };
        openBtn.Click += (_, _) => OpenDataFolder();
        sp.Children.Add(Group("数据", Row("本地目录", "%LOCALAPPDATA%\\ChronoIsland", null)));
        sp.Children.Add(openBtn);
        return sp;
    }

    /// <summary>几何设置变化 → 立即应用到灵动岛并推一条示例岛</summary>
    /// <summary>滑块变化 → 实时应用到预览岛（不重复创建）</summary>
    private void LivePreviewGeometry()
    {
        ApplyGeometryToIsland(enterPreview: true);
    }

    /// <summary>把几何/悬停设置应用到灵动岛</summary>
    private void ApplyGeometryToIsland(bool enterPreview = false)
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not IslandWindow island) continue;
            if (enterPreview) island.EnterPreview();
            island.ApplyGeometrySettings(_settings);
            break;
        }
    }

    /// <summary>离开几何页 / 关闭窗口 → 退出预览</summary>
    private void StopPreviewIfActive()
    {
        foreach (Window w in Application.Current.Windows)
            if (w is IslandWindow island) { island.ExitPreview(); break; }
    }

    // ================= 窗口行为 =================
    private void ApplyIcon()
    {
        try
        {
            var big = IconHelper.LoadWindowIconSized(48);
            if (big != null)
            {
                TitleIcon.Source = big;
                Icon = big;
                FallbackDot.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex) { App.Log("Icon", ex); }
    }

    private void AnimateIn()
    {
        if (_settings.General.ReduceMotion == "On") return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sx = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, sx);
        CardY.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });
        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settings.General.ReduceMotion == "On") return;
        e.Cancel = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var sx = new DoubleAnimation(1, 0.94, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
        var op = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        op.Completed += (_, _) => { StopPreviewIfActive(); Closing -= OnClosing; Close(); };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, sx);
        RootCard.BeginAnimation(OpacityProperty, op);
    }

    private void TitleBar_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        StopPreviewIfActive();
        Close();
    }

    private void BtnPreview_Click(object sender, RoutedEventArgs e)
    {
        foreach (Window w in Application.Current.Windows)
            if (w is IslandWindow island)
            {
                island.DebugPush("WeCom", "张伟", "晚上的评审会提前到 19:30，记得带 demo");
                break;
            }
        ShowStatus("已发送预览通知");
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var fresh = new Settings();
        _settings.Island = fresh.Island; _settings.Theme = fresh.Theme; _settings.Clock = fresh.Clock;
        _settings.Notification = fresh.Notification; _settings.Usage = fresh.Usage;
        _settings.Media = fresh.Media; _settings.Game = fresh.Game; _settings.General = fresh.General;
        _settings.Sys = fresh.Sys;
        _settings.Save();
        App.ApplyTheme(_settings.Theme.Mode);
        foreach (Window win in Application.Current.Windows)
            if (win is IslandWindow iw2) { iw2.ApplySourceSettings(_settings); break; }
        RefreshChrome();
        RenderPage();
        ShowStatus("已恢复默认设置");
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        App.LogInfo("Settings",
            $"已保存：Top={_settings.Island.Top} MaxWidth={_settings.Island.MaxWidth} Scale={_settings.Island.Scale}");
        // 重新应用到灵动岛（确保保存的值生效）
        foreach (Window win in Application.Current.Windows)
            if (win is IslandWindow iw) { iw.ApplyGeometrySettings(_settings); break; }
        ShowStatus("已保存");
        Close();
    }

    private void RefreshChrome()
    {
        var t = AppTheme.Current;
        RootCard.Background = t.PanelSurface;
        RootCard.BorderBrush = t.Stroke;
        if (RootCard.Effect is System.Windows.Media.Effects.DropShadowEffect ds) ds.Opacity = t.ShadowOpacity;
        if (FallbackDot.Fill is LinearGradientBrush) FallbackDot.Fill = t.AccentGradient;
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        t.Tick += (_, _) => { StatusText.Text = "v0.0.1 dev"; t.Stop(); };
        t.Start();
    }
}

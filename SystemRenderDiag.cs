using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsland.Core;
using ChronoIsland.Native;
using ChronoIsland.Sources;
using ChronoIsland.UI;

namespace ChronoIsland;

/// <summary>
/// 系统状态岛渲染自检（docs §5.8–§5.13）：
///   麦克风 / 电池 / Wi-Fi / 移动热点 / 飞行模式 / 深浅色切换
///
/// 【要证明什么】这六类岛同时有"胶囊 / 详情卡 / 球体"三种形态，
/// 而它们的宽度是**公式算出来的**（MeasureSystemPillWidth）。只在控制台看数字
/// 无法确认图标是否画对、颜色是否可读、排版有没有压字 —— 必须真的渲染出来看。
///
/// 输出一张 PNG（深色 + 浅色两列），每行一类岛：胶囊 + 球体 + 详情卡。
/// 用法：ChronoIsland.exe sysrender [输出png]
/// </summary>
public static class SystemRenderDiag
{
    public static void Run(string outPath)
    {
        if (string.IsNullOrWhiteSpace(outPath))
            outPath = Path.Combine(AppContext.BaseDirectory, "system_render.png");

        var s = Core.Settings.Load();
        IslandMetrics.Apply(s);

        Console.WriteLine("=== 系统状态岛渲染自检 ===");
        Console.WriteLine($"Scale={IslandMetrics.Scale}  MaxWidth={IslandMetrics.MaxWidth}");

        var win = new IslandWindow();

        // 每类岛给两三个状态：既不漏掉图标分支（充电 / 低电量、太阳 / 月亮），
        // 也覆盖"内容最长"的排版（带尾巴、带占用者名、带两段速率）。
        var cases = new (string Label, string Kind, object Payload, string Title, string Subtitle)[]
        {
            ("麦克风·占用", "mic", new MicSnapshot(true, "WeChat", 1), "麦克风被占用", "WeChat 正在使用"),
            ("麦克风·空闲", "mic", new MicSnapshot(false, "", 0), "麦克风已释放", "已没有程序在录音"),
            ("电池·充电", "battery", new BatterySnapshot(true, 85, true, true, 3600, false), "已接通电源", "85%"),
            ("电池·低电", "battery", new BatterySnapshot(true, 18, false, false, 5400, false), "电量不足", "18% · 建议接通电源"),
            ("Wi-Fi·已连", "wifi", new WifiSnapshot(true, "JCG_WLAN", 88, 344, 12_500_000, 2_100_000, true), "已连接 Wi-Fi", "JCG_WLAN"),
            ("Wi-Fi·断开", "wifi", new WifiSnapshot(false, "", 0, 0, 0, 0, true), "Wi-Fi 已断开", "网络不可用"),
            ("热点·开启", "hotspot", new HotspotSnapshot(true, 2, new[] { "192.168.137.27", "192.168.137.31" }, true), "移动热点已开启", "2 台设备已连接"),
            ("飞行模式·开", "airplane", new RadioSnapshot(true, false), "飞行模式已开启", "所有无线连接已关闭"),
            ("飞行模式·关", "airplane", new RadioSnapshot(false, true), "飞行模式已关闭", "无线连接已恢复"),
            ("深色模式", "theme", new ThemeSnapshot(true, "跟随系统 · 深色"), "已切换到深色", "跟随系统 · 深色"),
            ("浅色模式", "theme", new ThemeSnapshot(false, "跟随系统 · 浅色"), "已切换到浅色", "跟随系统 · 浅色"),
        };

        var png = Path.GetFullPath(outPath);
        Render(win, cases, png);
        Console.WriteLine("已输出：" + png);

        Console.WriteLine();
        Console.WriteLine("=== 结构与图标自检 ===");
        int fail = 0;
        foreach (var c in cases)
        {
            var ev = MakeEvent(c.Kind, c.Payload, c.Title, c.Subtitle);
            var pill = win.BuildContentPublic(ev);
            var sphere = IslandWindow.BuildSpherePublic(ev);

            double formula = win.MeasurePillWidthPublic(ev);
            double need;
            if (pill != null) { var p = new Border { Child = pill }; p.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); need = p.DesiredSize.Width; p.Child = null; }
            else need = 0;

            // 球体必须是图标（Grid），不能是 SvgIcons 的回退文本 "?"（TextBlock）
            bool sphereOk = sphere is Grid;
            // 图标不能是回退占位
            bool pillOk = pill != null && !ContainsFallback(pill);
            bool sphereIconOk = !ContainsFallback(sphere);

            var card = win.ProbeCard(c.Kind, AppTheme.Current, null, ev);
            double cardH = 0;
            if (card != null)
            {
                card.Measure(new Size(IslandMetrics.CardWidth * Math.Max(1, IslandMetrics.Scale), double.PositiveInfinity));
                cardH = card.DesiredSize.Height;
            }

            double diff = need - formula;
            // 允许两类"看起来不一致但属于设计"的情况（与 PillWidthDiag 同一口径）：
            //   · 公式已按上限封顶 → 内容超出是预期；
            //   · 公式比需求宽出 ≤ EllipsisSlack(12) → 那是给 TextTrimming 的省略号余量，
            //     不是"右侧多出空白"（Wi-Fi 的 SSID 列就有 MaxWidth 截断）。
            const double slack = 13.5;
            bool widthOk = Math.Abs(diff) <= 1.5
                           || formula >= IslandMetrics.MaxWidth - 1.5
                           || (diff < 0 && -diff <= slack);

            // 【像素级验算】按公式宽度真渲染一次，扫描"墨迹"列：
            //   左右两侧都必须留出内边距（≥2px），且墨迹不能越过右边界（越界 = 内容被裁/溢出）。
            // 这是唯一能确认"公式真的够用"的手段 —— 只比较 DesiredSize 无法发现
            // "内容被容器裁掉但 DesiredSize 仍然等于容器宽"的情况。
            var (inkL, inkR) = InkMargins(win, ev, formula);
            bool inkOk = inkL >= 1.5 && inkR >= 1.5;

            bool cardOk = cardH > 40;
            if (!(sphereOk && pillOk && sphereIconOk && widthOk && cardOk && inkOk)) fail++;

            Console.WriteLine($"  {c.Label,-12} 宽={formula,6:0.0} 需求={need,6:0.0} 差={diff,6:0.0} " +
                              $"卡片高={cardH,5:0.0} 图标={(pillOk && sphereIconOk ? "OK" : "回退!")} " +
                              $"墨迹左右={inkL,4:0.0}/{inkR,4:0.0} " +
                              $"{(widthOk && cardOk && pillOk && sphereOk && sphereIconOk && inkOk ? "" : " ← 异常")}");
        }
        Console.WriteLine(fail == 0 ? "全部通过。" : $"{fail} 项异常（见上）。");
    }

    /// <summary>
    /// 把胶囊按公式宽度渲染成一张小图，扫描非背景像素，返回左 / 右两侧的空白宽度。
    ///
    /// 【为什么这次能扫出墨迹】元素只要真正走过 Measure/Arrange 并被
    /// RenderTargetBitmap 渲染，文本层就会参与光栅化 —— 这与"未挂载元素扫出空白图"
    /// 是两件事：后者根本没做 Arrange。
    /// 判据：左、右空白都必须 &gt; 1.5px（内边距 11/12 + 描边），否则说明内容顶到边框了。
    /// </summary>
    private static (double Left, double Right) InkMargins(IslandWindow win, IslandEvent ev, double width)
    {
        const double h = 36;
        var pill = win.BuildContentPublic(ev);
        if (pill == null) return (0, 0);

        var host = new Border
        {
            Width = width, Height = h,
            Background = Brushes.Black,             // 对比底：任何非黑像素都算墨迹
            Child = pill
        };
        var roots = new Grid { Width = width, Height = h };
        roots.Children.Add(host);
        roots.Measure(new Size(width, h));
        roots.Arrange(new Rect(0, 0, width, h));
        roots.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(width), (int)h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(roots);

        int w = rtb.PixelWidth, hh = rtb.PixelHeight;
        var px = new byte[w * hh * 4];
        rtb.CopyPixels(px, w * 4, 0);

        int minX = w, maxX = -1;
        for (int y = 0; y < hh; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a > 40 && (b > 30 || g > 30 || r > 30))
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }
        }
        if (maxX < 0) return (0, 0);               // 全黑：内容根本没画出来
        return (minX, w - 1 - maxX);
    }

    /// <summary>子树里是否出现 SvgIcons 的回退占位（Text == "?"）—— 说明图标没解析出来</summary>
    private static bool ContainsFallback(DependencyObject? root)
    {
        if (root == null) return false;
        if (root is TextBlock tb && tb.Text == "?") return true;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (ContainsFallback(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    private static IslandEvent MakeEvent(string kind, object payload, string title, string subtitle)
        => new(Id: "probe-" + kind, SourceKey: "probe", Kind: kind, Priority: 2,
            Title: title, Subtitle: subtitle, Payload: payload,
            Policy: new IslandPolicy(TimeSpan.FromSeconds(5)))
        { AppKey = kind };

    private static void Render(IslandWindow win,
        (string Label, string Kind, object Payload, string Title, string Subtitle)[] cases, string pngPath)
    {
        // 两列：深色 / 浅色。行高按"胶囊 44 + 详情卡高 + 间距"给足，避免内容被裁。
        const double colW = 620;
        const double pad = 22;
        double rowH = 200;
        double canvasW = colW * 2;
        double canvasH = pad * 2 + 34 + cases.Length * rowH;

        var rootGrid = new Grid { Width = canvasW, Height = canvasH };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        rootGrid.Children.Add(panel);
        foreach (var dark in new[] { true, false })
        {
            var t = dark ? ThemeTokens.Dark : ThemeTokens.Light;
            var col = new StackPanel { Width = colW, Margin = new Thickness(pad, pad, pad, pad) };

            col.Children.Add(new TextBlock
            {
                Text = dark ? "DARK" : "LIGHT",
                FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = t.TextPrimary, Margin = new Thickness(0, 0, 0, 14)
            });

            foreach (var c in cases)
            {
                var row = new Border
                {
                    Background = t.WindowSurface,
                    CornerRadius = new CornerRadius(0),
                    Margin = new Thickness(0, 0, 0, 14),
                    Child = BuildRow(win, c, t)
                };
                col.Children.Add(row);
            }
            panel.Children.Add(col);
        }

        // 离屏渲染必须真的走 Measure/Arrange，否则文本层不参与光栅化（会得到空白图）
        // 注意：rootGrid 只能有一个父级 —— 不能再往另一个 Grid 里加一次，
        // 否则会抛"元素已经是另一个元素的逻辑子元素"。
        var host = new Border
        {
            Width = canvasW, Height = canvasH,
            Background = ThemeTokens.Dark.PanelSurface,
            Child = rootGrid
        };
        host.Measure(new Size(canvasW, canvasH));
        host.Arrange(new Rect(0, 0, canvasW, canvasH));
        host.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)canvasW, (int)canvasH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(pngPath);
        enc.Save(fs);
    }

    /// <summary>一行：胶囊（按公式宽度）+ 球体 + 详情卡</summary>
    private static FrameworkElement BuildRow(IslandWindow win,
        (string Label, string Kind, object Payload, string Title, string Subtitle) c, ThemeTokens t)
    {
        var ev = MakeEvent(c.Kind, c.Payload, c.Title, c.Subtitle);

        var prev = AppTheme.Current;
        AppTheme.SetCurrentForProbe(t);
        try
        {
            var wrap = new StackPanel { Margin = new Thickness(0) };

            // 标题行：这类岛的 key + 公式算出的胶囊宽
            double pillW = win.MeasurePillWidthPublic(ev);
            wrap.Children.Add(new TextBlock
            {
                Text = $"{c.Label}   pill={pillW:0.0}",
                FontSize = 10, Foreground = t.TextTertiary,
                Margin = new Thickness(0, 0, 0, 5)
            });

            // 胶囊：真实内容 + 按公式宽度收紧（宽度不对就会露馅：右侧留白 / 文字被裁）
            var pill = win.BuildContentPublic(ev);
            var pillHost = new Border
            {
                Width = pillW,
                Height = IslandMetrics.IslandHeight,
                CornerRadius = new CornerRadius(IslandMetrics.IslandHeight / 2),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(((SolidColorBrush)t.IslandTop).Color, 0),
                        new(((SolidColorBrush)t.IslandBottom).Color, 1)
                    }
                },
                BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = pill
            };

            // 球体：36 正圆
            var sphere = IslandWindow.BuildSpherePublic(ev);
            var sphereHost = new Border
            {
                Width = IslandMetrics.SphereSize, Height = IslandMetrics.SphereSize,
                CornerRadius = new CornerRadius(IslandMetrics.SphereSize / 2),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new(((SolidColorBrush)t.IslandTop).Color, 0),
                        new(((SolidColorBrush)t.IslandBottom).Color, 1)
                    }
                },
                BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = sphere
            };

            var topRow = new StackPanel { Orientation = Orientation.Horizontal };
            topRow.Children.Add(pillHost);
            topRow.Children.Add(sphereHost);
            wrap.Children.Add(topRow);

            // 详情卡：走真实 BuildCard（含关闭按钮 + 缩放），外层给一张卡宽的底板
            var card = win.ProbeCard(c.Kind, t, null, ev);
            if (card != null)
            {
                double cardW = IslandMetrics.CardWidth * (IslandMetrics.Scale <= 0 ? 1 : IslandMetrics.Scale);
                card.Measure(new Size(cardW, double.PositiveInfinity));
                double cardH = card.DesiredSize.Height;
                var cardHost = new Border
                {
                    Width = cardW,
                    Height = double.IsNaN(cardH) || cardH <= 0 ? 160 : cardH,
                    Margin = new Thickness(0, 10, 0, 0),
                    CornerRadius = new CornerRadius(IslandMetrics.CardRadius),
                    Background = t.IslandTop, BorderBrush = t.Stroke,
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = card
                };
                wrap.Children.Add(cardHost);
            }
            return wrap;
        }
        finally { AppTheme.SetCurrentForProbe(prev); }
    }
}

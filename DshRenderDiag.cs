using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChronoIsland.Core;
using ChronoIsland.Sources;

namespace ChronoIsland;

/// <summary>
/// DSH 胶囊 / 球体渲染自检：
/// 用真实数据构造一条 DshSnapshot，走真实 BuildDshPill / BuildSphere，
/// 测量布局并输出 PNG，便于肉眼确认样式。
/// 用法：ChronoIsland.exe dshrender [输出png]
/// </summary>
public static class DshRenderDiag
{
    public static void Run(string outPath)
    {
        if (string.IsNullOrWhiteSpace(outPath))
            outPath = Path.Combine(AppContext.BaseDirectory, "dsh_render.png");

        var live = DshSource.ReadActiveSession();
        var baseSnap = live ?? new DshSnapshot(false, false, 20, 62, 20, 657,
            "帮我看看音乐播放时歌词的显示", "deepseek-v4.1-flash",
            110539, 1000000, 0, 406, "8c05ed6c");
        // 强制构造三种状态，确保每条路径都被渲染到（live 可能正好空闲）
        var working = baseSnap with { Working = true, Stepping = true, Step = 62, StepElapsedMs = 9306 };
        var tooling = baseSnap with { Working = true, Stepping = false, Step = 62 };
        // 子代理态：正在干活的子代理（独立 session，标题继承自父会话）
        var subagent = baseSnap with { Working = true, Stepping = false, Step = 12,
            SubagentLabel = "Redesign widgets per mockup", SubagentMode = "continuable" };
        var done = baseSnap with { Working = false, Stepping = false, StepElapsedMs = 0 };

        Console.WriteLine("=== DSH 渲染自检 ===");
        Console.WriteLine("数据来源: " + (live != null ? "真实投影缓存" : "内置样例（未读到缓存）"));
        Console.WriteLine();

        var scale = Core.IslandMetrics.Scale;
        Console.WriteLine("缩放 Scale = " + scale);

        var panel = new StackPanel { Background = Brushes.Transparent };
        double maxW = 0, maxH = 0;

        void Add(string label, Func<FrameworkElement> build, double width)
        {
            var host = new Border
            {
                Width = width,
                Background = UI.AppTheme.Current.PanelSurface,
                CornerRadius = new CornerRadius(IslandMetrics.IslandHeight / 2),
                Padding = new Thickness(IslandMetrics.PaddingX, 0, IslandMetrics.PaddingX, 0),
                Margin = new Thickness(0, 0, 0, 14),
                Child = null
            };
            var content = build();
            host.Child = content;
            host.Measure(new Size(width, IslandMetrics.IslandHeight));
            var d = host.DesiredSize;
            maxW = Math.Max(maxW, d.Width);
            maxH += d.Height;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = label, Width = 150, FontSize = 11,
                Foreground = UI.AppTheme.Current.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(host);
            panel.Children.Add(row);

            Console.WriteLine(label.PadRight(22) + " 期望宽=" + width.ToString("F1")
                + "  实测宽=" + d.Width.ToString("F1")
                + "  实测高=" + d.Height.ToString("F1"));
        }

        // 用真实注册表取到宽度（反射调用私有 MeasurePillWidth 不可行，这里复算同一公式）
        var win = new UI.IslandWindow();
        double W(DshSnapshot s)
        {
            double w1 = Measure(s.Title.Length > 0 ? s.Title : "DSH 工作中", 12.5, true);
            double w2 = Measure(Describe(s), 11);
            return Math.Clamp(18 + 8 + Math.Max(w1, w2) + IslandMetrics.PaddingX * 2,
                IslandMetrics.MinWidth, IslandMetrics.MaxWidth);
        }

        Add("工作中 / sparkles", () => InvokeBuild(win, working), W(working));
        Add("执行工具 / sparkles", () => InvokeBuild(win, tooling), W(tooling));
        Add("子代理工作中 / sparkles", () => InvokeBuild(win, subagent), W(subagent));
        Add("已完成 / check", () => InvokeBuild(win, done), W(done));

        // 球体
        var sphereRow = new StackPanel { Orientation = Orientation.Horizontal };
        sphereRow.Children.Add(new TextBlock
        {
            Text = "球体", Width = 150, FontSize = 11,
            Foreground = UI.AppTheme.Current.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (var s in new[] { working, done })
        {
            // 注意 IslandEvent 的形参顺序是 (Id, SourceKey, Kind, Priority, Title,
            // Subtitle, Icon, Payload, Policy) —— Payload 是第 8 个，必须用具名实参，
            // 否则快照会被塞进 Icon，Payload 为 null → BuildSphere 走不到 DSH 分支。
            var ev = new IslandEvent("dsh", "dsh", "dsh", 4, "t",
                Payload: s, Policy: new IslandPolicy(null));
            var sp = UI.IslandWindow.BuildSpherePublic(ev);
            // 球体容器：必须显式 Arrange 到 36×36，否则 Grid 的 Stretch 子元素拿不到
            // 最终尺寸，渲染出来是一片空白（第一版自检图里"绿色方块"就是这个原因，
            // 与产品代码无关 —— 单独渲染图标时图标是正常的）。
            var box = new Border
            {
                Width = IslandMetrics.SphereSize, Height = IslandMetrics.SphereSize,
                Background = UI.AppTheme.Current.PanelSurface,
                CornerRadius = new CornerRadius(IslandMetrics.SphereSize / 2),
                Margin = new Thickness(0, 0, 12, 0),
                Child = sp
            };
            box.Measure(new Size(IslandMetrics.SphereSize, IslandMetrics.SphereSize));
            box.Arrange(new Rect(0, 0, IslandMetrics.SphereSize, IslandMetrics.SphereSize));
            box.UpdateLayout();
            sphereRow.Children.Add(box);
        }
        panel.Children.Add(sphereRow);

        // 详情卡
        try
        {
            var cardHost = win.BuildDshCardPublic(working);
            panel.Children.Add(new TextBlock
            {
                Text = "详情卡（悬浮 / 点击展开）", FontSize = 11,
                Foreground = UI.AppTheme.Current.TextSecondary,
                Margin = new Thickness(0, 16, 0, 8)
            });
            panel.Children.Add(cardHost);
        }
        catch (Exception ex) { Console.WriteLine("卡片渲染失败: " + ex.Message); }

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = panel.DesiredSize;
        int pw = (int)Math.Ceiling(size.Width + 20);
        int ph = (int)Math.Ceiling(size.Height + 20);
        Console.WriteLine();
        Console.WriteLine("输出画布: " + pw + " x " + ph);

        var bg = new Border
        {
            Background = UI.AppTheme.Current.WindowSurface,
            Padding = new Thickness(10),
            Child = panel
        };
        bg.Measure(new Size(pw, ph));
        bg.Arrange(new Rect(0, 0, pw, ph));
        bg.UpdateLayout();

        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(bg);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        Console.WriteLine("已写出: " + outPath);
    }

    private static FrameworkElement InvokeBuild(UI.IslandWindow win, DshSnapshot s)
        => UI.IslandWindow.BuildDshPillPublic(s);

    private static string Describe(DshSnapshot snap)
    {
        var parts = new System.Collections.Generic.List<string>();
        parts.Add(snap.Working
            ? "第 " + Math.Max(snap.Turn, 1) + " 轮 · 步 " + snap.Step
            : "第 " + Math.Max(snap.Turn, 1) + " 轮 · 共 " + snap.Steps + " 步");
        if (snap.ContextWindow > 0) parts.Add("上下文 " + snap.ContextPercent + "%");
        if (!string.IsNullOrWhiteSpace(snap.Model))
        {
            var m = snap.Model;
            var i = m.LastIndexOf('/');
            if (i >= 0 && i < m.Length - 1) m = m[(i + 1)..];
            parts.Add(m);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>与 IslandWindow.MeasureText 同口径（FormattedText）</summary>
    private static double Measure(string text, double size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var tf = new Typeface(
            new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight, tf, size, Brushes.Black, 1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }
}

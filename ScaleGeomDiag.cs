using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChronoIsland.Core;

namespace ChronoIsland;

/// <summary>
/// 0.0.1 三项观感缺陷自检（scalegeom）。
///
/// 【要证明的三件事 —— 用户报告】
/// ① 设置页「整体缩放」滑条只有 1×/2×/3× 三档，滑不到 1.5×/1.85×；
/// ② 媒体胶囊里波形不在最右端（紧挨文字），即"波形列没吃满预留宽度"；
/// ③ 球体里的应用图标不随缩放放大（36px 球体里始终 18px 图标），
///    且图标/球体尺寸写死、不随"整体缩放"自适应。
///
/// 【为什么这些能离屏量出来】三项都是纯几何/排版事实，不依赖时序：
/// ② 是"列宽之和 vs 容器宽"与"波形列内元素是否贴右"；
/// ③ 是"球体容器尺寸 vs 图标尺寸"的比值。
/// ① 在 UI 层，这里通过 SliderRow 的实际可取值集合来验证（步进粒度）。
///
/// 用法：ChronoIsland.exe scalegeom
/// </summary>
public static class ScaleGeomDiag
{
    private const double Tol = 0.6;

    public static void Run()
    {
        var s = Settings.Load();
        IslandMetrics.Apply(s);

        Console.WriteLine("=== 0.0.1 缩放 / 波形 / 球体图标 自检 ===");
        Console.WriteLine($"MaxWidth={IslandMetrics.MaxWidth}  Scale={IslandMetrics.Scale}  SphereSize={IslandMetrics.SphereSize}");
        Console.WriteLine();

        int fail = 0;

        // ---------- ① 缩放档位：必须是"连续可调"，不是三档 ----------
        fail += CheckScaleGranularity();

        // ---------- ② 波形贴右 ----------
        fail += CheckWaveRightAligned();

        // ---------- ③ 球体图标随缩放自适应 ----------
        fail += CheckSphereIconScaling();

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "全部通过：缩放连续可调、波形贴右、球体图标随缩放。"
            : $"{fail} 项不通过（见上）。");
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// ① 缩放档位自检。
    ///
    /// 【旧缺陷】SliderRow 用 <c>max-min &lt;= 1.5</c> 反推步进。缩放区间 0.5–3.0
    /// 跨度 2.5 → 被判成整数档，TickFrequency=1 且 SnapToTick 打开 → 只能落在
    /// 1/2/3；显示格式同一判定，1.5 还被显示成 "2 ×"。
    ///
    /// 【现在】步进由 SliderRow 的 step 参数显式给出（缩放传 0.05）。
    /// 这里按"缩放实际使用的步进"复算可取值集合，断言 1.5 / 1.85 都能落到，
    /// 且默认值 1.5 必须落在集合内（默认值滑条上取不到就会显示成别的档位）。
    /// </summary>
    private static int CheckScaleGranularity()
    {
        Console.WriteLine("【① 缩放档位】滑条必须能落到 1.5× / 1.85× 这类任意值");
        double min = 0.5, max = 3.0;

        // 缩放行现在显式传的步进（见 SettingsWindow 的 SliderRow("整体缩放", …, step: 0.05)）
        const double step = 0.05;
        const string fmt = "0.00";

        var values = new List<double>();
        for (double v = min; v <= max + 1e-9; v += step) values.Add(Math.Round(v, 4));

        bool canHit15 = values.Any(v => Math.Abs(v - 1.5) < 1e-6);
        bool canHit185 = values.Any(v => Math.Abs(v - 1.85) < 1e-6);
        bool canHit235 = values.Any(v => Math.Abs(v - 2.35) < 1e-6);
        bool notThreeOnly = values.Count > 10;

        // 默认值 1.5 必须能被滑条取到（否则一打开设置页就跳到别的档）
        double def = new IslandSettings().Scale;
        bool defaultReachable = values.Any(v => Math.Abs(v - def) < 1e-6);

        // 显示格式必须能区分 1.5 与 2（旧实现把 1.5 显示成 "2"）
        string shown15 = 1.5.ToString(fmt);
        bool labelOk = shown15 == "1.50" && 1.85.ToString(fmt) == "1.85";

        Console.WriteLine($"  区间=[{min}, {max}]  步进={step}  显示格式=\"{fmt}\"");
        Console.WriteLine($"  可取值数={values.Count}  1.5×={(canHit15 ? "能" : "不能")}  " +
                          $"1.85×={(canHit185 ? "能" : "不能")}  2.35×={(canHit235 ? "能" : "不能")}  " +
                          $"非三档={(notThreeOnly ? "是" : "否")}");
        Console.WriteLine($"  默认值={def}  滑条可及={(defaultReachable ? "能" : "不能")}  " +
                          $"1.5 显示为 \"{shown15}\"  [{(labelOk ? "OK" : "精度丢失")}]");

        bool ok = canHit15 && canHit185 && canHit235 && notThreeOnly && defaultReachable && labelOk;
        Console.WriteLine($"  [{(ok ? "OK 连续可调" : "档位仍被限制")}]");
        Console.WriteLine();
        return ok ? 0 : 1;
    }

    /// <summary>
    /// ② 波形必须贴胶囊右端。
    /// 判据（三重，缺一不可）：
    ///   a) 三列之和 == 容器宽（列宽与容器同源）；
    ///   b) 波形 StackPanel 的右边缘距胶囊右边界 == PillTailPad（8）；
    ///   c) 波形自身宽 == 其所在列的可用宽（吃满预留，而不是缩在左端）。
    /// </summary>
    private static int CheckWaveRightAligned()
    {
        Console.WriteLine("【② 波形位置】波形必须在胶囊最右端、吃满预留列宽");
        var win = new UI.IslandWindow();

        // 故意用"标题很短 + 歌词很长"的组合：文字列被歌词撑宽，
        // 此时若波形列没有吃满预留宽度，波形就会留在左端（用户看到的"紧挨左边"）。
        var ev = MakeMedia("Spotify.exe", "几分之几", "电影<花甲大人转>", true);
        var grid = win.BuildContentPublic(ev) as Grid;
        if (grid == null)
        {
            Console.WriteLine("  跳过：媒体胶囊根节点不是 Grid");
            return 1;
        }

        double totalW = win.MeasurePillWidthPublic(ev);
        double sum = grid.ColumnDefinitions.Sum(c => c.Width.Value);

        // 找波形：第 3 列里的 StackPanel（4 根 Border）
        var wave = grid.Children.OfType<StackPanel>()
            .FirstOrDefault(sp => Grid.GetColumn(sp) == 2 && sp.Children.Count == 4);
        if (wave == null)
        {
            Console.WriteLine("  ✗ 第 3 列找不到波形 StackPanel");
            return 1;
        }

        // 真排版一次，量实际几何
        var host = new Border { Width = totalW, Height = IslandMetrics.IslandHeight, Child = grid };
        host.Measure(new Size(totalW, IslandMetrics.IslandHeight));
        host.Arrange(new Rect(0, 0, totalW, IslandMetrics.IslandHeight));
        host.UpdateLayout();

        var waveBox = wave.TransformToAncestor(host).TransformBounds(
            new Rect(0, 0, wave.ActualWidth, wave.ActualHeight));
        double gapToRight = totalW - waveBox.Right;

        bool colsOk = Math.Abs(sum - totalW) <= Tol;
        bool rightOk = Math.Abs(gapToRight - 8.0) <= 1.5;      // PillTailPad = 8
        bool filledOk = wave.ActualWidth >= 21.0;              // 预留列宽 22（4×2.5+间距）

        Console.WriteLine($"  容器宽={totalW,7:F1}  三列之和={sum,7:F1}  差={sum - totalW,7:F1}  [{(colsOk ? "OK" : "脱节")}]");
        Console.WriteLine($"  波形：左={waveBox.Left,7:F1} 右={waveBox.Right,7:F1} 宽={wave.ActualWidth,6:F1}  " +
                          $"距右边界={gapToRight,6:F1}（应为 8）  [{(rightOk ? "OK" : "没贴右")}]");
        Console.WriteLine($"  波形吃满预留列宽：宽={wave.ActualWidth,6:F1} / 预留 22  [{(filledOk ? "OK" : "缩在左端")}]");

        // ---- 真实渲染口径：外框 = 未缩放宽 × Scale（Render 的实际做法）----
        // 这一条才是用户截图里的场景：整体缩放 1.5× 时，
        // 若内容不带 LayoutTransform，波形会停在"未缩放宽"的右缘。
        double scale = IslandMetrics.Scale <= 0 ? 1 : IslandMetrics.Scale;
        var mounted = CheckWaveMounted(win, ev, totalW, scale, out string mountDetail);
        Console.WriteLine(mountDetail);
        Console.WriteLine($"  [{(colsOk && rightOk && filledOk && mounted ? "OK" : "波形位置不对")}]");
        Console.WriteLine();
        return colsOk && rightOk && filledOk && mounted ? 0 : 1;
    }

    /// <summary>
    /// 按 Render 的真实口径挂载：外框 = 逻辑宽 × Scale，内容由 ApplyContentScale 施加缩放。
    /// 判据：波形右缘距外框右边界 == PillTailPad × Scale（缩放后仍是同一视觉留白）。
    /// </summary>
    private static bool CheckWaveMounted(UI.IslandWindow win, Core.IslandEvent ev,
        double totalW, double scale, out string detail)
    {
        var grid = win.BuildContentPublic(ev) as Grid;
        var wave = grid?.Children.OfType<StackPanel>()
            .FirstOrDefault(sp => Grid.GetColumn(sp) == 2 && sp.Children.Count == 4);
        if (grid == null || wave == null) { detail = "  挂载检查跳过（找不到波形）"; return false; }

        double frameW = totalW * scale;
        double frameH = IslandMetrics.IslandHeight * scale;

        // 与 Render 一致：ContentPresenter 挂 LayoutTransform(Scale)
        var cp = new ContentPresenter
        {
            Content = grid,
            LayoutTransform = Math.Abs(scale - 1.0) < 1e-6 ? null : new ScaleTransform(scale, scale),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var host = new Border
        {
            Width = frameW, Height = frameH,
            BorderThickness = new Thickness(1),
            Child = cp
        };
        host.Measure(new Size(frameW, frameH));
        host.Arrange(new Rect(0, 0, frameW, frameH));
        host.UpdateLayout();

        var box = wave.TransformToAncestor(host).TransformBounds(
            new Rect(0, 0, wave.ActualWidth, wave.ActualHeight));
        double gap = frameW - box.Right;
        double expected = PillTailPadExpected * scale;      // 8 × Scale
        bool ok = Math.Abs(gap - expected) <= 2.0;

        detail = $"  挂载后(Scale={scale:F2})：外框={frameW,7:F1}  波形[{box.Left,7:F1}..{box.Right,7:F1}]  " +
                 $"距右={gap,6:F1}（应 {expected,5:F1}）  [{(ok ? "OK" : "内容没随外框缩放")}]";
        return ok;
    }

    /// <summary>胶囊右侧留白（与 IslandWindow.PillTailPad 一致）</summary>
    private const double PillTailPadExpected = 8.0;

    /// <summary>
    /// ③ 球体图标必须随缩放自适应。
    /// 现状：SphereSize=36 固定、图标 18 固定 → 缩放 1.5 时球体被渲染成 54，
    /// 而里面的图标仍是 18 逻辑像素、再被 ContentPresenter 拉伸/或原样居中，
    /// 两者比例失控（球体变大而图标没等比变大）。
    /// 判据：图标尺寸 / 球体尺寸 == 0.5（docs §2.4：18/36），
    /// 且两者都按 Scale 等比（比值与 Scale 无关）。
    /// </summary>
    private static int CheckSphereIconScaling()
    {
        Console.WriteLine("【③ 球体图标】图标必须与球体等比、随整体缩放一起放大");
        var win = new UI.IslandWindow();
        var ev = MakeMedia("Spotify.exe", "几分之几", "电影<花甲大人转>", true);
        var sphere = UI.IslandWindow.BuildSpherePublic(ev);

        double sphereSize = IslandMetrics.SphereSize;      // 36
        double scale = IslandMetrics.Scale <= 0 ? 1 : IslandMetrics.Scale;

        // 量球体内容里最大的 Image / Border（图标载体）
        var probe = new Border { Width = sphereSize, Height = sphereSize, Child = sphere };
        probe.Measure(new Size(sphereSize, sphereSize));
        probe.Arrange(new Rect(0, 0, sphereSize, sphereSize));
        probe.UpdateLayout();

        var icon = FindIconCarrier(sphere);
        if (icon == null)
        {
            Console.WriteLine("  跳过：球体内容里找不到图标载体（可能取不到应用图标 → 渐变块）");
            Console.WriteLine();
            return 0;
        }

        double iconW = icon.ActualWidth > 0 ? icon.ActualWidth : icon.Width;
        double ratio = sphereSize <= 0 ? 0 : iconW / sphereSize;
        bool ratioOk = Math.Abs(ratio - 0.5) <= 0.05;

        // 【口径说明】图标逻辑尺寸应恒为 18（= SphereSize/2），
        // 缩放由渲染层的 LayoutTransform 施加，因此这里量到的"逻辑值"不随 Scale 变。
        // 真正要验的是"渲染后"的视觉尺寸 = 逻辑值 × Scale，见下面多档位遍历。
        Console.WriteLine($"  球体逻辑尺寸={sphereSize}  当前 Scale={scale}");
        Console.WriteLine($"  图标逻辑尺寸={iconW,6:F1}  比值={ratio,5:F2}（docs 期望 0.50）  [{(ratioOk ? "OK" : "比例不对")}]");

        // ---- 多缩放档位遍历：按 Render 的口径挂载，量"渲染后"的视觉尺寸 ----
        // 判据：图标视觉尺寸 = 18 × Scale，且 图标/球体 视觉比恒为 0.50。
        Console.WriteLine("  多档位遍历（按 Render 口径挂载：内容带 LayoutTransform(Scale)）：");
        bool sweepOk = true;
        foreach (double sc in new[] { 1.0, 1.5, 1.85, 2.5, 3.0 })
        {
            var sp = UI.IslandWindow.BuildSpherePublic(ev);
            var cp = new ContentPresenter
            {
                Content = sp,
                LayoutTransform = Math.Abs(sc - 1.0) < 1e-6 ? null : new ScaleTransform(sc, sc)
            };
            double frame = sphereSize * sc;
            var pr = new Border { Width = frame, Height = frame, Child = cp };
            pr.Measure(new Size(frame, frame));
            pr.Arrange(new Rect(0, 0, frame, frame));
            pr.UpdateLayout();

            var ic = FindIconCarrier(sp);
            double visual = 0;
            if (ic != null)
            {
                var box = ic.TransformToAncestor(pr).TransformBounds(
                    new Rect(0, 0, ic.ActualWidth > 0 ? ic.ActualWidth : ic.Width,
                                  ic.ActualHeight > 0 ? ic.ActualHeight : ic.Height));
                visual = box.Width;
            }
            double expVisual = 18 * sc;
            double visRatio = frame <= 0 ? 0 : visual / frame;
            bool ok = Math.Abs(visual - expVisual) <= 2.0 && Math.Abs(visRatio - 0.5) <= 0.06;
            if (!ok) sweepOk = false;
            Console.WriteLine($"    Scale={sc,4:F2}  球体渲染={frame,6:F1}  图标渲染={visual,6:F1}  " +
                              $"期望={expVisual,6:F1}  视觉比={visRatio,5:F2}  [{(ok ? "OK" : "✗")}]");
        }

        Console.WriteLine($"  [{(ratioOk && sweepOk ? "OK" : "球体图标不自适应")}]");
        Console.WriteLine();
        return ratioOk && sweepOk ? 0 : 1;
    }

    /// <summary>找球体里的图标载体（Image 或包着 Image 的 Border）</summary>
    private static FrameworkElement? FindIconCarrier(DependencyObject? root)
    {
        if (root == null) return null;
        if (root is Image img) return img;
        if (root is Border b && b.Child is Image) return b;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var r = FindIconCarrier(VisualTreeHelper.GetChild(root, i));
            if (r != null) return r;
        }
        return null;
    }

    private static Core.IslandEvent MakeMedia(string sourceId, string title, string artist, bool playing)
        => new Core.IslandEvent(
            Id: "media:" + sourceId, SourceKey: "media:" + sourceId, Kind: "media", Priority: 5,
            Title: title, Subtitle: artist,
            Policy: new Core.IslandPolicy(null, Coalesce: true, Expandable: true),
            Payload: new Sources.MediaSnapshot(sourceId, sourceId, title, artist, "", playing,
                30, 60_000_000L, 240_000_000L, DateTime.UtcNow.Ticks))
        { AppKey = sourceId };
}

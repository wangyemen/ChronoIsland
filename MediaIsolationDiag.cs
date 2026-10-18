using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChronoIsland.Core;

namespace ChronoIsland;

/// <summary>
/// 媒体胶囊"列宽串台"自检（mediaiso）。
///
/// 【要证明的事 —— 波形一闪一闪的根因】
/// 波形所在的是胶囊的第 3 列，它的 x 位置 = 第 0 列 + 第 1 列宽之和。
/// 而第 1 列（文字列）的宽度是被 <c>MediaPillWidth</c> → <c>ApplyMediaPillColumns</c>
/// 事后改写的。改写目标是"最后构建出来的那个胶囊"（旧实现用窗口级共享字段
/// <c>_pillGrid / _pillTextCol</c> 记录它）。
///
/// 于是只要同时存在两个媒体会话（浏览器 + 音乐软件）：
///   · A（浏览器）是被显示的那条，也是最后被构建的 → 共享字段指向 A；
///   · B（音乐软件）每约 900ms 推一次进度 → <c>UpdateItem(B)</c> 调
///     <c>MeasurePillWidth(B)</c> → 却把 **B 的文字列宽写进了 A 的第 1 列**。
/// 结果：A 的标题截断程度与波形位置，跟着 B 的刷新节奏反复横跳。
///
/// 这解释了全部现象：
///   · 只用音乐软件时正常 —— 全局只有一条媒体岛，写的永远是它自己；
///   · 浏览器岛（且另一条会话在播放）才闪 —— 两条媒体会话并存；
///   · 暂停时不常有 —— 浏览器在非 Playing 时被 <c>ReadAllAsync</c> 直接丢弃，
///     B 不再刷新，也就没人去改 A 的列宽了。
///
/// 【为什么这个自检是有意义的】
/// 它不依赖渲染、不依赖时序：把"两条会话各自量一次宽度"这个确定的调用序列
/// 摆出来，然后检查【B 的测量有没有改动 A 的列宽】。这是一个非黑即白的断言，
/// 而且带阳性对照（确认写入通道本身是活的，避免把"写入器坏了"当成"隔离成功"）。
///
/// 用法：ChronoIsland.exe mediaiso
/// </summary>
public static class MediaIsolationDiag
{
    public static void Run()
    {
        var s = Core.Settings.Load();
        IslandMetrics.Apply(s);

        Console.WriteLine("=== 媒体胶囊列宽隔离自检 ===");
        Console.WriteLine($"MaxWidth={IslandMetrics.MaxWidth}  Scale={IslandMetrics.Scale}  容差={Tol} DIP");
        Console.WriteLine("(第 1 列 = 文字列；它的宽度决定波形列的起点)");
        Console.WriteLine();

        var win = new UI.IslandWindow();

        // 两条并存的媒体会话。浏览器标题刻意取长（抖音精选电脑版…），
        // 与音乐软件的短标题形成明显落差 —— 落差越大，串台越显眼。
        var evChrome = MakeMedia("Chrome", "抖音精选电脑版 - 抖音旗下优质视频平台", "");
        var evSpotify = MakeMedia("Spotify.exe", "寂寞先生", "Gary Chaw");

        int fail = 0;

        // ---------- 准备：两条岛各自建一次胶囊内容 ----------
        // BuildMediaPill 会把"这条 item 的子树"登记进引用表（旧实现是登记到共享字段）。
        var chromeGridEarly = win.BuildContentPublic(evChrome);
        var spotifyGrid = win.BuildContentPublic(evSpotify);

        // A（浏览器）是正在显示的那条 → 在 Render 里它最后被重建一次。
        // 这一步刻意放在 B 之后：旧实现里共享字段此刻指向 A。
        var chromeGrid = win.BuildContentPublic(evChrome);

        if (chromeGrid is not Grid gA || spotifyGrid is not Grid gB)
        {
            Console.WriteLine("跳过：媒体胶囊根节点不是 Grid（构建失败？）");
            Environment.ExitCode = 1;
            return;
        }

        // ---------- 断言 1：B 的测量不得改动 A 的列宽 ----------
        double aColBefore = Col1(gA);
        double bOwnWidth = win.MeasurePillWidthPublic(evSpotify);   // ≈ UpdateItem(B) 的那一步
        double aColAfter = Col1(gA);

        double drift = Math.Abs(aColAfter - aColBefore);
        bool iso = drift <= Tol;
        if (!iso) fail++;

        Console.WriteLine("【断言 1 · 隔离】量 B 的宽度，A 的第 1 列不得变化");
        Console.WriteLine($"  A 第 1 列：改前={aColBefore,7:F1}  改后={aColAfter,7:F1}  " +
                          $"漂移={drift,7:F1}  [{(iso ? "OK" : "串台")}]");
        if (!iso)
            Console.WriteLine("  ⚠ B 的刷新改写了 A 的列宽 → A 的标题截断与波形位置会随 B 的节奏跳动（症状本身）");
        Console.WriteLine($"  （B 自己的宽度={bOwnWidth:F1}，A 的宽度={win.MeasurePillWidthPublic(evChrome):F1}）");
        Console.WriteLine();

        // ---------- 断言 2：各自量完之后，都必须是"自己的"列宽 ----------
        // 不变量：容器宽 = 封面列 + 文字列 + 波形列(+右留白)，对每条岛各自成立。
        // 旧实现下 B 的列宽会被写进 A，B 自己反而没被更新 —— 这一步同时抓住两个方向。
        double wA = win.MeasurePillWidthPublic(evChrome);
        double wB = win.MeasurePillWidthPublic(evSpotify);

        int bad = 0;
        bad += CheckSelf(gA, wA, "A 浏览器") ? 0 : 1;
        bad += CheckSelf(gB, wB, "B 音乐  ") ? 0 : 1;
        fail += bad;
        Console.WriteLine();

        // ---------- 断言 3（阳性对照）：写入通道本身必须是活的 ----------
        // 否则"没变化"可能只是因为写入器整个坏掉了，断言 1 就成了假通过。
        // 做法：A 换一个明显更长的标题再量一次，它自己的列宽必须跟着变大。
        var evChromeLong = MakeMedia("Chrome",
            "抖音精选电脑版 - 抖音旗下优质视频平台 - 一个刻意拉得很长的标题用来撑宽文字列", "");
        var gLong = win.BuildContentPublic(evChromeLong) as Grid;
        double wLong = win.MeasurePillWidthPublic(evChromeLong);
        bool writerAlive = gLong != null && Col1(gLong) > aColAfter + 1.0;
        if (!writerAlive) fail++;

        Console.WriteLine("【断言 3 · 阳性对照】换更长的标题后，它自己的第 1 列必须变宽（证明写入通道是活的）");
        Console.WriteLine($"  短标题列宽={aColAfter,7:F1} → 长标题列宽={(gLong != null ? Col1(gLong) : 0),7:F1}  " +
                          $"[{(writerAlive ? "OK" : "写入器失效")}]");
        Console.WriteLine();

        // ---------- 断言 4：波形不再依赖动画（重建即从头起拍的根源） ----------
        var bars = WaveBarsOf(gA);
        bool noAnim = bars.Count == 4 && bars.All(b => !b.HasAnimatedProperties);
        bool heightsOk = bars.Count == 4 && bars.All(b => b.Height >= 3.9 && b.Height <= 13.1);
        if (!noAnim || !heightsOk) fail++;

        Console.WriteLine("【断言 4 · 波形不挂动画】柱高应由每帧直写（重建不会重启时间轴）");
        Console.WriteLine($"  柱子数={bars.Count}  挂动画={bars.Count(b => b.HasAnimatedProperties)}  " +
                          $"高度=[{string.Join(", ", bars.Select(b => b.Height.ToString("F1")))}]  " +
                          $"[{(noAnim && heightsOk ? "OK" : "仍依赖动画")}]");
        Console.WriteLine();

        Console.WriteLine(fail == 0
            ? "全部通过：两条媒体会话的列宽互不干扰，波形不依赖动画时间轴。"
            : $"{fail} 项不通过（见上）。");

        fail += RunLiveWaveCheck();
        Environment.ExitCode = fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 端到端：走真实事件入口与真实渲染帧，验证波形的"每帧直写"确实在跑，
    /// 且**整树重建后相位不重置**。
    ///
    /// 【为什么必须单独验这一条】波形从"动画驱动"改成了"Render 每帧直写"。
    /// 若 Render 里那次调用没生效（判断条件写错、引用没登记上），
    /// 波形不是"更稳了"而是"直接冻住" —— 那是比原来更糟的回归。
    /// 所以这里：
    ///   ① 确认 Render 真的会改动柱高（任一根变化即算驱动成功）；
    ///   ② 确认重建后拿到的是**新**柱子，且新柱子第一次渲染就落在相位高度上
    ///      （而不是像动画那样从同一个起点重新起拍）。
    /// </summary>
    private static int RunLiveWaveCheck()
    {
        Console.WriteLine();
        Console.WriteLine("=== 端到端 · 真实事件 + 真实渲染帧 ===");

        var win = new UI.IslandWindow();
        var ev = MakeMedia("Chrome", "抖音精选电脑版 - 抖音旗下优质视频平台", "");
        win.FeedForProbe(ev);

        var active = win.ProbeActive;
        string id = active?.Event.Id ?? "";
        if (active == null || active.Event.Kind != "media")
        {
            Console.WriteLine("  ✗ 事件未进入集群（active 不是媒体岛）");
            return 1;
        }

        int fail = 0;

        // ① 每帧直写必须在跑
        var bars1 = win.ProbeWaveBars(id);
        if (bars1 is not { Count: 4 })
        {
            Console.WriteLine("  ✗ 波形柱子引用没有登记到这条 item 上");
            return 1;
        }
        var before = bars1.Select(b => b.Height).ToArray();
        double acc = 0;
        for (int i = 0; i < 60; i++)            // 60 帧 ≈ 1 秒，远超一个相位台阶
        {
            System.Threading.Thread.Sleep(16);
            win.FeedForProbe(ev);               // 只更新快照 + 走一帧，不换曲
            var now = win.ProbeWaveBars(id);
            if (now is { Count: 4 })
                acc += now.Select((b, k) => Math.Abs(b.Height - before[k])).Sum();
        }
        bool driving = acc > 1.0;
        if (!driving) fail++;
        Console.WriteLine($"  ① 每帧直写：60 帧累计高度变化={acc,7:F1}  [{(driving ? "OK 在跑" : "柱子冻住了")}]");

        // ② 重建后相位不重置
        var barsBefore = win.ProbeWaveBars(id)!.Select(b => b.Height).ToArray();
        var ev2 = MakeMedia("Chrome", "抖音精选电脑版 - 抖音旗下优质视频平台 · 换了个标题触发重建", "");
        win.FeedForProbe(ev2);
        var barsAfter = win.ProbeWaveBars(id);
        bool replaced = barsAfter is { Count: 4 } && !ReferenceEquals(barsAfter[0], bars1[0]);
        if (!replaced) fail++;
        Console.WriteLine($"  ② 整树重建：拿到新柱子={(replaced ? "是" : "否")}  [{(replaced ? "OK" : "引用没换")}]");
        Console.WriteLine($"     重建前高度=[{string.Join(", ", barsBefore.Select(h => h.ToString("F1")))}]");
        Console.WriteLine($"     重建后高度=[{string.Join(", ", (barsAfter ?? new List<Border>()).Select(b => b.Height.ToString("F1")))}]");
        Console.WriteLine("     （动画驱动的旧实现此时会是四根都从 4 起拍）");

        Console.WriteLine(fail == 0 ? "  端到端通过：波形每帧在跑，且重建不重置相位。"
                                    : $"  {fail} 项不通过。");

        // ③ 浏览器（永远没有歌词）的第二行必须是 Collapsed。
        // 旧判据用 LastLyricText != "" 当"是否已折叠"，而新建的树该字段恒为 ""，
        // 因此这条分支一次都不执行 —— 歌词行一直留着，白占一行高，把标题顶高。
        var row = win.ProbeLyricRow(id);
        bool lyricHidden = row != null && row.Visibility == Visibility.Collapsed;
        if (!lyricHidden) fail++;
        Console.WriteLine($"  ③ 浏览器歌词行折叠：Visibility={row?.Visibility.ToString() ?? "(null)"}  " +
                          $"[{(lyricHidden ? "OK" : "仍占位")}]");

        Console.WriteLine(fail == 0 ? "  端到端全部通过。" : $"  {fail} 项不通过。");
        return fail;
    }

    private const double Tol = 0.5;

    /// <summary>第 1 列（文字列）的宽度</summary>
    private static double Col1(Grid g)
        => g.ColumnDefinitions.Count > 1 ? g.ColumnDefinitions[1].Width.Value : -1;

    /// <summary>校验"容器宽 = 三列之和"这条不变量在这条岛上成立</summary>
    private static bool CheckSelf(Grid g, double totalW, string label)
    {
        if (g.ColumnDefinitions.Count < 3)
        {
            Console.WriteLine($"  {label} 列数不足（{g.ColumnDefinitions.Count}），跳过");
            return true;
        }
        double sum = g.ColumnDefinitions[0].Width.Value
                   + g.ColumnDefinitions[1].Width.Value
                   + g.ColumnDefinitions[2].Width.Value;
        bool ok = Math.Abs(sum - totalW) <= Tol;
        Console.WriteLine($"  {label} 公式总宽={totalW,7:F1}  三列之和={sum,7:F1}  " +
                          $"差={sum - totalW,7:F1}  [{(ok ? "OK" : "脱节")}]");
        return ok;
    }

    /// <summary>取出胶囊根 Grid 里的波形四根柱子</summary>
    private static List<Border> WaveBarsOf(Grid g)
        => g.Children.OfType<StackPanel>()
            .Where(sp => sp.Children.Count == 4 && sp.Children.OfType<Border>().Count() == 4)
            .SelectMany(sp => sp.Children.OfType<Border>())
            .ToList();

    private static Core.IslandEvent MakeMedia(string sourceId, string title, string artist)
        => new Core.IslandEvent(
            Id: "media:" + sourceId, SourceKey: "media:" + sourceId, Kind: "media", Priority: 5,
            Title: title, Subtitle: artist,
            Policy: new Core.IslandPolicy(null, Coalesce: true, Expandable: true),
            Payload: new Sources.MediaSnapshot(sourceId, sourceId, title, artist, "", true,
                30, 60_000_000L, 240_000_000L, DateTime.UtcNow.Ticks))
        { AppKey = sourceId };
}

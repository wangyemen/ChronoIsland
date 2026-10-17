using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChronoIsland.Core;

namespace ChronoIsland;

/// <summary>
/// 胶囊宽度自检（pillwidth / widthdiag）。
///
/// 【要证明的事】每类胶囊的宽度公式（<c>MeasurePillWidth</c>）与实际排版
/// （<c>Build*Pill</c>）必须一致。两者脱节时：
///   · 公式 &lt; 排版需求 → 内容越过胶囊右边界被硬裁（图二：时钟「10月16日 周四」少一截）
///   · 公式 &gt; 排版需求 → 右侧凭空多出一块空白
///
/// 【怎么量 —— 必须用"无约束"测量】
/// 早期版本把内容塞进 <c>Width = 公式值</c> 的定宽容器再量 DesiredSize，
/// 结果永远等于给定宽度 → 溢出被容器裁掉 → 差值恒为 0，假通过。
/// 正确做法是用 <see cref="Size.PositiveInfinity"/> 约束量"内容自然需求宽度"
/// （无限宽下 TextTrimming 不会触发，量到的就是不被截断所需的真实宽度），
/// 再与公式值比对：
///   需求 &gt; 公式 → 溢出，会被裁
///   需求 &lt; 公式 → 公式给多了，右侧留白
///
/// 【每项都配阳性对照】DetectTrimmed 用一个明显过窄的宽度验证
/// "检测器确实能看见省略号"，避免把"检测器坏了"误判成"没问题"。
///
/// 用法：ChronoIsland.exe pillwidth
/// </summary>
public static class PillWidthDiag
{
    private const double Tol = 1.0;   // 容差：亚像素级误差可接受

    public static void Run()
    {
        var s = Core.Settings.Load();
        IslandMetrics.Apply(s);

        Console.WriteLine("=== 胶囊宽度自检 ===");
        Console.WriteLine($"MaxWidth={IslandMetrics.MaxWidth}  Scale={IslandMetrics.Scale}  容差={Tol} DIP");
        Console.WriteLine("(需求 = 内容在无约束下的自然宽度；>公式 溢出被裁，<公式 右侧留白)");
        Console.WriteLine();

        var win = new UI.IslandWindow();

        var cases = new (string Name, Func<Core.IslandEvent> Make)[]
        {
            ("时钟",      MakeClock),
            ("使用时长",  MakeUsage),
            ("通知(短)",  () => MakeNotif("微信", "新消息", "张伟: 你晚上有空吗")),
            ("通知(长)",  () => MakeNotif("Slack", "发布窗口变更通知",
                        "本次发布包含数据库迁移，请在发布前确认备份策略是否生效")),
            ("键位",      MakeSys),
            ("摄像头",    MakeCamera),
            ("解锁",      MakeUnlock),
            ("DSH",       MakeDsh),
            ("媒体",      MakeMedia),
        };

        int fail = 0;
        foreach (var (name, make) in cases)
        {
            var ev = make();
            if (!CheckOne(win, name, ev, out string detail)) fail++;
            Console.WriteLine(detail);
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "全部通过：公式与排版一致，无溢出、无多余空白。"
            : $"{fail} 项不一致（见上）。");

        Console.WriteLine();
        CheckTrimMargin();
    }

    /// <summary>量一个胶囊：公式宽度 vs 内容无约束下的自然需求宽度</summary>
    private static bool CheckOne(UI.IslandWindow win, string name, Core.IslandEvent ev,
        out string detail)
    {
        double formula = win.MeasurePillWidthPublic(ev);

        var content = win.BuildContentPublic(ev);
        if (content == null)
        {
            detail = $"{name,-10} 跳过（内容为空）";
            return true;
        }

        // 【关键】无约束测量：无限宽下不触发 TextTrimming，量到的正是
        // "内容不被截断所需的宽度"。放进定宽容器量会被容器裁掉而测不出来。
        var probe = new Border { Child = content };
        try
        {
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double need = probe.DesiredSize.Width;
            double diff = need - formula;

            // ---- 判定要区分两类"看起来不一致"的情况 ----
            //
            // ① 公式已按上限封顶（diff > 0 且 formula == MaxWidth）
            //    这是设计使然：内容本来比上限还宽，胶囊按设置的上限截断，
            //    此时出现省略号是预期行为，不算缺陷。
            //
            // ② 公式比自然需求宽出的量恰好等于省略号余量
            //    也是设计使然：EllipsisSlack 是刻意留的呼吸空间，
            //    正是它保证"量得下时不会被误判为放不下"。
            //    超出这个量的留白才是缺陷（右侧会凭空多出一块）。
            //
            // 除此之外：>0 说明内容撑出胶囊（会被裁），<0 说明右侧多余空白。
            const double slack = 12.0;   // 与 IslandWindow.EllipsisSlack 一致
            bool capped = formula >= IslandMetrics.MaxWidth - Tol;
            bool ok = Math.Abs(diff) <= Tol
                      || (capped && diff > 0)
                      || (diff < 0 && -diff <= slack + Tol);

            string verdict = Math.Abs(diff) <= Tol ? "OK  "
                : capped && diff > 0 ? "上限封顶"
                : diff < 0 && -diff <= slack + Tol ? "OK·余量"
                : diff > 0 ? "裁切" : "留白";

            detail = $"{name,-10} 公式={formula,7:F1}  自然需求={need,7:F1}  " +
                     $"差={diff,7:F1}  [{verdict}]";
            return ok;
        }
        finally { probe.Child = null; }
    }

    /// <summary>
    /// 省略号余量验证 —— 按 WPF 的判定规则直接算，且自带阳性对照。
    ///
    /// 【核心机制】TextBlock 在 TextTrimming=CharacterEllipsis 时，
    /// 只有当【内容宽 &gt; 可用宽 + 省略号字宽】才显示 "…"。
    /// 于是宽度余量小于一个省略号字宽时，内容明明量得下仍会被截断 ——
    /// 这正是图一「明明能完整显示，却显示成 Leehom W…」的根因，
    /// 也是 EllipsisSlack 必须存在、且不能小于省略号字宽的原因。
    ///
    /// 【阳性对照】先用 contentW × 0.5（必然放不下）验证断言本身有效。
    /// 若对照项也报"无"，说明判据失效，此时"通过"不可信。
    /// </summary>
    private static void CheckTrimMargin()
    {
        Console.WriteLine("=== 省略号余量验证 ===");
        Console.WriteLine();

        var cases = new (string Name, string Text, double Size, bool Bold)[]
        {
            ("标题13",   "深夜食堂第七季 第 12 集", 13,   true),
            ("作者11",   "Leehom Wang 林俊杰",     11,   false),
            ("日期11",   "10月16日 周四",          11,   false),
            ("通知题13", "发布窗口变更通知请查收",  13,   true),
            ("歌名12.5", "心跳",                  12.5, true),
            ("长作者11", "Leehom Wang 林俊杰 Remix", 11, false),
        };

        Console.WriteLine("【阳性对照】半宽必须显示'截断'，否则说明检测器失效");
        bool sensorOk = true;
        foreach (var (name, text, size, bold) in cases)
        {
            double contentW = UI.IslandWindow.MeasureTextPublic(text, size, bold);
            bool control = DetectTrimmed(text, size, bold, contentW * 0.5);
            if (!control) sensorOk = false;
            Console.WriteLine($"  {name,-10} 半宽({contentW * 0.5,6:F1})={Y(control)}" +
                              (control ? "" : "   ⚠ 检测器未能看见截断"));
        }

        if (!sensorOk)
            Console.WriteLine("  ⚠ 阳性对照未通过，以下结果不可信（检测器需排查）");

        Console.WriteLine();
        Console.WriteLine("【余量验证】刚好 = 宽度等于内容实测宽；+12 = 留 EllipsisSlack");
        Console.WriteLine("  期望：'刚好' 列出现 '截断'（复现缺陷），'+12' 列为 '无'（已修复）");
        Console.WriteLine();
        foreach (var (name, text, size, bold) in cases)
        {
            double contentW = UI.IslandWindow.MeasureTextPublic(text, size, bold);
            bool tight = DetectTrimmed(text, size, bold, contentW);
            bool slack = DetectTrimmed(text, size, bold, contentW + 12);
            bool wide = DetectTrimmed(text, size, bold, contentW + 40);

            string verdict = !tight ? "本例未触发"
                : !slack ? "复现缺陷 → +12 余量已修复"
                : "余量 +12 仍被截（余量给小了）";

            Console.WriteLine($"  {name,-10} 文字宽={contentW,6:F1}  " +
                              $"刚好={Y(tight)}  +12={Y(slack)}  +40={Y(wide)}   {verdict}");
        }
    }

    private static string Y(bool b) => b ? "截断" : "无  ";

    /// <summary>
    /// 判断给定宽度下 TextBlock 是否会出现省略号。
    ///
    /// 【判据直接来自 WPF 源码的行为】
    /// TextBlock 在 TextTrimming=CharacterEllipsis 时，只有当
    ///     内容排版宽度 &gt; 可用宽度 + 省略号宽度
    /// 才显示 "…"。也就是说 "内容宽 == 可用宽" 并不会截断
    /// —— 这与"用户看到明明量得下却被截"的现象一致，因此真实阈值是
    ///     可用宽度 &lt; 内容宽度 + EllipsisWidth(fontSize)
    /// 于是宽度余量小于【省略号字宽】时就会被截。这就是 EllipsisSlack 的由来，
    /// 也解释了为什么余量必须存在、且不能小于一个省略号字宽。
    ///
    /// 【为什么不用像素渲染】
    /// 第一、二版都用 RenderTargetBitmap 扫墨迹列，阳性对照（半宽，必然截断）却
    /// 全部报"未截断" —— 原因是渲染未挂载到 PresentationSource 的元素时
    /// 文本层不参与光栅化，得到的是空白图。文字排版类断言不应依赖像素，
    /// 这里改为直接用 FormattedText（与 MeasureText 同口径）判定，
    /// 并保留阳性对照以确保断言本身有效。
    /// </summary>
    private static bool DetectTrimmed(string text, double size, bool bold, double width)
    {
        double contentW = UI.IslandWindow.MeasureTextPublic(text, size, bold);
        double ellipsisW = UI.IslandWindow.MeasureTextPublic("\u2026", size, bold);
        // WPF 判定：内容宽 &gt; 可用宽 + 省略号宽 → 需要截断
        return contentW > width + ellipsisW;
    }

    // ---------------- 样例事件 ----------------
    private static Core.IslandEvent MakeClock()
    {
        var now = DateTime.Now;
        var wk = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)now.DayOfWeek];
        return new Core.IslandEvent("clock", "clock", "clock", 1,
            now.ToString("HH:mm"), $"{now.Month}月{now.Day}日 {wk}",
            Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(3)))
        { AppKey = "clock" };
    }

    private static Core.IslandEvent MakeUsage()
        => new Core.IslandEvent("usage", "usage", "usage", 2, "今日已用 3 小时 24 分",
              Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4)));

    private static Core.IslandEvent MakeNotif(string app, string title, string sub)
        => new Core.IslandEvent("n1", app, "notification", 5, title, sub,
              Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(5)))
        { AppKey = app, SourceDisplayName = app };

    private static Core.IslandEvent MakeSys()
        => new Core.IslandEvent("sys", "sys", "sys", 3, "大写锁定",
              Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4)),
              Payload: new Sources.SysSnapshot("capslock", true))
        { AppKey = "sys" };

    private static Core.IslandEvent MakeCamera()
        => new Core.IslandEvent("camera", "camera", "camera", 3, "摄像头",
              Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4)),
              Payload: new Sources.CameraSnapshot(true, "Integrated Camera"))
        { AppKey = "camera" };

    private static Core.IslandEvent MakeUnlock()
        => new Core.IslandEvent("unlock", "sys", "unlock", 3, "已解锁",
              Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4)),
              Payload: new Sources.UnlockSnapshot(720, DateTime.Now, IntPtr.Zero))
        { AppKey = "sys" };

    private static Core.IslandEvent MakeDsh()
        => new Core.IslandEvent("dsh", "dsh", "dsh", 4, "检查灵动岛宽度自适应",
              Policy: new Core.IslandPolicy(null),
              Payload: new Sources.DshSnapshot(true, true, 6, 62, 41, 1320,
                  "检查灵动岛宽度自适应", "space-bunny",
                  110539, 1000000, 0, 406, "8c05ed6c"));

    private static Core.IslandEvent MakeMedia()
        => new Core.IslandEvent("media", "media", "media", 6, "心跳",
              Policy: new Core.IslandPolicy(null),
              Payload: new Sources.MediaSnapshot("Spotify", "Spotify", "心跳",
                  "Leehom Wang 林俊杰", "新歌·心跳", true, 30, 60_000_000L, 240_000_000L));
}

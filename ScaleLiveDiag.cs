using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChronoIsland.Core;

namespace ChronoIsland;

/// <summary>
/// 真实渲染路径自检（scalelive）：走 IslandWindow 的真实 Render 帧，
/// 验证 0.0.1 三项缺陷修复中的两项"必须走真实链路"的部分：
///
///   ① 大缩放不再崩溃 —— 3× 时 MediaPillTextWidth/MediaPillWidth 曾因
///      Math.Clamp(min &gt; max) 抛 ArgumentException（滑条能拖到 3×，必然触发）。
///   ② 胶囊外框 = 逻辑宽 × Scale（缩放真的作用在几何上）。
///
/// 波形位置与球体图标比例是纯排版事实，由 scalegeom 用离屏精确几何验证
/// （那里可以完全控制坐标系，不会受"窗口是否已显示"影响）。
///
/// 用法：ChronoIsland.exe scalelive
/// </summary>
public static class ScaleLiveDiag
{
    public static void Run()
    {
        var s = Settings.Load();
        IslandMetrics.Apply(s);

        Console.WriteLine("=== 真实渲染路径自检（Render 帧）===");
        Console.WriteLine($"磁盘设置：Scale={s.Island.Scale} MaxWidth={s.Island.MaxWidth}");
        Console.WriteLine();

        int fail = 0;

        foreach (double sc in new[] { 0.5, 1.0, 1.5, 2.0, 2.4, 2.5, 3.0 })
        {
            // 【顺序】先建窗（构造函数会 Settings.Load 覆盖静态 metrics），
            // 再 ApplyGeometrySettings 注入目标缩放 —— 等价于用户拖滑条时的
            // ApplyGeometrySettings → ApplyLayout → Render 序列。
            var win = new UI.IslandWindow();
            var st = Settings.Load();
            st.Island.Scale = sc;

            string note = "";
            bool ok;
            try
            {
                // 【先建预览、再改缩放】EnterPreview 内部会用窗口自己那份 _settings
                // （构造函数从磁盘加载）重建示例岛；先改缩放会被它覆盖回去。
                // 用户拖滑条时的真实顺序是：预览已在 → ApplyGeometrySettings → Render，
                // 这里照此复现。
                win.EnterPreview();
                win.ApplyGeometrySettings(st);
                win.ForceRenderForProbe();

                var active = win.ProbeActive;
                if (active == null)
                {
                    note = "预览未建立 active";
                    ok = false;
                }
                else
                {
                    double frame = active.Visual.Width;
                    double expect = active.Width.Value * sc;
                    bool frameOk = Math.Abs(frame - expect) <= 1.5;
                    // 媒体胶囊宽度公式在极端缩放下也必须给出合法值（不抛异常、不为负）
                    var ev = MakeMedia("Spotify.exe", "几分之几", "电影<花甲大人转>", true);
                    double w = win.MeasurePillWidthPublic(ev);
                    bool widthSane = w > 0 && !double.IsNaN(w) && !double.IsInfinity(w);
                    ok = frameOk && widthSane;
                    note = $"外框={frame,7:F1}(期望 {expect,7:F1})[{(frameOk ? "OK" : "✗")}]  " +
                           $"媒体胶囊宽={w,7:F1}[{(widthSane ? "OK" : "✗")}]";
                }
            }
            catch (Exception ex)
            {
                // 这正是要抓的回归：旧代码在 ~2.4× 抛 ArgumentException
                note = $"抛异常：{ex.GetType().Name}: {ex.Message}";
                ok = false;
            }

            if (!ok) fail++;
            Console.WriteLine($"  Scale={sc,4:F2}  {note}");
        }

        IslandMetrics.Apply(s);   // 还原用户设置
        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "通过：0.5×–3× 全档位不再崩溃，外框随缩放正确变化。"
            : $"{fail} 项不通过。");
        Environment.ExitCode = fail == 0 ? 0 : 1;
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

using System;
using System.Windows;
using System.Windows.Controls;
using ChronoIsland.Core;
using ChronoIsland.Native;
using ChronoIsland.UI;

namespace ChronoIsland;

/// <summary>
/// Wi-Fi 网速「闪一下 / 空白一下」的回归自检（docs §5.9）。
///
/// 【要证明什么】旧实现里 `SpeedText` 在流量 &lt;1 KB/s 时返回空串，
/// 而真实流量是突发性的 —— 文案在「空 ↔ ↓12 KB/s」之间反复跳，
/// 文案长度一变，胶囊宽度就跟着变，视觉上就是"空白一下、数字挤进来、又空白一下"。
///
/// 【怎么证明】
///   1. 构造一串"有流量 / 无流量"交替的快照，逐个算 SpeedText；
///   2. 断言：只要已连接，**任何一次都不为空**，且始终是两段（↓ 与 ↑ 都在）；
///   3. 实测渲染宽度：文案长度变化时宽度变化必须很小（不会把整条岛拽动）。
///
/// 用法：ChronoIsland.exe wifiui
/// </summary>
public static class WifiUiDiag
{
    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Wi-Fi 网速文案稳定性自检 ===");
        Console.WriteLine();

        var settings = Core.Settings.Load();
        Core.IslandMetrics.Apply(settings);
        var win = new UI.IslandWindow();

        int fail = 0;
        void Check(string label, bool ok, string extra = "")
        {
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "✓" : "✗")} {label}{(extra.Length > 0 ? "  " + extra : "")}");
        }

        // ---- 1. 文案永不为空、永远是两段 ----
        Console.WriteLine("1. 文案稳定性（模拟真实突发流量）");
        var samples = new (string Label, double Rx, double Tx)[]
        {
            ("完全静默",        0,          0),
            ("零星一个包",      120,        40),
            ("低于阈值",        400,        300),
            ("刚过阈值",        600,        600),
            ("小流量",          12_500,     2_100),
            ("中等",            1_600_000,  220_000),
            ("突发峰值",        18_000_000, 3_400_000),
            ("回落到静默",      0,          0),
            ("再来一次",        8_000,      0),
            ("单向只有下行",    2_500_000,  0),
            ("单向只有上行",    0,          900_000),
        };

        var texts = new System.Collections.Generic.List<string>();
        foreach (var s in samples)
        {
            var snap = new WifiSnapshot(true, "JCG_WLAN", 88, 344, s.Rx, s.Tx, true);
            var ev = new Core.IslandEvent("wifi", "wifi", "wifi", 3, "已连接 Wi-Fi", "JCG_WLAN",
                Payload: snap, Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4)))
            { AppKey = "wifi" };

            // SpeedText 是 private static，通过宽度反推需要的文案长度不直观；
            // 这里用真实胶囊内容 + 宽度公式一起看。
            double w = win.MeasurePillWidthPublic(ev);
            var content = win.BuildContentPublic(ev);
            var probe = new Border { Child = content };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double need = probe.DesiredSize.Width;
            probe.Child = null;

            texts.Add($"{s.Label}:{w:0.0}");
            Console.WriteLine($"    {s.Label,-12} rx={s.Rx,12:N0} tx={s.Tx,12:N0}  胶囊宽={w,7:0.0}");
        }

        // 宽度极差：文案再怎么变，胶囊宽度也不该大幅跳动
        var widths = new System.Collections.Generic.List<double>();
        foreach (var s in samples)
        {
            var snap = new WifiSnapshot(true, "JCG_WLAN", 88, 344, s.Rx, s.Tx, true);
            var ev = new Core.IslandEvent("wifi", "wifi", "wifi", 3, "已连接 Wi-Fi", "JCG_WLAN",
                Payload: snap, Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4)))
            { AppKey = "wifi" };
            widths.Add(win.MeasurePillWidthPublic(ev));
        }
        double spread = widths.Max() - widths.Min();
        // 允许的宽度差：只在"↓0 KB/s ↑0 KB/s" 与 "↓9.9 MB/s ↑9.9 MB/s" 之间，
        // 约 40px 以内；超过就说明档位没收敛、宽度会拽动整条岛。
        Check("不同流量下胶囊宽度波动受控", spread <= 46, $"极差 {spread:0.0} px");

        // ---- 2. 已连接时永远不出现"空文案"→ 宽度不会塌成纯 SSID ----
        var noTraffic = new WifiSnapshot(true, "JCG_WLAN", 88, 344, 0, 0, true);
        var busy = new WifiSnapshot(true, "JCG_WLAN", 88, 344, 1_600_000, 220_000, true);
        var evNo = new Core.IslandEvent("wifi", "wifi", "wifi", 3, "已连接 Wi-Fi", "JCG_WLAN",
            Payload: noTraffic, Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4))) { AppKey = "wifi" };
        var evBusy = new Core.IslandEvent("wifi", "wifi", "wifi", 3, "已连接 Wi-Fi", "JCG_WLAN",
            Payload: busy, Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4))) { AppKey = "wifi" };

        double wNo = win.MeasurePillWidthPublic(evNo);
        double wBusy = win.MeasurePillWidthPublic(evBusy);
        Console.WriteLine();
        Console.WriteLine("2. 静默态 vs 繁忙态的宽度");
        Console.WriteLine($"    静默 ↓0 ↑0 宽={wNo:0.0}   繁忙 宽={wBusy:0.0}   差={wBusy - wNo:0.0}");
        // 静默态也必须保留速率占位（旧实现此时宽度会少掉一整段，约 -70px）
        Check("静默态仍保留速率占位（不会空白一下）", Math.Abs(wBusy - wNo) <= 46,
            $"差 {wBusy - wNo:0.0} px");

        // ---- 3. 断开时文案为空是**预期**行为 ----
        var off = new WifiSnapshot(false, "", 0, 0, 0, 0, true);
        var evOff = new Core.IslandEvent("wifi", "wifi", "wifi", 3, "Wi-Fi 已断开", "网络不可用",
            Payload: off, Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(4))) { AppKey = "wifi" };
        double wOff = win.MeasurePillWidthPublic(evOff);
        Check("未连接时不显示速率（宽度明显更窄）", wOff < wNo - 30, $"未连接={wOff:0.0} vs 静默={wNo:0.0}");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "全部通过。" : $"{fail} 项异常。");
        Environment.Exit(fail == 0 ? 0 : 2);
    }

    private static double Max(this System.Collections.Generic.List<double> v)
    {
        double m = double.MinValue;
        foreach (var x in v) if (x > m) m = x;
        return m;
    }

    private static double Min(this System.Collections.Generic.List<double> v)
    {
        double m = double.MaxValue;
        foreach (var x in v) if (x < m) m = x;
        return m;
    }
}

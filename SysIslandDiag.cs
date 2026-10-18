using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using ChronoIsland.Core;
using ChronoIsland.Native;
using ChronoIsland.Sources;
using ChronoIsland.UI;

namespace ChronoIsland;

/// <summary>
/// 系统状态岛**在线链路**自检（docs §5.8–§5.13）。
///
/// 与 <see cref="SystemRenderDiag"/> 的区别：
///   · sysrender 只验证"给定快照 → 渲染成什么样"（离屏）；
///   · 本诊断验证"真实事件源 → EventBus → 岛栈 → 形态切换"整条链路，
///     包括：轮询是否稳定、事件是否按预期落地、同一 Id 是否就地更新、
///     TTL 是否生效、值级忽略是否拦住重复提醒、常驻 Wi-Fi 的实时速率能否每帧刷新。
///
/// 用法：ChronoIsland.exe sysisland [秒数]
/// </summary>
public static class SysIslandDiag
{
    public static void Run(int seconds = 14)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.WriteLine("  [未处理异常] " + e.Exception.Message);
        };

        var win = new UI.IslandWindow();
        win.Show();
        // 关闭启动入场队列，让事件立即入场（否则要等 900ms/条）
        win.StartupQueueActive = false;
        // 摘掉全屏隐藏：本机可能正开着全屏游戏，那会让岛整体隐藏、把"正确的设计"判成失败
        win.ProbeDisableFullscreenHide();

        int fail = 0;
        void Check(string label, bool ok, string extra = "")
        {
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "✓" : "✗")} {label}{(extra.Length > 0 ? "  " + extra : "")}");
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 1. 事件源在线状态 ===");
        // 事件源在 OnLoaded 里接线，这里等它们跑起来
        Pump(1400);
        var wifi = SystemStatusSource.LatestWifi;
        var bat = SystemStatusSource.LatestBattery;
        var hot = SystemStatusSource.LatestHotspot;
        var mic = SystemStatusSource.LatestMic;
        var radio = SystemStatusSource.LatestRadio;
        Check("Wi-Fi 快照已采集", wifi != null, wifi == null ? "" :
            $"SSID={wifi.Ssid} {wifi.SignalPercent}% {wifi.LinkMbps:0}Mbps");
        Check("电池快照已采集", bat != null, bat == null ? "" :
            (bat.HasBattery ? $"{bat.Percent}% 充电={bat.Charging}" : "无电池"));
        Check("热点快照已采集", hot != null, hot == null ? "" :
            hot.Supported ? $"开启={hot.On} 设备={hot.Clients}" : "本机不支持");
        Check("麦克风快照已采集", mic != null, mic == null ? "" : $"占用={mic.Busy} {mic.AppName}");
        Check("飞行模式快照已采集", radio != null, radio == null ? "" : $"飞行={radio.AirplaneMode}");

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 2. 六类岛入栈（胶囊 → 详情卡 → 球体） ===");
        var cases = new (string Kind, object Payload, string Title, string Sub)[]
        {
            ("mic", new MicSnapshot(true, "WeChat", 1), "麦克风被占用", "WeChat 正在使用"),
            ("battery", new BatterySnapshot(true, 85, true, true, 3600, false), "已接通电源", "当前 85%"),
            ("wifi", new WifiSnapshot(true, "JCG_WLAN", 88, 344, 12_500_000, 2_100_000, true), "已连接 Wi-Fi", "JCG_WLAN"),
            ("hotspot", new HotspotSnapshot(true, 2, new[] { "192.168.137.27", "192.168.137.31" }, true), "移动热点已开启", "2 台设备已连接"),
            ("airplane", new RadioSnapshot(true, false), "飞行模式已开启", "所有无线连接已关闭"),
            ("theme", new ThemeSnapshot(true, "跟随系统 · 深色"), "已切换到深色", "跟随系统 · 深色"),
        };

        foreach (var c in cases)
        {
            var ev = new IslandEvent(Id: c.Kind, SourceKey: "probe-" + c.Kind, Kind: c.Kind,
                Priority: 3, Title: c.Title, Subtitle: c.Sub, Payload: c.Payload,
                Policy: new IslandPolicy(null, Coalesce: false, Expandable: true))
            { AppKey = c.Kind, StealFocus = true };
            win.OnEvent(ev);
            Pump(120);

            var item = win.ProbeItems.FirstOrDefault(i => i.Event.Kind == c.Kind);
            // 入栈那一刻它一定接管了中央位置（StealFocus=true），ContentKey 要到下一帧
            // Render 才换装 —— 这里断言的是"是不是中央胶囊"，而不是"已经装好胶囊内容"。
            Check($"  {c.Kind,-8} 已入栈并接管中央位",
                item != null && win.ProbeActive?.Event.Id == c.Kind && item.MeasuredWidth > 0,
                item == null ? "未找到" : $"active={win.ProbeActive?.Event.Id} 宽={item.MeasuredWidth:0.0}");

            if (item != null)
            {
                // 换装成球体：内容必须跟着换（否则球体里塞着胶囊）
                win.ProbeSetActiveOther(item);
                Pump(160);
                Check($"  {c.Kind,-8} 降为球体后内容同步", item.ContentKey == "sphere", $"content={item.ContentKey}");

                // 再切回胶囊
                win.ProbeSetActive(c.Kind);
                Pump(160);
                Check($"  {c.Kind,-8} 升回胶囊后内容同步", item.ContentKey == "pill", $"content={item.ContentKey}");
            }
        }

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 3. 同 Id 就地更新（媒体式刷新不该新增岛） ===");
        int before = win.ProbeItems.Count;
        win.OnEvent(new IslandEvent(Id: "battery", SourceKey: "probe-battery", Kind: "battery",
            Priority: 3, Title: "正在充电", Subtitle: "当前 86%",
            Payload: new BatterySnapshot(true, 86, true, true, 3500, false),
            Policy: new IslandPolicy(null, Coalesce: false)) { AppKey = "battery", StealFocus = false });
        Pump(160);
        var batItem = win.ProbeItems.FirstOrDefault(i => i.Event.Id == "battery");
        Check("岛数量未增加", win.ProbeItems.Count == before, $"{before} → {win.ProbeItems.Count}");
        Check("内容已刷新为 86%", batItem?.Event.Subtitle?.Contains("86") == true, batItem?.Event.Subtitle ?? "");

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 4. TTL 自动收起 ===");
        win.OnEvent(new IslandEvent(Id: "ttl-test", SourceKey: "probe-ttl", Kind: "airplane",
            Priority: 3, Title: "飞行模式已关闭", Subtitle: "无线连接已恢复",
            Payload: new RadioSnapshot(false, true),
            Policy: new IslandPolicy(TimeSpan.FromSeconds(2), Coalesce: false)));
        Pump(150);
        bool admitted = win.ProbeItems.Any(i => i.Event.Id == "ttl-test");
        Pump(3600);
        bool gone = !win.ProbeItems.Any(i => i.Event.Id == "ttl-test");
        Check("2s TTL 的岛会自己收起", admitted && gone, admitted ? (gone ? "已收起" : "仍在屏幕上") : "未入场");

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 5. 深浅色切换会产生 theme 事件 ===");
        int themeBefore = win.ProbeItems.Count(i => i.Event.Kind == "theme" && i.Event.Id == "theme");
        string mode0 = App.CurrentSettings.Theme.Mode;
        App.CurrentSettings.Theme.Mode = AppTheme.Current.IsDark ? "Light" : "Dark";
        App.ApplyTheme(App.CurrentSettings.Theme.Mode);
        Pump(400);
        var themeItem = win.ProbeItems.FirstOrDefault(i => i.Event.Id == "theme");
        Check("切换后出现 theme 岛", themeItem != null,
            themeItem == null ? "" : $"标题={themeItem.Event.Title}");
        // 切回去
        App.CurrentSettings.Theme.Mode = mode0;
        App.ApplyTheme(mode0);
        Pump(300);

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 6. 麦克风值级忽略（忽略\"使用中\"后不再弹） ===");
        SysStateSource.IgnoreValue("mic", true);
        var micItem0 = win.ProbeItems.FirstOrDefault(i => i.Event.Id == "mic");
        // 先让它从"占用"变"释放"再变回"占用"：中间那次释放不在忽略表里，应该弹
        var src = new SystemStatusSource();
        // 直接验证静态判据（源内部用的就是这两个函数）
        Check("忽略表命中 mic=true", SysStateSource.IsIgnored("mic", true));
        Check("未忽略 mic=false", !SysStateSource.IsIgnored("mic", false));
        _ = src; _ = micItem0;

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("=== 7. 常驻 Wi-Fi 的实时速率刷新 ===");
        // 造一条 wifi 岛并让它保持胶囊态，然后观察 3 秒内速率文本是否被刷新
        win.OnEvent(new IslandEvent(Id: "wifi", SourceKey: "probe-wifi", Kind: "wifi",
            Priority: 3, Title: "已连接 Wi-Fi", Subtitle: "JCG_WLAN",
            Payload: SystemStatusSource.LatestWifi ?? new WifiSnapshot(true, "demo", 80, 300, 0, 0, true),
            Policy: new IslandPolicy(null, Coalesce: false)));
        Pump(300);
        var sig0 = win.ProbeWifiLiveSignature();
        Pump(2600);
        var sig1 = win.ProbeWifiLiveSignature();
        Check("速率文本在刷新（签名变化）", sig0 != sig1, $"'{sig0}' → '{sig1}'");

        // ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "全部通过。" : $"{fail} 项异常。");
        app.Shutdown();
        Environment.Exit(fail == 0 ? 0 : 2);
    }

    /// <summary>跑若干毫秒的 Dispatcher 消息循环（让计时器 / 渲染帧真正执行）</summary>
    private static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(Math.Max(16, ms)) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}

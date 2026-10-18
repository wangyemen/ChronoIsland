using System;
using System.Collections.Generic;
using System.Linq;
using ChronoIsland.Core;
using ChronoIsland.Native;
using ChronoIsland.Sources;

namespace ChronoIsland;

/// <summary>
/// 系统状态源的**差分逻辑**自检（docs §5.8–§5.12）。
///
/// 通过注入 <see cref="ISystemSnapshotProvider"/> 伪造快照，把每个"变化 → 发事件"的
/// 分支都跑一遍，**全程不触碰真实系统**（不会真的开飞行模式、拔电源、占麦克风）。
///
/// 覆盖面：Wi-Fi 连上 / 切换 / 断开、热点开关与设备增减、飞行模式开关、
///         电池插拔 / 充放电 / 低电量 / 充满（含"每周期只提醒一次"）、
///         麦克风占用 / 释放 / 值级忽略。
///
/// 用法：ChronoIsland.exe sysdiff
/// </summary>
public static class SysDiffDiag
{
    /// <summary>可编程的假快照源：测试里直接改字段，下一次 Tick 就读到新值</summary>
    private sealed class FakeProvider : ISystemSnapshotProvider
    {
        public WifiSnapshot Wifi = new(false, "", 0, 0, 0, 0, true);
        public BatterySnapshot Battery = new(true, 80, false, false, 7200, false);
        public HotspotSnapshot Hotspot = new(true, 0, Array.Empty<string>(), true);
        public MicSnapshot Mic = new(false, "", 0);
        public RadioSnapshot Radio = new(false, true);
        public BluetoothSnapshot Bluetooth = new(true, Array.Empty<string>(), 3);

        public WifiSnapshot ReadWifi() => Wifi;
        public BatterySnapshot ReadBattery() => Battery;
        public HotspotSnapshot ReadHotspot() => Hotspot;
        public MicSnapshot ReadMicrophone() => Mic;
        public RadioSnapshot ReadRadio() => Radio;
        public BluetoothSnapshot ReadBluetooth() => Bluetooth;
    }

    /// <summary>收集源发出的所有事件（不走 UI，直接看总线出口）</summary>
    private sealed class CapturingSink
    {
        public readonly List<IslandEvent> Events = new();
        public void OnEvent(IslandEvent ev) => Events.Add(ev);
        public IEnumerable<IslandEvent> Of(string kind) => Events.Where(e => e.Kind == kind);
        public void Clear() => Events.Clear();
    }

    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 系统状态源 · 差分逻辑自检（全程使用假快照，不触碰真实系统） ===");
        Console.WriteLine();

        int fail = 0;
        void Check(string label, bool ok, string extra = "")
        {
            if (!ok) fail++;
            Console.WriteLine($"  {(ok ? "✓" : "✗")} {label}{(extra.Length > 0 ? "  " + extra : "")}");
        }

        // 源内部按"每 N 秒采一次"分频（Wi-Fi 1s / 麦克风热点 2s / 飞行模式 3s / 电池 5s），
        // 所以要跑满 1、2、3、5 的公倍数 30 拍，才能保证**每个信号都至少被采到一次**。
        // 状态没变时后续拍不会重复发事件，因此连跑多拍不会污染"只提醒一次"之类的断言。
        void Pump(SystemStatusSource s) { for (int i = 0; i < 30; i++) s.ProbeTick(); }

        var settings = Core.Settings.Load();
        var fake = new FakeProvider();
        var sink = new CapturingSink();
        var bus = new EventBus
        {
            // 自检要一秒内连发多条断言：关掉同类合并与限流，
            // 否则 200ms 内的第二次采样会被合并逻辑吞掉，测不出差分行为。
            CoalesceWindow = TimeSpan.Zero,
            RateLimitEnabled = false
        };
        bus.Subscribe(sink.OnEvent);            // 不设 Marshaler → 直调，同步拿到事件

        var src = new SystemStatusSource();
        src.SetProvider(fake);
        src.Configure(settings);
        src.Start(bus);                          // 首轮只建基线

        // ---- 基线：首轮不该发任何事件 ----
        Check("首轮基线不发事件", sink.Events.Count == 0, $"{sink.Events.Count} 条");

        // ================= Wi-Fi =================
        Console.WriteLine();
        Console.WriteLine("Wi-Fi（docs §5.9）");
        // 【必须先关掉常驻】本机 settings.json 里 PinSpeed 可能已被前一次设置操作写成 true，
        // 那样第一条 wifi 事件的 TTL 就是 null，"不常驻时应带 TTL"这条断言会假失败。
        var plain = Core.Settings.Load();
        plain.Network.PinSpeed = false;
        src.Configure(plain);

        fake.Wifi = new WifiSnapshot(true, "Home_5G", 82, 866, 0, 0, true);
        Pump(src);
        var wifiUp = sink.Of("wifi").LastOrDefault();
        Check("连上 Wi-Fi 会发事件", wifiUp != null, wifiUp?.Title ?? "");
        Check("标题/副标题带上 SSID",
            wifiUp?.Title == "已连接 Wi-Fi" && wifiUp.Subtitle == "Home_5G",
            $"{wifiUp?.Title} / {wifiUp?.Subtitle}");
        Check("未开启常驻时 TTL 不为 null", wifiUp?.Policy?.Ttl != null,
            wifiUp?.Policy?.Ttl?.ToString() ?? "null");

        sink.Clear();
        Pump(src);                          // 同一状态再采：不该重复发
        Check("状态没变就不重复发", sink.Of("wifi").Count() == 0);

        fake.Wifi = new WifiSnapshot(true, "Office_6G", 70, 1200, 0, 0, true);
        Pump(src);
        Check("换网会发事件", sink.Of("wifi").LastOrDefault()?.Title == "已切换 Wi-Fi",
            sink.Of("wifi").LastOrDefault()?.Title ?? "");

        sink.Clear();
        fake.Wifi = new WifiSnapshot(false, "", 0, 0, 0, 0, true);
        Pump(src);
        var down = sink.Of("wifi").LastOrDefault();
        Check("断开 Wi-Fi 会发事件", down?.Title == "Wi-Fi 已断开", down?.Title ?? "");
        Check("断开事件带上原 SSID", down?.Subtitle == "Office_6G", down?.Subtitle ?? "");

        // 常驻模式：TTL 应该被置空（岛不自动收起）
        sink.Clear();
        var pinSettings = Core.Settings.Load();
        pinSettings.Network.PinSpeed = true;
        src.Configure(pinSettings);
        fake.Wifi = new WifiSnapshot(true, "Home_5G", 90, 866, 1000, 500, true);
        Pump(src);
        var pinned = sink.Of("wifi").LastOrDefault();
        Check("常驻模式下 TTL = null（不自动收起）", pinned?.Policy?.Ttl == null,
            pinned?.Policy?.Ttl?.ToString() ?? "null");
        Check("常驻模式下抢焦点", pinned?.StealFocus == true);
        src.Configure(settings);
        // ================= 移动热点 =================
        Console.WriteLine();
        Console.WriteLine("移动热点（docs §5.10）");
        sink.Clear();
        fake.Hotspot = new HotspotSnapshot(true, 1, new[] { "192.168.137.27" }, true);
        Pump(src);        // 热点是 2s 周期
        var hotJoin = sink.Of("hotspot").LastOrDefault();
        Check("设备接入热点会发事件", hotJoin?.Title == "设备接入热点", hotJoin?.Title ?? "");

        sink.Clear();
        fake.Hotspot = new HotspotSnapshot(true, 0, Array.Empty<string>(), true);
        Pump(src);
        var hotLeave = sink.Of("hotspot").LastOrDefault();
        Check("设备全部离开会发事件", hotLeave?.Title == "设备离开热点", hotLeave?.Title ?? "");
        Check("0 台时副标题明确", hotLeave?.Subtitle == "当前没有设备连接", hotLeave?.Subtitle ?? "");

        sink.Clear();
        fake.Hotspot = new HotspotSnapshot(false, 0, Array.Empty<string>(), true);
        Pump(src);
        Check("关闭热点会发事件",
            sink.Of("hotspot").LastOrDefault()?.Title == "移动热点已关闭",
            sink.Of("hotspot").LastOrDefault()?.Title ?? "");

        // ================= 飞行模式 =================
        Console.WriteLine();
        Console.WriteLine("飞行模式（docs §5.11）");
        sink.Clear();
        fake.Radio = new RadioSnapshot(true, false);
        Pump(src);   // 飞行模式是 3s 周期
        var airOn = sink.Of("airplane").LastOrDefault();
        Check("打开飞行模式会发事件", airOn?.Title == "飞行模式已开启", airOn?.Title ?? "");
        Check("副标题说明无线已全部关闭", airOn?.Subtitle == "所有无线连接已关闭", airOn?.Subtitle ?? "");

        sink.Clear();
        fake.Radio = new RadioSnapshot(true, true);     // 飞行模式下单独开 Wi-Fi
        Pump(src);
        Check("飞行模式内单独开 Wi-Fi 不重复发（飞行模式本身没变）",
            sink.Of("airplane").Count() == 0);

        sink.Clear();
        fake.Radio = new RadioSnapshot(false, true);
        Pump(src);
        Check("关闭飞行模式会发事件",
            sink.Of("airplane").LastOrDefault()?.Title == "飞行模式已关闭",
            sink.Of("airplane").LastOrDefault()?.Title ?? "");

        // ================= 电池 =================
        Console.WriteLine();
        Console.WriteLine("电池 / 电源（docs §5.8）");
        sink.Clear();
        fake.Battery = new BatterySnapshot(true, 80, true, true, 1800, false);
        Pump(src);   // 电池是 5s 周期
        var plug = sink.Of("battery").LastOrDefault();
        Check("接通电源会发事件", plug?.Title == "已接通电源", plug?.Title ?? "");
        Check("副标题带充电状态与电量",
            plug?.Subtitle?.Contains("充电") == true && plug?.Subtitle?.Contains("80") == true,
            plug?.Subtitle ?? "");

        sink.Clear();
        fake.Battery = new BatterySnapshot(true, 60, false, false, 3600, false);
        Pump(src);
        var unplug = sink.Of("battery").LastOrDefault();
        Check("拔掉电源会发事件", unplug?.Title == "已改用电池", unplug?.Title ?? "");
        Check("副标题带预计可用时间", unplug?.Subtitle?.Contains("预计可用") == true, unplug?.Subtitle ?? "");

        // 低电量：只提醒一次
        sink.Clear();
        fake.Battery = new BatterySnapshot(true, 15, false, false, 1800, false);
        Pump(src);
        int lowCount = sink.Of("battery").Count(e => e.Title == "电量不足");
        Check("低电量提醒只发一次", lowCount == 1, $"{lowCount} 次");
        var lowEv = sink.Of("battery").FirstOrDefault(e => e.Title == "电量不足");
        Check("低电量优先级 +1（更醒目）", (lowEv?.Priority ?? 0) > src.Priority,
            $"priority={lowEv?.Priority}");

        // 继续掉电不重复提醒
        sink.Clear();
        fake.Battery = new BatterySnapshot(true, 10, false, false, 900, false);
        Pump(src);
        Check("掉到更低电量不再重复提醒", sink.Of("battery").Count() == 0);

        // 充满：只提醒一次
        sink.Clear();
        fake.Battery = new BatterySnapshot(true, 100, false, true, 0, false);
        Pump(src);
        int fullCount = sink.Of("battery").Count(e => e.Title == "电池已充满");
        Check("充满提醒只发一次", fullCount == 1, $"{fullCount} 次");

        // 台式机（无电池）→ 全程静默
        sink.Clear();
        fake.Battery = new BatterySnapshot(false, 0, false, true, 0, false);
        Pump(src);
        Check("无电池（台式机）整块静默", sink.Of("battery").Count() == 0);

        // ================= 蓝牙 =================
        Console.WriteLine();
        Console.WriteLine("蓝牙（docs §5.14）");

        // 连接一台
        sink.Clear();
        fake.Bluetooth = new BluetoothSnapshot(true, new[] { "Redmi Buds 5 Pro" }, 3);
        Pump(src);
        var btOn = sink.Of("bluetooth").LastOrDefault();
        Check("设备连接会发事件", btOn?.Title == "蓝牙设备已连接", btOn?.Title ?? "");
        Check("副标题带设备名", btOn?.Subtitle?.Contains("Redmi Buds 5 Pro") == true, btOn?.Subtitle ?? "");
        Check("连接时优先级 +1（更醒目）", (btOn?.Priority ?? 0) > src.Priority, $"priority={btOn?.Priority}");

        // 再连一台：数量 1→2。标题说的是"新增了哪些"，不是总数
        sink.Clear();
        fake.Bluetooth = new BluetoothSnapshot(true, new[] { "Redmi Buds 5 Pro", "BT5.2 Mouse" }, 3);
        Pump(src);
        var btMore = sink.Of("bluetooth").LastOrDefault();
        Check("第二台接入会发事件", btMore != null, btMore?.Title ?? "");
        Check("只报新增的那台（不重复报已在线的）",
            btMore?.Subtitle?.Contains("BT5.2 Mouse") == true
            && btMore?.Subtitle?.Contains("Redmi Buds 5 Pro") != true,
            btMore?.Subtitle ?? "");

        // 断一台（还剩一台）
        sink.Clear();
        fake.Bluetooth = new BluetoothSnapshot(true, new[] { "Redmi Buds 5 Pro" }, 3);
        Pump(src);
        var btOff = sink.Of("bluetooth").LastOrDefault();
        Check("设备断开会发事件", btOff?.Title == "蓝牙设备已断开", btOff?.Title ?? "");
        Check("副标题带断开设备名", btOff?.Subtitle?.Contains("BT5.2 Mouse") == true, btOff?.Subtitle ?? "");

        // 【换设备：数量不变但集合变了】只看数量会漏掉
        sink.Clear();
        fake.Bluetooth = new BluetoothSnapshot(true, new[] { "EWEADN X23 Pro" }, 3);
        Pump(src);
        Check("换设备（数量不变、集合变了）也能识别",
            sink.Of("bluetooth").Count() >= 1,
            $"事件 {sink.Of("bluetooth").Count()} 条");

        // 全部断开
        sink.Clear();
        fake.Bluetooth = new BluetoothSnapshot(true, Array.Empty<string>(), 3);
        Pump(src);
        Check("全部断开 → 事件 + 设备名",
            sink.Of("bluetooth").LastOrDefault()?.Title == "蓝牙设备已断开",
            sink.Of("bluetooth").LastOrDefault()?.Title ?? "");

        // 状态没变不重复发
        sink.Clear();
        Pump(src);
        Check("状态未变不重复发", sink.Of("bluetooth").Count() == 0);

        // 无蓝牙适配器 → 整块静默
        sink.Clear();
        fake.Bluetooth = BluetoothSnapshot.Unsupported;
        Pump(src);
        fake.Bluetooth = new BluetoothSnapshot(true, new[] { "X" }, 1);
        Pump(src);
        Check("无蓝牙适配器时静默（不因插拔适配器乱弹）",
            !sink.Of("bluetooth").Any(e => e.Title.Contains("已连接") && e.Subtitle == "X"),
            $"{sink.Of("bluetooth").Count()} 条");

        // ================= 麦克风 =================
        Console.WriteLine();
        Console.WriteLine("麦克风（docs §5.12）");
        sink.Clear();
        fake.Mic = new MicSnapshot(true, "WeChat", 1);
        Pump(src);
        var micOn = sink.Of("mic").LastOrDefault();
        Check("麦克风被占用会发事件", micOn?.Title == "麦克风被占用", micOn?.Title ?? "");
        Check("副标题带占用者", micOn?.Subtitle?.Contains("WeChat") == true, micOn?.Subtitle ?? "");

        sink.Clear();
        fake.Mic = new MicSnapshot(true, "WeChat", 2);
        Pump(src);
        Check("多程序共用会更新副标题",
            sink.Of("mic").LastOrDefault()?.Subtitle?.Contains("2 个程序") == true,
            sink.Of("mic").LastOrDefault()?.Subtitle ?? "");

        sink.Clear();
        fake.Mic = new MicSnapshot(false, "", 0);
        Pump(src);
        Check("麦克风释放会发事件",
            sink.Of("mic").LastOrDefault()?.Title == "麦克风已释放",
            sink.Of("mic").LastOrDefault()?.Title ?? "");

        // 值级忽略：忽略"使用中"后再占用，不该弹
        SysStateSource.IgnoreValue("mic", true);
        sink.Clear();
        fake.Mic = new MicSnapshot(true, "OBS", 1);
        Pump(src);
        Check("值级忽略生效：忽略【使用中】后不再弹", sink.Of("mic").Count() == 0);
        SysStateSource.IgnoreValue("mic", false);   // 清掉，别影响后续

        // ---- 总开关关掉后完全静默 ----
        Console.WriteLine();
        Console.WriteLine("总开关");
        var off = Core.Settings.Load();
        off.Power.Enabled = false;
        off.Network.Enabled = false;
        off.Mic.Enabled = false;
        src.Configure(off);
        sink.Clear();
        fake.Battery = new BatterySnapshot(true, 30, true, true, 900, false);
        fake.Wifi = new WifiSnapshot(true, "X", 50, 100, 0, 0, true);
        fake.Mic = new MicSnapshot(true, "App", 1);
        Pump(src);
        Check("三个总开关全关 → 一条事件都不发", sink.Events.Count == 0, $"{sink.Events.Count} 条");

        src.Dispose();
        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "全部通过。" : $"{fail} 项异常。");
        Environment.Exit(fail == 0 ? 0 : 2);
    }
}

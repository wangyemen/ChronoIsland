using System;
using System.Collections.Generic;
using System.Linq;
using ChronoIsland.Core;
using ChronoIsland.Native;

namespace ChronoIsland.Sources;

/// <summary>
/// 系统状态快照的提供者。
///
/// 【为什么有这层】轮询源的核心逻辑是"两帧快照相比，变化了就发事件"（差分）。
/// 这段逻辑与"快照从哪来"无关：真实运行时来自 <see cref="SystemProbe"/>，
/// 自检时来自注入的假数据。有了这层，才能在不改动机器真实状态的前提下
/// （不真的开飞行模式、不真的拔电源、不真的开麦克风）把每个 diff 分支都跑一遍。
/// </summary>
public interface ISystemSnapshotProvider
{
    WifiSnapshot ReadWifi();
    BatterySnapshot ReadBattery();
    HotspotSnapshot ReadHotspot();
    MicSnapshot ReadMicrophone();
    RadioSnapshot ReadRadio();
}

/// <summary>默认提供者：读真实系统（docs §5.8–§5.12）</summary>
public sealed class RealSystemSnapshotProvider : ISystemSnapshotProvider
{
    public WifiSnapshot ReadWifi() => SystemProbe.ReadWifi();
    public BatterySnapshot ReadBattery() => SystemProbe.ReadBattery();
    public HotspotSnapshot ReadHotspot() => SystemProbe.ReadHotspot();
    public MicSnapshot ReadMicrophone() => SystemProbe.ReadMicrophone();
    public RadioSnapshot ReadRadio() => SystemProbe.ReadRadio();
}

/// <summary>
/// 系统状态源（docs §5.8–§5.12）：
///   · 麦克风占用     kind="mic"       —— 有程序开始 / 停止使用麦克风
///   · 电池 / 电源   kind="battery"   —— 插拔电源、开始充电、充满、低电量
///   · Wi-Fi 连接与网速 kind="wifi"    —— 连上 / 断开 / 换网；可选常驻显示实时网速
///   · 移动热点连接数 kind="hotspot"  —— 热点开关、设备上下线
///   · 飞行模式       kind="airplane" —— 打开 / 关闭（含"飞行模式下单独开 Wi-Fi"）
///
/// 【为什么是轮询】这五个信号都没有可靠的系统事件可订阅：
///   麦克风占用只有隐私库里的时间戳；热点客户端上下线只有 ARP；
///   飞行模式没有公开通知。因此统一低频轮询，单次开销都在几十微秒量级
///   （注册表读 + iphlpapi + wlanapi，都是进程内系统调用，不起进程）。
///
/// 【零事件即零岛】首次采样只建立基线、不弹岛 —— 开机时不该一口气冒出五条岛。
/// </summary>
public sealed class SystemStatusSource : IEventSource
{
    public string Key => "system";
    /// <summary>与键位 / 摄像头同级：状态类提醒值得看见，但不抢通知。</summary>
    public int Priority => 3;

    private EventBus? _bus;
    private Settings _settings = new();
    private System.Threading.Timer? _timer;

    // 上一轮快照（用于差分判断"变了没有"）
    private WifiSnapshot? _wifi;
    private BatterySnapshot? _battery;
    private HotspotSnapshot? _hotspot;
    private MicSnapshot? _mic;
    private RadioSnapshot? _radio;

    // 首次采样只建基线
    private bool _seeded;

    // 低电量 / 充满每次放电周期只提醒一次
    private bool _lowNotified;
    private bool _fullNotified;

    // 轮询计数器（1s 基础节拍，各信号按自己的周期采样）
    private int _tick;

    // ---- 供 UI 实时读取的最新快照（记录不可变，引用赋值天然原子）----
    private static volatile WifiSnapshot? s_wifi;
    private static volatile BatterySnapshot? s_battery;
    private static volatile HotspotSnapshot? s_hotspot;
    private static volatile MicSnapshot? s_mic;
    private static volatile RadioSnapshot? s_radio;

    /// <summary>最新 Wi-Fi 快照（未采样过时为 null）</summary>
    public static WifiSnapshot? LatestWifi => s_wifi;
    /// <summary>最新电池快照</summary>
    public static BatterySnapshot? LatestBattery => s_battery;
    /// <summary>最新热点快照</summary>
    public static HotspotSnapshot? LatestHotspot => s_hotspot;
    /// <summary>最新麦克风快照</summary>
    public static MicSnapshot? LatestMic => s_mic;
    /// <summary>最新电台 / 飞行模式快照</summary>
    public static RadioSnapshot? LatestRadio => s_radio;

    /// <summary>网速读数变化时触发（UI 用来在常驻卡里刷数字，不产生事件）</summary>
    public static event Action<WifiSnapshot>? WifiSpeedChanged;

    /// <summary>快照提供者（默认真实系统；自检可注入假数据）</summary>
    private ISystemSnapshotProvider _probe = new RealSystemSnapshotProvider();

    /// <summary>
    /// 替换快照提供者（仅自检用）。
    /// 它让"差分 → 发事件"这段逻辑可以在**不改动真实系统**的前提下被完整验证
    /// （不真的开飞行模式、不真的拔电源、不真的占麦克风）。
    /// </summary>
    internal void SetProvider(ISystemSnapshotProvider probe) => _probe = probe;

    /// <summary>立即执行一轮采样（仅自检用：不必等 1s 定时器）</summary>
    internal void ProbeTick() => Tick();

    public void Start(EventBus bus)
    {
        _bus = bus;
        // 立即采一次建立基线（不弹岛），之后每秒推进
        Tick();
        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    /// <summary>
    /// 岛上有没有东西可看。空态时把 Wi-Fi 采样降到 2s 一次（docs §6「空闲 CPU ≈ 0」）。
    ///
    /// 【为什么不能干脆停掉】停掉就检测不到"此刻连上 Wi-Fi / 插入电源"这类变化，
    /// 第一条岛永远等不来。折中：空态放大采样间隔（仍然是事件驱动的代价量级），
    /// 有岛可看时回到 1s（网速数字要跟手）。
    /// </summary>
    public void SetIslandVisible(bool visible) => _islandVisible = visible;

    private volatile bool _islandVisible;

    public void Configure(Settings s) => _settings = s ?? new Settings();

    public void Dispose() => _timer?.Dispose();

    private void Tick()
    {
        try
        {
            _tick++;
            var s = _settings;
            // 【首轮全采】tick 1 时把所有信号都采一遍：设置页的"当前状态"、
            // 详情卡里的实时读数都依赖这些静态快照，等 5 秒才有电池数据会显得"功能没生效"。
            bool first = _tick == 1;

            // ---- Wi-Fi：1s（要算实时网速，采样间隔就是网速的分母）----
            // 空态（屏幕上没有岛）时降到 2s：仍然是"有变化就能发现"的粒度，
            // 但把常驻开销再砍一半 —— 这是 docs §6 空态零开销要求在轮询源上的落法。
            if (s.Network.Enabled && s.Network.Wifi && (first || _islandVisible || _tick % 2 == 0))
            {
                var w = _probe.ReadWifi();
                s_wifi = w;
                if (_seeded) DiffWifi(w, s);
                _wifi = w;
                try { WifiSpeedChanged?.Invoke(w); } catch { }
            }

            // ---- 麦克风 / 热点：2s ----
            if (first || _tick % 2 == 0)
            {
                if (s.Mic.Enabled)
                {
                    var m = _probe.ReadMicrophone();
                    s_mic = m;
                    if (_seeded) DiffMic(m);
                    _mic = m;
                }
                if (s.Network.Enabled && s.Network.Hotspot)
                {
                    var h = _probe.ReadHotspot();
                    s_hotspot = h;
                    if (_seeded) DiffHotspot(h);
                    _hotspot = h;
                }
            }

            // ---- 飞行模式：3s（关掉一次 WiFi 要等网络栈刷新，太快会读到中间态）----
            if ((first || _tick % 3 == 0) && s.Network.Enabled && s.Network.Airplane)
            {
                var r = _probe.ReadRadio();
                s_radio = r;
                if (_seeded) DiffRadio(r);
                _radio = r;
            }

            // ---- 电池 / 电源：5s ----
            if ((first || _tick % 5 == 0) && s.Power.Enabled)
            {
                var b = _probe.ReadBattery();
                s_battery = b;
                if (_seeded) DiffBattery(b, s);
                _battery = b;
            }

            _seeded = true;
            if (first)
            {
                // 启动基线日志：把"这台机器上哪些信号真的可用"一次说清楚。
                // 没有这一行时，排查"为什么电池不弹"要翻很久日志才知道是台式机没电池。
                App.LogInfo("System", "基线采集完成 · "
                    + $"电池={DescribeBattery(s_battery)} · "
                    + $"Wi-Fi={DescribeWifi(s_wifi)} · "
                    + $"热点={DescribeHotspot(s_hotspot)} · "
                    + $"麦克风={(s_mic?.Busy == true ? "占用" : "空闲")} · "
                    + $"飞行模式={((s_radio?.AirplaneMode) == true ? "开" : "关")}");
            }
        }
        catch (Exception ex) { App.Log("System", ex); }
    }

    private static string DescribeBattery(BatterySnapshot? b)
        => b == null ? "未采集"
         : !b.HasBattery ? "无电池（静默）"
         : $"{b.Percent}%{(b.Charging ? " 充电中" : b.AcOnline ? " 已接电" : " 放电中")}";

    private static string DescribeWifi(WifiSnapshot? w)
        => w == null ? "未采集"
         : !w.WifiRadioOn ? "网卡关闭"
         : w.Connected ? $"{w.Ssid} {w.SignalPercent}% {w.LinkMbps:0}Mbps"
         : "未连接";

    private static string DescribeHotspot(HotspotSnapshot? h)
        => h == null ? "未采集"
         : !h.Supported ? "不支持（无 Wi-Fi 网卡）"
         : h.On ? $"开启 · {h.Clients} 台设备" : "关闭";

    // ================= Wi-Fi（docs §5.9） =================

    private void DiffWifi(WifiSnapshot w, Settings s)
    {
        var prev = _wifi;
        if (prev == null) return;

        bool cameUp = !prev.Connected && w.Connected;
        bool wentDown = prev.Connected && !w.Connected;
        bool ssidChanged = w.Connected && prev.Connected
                           && !string.Equals(prev.Ssid, w.Ssid, StringComparison.Ordinal);

        if (cameUp)
        {
            App.LogInfo("System", $"Wi-Fi 已连接：{w.Ssid}（{w.SignalPercent}% · {w.LinkMbps:0} Mbps）");
            EmitWifi(w, s, "已连接 Wi-Fi", w.Ssid);
        }
        else if (ssidChanged)
        {
            App.LogInfo("System", $"Wi-Fi 切换：{prev.Ssid} → {w.Ssid}");
            EmitWifi(w, s, "已切换 Wi-Fi", w.Ssid);
        }
        else if (wentDown)
        {
            App.LogInfo("System", $"Wi-Fi 已断开：{prev.Ssid}");
            EmitWifi(w, s, "Wi-Fi 已断开", string.IsNullOrWhiteSpace(prev.Ssid) ? "网络不可用" : prev.Ssid);
        }
    }

    private void EmitWifi(WifiSnapshot w, Settings s, string title, string subtitle)
    {
        // 常驻模式：连上后岛不自动收起，靠 UI 每帧刷新网速数字（见 IslandWindow.RefreshWifiCard）
        bool pin = s.Network.PinSpeed && w.Connected;
        int ttl = Math.Max(2, s.Network.TtlSeconds);

        _bus?.Publish(new IslandEvent(
            Id: "wifi",
            SourceKey: "wifi",
            Kind: "wifi",
            Priority: Priority,
            Title: title,
            Subtitle: subtitle,
            Payload: w,
            Policy: new IslandPolicy(
                pin ? null : TimeSpan.FromSeconds(ttl),
                Coalesce: true, Expandable: true, HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "wifi", StealFocus = pin });
    }

    // ================= 移动热点（docs §5.10） =================

    private void DiffHotspot(HotspotSnapshot h)
    {
        var prev = _hotspot;
        if (prev == null) return;
        if (prev.On == h.On && prev.Clients == h.Clients) return;

        string title, sub;
        if (!prev.On && h.On) { title = "移动热点已开启"; sub = $"{h.Clients} 台设备已连接"; }
        else if (prev.On && !h.On) { title = "移动热点已关闭"; sub = "已停止共享网络"; }
        else if (h.Clients > prev.Clients) { title = "设备接入热点"; sub = $"共 {h.Clients} 台设备在线"; }
        else { title = "设备离开热点"; sub = h.Clients == 0 ? "当前没有设备连接" : $"共 {h.Clients} 台设备在线"; }

        App.LogInfo("System", $"热点：{title}（{h.Clients} 台）");
        int ttl = Math.Max(2, _settings.Network.TtlSeconds);
        _bus?.Publish(new IslandEvent(
            Id: "hotspot",
            SourceKey: "hotspot",
            Kind: "hotspot",
            Priority: Priority,
            Title: title,
            Subtitle: sub,
            Payload: h,
            Policy: new IslandPolicy(TimeSpan.FromSeconds(ttl), Coalesce: true, Expandable: true,
                HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "hotspot", StealFocus = true });
    }

    // ================= 飞行模式（docs §5.11） =================

    private void DiffRadio(RadioSnapshot r)
    {
        var prev = _radio;
        if (prev == null) return;
        if (prev.AirplaneMode == r.AirplaneMode) return;

        App.LogInfo("System", r.AirplaneMode ? "飞行模式已开启" : "飞行模式已关闭");
        string title = r.AirplaneMode ? "飞行模式已开启" : "飞行模式已关闭";
        string sub = r.AirplaneMode
            ? (r.WifiRadioOn ? "无线已关闭 · Wi-Fi 仍开启" : "所有无线连接已关闭")
            : "无线连接已恢复";

        int ttl = Math.Max(2, _settings.Network.TtlSeconds);
        _bus?.Publish(new IslandEvent(
            Id: "airplane",
            SourceKey: "airplane",
            Kind: "airplane",
            Priority: Priority,
            Title: title,
            Subtitle: sub,
            Payload: r,
            Policy: new IslandPolicy(TimeSpan.FromSeconds(ttl), Coalesce: true, Expandable: true,
                HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "airplane", StealFocus = true });
    }

    // ================= 电池 / 电源（docs §5.8） =================

    private void DiffBattery(BatterySnapshot b, Settings s)
    {
        var prev = _battery;
        if (prev == null) return;
        if (!b.HasBattery) return;              // 台式机：整块静默

        // 插拔电源
        if (prev.AcOnline != b.AcOnline)
        {
            if (!s.Power.NotifyPlug) { _lowNotified = false; _fullNotified = false; return; }
            if (b.AcOnline)
                EmitBattery(b, s, "已接通电源", b.Charging ? $"正在充电 · {b.Percent}%" : $"{b.Percent}%");
            else
                EmitBattery(b, s, "已改用电池", $"{b.Percent}%" + (b.SecondsRemaining > 0
                    ? " · 预计可用 " + FmtSpan(b.SecondsRemaining) : ""));
            if (!b.AcOnline) { _fullNotified = false; }
            else { _lowNotified = false; }
            return;
        }

        // 充电开始 / 停止（不插拔但状态变化：例如电池保护在 80% 停止充电）
        if (prev.Charging != b.Charging)
        {
            if (s.Power.NotifyPlug) EmitBattery(b, s, b.Charging ? "正在充电" : "已停止充电", $"{b.Percent}%");
            return;
        }

        // 低电量：一次放电只提醒一次（掉到阈值以下那一刻）
        if (b.IsLow && !_lowNotified)
        {
            _lowNotified = true;
            if (!s.Power.NotifyLow) return;
            App.LogInfo("System", $"低电量提醒：{b.Percent}%");
            EmitBattery(b, s, "电量不足", $"{b.Percent}% · 建议接通电源",
                warn: true, ttlOverride: Math.Max(6, s.Power.TtlSeconds));
            return;
        }
        if (!b.IsLow && !b.AcOnline) _lowNotified = false;

        // 充满：一次充电只提醒一次
        if (b.IsFull && !_fullNotified && s.Power.NotifyFull)
        {
            _fullNotified = true;
            EmitBattery(b, s, "电池已充满", "100% · 可拔掉电源");
            return;
        }
        if (!b.IsFull && !b.AcOnline) _fullNotified = false;
    }

    private void EmitBattery(BatterySnapshot b, Settings s, string title, string sub,
        bool warn = false, int ttlOverride = 0)
    {
        App.LogInfo("System", $"电源：{title}（{b.Percent}%{b.Charging switch { true => " 充电中", false => "" }}）");
        int ttl = Math.Max(ttlOverride > 0 ? ttlOverride : 0, Math.Max(2, s.Power.TtlSeconds));
        _bus?.Publish(new IslandEvent(
            Id: "battery",
            SourceKey: "battery",
            Kind: "battery",
            Priority: warn ? Priority + 1 : Priority,
            Title: title,
            Subtitle: sub,
            Payload: b,
            Policy: new IslandPolicy(TimeSpan.FromSeconds(ttl), Coalesce: true, Expandable: true,
                HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "battery", StealFocus = warn });
    }

    private static string FmtSpan(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分" : $"{ts.Minutes} 分钟";
    }

    // ================= 麦克风占用（docs §5.12） =================

    private void DiffMic(MicSnapshot m)
    {
        var prev = _mic;
        if (prev == null) return;
        if (prev.Busy == m.Busy && prev.Users == m.Users
            && string.Equals(prev.AppName, m.AppName, StringComparison.Ordinal)) return;
        if (!m.Busy && !prev.Busy) return;

        // 值级忽略：忽略"麦克风=使用中"后，其他程序再次启用仍会弹（与摄像头同一套语义）
        if (SysStateSource.IsSnoozed("mic", m.Busy) || SysStateSource.IsIgnored("mic", m.Busy))
        {
            App.LogInfo("System", $"麦克风={m.Busy} 处于忽略/稍后提醒期，不弹");
            return;
        }

        string title = m.Busy ? "麦克风被占用" : "麦克风已释放";
        string sub = m.Busy
            ? (m.Users > 1 ? $"{m.AppName} 等 {m.Users} 个程序正在使用" : $"{m.AppName} 正在使用")
            : "已没有程序在录音";

        App.LogInfo("System", $"{title}：{sub}");
        int ttl = Math.Max(2, _settings.Mic.TtlSeconds);
        _bus?.Publish(new IslandEvent(
            Id: "mic",
            SourceKey: "mic",
            Kind: "mic",
            Priority: Priority,
            Title: title,
            Subtitle: sub,
            Payload: m,
            Policy: new IslandPolicy(TimeSpan.FromSeconds(ttl), Coalesce: true, Expandable: true,
                HoverPauses: true, QueueWhenHidden: false))
        { AppKey = "mic", StealFocus = m.Busy });
    }
}

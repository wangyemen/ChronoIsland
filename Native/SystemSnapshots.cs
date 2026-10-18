using System;

namespace ChronoIsland.Native;

// ============================================================================
// 系统状态快照（docs §5.8–§5.12）
// 全部是**不可变** record：源侧每轮采样新建一个，UI 侧持有引用即快照，
// 不存在"读到一半被改"的问题，也不需要加锁。
// ============================================================================

/// <summary>电池 / 供电快照（docs §5.8）。</summary>
/// <param name="HasBattery">本机是否有电池（台式机为 false，功能静默）</param>
/// <param name="Percent">剩余电量 0–100</param>
/// <param name="Charging">是否正在充电</param>
/// <param name="AcOnline">是否接交流电</param>
/// <param name="SecondsRemaining">距耗尽 / 充满的秒数；0 = 未知</param>
/// <param name="SaverOn">是否开启了节电模式</param>
public sealed record BatterySnapshot(
    bool HasBattery, int Percent, bool Charging, bool AcOnline,
    int SecondsRemaining, bool SaverOn)
{
    /// <summary>低电量阈值（百分比）。低于等于它且未充电 → 提醒一次。</summary>
    public const int LowThreshold = 20;

    /// <summary>是否"低电量"（≤20% 且没在充电）</summary>
    public bool IsLow => HasBattery && !Charging && Percent <= LowThreshold;

    /// <summary>是否已充满（接电 + 100%）</summary>
    public bool IsFull => HasBattery && AcOnline && Percent >= 100;
}

/// <summary>Wi-Fi 连接快照（docs §5.9）。</summary>
/// <param name="Connected">是否已连上某个无线网络</param>
/// <param name="Ssid">网络名（SSID）</param>
/// <param name="SignalPercent">信号强度 0–100</param>
/// <param name="LinkMbps">协商速率（Mbps）；0 = 未知</param>
/// <param name="RxBps">下行实时速率（字节/秒）</param>
/// <param name="TxBps">上行实时速率（字节/秒）</param>
/// <param name="WifiRadioOn">无线网卡是否可用（关掉 Wi-Fi / 飞行模式时为 false）</param>
public sealed record WifiSnapshot(
    bool Connected, string Ssid, int SignalPercent, double LinkMbps,
    double RxBps, double TxBps, bool WifiRadioOn)
{
    /// <summary>是否有可感知的流量（>1 KB/s），用于决定要不要显示速率</summary>
    public bool HasTraffic => RxBps > 1024 || TxBps > 1024;
}

/// <summary>移动热点快照（docs §5.10）。</summary>
/// <param name="On">热点是否开启</param>
/// <param name="Clients">已连接设备数</param>
/// <param name="ClientAddresses">已连接设备的 IPv4（详情卡列出用）</param>
/// <param name="Supported">本机是否支持热点（无 Wi-Fi 网卡的台式机为 false）</param>
public sealed record HotspotSnapshot(bool On, int Clients, IReadOnlyList<string> ClientAddresses, bool Supported)
{
    public static readonly HotspotSnapshot Unsupported = new(false, 0, Array.Empty<string>(), false);
}

/// <summary>飞行模式 / 无线电台快照（docs §5.11）。</summary>
/// <param name="AirplaneMode">飞行模式是否开启</param>
/// <param name="WifiRadioOn">Wi-Fi 电台是否开启（飞行模式下仍可单独打开）</param>
public sealed record RadioSnapshot(bool AirplaneMode, bool WifiRadioOn);

/// <summary>麦克风占用快照（docs §5.12）。</summary>
/// <param name="Busy">是否有程序正在使用麦克风</param>
/// <param name="AppName">占用者的显示名（可能为空）</param>
/// <param name="Users">同时在用的程序数</param>
public sealed record MicSnapshot(bool Busy, string AppName, int Users);

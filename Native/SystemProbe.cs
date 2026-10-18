using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ChronoIsland.Native;

// 快照类型（BatterySnapshot / WifiSnapshot / HotspotSnapshot / RadioSnapshot / MicSnapshot）
// 定义在 Native/SystemSnapshots.cs —— 它们同时被 Sources 与 UI 引用，单独一个文件更清楚。
/// <summary>
/// 系统状态探测（docs §5.8–§5.11）。
///
/// 【为什么集中在一个文件】电池 / Wi-Fi / 热点 / 飞行模式都来自"系统当前状态"，
/// 没有完整的事件可订阅（电池有 WM_POWERBROADCAST，热点客户端上下线事件不可靠），
/// 所以统一走低频轮询；Win32 / WLAN / ICS 细节收在这一层，Sources 只拿快照。
///
/// 【零第三方依赖】全部是系统自带 API：GetSystemPowerStatus、wlanapi、iphlpapi、
/// 无线电管理注册表。不引入任何包。
/// </summary>
public static class SystemProbe
{
    // ================== 电池 / 电源（docs §5.8） ==================

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;          // 0 未接电, 1 接电, 255 未知
        public byte BatteryFlag;           // 1 充电中, 2 未接电, 4 电量充足, 8 低电量, 128 无电池, 255 未知
        public byte BatteryLifePercent;    // 0–100, 255 未知
        public byte SystemStatusFlag;      // 1 = 节电模式开启（Win10 1703+）
        public int BatteryLifeTime;        // 秒；-1 未知
        public int BatteryFullLifeTime;    // 秒；-1 未知
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>读电池 / 电源状态。台式机（无电池）返回 HasBattery=false，调用方据此静默。</summary>
    public static BatterySnapshot ReadBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s))
                return new BatterySnapshot(false, 0, false, true, 0, false);

            // BatteryFlag = 128 表示"无系统电池"；255 表示未知
            bool hasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
            bool ac = s.ACLineStatus == 1;
            // 充电中：BatteryFlag bit0（charging）置位
            bool charging = (s.BatteryFlag & 1) != 0;
            int pct = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;

            // BatteryLifeTime：未接电 = 剩余可用秒数；接电 = 充满还需秒数；-1 = 计算中/未知
            int remain = s.BatteryLifeTime > 0 ? s.BatteryLifeTime : 0;
            return new BatterySnapshot(hasBattery, Math.Clamp(pct, 0, 100), charging, ac, remain,
                s.SystemStatusFlag == 1);
        }
        catch { return new BatterySnapshot(false, 0, false, true, 0, false); }
    }

    // ================== Wi-Fi 状态（docs §5.9） ==================

    // WLAN_CONNECTION_ATTRIBUTES 里关联属性从偏移 520 开始：
    //   isState(4) + wlanConnectionMode(4) + strProfileName[256](512) = 520
    // 关联属性内部（相对偏移）：
    //   +0  uSSIDLength(4)      +4  ucSSID[32]        +36 dot11BssType(4)
    //   +40 dot11Bssid[6]+pad   +48 dot11PhyType(4)   +52 uDot11PhyIndex(4)
    //   +56 wlanSignalQuality(4) +60 ulRxRate(4)      +64 ulTxRate(4)
    // 【为什么按偏移读而不是映射结构体】这段结构里 6 字节 MAC 与 32 字节 SSID 混排，
    // 用 Marshal 结构体很容易因默认 Pack 与实际布局不一致而整体错位 ——
    // 这正是"看起来写对了、读出来是垃圾"的典型来源。按官方头文件的固定偏移读最稳。
    private const int AssocOffset = 520;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
        public int isState;                     // WLAN_INTERFACE_STATE：1 = connected
    }

    /// <summary>读 Wi-Fi 的 SSID / 信号 / 协商速率；实时上下行速率由 <see cref="ReadThroughput"/> 另行差分。</summary>
    public static WifiSnapshot ReadWifi()
    {
        var tp = ReadThroughput();

        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0)
                return new WifiSnapshot(false, "", 0, 0, tp.RxBps, tp.TxBps, false);

            try
            {
                if (WlanEnumInterfaces(client, IntPtr.Zero, out var listPtr) != 0)
                    return new WifiSnapshot(false, "", 0, 0, tp.RxBps, tp.TxBps, false);

                try
                {
                    int count = Marshal.ReadInt32(listPtr);
                    int itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                    IntPtr first = listPtr + 8;      // dwNumberOfItems + dwIndex

                    for (int i = 0; i < count; i++)
                    {
                        var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(first + i * itemSize);
                        if (info.isState != 1) continue;          // 1 = connected

                        var guid = info.InterfaceGuid;
                        if (WlanQueryInterface(client, ref guid, 7 /*current_connection*/,
                                IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0)
                            continue;
                        try
                        {
                            int ssidLen = Math.Clamp(Marshal.ReadInt32(data, AssocOffset), 0, 32);
                            var raw = new byte[ssidLen];
                            if (ssidLen > 0) Marshal.Copy(data + AssocOffset + 4, raw, 0, ssidLen);
                            string ssid = Encoding.UTF8.GetString(raw);
                            int signal = Marshal.ReadInt32(data, AssocOffset + 56);   // 0–100
                            uint rxKbps = (uint)Marshal.ReadInt32(data, AssocOffset + 60);
                            uint txKbps = (uint)Marshal.ReadInt32(data, AssocOffset + 64);
                            double mbps = Math.Round(Math.Max(rxKbps, txKbps) / 1000.0, 0);
                            return new WifiSnapshot(true, ssid, Math.Clamp(signal, 0, 100), mbps,
                                tp.RxBps, tp.TxBps, true);
                        }
                        finally { WlanFreeMemory(data); }
                    }

                    // 有无线网卡但一个都没连上：WifiRadioOn = 网卡在（count > 0）
                    return new WifiSnapshot(false, "", 0, 0, tp.RxBps, tp.TxBps, count > 0);
                }
                finally { WlanFreeMemory(listPtr); }
            }
            finally { WlanCloseHandle(client, IntPtr.Zero); }
        }
        catch
        {
            // WLAN 服务未启动 / 无无线网卡（台式机）：按"Wi-Fi 不可用"处理，功能静默
            return new WifiSnapshot(false, "", 0, 0, tp.RxBps, tp.TxBps, false);
        }
    }

    /// <summary>
    /// 无线网卡是否可用（有 WLAN 接口即视为 Wi-Fi 电台开着）。
    ///
    /// 【为什么单独一个轻量函数】<see cref="ReadWifi"/> 会顺带采样网速（差分需要时间基线），
    /// 而飞行模式每 3s 查一次、只想问"电台开没开"。若它走 ReadWifi，
    /// 网速的差分间隔会被打乱成 1s / 3s 交替，读数一跳一跳。
    /// </summary>
    public static bool IsWifiRadioAvailable()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0) return false;
            try
            {
                if (WlanEnumInterfaces(client, IntPtr.Zero, out var listPtr) != 0) return false;
                try { return Marshal.ReadInt32(listPtr) > 0; }
                finally { WlanFreeMemory(listPtr); }
            }
            finally { WlanCloseHandle(client, IntPtr.Zero); }
        }
        catch { return false; }
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        int opCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    // ================== 实时网速（docs §5.9） ==================

    /// <summary>一次网速采样（全系统合计：Wi-Fi / 有线都算）。</summary>
    public readonly record struct Throughput(double RxBps, double TxBps);

    private static readonly object TpGate = new();
    private static long _lastTicks;
    private static long _lastRx, _lastTx;
    private static double _rxBps, _txBps;

    /// <summary>
    /// 全系统上下行速率（字节/秒）。
    ///
    /// 【差分口径】系统给的是累计字节数，两次采样相减再除以间隔即瞬时速率。
    /// 首次调用没有基线，返回 0（调用方应视为"刚启动、还没测出来"）。
    ///
    /// 【为什么用 NetworkInterface 而不是 GetIfTable2】
    /// GetIfTable2 返回的是**含 NDIS 过滤器的完整接口列表**：一块物理网卡会衍生出
    /// 七八个 WFP / QoS / 杀软过滤驱动接口，它们的收发计数与物理网卡完全相同 ——
    /// 直接求和会把同一份流量放大数倍（实测偏差数倍）。
    /// 用 GetIPv4Properties() 能拿到索引的只有"真正持有 IP 的接口"，
    /// 过滤驱动层全部返回 null，天然把它们排除掉，同时排除了 WAN Miniport 这类空壳。
    /// </summary>
    public static Throughput ReadThroughput()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                try { if (ni.GetIPProperties().GetIPv4Properties() == null) continue; }
                catch { continue; }                       // 过滤驱动层没有 IPv4 属性

                try
                {
                    var st = ni.GetIPv4Statistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
                catch { }
            }

            lock (TpGate)
            {
                long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_lastTicks > 0 && rx >= _lastRx && tx >= _lastTx)
                {
                    double sec = (nowTicks - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (sec > 0.2)
                    {
                        _rxBps = (rx - _lastRx) / sec;
                        _txBps = (tx - _lastTx) / sec;
                        if (_rxBps < 1) _rxBps = 0;      // 无流量归零，不残留旧值
                        if (_txBps < 1) _txBps = 0;
                    }
                }
                _lastTicks = nowTicks;
                _lastRx = rx;
                _lastTx = tx;
                return new Throughput(_rxBps, _txBps);
            }
        }
        catch { return new Throughput(_rxBps, _txBps); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public int TunnelType;
        public int MediaType;
        public int PhysicalMediaType;
        public byte AdminStatus;
        public byte MediaConnectState;
        public int ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
        public byte OperStatus;                      // IF_OPER_STATUS：1 = up
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr table);
// ================== 移动热点（docs §5.10） ==================

    // 热点接入设备的固定网段：Windows 内置热点走 ICS，网卡固定拿 192.168.137.1/24
    private const string HotspotSubnet = "192.168.137.";

    /// <summary>
    /// 读移动热点状态与已连接设备数。
    ///
    /// 【为什么用 ICS 邻居表而不是 NetworkOperatorTetheringManager】
    /// 后者面向"移动宽带共享"，桌面版 Windows 上通常拿不到 ConnectionProfile（直接抛异常），
    /// 而系统自带热点实际是 ICS：Wi-Fi Direct 虚拟网卡固定拿到 192.168.137.1，
    /// 接入设备会出现在该网卡的 ARP 邻居表里。这条路径不需要任何权限、无平台限制，
    /// 且对"系统设置里开的热点"同样有效（不依赖是谁开的）。
    ///
    /// 【"支持"与"开着"必须分开判】
    /// 热点没开时，Windows 会把那块 Wi-Fi Direct 虚拟网卡**整个移除**（不是置为 Down）。
    /// 若把"找不到虚拟网卡"当成"本机不支持"，关闭热点后就再也读不到"已关闭"，
    /// 设置页会一直显示"本机不支持（无 Wi-Fi 网卡）"—— 这是实测踩到的坑。
    /// 因此：
    ///   · Supported = 本机有无线网卡（WLAN 接口存在）；
    ///   · On        = Wi-Fi Direct 虚拟网卡在，且拿到 192.168.137.x 的地址（ICS 已生效）。
    /// </summary>
    public static HotspotSnapshot ReadHotspot()
    {
        // 没有任何无线网卡 → 真不支持（台式机插网线的情况）
        if (!IsWifiRadioAvailable()) return HotspotSnapshot.Unsupported;

        if (!TryFindHotspotInterface(out int ifIndex))
            return new HotspotSnapshot(false, 0, Array.Empty<string>(), true);

        var clients = EnumerateHotspotClients(ifIndex);
        return new HotspotSnapshot(true, clients.Count, clients, true);
    }

    /// <summary>
    /// 找热点（Wi-Fi Direct 虚拟）网卡：**正在承载热点**时返回 true 与 IPv4 接口索引。
    ///
    /// 【为什么用 NetworkInterface 而不是 GetIfTable2】MIB_IF_ROW2 是一大坨变长结构体，
    /// 字段顺序错一个就整体读偏；而我们只关心"哪块网卡 + 它的 IPv4 索引"，
    /// NetworkInterface 直接给出描述与 GetIPv4Properties().Index，
    /// 且索引口径与 GetIpNetTable 的 dwIndex 一致（都是 IPv4 视图的接口号）。
    ///
    /// 【为什么还要看地址】虚拟网卡可能处于 Up 但尚未分到 ICS 地址的瞬间状态；
    /// 只有真的拿到 192.168.137.x 才说明共享已生效，此时才不会误报"热点已开启"。
    /// </summary>
    private static bool TryFindHotspotInterface(out int ifIndex)
    {
        ifIndex = 0;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!(ni.Description ?? "").Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase)) continue;
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;

                System.Net.NetworkInformation.IPv4InterfaceProperties? v4;
                int idx;
                try
                {
                    var ip = ni.GetIPProperties();
                    v4 = ip.GetIPv4Properties();
                    idx = v4?.Index ?? -1;
                }
                catch { continue; }
                if (idx <= 0) continue;

                // ICS 生效的标志：该网卡上分到了 192.168.137.x
                bool hasIcsAddress = false;
                try
                {
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        if (ua.Address.ToString().StartsWith(HotspotSubnet, StringComparison.Ordinal))
                        { hasIcsAddress = true; break; }
                    }
                }
                catch { }
                if (!hasIcsAddress) continue;

                ifIndex = idx;
                return true;
            }
        }
        catch { }
        return false;
    }
    /// <summary>读热点网卡接口上的接入设备（ARP 邻居表里的 192.168.137.x）。</summary>
    private static List<string> EnumerateHotspotClients(int ifIndex)
    {
        var list = new List<string>();
        try
        {
            // 用老而稳的 GetIpNetTable（IPv4 专用）：结构体简单、只有一个变长数组，
            // 不像 MIB_IPNET_ROW2 那样混排 SOCKADDR_INET，错位风险低。
            int size = 0;
            GetIpNetTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return list;

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buf, ref size, false) != 0) return list;
                int rows = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf<MIB_IPNETROW>();
                for (int i = 0; i < rows; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_IPNETROW>(buf + 4 + i * rowSize);
                    if ((int)row.dwIndex != ifIndex) continue;
                    if (row.dwType == 2) continue;                 // 2 = invalid
                    var s = new Ip4(row.dwAddr).ToString();
                    if (!s.StartsWith(HotspotSubnet, StringComparison.Ordinal)) continue;
                    // 去掉广播地址（x.x.x.255）与热点网关自身（x.x.x.1）
                    var tail = s[HotspotSubnet.Length..];
                    if (tail == "255" || tail == "1") continue;
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 把 MIB 里的网络序 DWORD 拆成 IPv4 点分串。
    /// 【为什么手写】只为一次 ToString 引入 System.Net.IPAddress 不划算，
    /// 而且 MIB 值是小端存储的网络序，IPAddress 构造语义容易搞反。
    /// </summary>
    private readonly record struct Ip4(uint Raw)
    {
        public override string ToString()
            => $"{Raw & 0xFF}.{(Raw >> 8) & 0xFF}.{(Raw >> 16) & 0xFF}.{(Raw >> 24) & 0xFF}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public uint dwIndex;
        public uint dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] bPhysAddr;
        public uint dwAddr;
        public uint dwType;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    // ================== 飞行模式 / 无线电台（docs §5.11） ==================

    /// <summary>
    /// 读飞行模式状态。
    ///
    /// 【检测原理】Windows 的无线电管理把"飞行模式"落在
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState</c>
    /// 这个键的**默认值**上（0 = 关闭，1 = 开启）。它就是设置界面那个开关背后的
    /// 同一份状态，读取不需要任何特殊权限。
    ///
    /// 【为什么要同时看 Wi-Fi 电台】飞行模式打开后，用户仍可单独把 Wi-Fi 打开，
    /// 这时注册表仍是 1、但网络是通的 —— 胶囊必须区分这两种情况，
    /// 否则会出现"显示飞行模式、却在上网"的割裂观感。
    /// </summary>
    public static RadioSnapshot ReadRadio()
    {
        bool airplane = false;
        try
        {
            using var hk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\RadioManagement\SystemRadioState");
            // 这是键的默认值（无名值），GetValue("") 才读得到
            var v = hk?.GetValue("");
            airplane = v is int i && i == 1;
        }
        catch { }

        bool wifiRadioOn = false;
        try { wifiRadioOn = IsWifiRadioAvailable(); }
        catch { }

        return new RadioSnapshot(airplane, wifiRadioOn);
    }

    // ================== 麦克风占用（docs §5.12） ==================

    private const string MicConsentKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>
    /// 读"麦克风是否正被某个程序占用"。
    ///
    /// 【检测原理】Windows 10 1903+ 把每个程序的麦克风使用记录写进隐私授权库：
    /// <c>HKCU\...\CapabilityAccessManager\ConsentStore\microphone</c>
    /// 下每个程序一个子键（打包应用直接用包族名，桌面程序在 <c>NonPackaged</c> 子键里、
    /// 名字是 exe 全路径把 <c>\</c> 换成 <c>#</c>），值有两个：
    ///   · <c>LastUsedTimeStart</c>（FILETIME）：开始使用时刻
    ///   · <c>LastUsedTimeStop</c>（FILETIME）：结束使用时刻；**0 表示"现在仍在用"**
    /// 系统设置的"隐私 → 麦克风 → 最近活动"页读的就是这份数据，所以它天然可信，
    /// 而且不需要打开设备（不会像探测设备那样打断正在录音的程序）。
    ///
    /// 【为什么不用 CreateFile 探测麦克风设备】音频设备的独占语义与摄像头不同：
    /// 共享模式打开是常态，探测不出"是否有人录音"，反而可能扰动音频链路。
    ///
    /// 【降级】Win10 1903 之前没有这个位置 → 读不到任何子键 → 一律按"未占用"处理（不误报）。
    /// </summary>
    public static MicSnapshot ReadMicrophone()
    {
        var users = new List<string>();
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(MicConsentKey);
            if (root != null)
            {
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (string.Equals(sub, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                    {
                        using var np = root.OpenSubKey(sub);
                        if (np == null) continue;
                        foreach (var app in np.GetSubKeyNames())
                        {
                            if (IsMicActive(np, app)) users.Add(FriendlyName(app));
                        }
                    }
                    else if (IsMicActive(root, sub))
                    {
                        users.Add(FriendlyName(sub));
                    }
                }
            }
        }
        catch { /* 读不到一律按未占用处理 */ }

        if (users.Count == 0) return new MicSnapshot(false, "", 0);
        // 同名去重（KOOK 这类程序每个版本一个键）
        var distinct = new List<string>();
        foreach (var u in users)
            if (!distinct.Contains(u)) distinct.Add(u);
        return new MicSnapshot(true, distinct[0], distinct.Count);
    }

    /// <summary>某个程序是否"正在使用"：LastUsedTimeStart &gt; LastUsedTimeStop（stop=0 即一直在用）</summary>
    private static bool IsMicActive(Microsoft.Win32.RegistryKey parent, string name)
    {
        try
        {
            using var k = parent.OpenSubKey(name);
            if (k == null) return false;
            long start = AsLong(k.GetValue("LastUsedTimeStart"));
            long stop = AsLong(k.GetValue("LastUsedTimeStop"));
            return start > 0 && start > stop;
        }
        catch { return false; }
    }

    /// <summary>注册表里的 FILETIME 可能以 long 或 byte[] 存放，两种都要认</summary>
    private static long AsLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        _ => 0
    };

    /// <summary>
    /// 把授权库里的键名转成能给人看的程序名。
    /// 桌面程序的键名形如 <c>C:#Program Files#WeChat#Weixin.exe</c>，取文件名去扩展名即可；
    /// 打包应用形如 <c>Microsoft.WindowsCamera_8wekyb3d8bbwe</c>，去掉包族后缀。
    /// </summary>
    private static string FriendlyName(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "未知程序";
        var name = key.Replace('#', '\\');
        int slash = name.LastIndexOf('\\');
        if (slash >= 0 && slash + 1 < name.Length) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int us = name.IndexOf('_');
        if (us > 0) name = name[..us];
        return string.IsNullOrWhiteSpace(name) ? "未知程序" : name;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChronoIsland.Core;
using ChronoIsland.Native;

namespace ChronoIsland.UI;

/// <summary>
/// 岛窗口的「系统状态」分部类（docs §5.8–§5.13）：
///   麦克风占用 / 电池电源 / Wi-Fi 与网速 / 移动热点 / 飞行模式 / 深浅色切换。
///
/// 【为什么单独一个文件】这六类都是"看一眼就好"的状态岛，结构高度相似
/// （图标键帽 + 名称 + 数值 + 一句话副标题），与主窗口里那些交互复杂的岛
/// （媒体卡、通知卡、DSH 卡）不是一回事。放在一起既不淹没主流程，
/// 又能保证它们的胶囊 / 详情卡 / 球体 / 宽度公式四件套口径一致。
///
/// 【四件套必须同时改】新增一类岛要同时补齐：
///   ① 胶囊 <see cref="BuildSystemPill"/>   ② 详情卡 <see cref="BuildSystemCard"/>
///   ③ 球体 <see cref="BuildSystemSphere"/> ④ 宽度 <see cref="MeasureSystemPillWidth"/>
/// 少任何一项的症状分别是：胶囊空白 / 展开走通知卡兜底 / 球体空白 / 胶囊右侧多一块空白。
/// </summary>
public partial class IslandWindow
{
    // ==================== 胶囊（简略态） ====================

    /// <summary>系统状态胶囊的统一入口。返回 null 表示"这个 kind 不归这里管"。</summary>
    private FrameworkElement? BuildSystemPill(IslandEvent ev) => ev.Kind switch
    {
        "mic" when ev.Payload is MicSnapshot m => BuildMicPill(m),
        "battery" when ev.Payload is BatterySnapshot b => BuildBatteryPill(b),
        "wifi" when ev.Payload is WifiSnapshot w => BuildWifiPill(w),
        "hotspot" when ev.Payload is HotspotSnapshot h => BuildHotspotPill(h),
        "bluetooth" when ev.Payload is BluetoothSnapshot bt => BuildBluetoothPill(bt),
        "airplane" when ev.Payload is RadioSnapshot r => BuildAirplanePill(r),
        "theme" when ev.Payload is Sources.ThemeSnapshot th => BuildThemePill(th),
        _ => null
    };

    /// <summary>麦克风：麦克风键帽 + 状态 + 占用者</summary>
    private FrameworkElement BuildMicPill(MicSnapshot m)
    {
        var t = AppTheme.Current;
        return StatusPill(
            icon: m.Busy ? "mic" : "mic-off",
            on: m.Busy,
            stateColor: m.Busy ? t.Warning : t.TextSecondary,
            name: "麦克风",
            value: m.Busy ? "使用中" : "空闲",
            tail: m.Busy && !string.IsNullOrWhiteSpace(m.AppName) ? m.AppName : null);
    }

    /// <summary>
    /// 电池：按"充电 / 满 / 低 / 普通"换图标，百分比按危险程度换色。
    /// 【为什么不做刻度条】36px 高的胶囊里塞条会挤掉文字，百分比数字本身已经够看；
    /// 详情卡里才用条（那里有空间做 20 段刻度）。
    /// </summary>
    private FrameworkElement BuildBatteryPill(BatterySnapshot b)
    {
        var t = AppTheme.Current;
        string icon = b.Charging ? "battery-charging"
                    : b.IsFull ? "battery-full"
                    : b.Percent <= BatterySnapshot.LowThreshold ? "battery-low"
                    : "battery";
        var color = b.IsLow ? t.Danger
                  : b.Charging ? t.Success
                  : b.Percent <= 40 ? t.Warning
                  : t.TextPrimary;

        string state = b.Charging ? "充电中"
                     : b.IsFull ? "已充满"
                     : b.IsLow ? "电量不足"
                     : b.AcOnline ? "已接电源"
                     : "使用中";

        return StatusPill(
            icon: icon,
            on: b.Charging || b.AcOnline,
            stateColor: color,
            name: "电池",
            value: $"{b.Percent}%",
            valueColor: color,
            tail: state);
    }

    /// <summary>
    /// Wi-Fi 胶囊里速率列的**固定宽度**（未缩放 DIP）。
    ///
    /// 取值依据：最宽的文案是 `↓999.9 MB/s ↑999.9 MB/s`（两个方向都接近 1 GB/s），
    /// 实测约 118px；留 2px 余量 → 120。所有速率都按这个宽度右对齐排，
    /// 于是"数字在变、占位不变"，胶囊宽度恒定，不会拽动弹簧。
    /// </summary>
    private const double WifiSpeedColumnWidth = 120;

    /// <summary>Wi-Fi：SSID + 实时上下行（常驻模式每帧刷新）</summary>
    private FrameworkElement BuildWifiPill(WifiSnapshot w)
    {
        var t = AppTheme.Current;
        // 信号强弱只影响颜色，不换图标：Wi-Fi 的扇弧形状本身已足够识别，
        // 换图标会让胶囊在信号抖动时"闪图标"。
        var color = !w.Connected ? t.TextTertiary
                  : w.SignalPercent >= 66 ? t.Success
                  : w.SignalPercent >= 33 ? t.Warning
                  : t.Danger;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new Border
        {
            Margin = new Thickness(11, 0, 9, 0),
            Child = SvgIcons.CreateKeycap(w.Connected ? "wifi" : "wifi-off", 22, w.Connected, t)
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(w.Ssid) ? "Wi-Fi" : w.Ssid,
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = t.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = w.Connected ? $"{w.SignalPercent}%" : "未连接",
            FontSize = 11.5, Margin = new Thickness(7, 0, 0, 0),
            Foreground = color, VerticalAlignment = VerticalAlignment.Center
        });

        // 实时网速：**固定宽度列**。
        //
        // 【为什么必须定宽】速率文案长度天然在变（"↓0 KB/s" vs "↓12.3 MB/s"）。
        // 若让文字自然撑开，宽度就会随流量抖动，胶囊的弹簧跟着重新收敛 ——
        // 表现就是"数字一挤进来，整条岛被拽长一下"，也就是用户说的"空白/闪烁"。
        // 定宽后：文案变了但占位不变 → 弹簧目标恒等 → 岛屿纹丝不动，只有数字在变。
        //
        // 【宽度取多少】"↓999.9 MB/s ↑999.9 MB/s" 这种极端值也放得下（实测约 118px），
        // 再留一点呼吸 → 120。所有速率都用 TextAlign=Right 排，小数点右对齐更整齐。
        //
        // 【不要指定 FontFamily】宽度公式用的是 MeasureText（默认字体）实测，
        // 这里若换成别的字体，两边字宽就不一致 —— 实测差 12.6px（右侧多一块空白）。
        //
        // 【断开时不占位】定宽列在有连接时才是"占位不变"的收益；
        // 未连接时它只会白占 120px（实测胶囊被撑到 284，内容却只有 130）。
        if (w.Connected)
        {
            var speed = new TextBlock
            {
                Text = SpeedText(w),
                FontSize = 11,
                Width = WifiSpeedColumnWidth,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(7, 0, 12, 0),
                Foreground = t.TextTertiary, VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(speed);
            _wifiPillSpeedText = speed;      // 供 Render 每帧刷新（常驻模式下数字要动）
        }
        else
        {
            _wifiPillSpeedText = null;
        }

        return panel;
    }

    /// <summary>当前 Wi-Fi 胶囊里的速率文本（常驻模式靠它每帧刷新）</summary>
    private TextBlock? _wifiPillSpeedText;

    /// <summary>
    /// 内容换装时同步系统状态类的引用。
    ///
    /// 【为什么必须清】这些引用指向"最后一次构建出来的那棵子树"。
    /// 岛从胶囊变成球体 / 详情卡后，旧子树的 TextBlock 已经脱离视觉树，
    /// 继续往里写文本不会报错、也不会显示 —— 真正的危害是**新子树没人刷新**，
    /// 表现为"常驻 Wi-Fi 岛的速率停在第一次显示的数字上"。
    /// </summary>
    private void SyncSystemContentRefs(IslandItem item, FrameworkElement? content)
    {
        if (item.Event.Kind != "wifi") return;
        bool isPill = item.ContentKey == "pill" && content != null;
        if (!isPill) _wifiPillSpeedText = null;
        if (item.ContentKey != "card") _wifiCardParts = null;
        // 换装后速率文案必然要重算一次（新 TextBlock 的初始文本来自快照）
        _wifiLiveSig = "";
    }

    /// <summary>
    /// 网速文案：**永远占位、永远不返回空串**。
    ///
    /// 【曾经的空一下再加载】旧实现在流量低于 1 KB/s 时直接返回 `""`。
    /// 真实网络流量是突发性的：一发一收之后常常有几百毫秒完全静默，
    /// 于是文案在「空串 ↔ ↓12 KB/s」之间来回跳。而文案长度变化会牵动
    /// `MeasureSystemPillWidth` → 胶囊宽度 → 弹簧重新收敛，
    /// 视觉上就是「空白一下、数字挤进来、又空白一下」的闪烁。
    ///
    /// 【现在的做法】
    ///   · 有流量 → 照实显示；
    ///   · 无流量 → 显示 `↓0 KB/s ↑0 KB/s`（零值也是信息，且长度稳定）；
    ///   · 格式固定为两段，上下行都占位，避免"少一段"导致宽度跳变。
    /// </summary>
    private static string SpeedText(WifiSnapshot w)
    {
        if (!w.Connected) return "";
        return "↓" + FmtRate(SmoothBytes(w.RxBps)) + " ↑" + FmtRate(SmoothBytes(w.TxBps));
    }

    /// <summary>
    /// 速率平滑 + 量化：把原始 B/s 收敛到"不会来回跳档"的稳定读数。
    ///
    /// 【为什么需要它】源每 1s 采样一次，读数天然在 `0 / 12 KB/s / 380 KB/s` 之间抖。
    /// 若把每次采样直接显示，数字会疯狂跳动；若按 <see cref="FmtRate"/> 保留小数，
    /// "9.8 → 10.2 KB/s" 还会跨一位、牵动胶囊宽度。
    ///
    /// 因此这里把量级固化成 5 档（0 / KB 整数 / MB 一位），档内取整，
    /// 让显示值在同一量级内基本不动。
    /// </summary>
    private static double SmoothBytes(double bps)
    {
        if (bps < 512) return 0;                          // 半 KB 以下视为静默，归零
        if (bps < 1024 * 1024) return Math.Round(bps / 1024.0) * 1024;   // KB 整数档
        return Math.Round(bps / (1024.0 * 1024.0) * 10) / 10 * 1024 * 1024;  // MB 一位档
    }

    // ==================== 在线自检入口（仅 SysIslandDiag 使用） ====================

    /// <summary>仅供自检：把当前胶囊切到另一条岛（模拟点击球体 / 新事件抢焦点）</summary>
    internal void ProbeSetActiveOther(IslandItem current)
    {
        var other = _stack.Items.FirstOrDefault(i => !ReferenceEquals(i, current));
        if (other != null) _stack.SetActive(other.Event.Id);
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
    }

    /// <summary>仅供自检：把指定 id 的岛设为中央胶囊</summary>
    internal void ProbeSetActive(string id)
    {
        _stack.SetActive(id);
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
    }

    /// <summary>仅供自检：当前 Wi-Fi 胶囊里显示的速率文案</summary>
    internal string ProbeWifiPillText() => _wifiPillSpeedText?.Text ?? "";

    /// <summary>
    /// 仅供自检：摘掉全屏检测，让岛不再因为"前台有个全屏游戏"而整体隐藏。
    ///
    /// 【为什么需要】游戏/全屏时隐藏全栈是设计行为（docs §8）。但在开着 CS2 的机器上
    /// 跑在线自检，岛会被正确地隐藏 —— 于是自检看到的是"岛一条都没入场"，
    /// 这会把正确的设计判成失败。自检要验证的是系统状态链路，不是全屏策略，
    /// 因此这里把它摘掉，让断言只在"岛应该可见"的假设下成立。
    /// </summary>
    internal void ProbeDisableFullscreenHide()
    {
        try { Native.FullscreenDetector.FullscreenChanged -= OnFullscreenChanged; } catch { }
        Native.FullscreenDetector.Stop();
        _gameHidden = false;
    }

    // ==================== 每帧刷新（常驻 Wi-Fi） ====================

    /// <summary>本帧已经刷新过的速率快照字符串（避免每帧改写 TextBlock 触发重排）</summary>
    private string _wifiLiveSig = "";

    /// <summary>
    /// 每帧刷新 Wi-Fi 岛里的实时速率。
    ///
    /// 【为什么不在源里发事件】网速每秒都会变，若每变一次就发一条岛事件，
    /// 会触发整条岛的宽度重算 + 内容重建（胶囊每秒闪一下），
    /// 而且会把 EventBus 的 8 条/秒限流占满。这里只改两个 TextBlock 的文本，
    /// 零重建、零事件，代价只有一次字符串比较。
    ///
    /// 【只在需要时写】速率文案没变就直接返回 —— 静止网络下这帧等于空操作。
    ///
    /// 【宽度会变怎么办】速率的位数会变（9 KB/s → 120 KB/s），胶囊宽度跟着变才不裁。
    /// 但每秒改变弹簧目标会抖，所以这里只取整到"不会频繁跨档"的格式（KB/s 不带小数），
    /// 并让 MediaIsland 同一套"未落定就不改目标"的护栏生效（见 TryResizeWifiPill）。
    /// </summary>
    private void RefreshWifiLive()
    {
        var w = Sources.SystemStatusSource.LatestWifi;
        if (w == null) return;

        string sig = WifiLiveSignature(w);
        if (sig == _wifiLiveSig) return;
        _wifiLiveSig = sig;

        // ---- 胶囊态 ----
        if (_wifiPillSpeedText != null)
            _wifiPillSpeedText.Text = SpeedText(w);

        // ---- 卡片态 ----
        if (_wifiCardParts != null)
        {
            if (_wifiCardParts.Rx != null) _wifiCardParts.Rx.Text = FmtRate(w.RxBps);
            if (_wifiCardParts.Tx != null) _wifiCardParts.Tx.Text = FmtRate(w.TxBps);
        }

        // ---- 宽度：定宽速率列 ⇒ 文案变化不再需要改宽度 ----
        // 保留这个调用只为覆盖一种情况：连接状态翻转（未连接 ↔ 已连接）会增减速率列，
        // 那时宽度确实该变一次。纯流量波动不再触发。
    }

    /// <summary>
    /// 速率签名：只有"会改变胶囊宽度的东西"变了才需要刷新文本。
    /// 现在速率列是定宽的，文案怎么变都不影响布局，所以签名只看连接状态与信号。
    /// </summary>
    private static string WifiLiveSignature(WifiSnapshot w)
        => $"{w.Connected}|{w.SignalPercent}|{w.Ssid}";

    /// <summary>等宽度弹簧落定后再换宽（与歌词换档同一套护栏）</summary>
    private DispatcherTimer? _wifiResizePending;

    private void TryResizeWifiPill(WifiSnapshot w)
    {
        var item = _stack.Items.FirstOrDefault(i => i.Event.Id == "wifi" && !i.IsDying);
        if (item == null) return;
        if (item.Role != IslandRole.Pill || _stack.IsExpanded) return;
        if (!IsPillWidthSettled())
        {
            // 弹簧正在动：挂一个短延时，等它站稳再换（速率晚 200ms 显示完全看不出来）
            if (_wifiResizePending == null)
            {
                _wifiResizePending = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                _wifiResizePending.Tick += (_, _) =>
                {
                    _wifiResizePending!.Stop();
                    var cur = Sources.SystemStatusSource.LatestWifi;
                    if (cur != null) TryResizeWifiPill(cur);
                };
            }
            _wifiResizePending.Stop();
            _wifiResizePending.Start();
            return;
        }

        double want = MeasureSystemPillWidth(item.Event) ?? item.MeasuredWidth;
        if (Math.Abs(want - item.MeasuredWidth) < 4) return;    // 死区：吸收浮点与位数边界抖动
        item.MeasuredWidth = want;
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
    }

    /// <summary>字节/秒 → 人读的速率（B/s 以下不显示，KB/s、MB/s 各保留一位/两位）</summary>
    private static string FmtRate(double bps)
    {
        if (bps >= 1024 * 1024) return $"{bps / 1024.0 / 1024.0:0.0} MB/s";
        if (bps >= 1024) return $"{bps / 1024.0:0} KB/s";
        return $"{bps:0} B/s";
    }

    /// <summary>移动热点：路由图标 + 设备数</summary>
    private FrameworkElement BuildHotspotPill(HotspotSnapshot h)
    {
        var t = AppTheme.Current;
        return StatusPill(
            icon: "router",
            on: h.On,
            stateColor: h.On ? t.Success : t.TextSecondary,
            name: "热点",
            value: h.On ? $"{h.Clients} 台" : "关闭",
            tail: h.On && h.Clients > 0 ? "设备在线" : null);
    }

    /// <summary>
    /// 蓝牙胶囊：蓝牙图标 + 连接状态 + **已连接设备名**。
    ///
    /// 【设备名是重点】用户要的不是"蓝牙开/关"，而是"我现在连的是哪只耳机"。
    /// 所以副标题位置直接给设备名，多台时显示"第一台 等 N 台"。
    /// </summary>
    private FrameworkElement BuildBluetoothPill(BluetoothSnapshot bt)
    {
        var t = AppTheme.Current;
        return StatusPill(
            icon: "bluetooth",
            on: bt.Any,
            stateColor: bt.Any ? t.Accent : t.TextSecondary,
            name: "蓝牙",
            value: bt.Any ? $"已连接 {bt.Count} 台" : "未连接",
            valueColor: bt.Any ? t.Accent : t.TextTertiary,
            tail: bt.Any ? bt.Display : null);
    }

    /// <summary>飞行模式：飞机图标 + 开/关（飞行模式下 Wi-Fi 单独开启时如实标注）</summary>
    private FrameworkElement BuildAirplanePill(RadioSnapshot r)
    {
        var t = AppTheme.Current;
        string state = !r.AirplaneMode ? "关闭"
                     : r.WifiRadioOn ? "开启 · Wi-Fi 可用"
                     : "开启";
        return StatusPill(
            icon: "plane",
            on: r.AirplaneMode,
            stateColor: r.AirplaneMode ? t.Accent : t.TextSecondary,
            name: "飞行模式",
            value: state);
    }

    /// <summary>
    /// 深浅色切换：太阳 + 月亮合成一个图标（Lucide 的 sun-moon）。
    /// 深色 / 浅色共用这一条岛 —— 图标表达"这是外观开关"，文字表达"现在是哪一端"。
    /// </summary>
    private FrameworkElement BuildThemePill(Sources.ThemeSnapshot th)
    {
        var t = AppTheme.Current;
        return StatusPill(
            icon: "sun-moon",
            on: true,
            stateColor: th.Dark ? t.Accent : t.Warning,
            name: "外观模式",
            value: th.Dark ? "深色" : "浅色",
            valueColor: th.Dark ? t.Accent : t.Warning,
            tail: th.ModeLabel);
    }

    /// <summary>
    /// 系统状态类胶囊的通用骨架：键帽图标 + 名称 + 数值 + 可选尾巴。
    ///
    /// 【与 MeasureSystemPillWidth 严格对应】每一项的尺寸/边距都在两侧各写一次，
    /// 改任何一处都必须同步另一处，否则胶囊会出现"右侧留白"或"内容被裁"。
    /// </summary>
    private static FrameworkElement StatusPill(string icon, bool on, Brush stateColor,
        string name, string value, string? tail = null, Brush? valueColor = null)
    {
        var t = AppTheme.Current;
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 键帽：开 = 强调色实底，关 = 行面底色（与键位胶囊同一套视觉语言）
        panel.Children.Add(new Border
        {
            Margin = new Thickness(11, 0, 9, 0),
            Child = SvgIcons.CreateKeycap(icon, 22, on, t)
        });

        panel.Children.Add(new TextBlock
        {
            Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = t.TextPrimary, VerticalAlignment = VerticalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = value, FontSize = 11.5,
            Margin = new Thickness(7, 0, string.IsNullOrWhiteSpace(tail) ? 12 : 0, 0),
            Foreground = valueColor ?? stateColor,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(tail))
            panel.Children.Add(new TextBlock
            {
                Text = tail, FontSize = 11, Margin = new Thickness(7, 0, 12, 0),
                Foreground = t.TextTertiary, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130
            });

        return panel;
    }

    // ==================== 详情卡（展开态） ====================

    /// <summary>系统状态详情卡统一入口。返回 null 表示不归这里管。</summary>
    private FrameworkElement? BuildSystemCard(IslandEvent ev) => ev.Kind switch
    {
        "mic" when ev.Payload is MicSnapshot m => BuildMicCard(m),
        "battery" when ev.Payload is BatterySnapshot b => BuildBatteryCard(b),
        "wifi" when ev.Payload is WifiSnapshot w => BuildWifiCard(w),
        "hotspot" when ev.Payload is HotspotSnapshot h => BuildHotspotCard(h),
        "bluetooth" when ev.Payload is BluetoothSnapshot bt => BuildBluetoothCard(bt),
        "airplane" when ev.Payload is RadioSnapshot r => BuildAirplaneCard(r),
        "theme" when ev.Payload is Sources.ThemeSnapshot th => BuildThemeCard(th),
        _ => null
    };

    /// <summary>
    /// 卡片大图标块：圆角方块 + 居中图标（48×48）。
    /// 统一走这里，六张卡的头部才长得一样。
    /// </summary>
    private static FrameworkElement BigIcon(string icon, bool filled, Brush fill, Brush fg, double box = 44)
    {
        return new Border
        {
            Width = box, Height = box, CornerRadius = new CornerRadius(box * 0.28),
            Background = filled ? fill : AppTheme.Current.RowSurface,
            BorderBrush = filled ? fill : AppTheme.Current.Stroke,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid
            {
                Children = { SvgIcons.CreateCentered(icon, box * 0.52, fg, 1.2) }
            }
        };
    }

    /// <summary>卡片头部：大图标 + 主标题 + 副标题</summary>
    private static FrameworkElement CardHead(FrameworkElement iconBox, string title, string subtitle,
        Brush? titleColor = null)
    {
        var t = AppTheme.Current;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        iconBox.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(iconBox);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = title, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = titleColor ?? t.TextPrimary
        });
        info.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 12, Foreground = t.TextSecondary,
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        row.Children.Add(info);
        return row;
    }

    /// <summary>麦克风详情卡：占用者 + [麦克风权限][稍后提醒][忽略]</summary>
    private FrameworkElement BuildMicCard(MicSnapshot m)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        root.Children.Add(CardHead(
            BigIcon(m.Busy ? "mic" : "mic-off", m.Busy, m.Busy ? t.Warning : t.RowSurface,
                m.Busy ? Brushes.White : t.TextSecondary),
            m.Busy ? "麦克风正在被使用" : "麦克风已释放",
            m.Busy
                ? (m.Users > 1 ? $"{m.AppName} 等 {m.Users} 个程序在录音" : $"{m.AppName} 正在录音")
                : "已没有程序占用麦克风"));

        // 占用者清单（多人时逐条列出，一眼看清是谁）
        if (m.Busy)
        {
            var listRow = new StackPanel { Orientation = Orientation.Horizontal };
            listRow.Children.Add(new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = SvgIcons.CreateCentered("mic", 16, t.Warning, 1.15)
            });
            listRow.Children.Add(new TextBlock
            {
                Text = m.AppName,
                FontSize = 11.5, Foreground = t.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });
            listRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(7), Background = t.RowSurface,
                BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
                Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(7, 1, 7, 1),
                Child = new TextBlock { Text = "正在录音", FontSize = 10, Foreground = t.Warning }
            });
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Background = t.RowSurface,
                BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(11, 8, 11, 8),
                Child = listRow
            });
        }

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("稍后提醒", "bell", t, false, () =>
        {
            Sources.SysStateSource.SnoozeValue("mic", m.Busy);
            DismissActive();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", "bell-off", t, false, () =>
        {
            Sources.SysStateSource.IgnoreValue("mic", m.Busy);
            DismissActive();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("麦克风权限", "settings", t, true, () =>
        {
            DismissActive();
            OpenSettingsPage("ms-settings:privacy-microphone");
            return Task.CompletedTask;
        }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>电池详情卡：电量刻度条 + 充电状态 + 剩余时间 + [电源设置][忽略]</summary>
    private FrameworkElement BuildBatteryCard(BatterySnapshot b)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        string icon = b.Charging ? "battery-charging"
                    : b.IsFull ? "battery-full"
                    : b.Percent <= BatterySnapshot.LowThreshold ? "battery-low"
                    : "battery";
        var color = b.IsLow ? t.Danger : b.Charging ? t.Success : t.Accent;

        root.Children.Add(CardHead(
            BigIcon(icon, true, color, Brushes.White),
            $"{b.Percent}%",
            b.Charging ? "正在充电"
                : b.IsFull ? "已充满 · 可以拔掉电源"
                : b.AcOnline ? "已接通电源"
                : "正在使用电池",
            b.IsLow ? t.Danger : t.TextPrimary));

        // ---- 电量刻度条 ----
        // 20 段小格而不是一条连续条：百分比读数更直观（一眼能数出"还剩几格"），
        // 与系统托盘电池图标的刻度感也一致。
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        const int segments = 20;
        int filled = (int)Math.Round(b.Percent / 100.0 * segments);
        double segW = Math.Max(4, (IslandMetrics.CardWidth - 32 - (segments - 1) * 3) / segments);
        for (int i = 0; i < segments; i++)
        {
            bool on = i < filled;
            bar.Children.Add(new Border
            {
                Width = segW, Height = 8, CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, i == segments - 1 ? 0 : 3, 0),
                Background = on ? color : t.Track
            });
        }
        root.Children.Add(bar);

        // ---- 明细行 ----
        var facts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Fact(string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            { Text = label, FontSize = 11.5, Foreground = t.TextSecondary });
            var v = new TextBlock
            {
                Text = value, FontSize = 11.5, Foreground = t.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            facts.Children.Add(g);
        }
        Fact("供电方式", b.AcOnline ? "交流电源" : "电池");
        Fact("充电状态", b.Charging ? "充电中" : b.AcOnline ? "未充电" : "放电中");
        if (b.SecondsRemaining > 0)
        {
            var ts = TimeSpan.FromSeconds(b.SecondsRemaining);
            string remain = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分"
                : $"{Math.Max(1, ts.Minutes)} 分钟";
            Fact(b.Charging ? "预计充满" : "预计可用", remain);
        }
        Fact("节电模式", b.SaverOn ? "已开启" : "关闭");
        root.Children.Add(facts);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("电源设置", "settings", t, true, () =>
        {
            DismissActive();
            OpenSettingsPage("ms-settings:batterysaver");
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", t, false, () => { DismissActive(); return Task.CompletedTask; }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>Wi-Fi 详情卡：SSID / 信号 / 协商速率 / 实时上下行（每帧刷新）</summary>
    private FrameworkElement BuildWifiCard(WifiSnapshot w)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        root.Children.Add(CardHead(
            BigIcon(w.Connected ? "wifi" : "wifi-off", w.Connected, t.Success,
                w.Connected ? Brushes.White : t.TextSecondary),
            w.Connected ? (string.IsNullOrWhiteSpace(w.Ssid) ? "已连接" : w.Ssid) : "Wi-Fi 未连接",
            w.Connected ? $"信号 {w.SignalPercent}% · 协商 {w.LinkMbps:0} Mbps" : "当前没有无线网络连接"));

        if (w.Connected)
        {
            // ---- 实时速率两块并排：下行 / 上行 ----
            var speedRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var rxText = SpeedTile("下行", "arrow-down", w.RxBps, t.Accent, false);
            var txText = SpeedTile("上行", "arrow-up", w.TxBps, t.Success, true);
            speedRow.Children.Add(rxText.Tile);
            speedRow.Children.Add(txText.Tile);
            root.Children.Add(speedRow);

            // 交给 Render 每帧刷新（网速是唯一需要持续动起来的数字）
            _wifiCardParts = new WifiCardParts { Rx = rxText.Value, Tx = txText.Value };
        }
        else
        {
            _wifiCardParts = null;
        }

        // ---- 明细行 ----
        var facts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Fact(string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = t.TextSecondary });
            var v = new TextBlock
            {
                Text = value, FontSize = 11.5, Foreground = t.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            facts.Children.Add(g);
        }
        Fact("无线网卡", w.WifiRadioOn ? "已启用" : "已关闭");
        Fact("信号强度", w.Connected ? $"{w.SignalPercent}%" : "—");
        Fact("协商速率", w.Connected ? $"{w.LinkMbps:0} Mbps" : "—");
        root.Children.Add(facts);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("网络设置", "settings", t, true, () =>
        {
            DismissActive();
            OpenSettingsPage("ms-settings:network-wifi");
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", t, false, () => { DismissActive(); return Task.CompletedTask; }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>Wi-Fi 卡片里需要每帧刷新的两个数字</summary>
    private sealed class WifiCardParts
    {
        public TextBlock? Rx;
        public TextBlock? Tx;
    }

    private WifiCardParts? _wifiCardParts;

    /// <summary>
    /// 一个速率方块：图标 + 标签 + 数值。
    /// 卡片宽 520、左右内边距 16×2 → 可用 488，两块各 240（留 8 间距）。
    /// </summary>
    private static (FrameworkElement Tile, TextBlock Value) SpeedTile(string label, string icon,
        double bps, Brush accent, bool last)
    {
        var t = AppTheme.Current;
        var value = new TextBlock
        {
            Text = FmtRate(bps), FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = t.TextPrimary, Margin = new Thickness(0, 3, 0, 0)
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Width = 14, Height = 14, Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = SvgIcons.CreateCentered(icon, 14, accent, 1.3)
        });
        head.Children.Add(new TextBlock
        {
            Text = label, FontSize = 10.5, Foreground = t.TextTertiary,
            VerticalAlignment = VerticalAlignment.Center
        });

        var sp = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
        sp.Children.Add(head);
        sp.Children.Add(value);

        var tile = new Border
        {
            Width = 240, Height = 58, CornerRadius = new CornerRadius(13),
            Background = t.RowSurface, BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, last ? 0 : 8, 0),
            Child = sp
        };
        return (tile, value);
    }

    /// <summary>热点详情卡：设备数 + 设备清单 + [热点设置][忽略]</summary>
    private FrameworkElement BuildHotspotCard(HotspotSnapshot h)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        root.Children.Add(CardHead(
            BigIcon("router", h.On, h.On ? t.Success : t.RowSurface,
                h.On ? Brushes.White : t.TextSecondary),
            h.On ? "移动热点已开启" : "移动热点已关闭",
            h.On ? $"{h.Clients} 台设备已连接" : "当前没有共享网络"));

        if (h.On)
        {
            // 设备清单：一行一台，超出（>4）折叠成 +N
            var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            const int maxShow = 4;
            for (int i = 0; i < Math.Min(h.ClientAddresses.Count, maxShow); i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new Border
                {
                    Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = SvgIcons.CreateCentered("smartphone", 15, t.TextSecondary, 1.15)
                });
                row.Children.Add(new TextBlock
                {
                    Text = h.ClientAddresses[i], FontSize = 11.5, Foreground = t.TextSecondary,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                list.Children.Add(row);
            }
            if (h.ClientAddresses.Count > maxShow)
                list.Children.Add(new TextBlock
                {
                    Text = $"还有 {h.ClientAddresses.Count - maxShow} 台…",
                    FontSize = 11, Foreground = t.TextTertiary, Margin = new Thickness(23, 2, 0, 0)
                });
            if (h.ClientAddresses.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = "暂无设备接入（等待手机 / 平板连接）",
                    FontSize = 11.5, Foreground = t.TextTertiary
                });
            root.Children.Add(list);
        }

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("热点设置", "settings", t, true, () =>
        {
            DismissActive();
            OpenSettingsPage("ms-settings:network-mobilehotspot");
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", t, false, () => { DismissActive(); return Task.CompletedTask; }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>
    /// 蓝牙详情卡：已连接设备清单 + 已配对总数 + [蓝牙设置]。
    ///
    /// 没有已连接设备时不说"未连接"就完事，而是区分两种情况：
    /// 已配对 N 台（只是没连）/ 一台都没配对（新机器或刚重置）。
    /// 这两种情况下用户要做的事完全不同。
    /// </summary>
    private FrameworkElement BuildBluetoothCard(BluetoothSnapshot bt)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        root.Children.Add(CardHead(
            BigIcon("bluetooth", bt.Any, bt.Any ? t.Accent : t.RowSurface,
                bt.Any ? Brushes.White : t.TextSecondary),
            bt.Any ? (bt.Count == 1 ? "已连接 1 台蓝牙设备" : $"已连接 {bt.Count} 台蓝牙设备")
                   : "没有已连接的蓝牙设备",
            bt.Any ? $"已配对 {bt.PairedCount} 台 · 当前在线 {bt.Count} 台"
                   : bt.PairedCount > 0 ? $"已配对 {bt.PairedCount} 台，但都没有连接"
                   : "本机还没有配对过蓝牙设备"));

        // 设备清单：一行一台，超出 5 台折叠
        if (bt.Any)
        {
            var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            const int maxShow = 5;
            for (int i = 0; i < Math.Min(bt.Devices.Count, maxShow); i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                row.Children.Add(new Border
                {
                    Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = SvgIcons.CreateCentered("bluetooth", 15, t.Accent, 1.15)
                });
                row.Children.Add(new TextBlock
                {
                    Text = bt.Devices[i], FontSize = 12, Foreground = t.TextPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380
                });
                list.Children.Add(row);
            }
            if (bt.Devices.Count > maxShow)
                list.Children.Add(new TextBlock
                {
                    Text = $"还有 {bt.Devices.Count - maxShow} 台…", FontSize = 11,
                    Foreground = t.TextTertiary, Margin = new Thickness(23, 3, 0, 0)
                });
            root.Children.Add(list);
        }

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("蓝牙设置", "settings", t, true, () =>
        {
            DismissActive();
            OpenSettingsPage("ms-settings:bluetooth");
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", t, false, () => { DismissActive(); return Task.CompletedTask; }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>飞行模式详情卡：状态 + Wi-Fi 电台 + [网络设置][关闭飞行模式]</summary>
    private FrameworkElement BuildAirplaneCard(RadioSnapshot r)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        root.Children.Add(CardHead(
            BigIcon("plane", r.AirplaneMode, r.AirplaneMode ? t.Accent : t.RowSurface,
                r.AirplaneMode ? Brushes.White : t.TextSecondary),
            r.AirplaneMode ? "飞行模式已开启" : "飞行模式已关闭",
            r.AirplaneMode
                ? (r.WifiRadioOn ? "所有无线已关闭，但 Wi-Fi 仍单独开启" : "所有无线连接已关闭")
                : "无线连接已恢复"));

        var facts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        void Fact(string label, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = t.TextSecondary });
            var v = new TextBlock
            {
                Text = value, FontSize = 11.5, Foreground = t.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            facts.Children.Add(g);
        }
        Fact("飞行模式", r.AirplaneMode ? "开" : "关");
        Fact("Wi-Fi 电台", r.WifiRadioOn ? "开" : "关");
        root.Children.Add(facts);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("网络设置", "settings", t, true, () =>
        {
            DismissActive();
            OpenSettingsPage("ms-settings:network-airplanemode");
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", t, false, () => { DismissActive(); return Task.CompletedTask; }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>
    /// 深浅色切换详情卡：当前模式 + 一键切到另一种。
    /// 【为什么要能一键切换】这个岛出现的时机正是"你刚改了外观"，
    /// 顺势给一个"切回去"的入口，比让你再翻一遍系统设置要顺手得多。
    /// </summary>
    private FrameworkElement BuildThemeCard(Sources.ThemeSnapshot th)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        root.Children.Add(CardHead(
            BigIcon("sun-moon", true, th.Dark ? t.Accent : t.Warning, Brushes.White),
            th.Dark ? "深色模式" : "浅色模式",
            th.ModeLabel));

        // 太阳 / 月亮并排：把"同一个开关的两端"同时画出来，一眼看懂这是二选一
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        pair.Children.Add(ThemeChoice("浅色", "sun", !th.Dark, t.Warning, t));
        pair.Children.Add(ThemeChoice("深色", "moon", th.Dark, t.Accent, t, last: true));
        root.Children.Add(pair);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton(th.Dark ? "切到浅色" : "切到深色",
            th.Dark ? "sun" : "moon", t, true, () =>
            {
                var next = th.Dark ? "Light" : "Dark";
                _settings.Theme.Mode = next;
                _settings.Save();
                App.ApplyTheme(next);
                return Task.CompletedTask;
            }));
        acts.Children.Add(MakeTextButton("跟随系统", "settings", t, false, () =>
        {
            _settings.Theme.Mode = "System";
            _settings.Save();
            App.ApplyTheme("System");
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", t, false, () => { DismissActive(); return Task.CompletedTask; }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>浅色 / 深色并排展示里的一格（选中态实底）</summary>
    private static FrameworkElement ThemeChoice(string label, string icon, bool selected,
        Brush color, ThemeTokens t, bool last = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        sp.Children.Add(new Border
        {
            Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = SvgIcons.CreateCentered(icon, 18, selected ? Brushes.White : t.TextTertiary, 1.2)
        });
        sp.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12.5,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = selected ? Brushes.White : t.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        });

        return new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = selected ? color : t.RowSurface,
            BorderBrush = selected ? color : t.Stroke,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, last ? 0 : 8, 0),
            Child = sp
        };
    }

    /// <summary>打开一个系统设置页（失败只记日志，不弹系统框）</summary>
    private static void OpenSettingsPage(string uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log("System", ex); }
    }

    // ==================== 设置页预览 ====================

    /// <summary>
    /// 设置页"预览"按钮的统一入口：按 key 造一条样例岛并送入正常事件通路。
    /// 返回 false 表示这个 key 不归本分部类管（继续交给键位 / 解锁 / 摄像头那套）。
    ///
    /// 【为什么用真实快照而不是假数据】样例值尽量镜像当前系统真实状态
    /// （例如电池用实际百分比、Wi-Fi 用实际 SSID），这样"预览长什么样、真弹出来就长什么样"，
    /// 也不会因为写死 "6h 12m" 这类数字让人误以为功能算错了。
    /// </summary>
    private bool PreviewSystemStatus(string key)
    {
        IslandEvent? ev = key switch
        {
            "mic" => MakeSysPreview("mic", "麦克风被占用", "WeChat 正在使用",
                new MicSnapshot(true, "WeChat", 1), 4),
            "mic-off" => MakeSysPreview("mic", "麦克风已释放", "已没有程序在录音",
                new MicSnapshot(false, "", 0), 4),

            "battery" => MakeSysPreview("battery", "已接通电源", "正在充电 · 85%",
                new BatterySnapshot(true, 85, true, true, 3600, false), 4),
            "battery-low" => MakeSysPreview("battery", "电量不足", "18% · 建议接通电源",
                new BatterySnapshot(true, 18, false, false, 2400, false), 6),

            "wifi" => PreviewWifi(),
            "hotspot" => MakeSysPreview("hotspot", "移动热点已开启", "2 台设备已连接",
                new HotspotSnapshot(true, 2, new[] { "192.168.137.27", "192.168.137.31" }, true), 4),

            "bluetooth" => PreviewBluetooth(),

            "airplane" => MakeSysPreview("airplane", "飞行模式已开启", "所有无线连接已关闭",
                new RadioSnapshot(true, false), 4),

            "theme-light" => MakeSysPreview("theme", "已切换到浅色", "跟随系统 · 浅色",
                new Sources.ThemeSnapshot(false, "跟随系统 · 浅色"), 3),
            "theme-dark" => MakeSysPreview("theme", "已切换到深色", "跟随系统 · 深色",
                new Sources.ThemeSnapshot(true, "跟随系统 · 深色"), 3),

            _ => null
        };
        if (ev == null) return false;

        OnEvent(ev);
        return true;
    }

    /// <summary>造一条系统状态预览事件（Id 带 preview 前缀，避免和真实事件抢同一个岛）</summary>
    private static IslandEvent MakeSysPreview(string kind, string title, string subtitle,
        object payload, int ttlSeconds) => new(
            Id: "preview-" + kind + "-" + Guid.NewGuid().ToString("N")[..6],
            SourceKey: "preview",
            Kind: kind,
            Priority: 2,
            Title: title,
            Subtitle: subtitle,
            Payload: payload,
            Policy: new IslandPolicy(TimeSpan.FromSeconds(ttlSeconds), Coalesce: false,
                Expandable: true, HoverPauses: true, QueueWhenHidden: false))
    { AppKey = kind, StealFocus = true };

    /// <summary>Wi-Fi 预览：优先用真实快照（连不上也没有关系，会退化为示例文案）</summary>
    private static IslandEvent PreviewWifi()
    {
        var live = Sources.SystemStatusSource.LatestWifi;
        var snap = live is { Connected: true }
            ? live
            : new WifiSnapshot(true, "JCG_WLAN", 88, 344, 1_600_000, 220_000, true);
        return MakeSysPreview("wifi", "已连接 Wi-Fi", snap.Ssid, snap, 6);
    }

    /// <summary>
    /// 蓝牙预览：优先用真实已连接设备；没连设备时用示例名，
    /// 这样"预览"永远能看到完整样式（而不是一条"未连接"的空壳）。
    /// </summary>
    private static IslandEvent PreviewBluetooth()
    {
        var live = Sources.SystemStatusSource.LatestBluetooth;
        BluetoothSnapshot snap;
        if (live is { Supported: true, Count: > 0 }) snap = live;
        else
        {
            var devices = new[] { "Redmi Buds 5 Pro", "BT5.2 Mouse" };
            snap = new BluetoothSnapshot(true, devices, Math.Max(live?.PairedCount ?? 0, 3));
        }
        return MakeSysPreview("bluetooth", "蓝牙设备已连接", snap.Display, snap, 5);
    }

    // ==================== 球体（侧列） ====================

    /// <summary>系统状态球体：用 Lucide 状态图标，而不是应用图标。返回 null 表示不归这里管。</summary>
    private static FrameworkElement? BuildSystemSphere(IslandEvent ev)
    {
        var t = AppTheme.Current;
        switch (ev.Kind)
        {
            case "mic" when ev.Payload is MicSnapshot m:
                return SvgIcons.CreateSphere(m.Busy ? "mic" : "mic-off", 19,
                    m.Busy ? t.Warning : t.TextSecondary);
            case "battery" when ev.Payload is BatterySnapshot b:
                return SvgIcons.CreateSphere(
                    b.Charging ? "battery-charging" : b.IsLow ? "battery-low" : "battery", 19,
                    b.IsLow ? t.Danger : b.Charging ? t.Success : t.TextSecondary);
            case "wifi" when ev.Payload is WifiSnapshot w:
                return SvgIcons.CreateSphere(w.Connected ? "wifi" : "wifi-off", 19,
                    w.Connected ? t.Success : t.TextSecondary);
            case "hotspot" when ev.Payload is HotspotSnapshot h:
                return SvgIcons.CreateSphere("router", 19, h.On ? t.Success : t.TextSecondary);
            case "bluetooth" when ev.Payload is BluetoothSnapshot bt:
                return SvgIcons.CreateSphere("bluetooth", 19,
                    bt.Any ? t.Accent : t.TextSecondary);
            case "airplane" when ev.Payload is RadioSnapshot r:
                return SvgIcons.CreateSphere("plane", 19, r.AirplaneMode ? t.Accent : t.TextSecondary);
            case "theme" when ev.Payload is Sources.ThemeSnapshot th:
                // 球体同样用合成图标：用户在侧列也能一眼认出这是"外观"类
                return SvgIcons.CreateSphere("sun-moon", 19,
                    th.Dark ? t.Accent : t.Warning);
            default:
                return null;
        }
    }

    // ==================== 宽度公式 ====================

    /// <summary>
    /// 系统状态胶囊宽度。返回 null 表示不归这里管（由主 switch 的默认分支接手）。
    ///
    /// 【必须与 StatusPill 的排版逐项对应】
    ///   左 11 + 键帽 22 + 9 + 名称 13(SemiBold) + 7 + 数值 11.5
    ///   + 尾巴（有则 7 + ≤130 + 12；无则 12 右内边距）
    /// 任何一项脱节，症状都是"胶囊右侧凭空多一块空白"或"内容被裁"。
    /// </summary>
    private double? MeasureSystemPillWidth(IslandEvent ev)
    {
        switch (ev.Kind)
        {
            case "mic" when ev.Payload is MicSnapshot m:
                return StatusPillWidth("麦克风", m.Busy ? "使用中" : "空闲",
                    m.Busy ? m.AppName : null);

            case "battery" when ev.Payload is BatterySnapshot b:
                return StatusPillWidth("电池", $"{b.Percent}%", BatteryStateText(b));

            case "wifi" when ev.Payload is WifiSnapshot w:
                {
                    // Wi-Fi 胶囊：11 + 键帽 22 + 9 + SSID(≤150) + 7 + 信号% + 7 + 速率列(定宽) + 12
                    //
                    // 【速率列用常量，不用 MeasureText(SpeedText)】
                    // 用实测文案宽会让胶囊宽度随流量抖动（这正是"闪一下"的成因）。
                    // 排版那边同样是定宽列，两边一致 ⇒ 宽度恒定，只有数字在变。
                    double w0 = 11 + 22 + 9
                        + Math.Min(MeasureText(string.IsNullOrWhiteSpace(w.Ssid) ? "Wi-Fi" : w.Ssid, 13, true), 150)
                        + 7 + MeasureText(w.Connected ? $"{w.SignalPercent}%" : "未连接", 11.5)
                        + (w.Connected ? 7 + WifiSpeedColumnWidth : 0)
                        + 12;
                    return w0;
                }

            case "hotspot" when ev.Payload is HotspotSnapshot h:
                return StatusPillWidth("热点", h.On ? $"{h.Clients} 台" : "关闭",
                    h.On && h.Clients > 0 ? "设备在线" : null);

            case "bluetooth" when ev.Payload is BluetoothSnapshot bt:
                return StatusPillWidth("蓝牙",
                    bt.Any ? $"已连接 {bt.Count} 台" : "未连接",
                    bt.Any ? bt.Display : null);

            case "airplane" when ev.Payload is RadioSnapshot r:
                return StatusPillWidth("飞行模式",
                    !r.AirplaneMode ? "关闭" : r.WifiRadioOn ? "开启 · Wi-Fi 可用" : "开启", null);

            case "theme" when ev.Payload is Sources.ThemeSnapshot th:
                return StatusPillWidth("外观模式", th.Dark ? "深色" : "浅色", th.ModeLabel);

            default:
                return null;
        }
    }

    /// <summary>电池胶囊右侧那句状态（与 BuildBatteryPill 的 tail 同源）</summary>
    private static string BatteryStateText(BatterySnapshot b)
        => b.Charging ? "充电中"
         : b.IsFull ? "已充满"
         : b.IsLow ? "电量不足"
         : b.AcOnline ? "已接电源"
         : "使用中";

    /// <summary>StatusPill 的宽度公式（与排版一一对应）</summary>
    private double StatusPillWidth(string name, string value, string? tail)
    {
        double w = 11 + 22 + 9
                 + MeasureText(name, 13, true)
                 + 7 + MeasureText(value, 11.5);
        if (!string.IsNullOrWhiteSpace(tail))
            w += 7 + Math.Min(MeasureText(tail!, 11), 130) + 12;
        else
            w += 12;
        return w;
    }
}

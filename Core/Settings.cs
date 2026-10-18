using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronoIsland.Core;

/// <summary>
/// 设置模型（docs §9）。JSON 持久化 + 防抖保存。
/// </summary>
public sealed class Settings
{
    /// <summary>
    /// 当前配置结构版本。**改配置结构时 +1，并在 <see cref="Migrate"/> 里补一段迁移。**
    /// v1 = 0.0.1 及以前（文件里没有 schemaVersion 字段）
    /// v2 = 0.0.2：首次写入 schemaVersion，为后续插件配置做铺垫
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 配置结构版本号（0.0.2 引入）。
    /// 【为什么要有它】0.0.1 的 settings.json 没有任何版本标识，
    /// 将来重命名 / 删除设置项时无法判断"这是老文件还是用户手改坏了"，
    /// 只能静默丢字段。有版本号才能写确定性的迁移。
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public IslandSettings Island { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
    public ClockSettings Clock { get; set; } = new();
    public NotificationSettings Notification { get; set; } = new();
    public UsageSettings Usage { get; set; } = new();
    public MediaSettings Media { get; set; } = new();
    public GameSettings Game { get; set; } = new();
    public SysSettings Sys { get; set; } = new();
    public DshSettings Dsh { get; set; } = new();
    /// <summary>电源 / 电池（docs §5.8）</summary>
    public PowerSettings Power { get; set; } = new();
    /// <summary>网络：Wi-Fi / 移动热点 / 飞行模式（docs §5.9–§5.11）</summary>
    public NetworkSettings Network { get; set; } = new();
    /// <summary>麦克风占用（docs §5.12）</summary>
    public MicSettings Mic { get; set; } = new();
    /// <summary>深浅色切换提示（docs §5.13）</summary>
    public ThemeSwitchSettings ThemeSwitch { get; set; } = new();
    public GeneralSettings General { get; set; } = new();

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChronoIsland", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                // 文件里没有 schemaVersion → 是 0.0.1 时代写出的，按 v1 处理。
                // 不能只看反序列化结果：属性带默认值，缺字段时读不出"缺过"。
                s.Migrate(FromDisk(json));
                return s;
            }
        }
        catch (Exception ex) { App.Log("Settings", ex); }
        return new Settings();
    }

    /// <summary>读出文件里记录的 schema 版本；缺字段 / 非法值一律按最早的 v1 处理。</summary>
    private static int FromDisk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("schemaVersion", out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0)
                return n;
        }
        catch { /* 解析失败按 v1 */ }
        return 1;
    }

    /// <summary>
    /// 逐级迁移到当前版本。幂等，每次加载都会调用。
    ///
    /// 【约定】新增迁移时只允许"补齐 / 重命名 / 换算"，不允许丢弃用户数据；
    /// 文件版本比当前**新**时（用户回退了版本）不降级，只记日志并保留原值。
    /// </summary>
    private void Migrate(int from)
    {
        if (from < 1) from = 1;

        if (from > CurrentSchemaVersion)
        {
            App.LogInfo("Settings",
                $"配置版本 v{from} 高于本程序支持的 v{CurrentSchemaVersion}（可能装过更新的版本）。保留原文件，不做降级改写。");
            SchemaVersion = from;
            return;
        }

        if (from < CurrentSchemaVersion)
        {
            // v1 → v2：0.0.2 只补版本号本身，字段结构未变，无需搬运数据。
            // （后续版本在这里追加 case，并注意每一步都要能从 from 连续走到 Current。）
            App.LogInfo("Settings", $"配置已从 v{from} 迁移到 v{CurrentSchemaVersion}");
        }

        SchemaVersion = CurrentSchemaVersion;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { App.Log("Settings", ex); }
    }
}

public sealed class IslandSettings
{
    public double Top { get; set; } = 8;
    public double MaxWidth { get; set; } = 420;
    /// <summary>整体缩放（默认 1.5×，可在 0.5× ~ 3× 间调整）</summary>
    public double Scale { get; set; } = 1.5;

    /// <summary>
    /// 悬停放行时间（毫秒）。
    /// 鼠标进入岛后，先保持"穿透"状态这么久；超过后才变为可交互。
    /// 这样想点岛后面被挡住的按钮时，鼠标划过不会误触到岛。
    /// </summary>
    public int HoverActivateMs { get; set; } = 400;

    /// <summary>是否启用"悬停穿透 → 停留后可交互"策略</summary>
    public bool HoverActivate { get; set; } = true;

    /// <summary>
    /// 悬浮自动展开详情卡的停留时间（毫秒）。
    /// 0 = 关闭（只允许点击展开）。
    /// 独立于 <see cref="HoverActivateMs"/>：那个管"能否交互"，这个管"要不要自动展开"。
    /// </summary>
    public int HoverExpandMs { get; set; } = 700;

    /// <summary>按住 Alt 时强制穿透（保险：万一误拦截可点到底下内容）</summary>
    public bool AltForceThrough { get; set; } = true;

    /// <summary>
    /// 半透明穿透态悬浮球体 -> 提升为胶囊。
    /// 该状态下岛是 WS_EX_TRANSPARENT，收不到鼠标事件，由命中轮询驱动；
    /// 开启后指针停在球体上约 120ms 就会把它提到中央胶囊（再由胶囊展开详情卡）。
    /// </summary>
    public bool HoverSphereInPassive { get; set; } = true;

    /// <summary>
    /// 可交互态（已停留够久、不再穿透）悬浮球体 -> 提升为胶囊。
    /// 与上一项分开控制：有些人只想要"可交互后才有反应"。
    /// </summary>
    public bool HoverSphereInInteractive { get; set; } = true;
}

public sealed class ThemeSettings
{
    /// <summary>System | Dark | Light</summary>
    public string Mode { get; set; } = "System";
    public bool Acrylic { get; set; } = true;
}

public sealed class ClockSettings
{
    public bool Hour24 { get; set; } = true;
    public bool ShowSeconds { get; set; }
    public bool Persistent { get; set; }
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>是否启用"鸠占鹊巢"：把系统通知从操作中心移除，由岛独占</summary>
    public bool TakeOver { get; set; } = true;
    public int TtlSeconds { get; set; } = 5;
    public bool SuppressNative { get; set; } = true;
    public int MaxStacked { get; set; } = 3;
}

public sealed class UsageSettings
{
    public bool Enabled { get; set; } = true;
    public bool CaptureTitle { get; set; }
    public int IdleSeconds { get; set; } = 300;
    public int BreakMinutes { get; set; } = 50;
}

public sealed class MediaSettings
{
    public bool Enabled { get; set; } = true;
    public bool BrowserAsFallback { get; set; } = true;
    /// <summary>暂停/停止后多少秒收起媒体岛</summary>
    public int PausedHideSeconds { get; set; } = 5;
    /// <summary>是否只在播放时显示（暂停立即收起）</summary>
    public bool HideWhenPaused { get; set; }

    // ---- 歌词（designWeb/docs/design-lyrics-0.0.1.md）----
    /// <summary>是否在胶囊态显示歌词（开启后胶囊会增高一行）</summary>
    public bool ShowLyrics { get; set; }
    /// <summary>歌词时间轴偏移（毫秒），LRC 常有数百毫秒偏差，供用户微调</summary>
    public int LyricOffsetMs { get; set; }
    /// <summary>歌词放不下时是否自动滚动（关闭则直接截断）</summary>
    public bool LyricAutoScroll { get; set; } = true;
    /// <summary>是否显示翻译行（歌词副行）</summary>
    public bool ShowLyricTranslation { get; set; }
}

public sealed class GameSettings
{
    public bool HideOnFullscreen { get; set; } = true;
    public string[] ExtraProcesses { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 系统状态模块（docs §5.5）：键位状态（CapsLock / NumLock / ScrollLock）与会话解锁。
/// 值级忽略：忽略的是"信号+值"，不是信号本身——值 60s 过期后再次触发重新弹出。
/// </summary>
public sealed class SysSettings
{
    /// <summary>整个模块总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>CapsLock 翻转提醒</summary>
    public bool CapsLock { get; set; } = true;

    /// <summary>NumLock 翻转提醒（无小键盘的笔记本可自动隐藏）</summary>
    public bool NumLock { get; set; } = true;

    /// <summary>ScrollLock 翻转提醒（极少使用，默认关）</summary>
    public bool ScrollLock { get; set; } = false;

    /// <summary>锁屏 / 休眠后解锁进入桌面时浮现</summary>
    public bool Unlock { get; set; } = true;

    /// <summary>摄像头被启用 / 释放时提醒（隐私提醒，docs §5.6）</summary>
    public bool Camera { get; set; } = true;

    /// <summary>状态快照胶囊存活秒数</summary>
    public int TtlSeconds { get; set; } = 4;

    /// <summary>
    /// 值级忽略条目过期秒数：忽略"大写=开"60s 后，若再次触发该值则重新弹出。
    /// </summary>
    public int IgnoreValueExpirySeconds { get; set; } = 60;

    /// <summary>笔记本无小键盘（GetKeyboardType()==10）时自动隐藏 NumLock 信号</summary>
    public bool AutoHideNumlockNoNumpad { get; set; } = true;
}

/// <summary>
/// DeepSeek Harness 工作状态模块。
///
/// 【数据来源】直接只读 DSH 的会话投影缓存（明文 JSON，非 zstd）：
///   <c>%DSH_HOME%\storages\session_projcache\sessions\&lt;sessionId&gt;.json</c>
/// 该文件由 DSH 原子写入（实测 68 文件 / 高频轮询 0 次解析失败，且无 .tmp/.lock 伴随文件），
/// 因此可以安全并发读取。关键字段：
///   · <c>record.rows.sessionStats.val.openStep</c> —— 非 null 即"正在跑某一步"（工作中）
///   · <c>record.rows.contextPressure.val</c>     —— 上下文占用 surfaceTokens / contextWindow
///   · <c>record.rows.tokenUsage.val</c>          —— 累计 token 与最近一步增量
///   · <c>record.rows.title.val</c>               —— 会话标题
///   · <c>record.rows.modelSelection.val.lastUsed</c> —— 当前模型
///
/// 【为什么不用别的信号】窗口标题恒为静态 "DeepSeek Harness"；
/// 会话日志 session.v4.jsonl.zstd 是多帧拼接 zstd，.NET 侧无可用解码器；
/// HTTP /api 需要 browser-trust cookie 与其签名密钥，过于脆弱。
/// </summary>
public sealed class DshSettings
{
    /// <summary>整个模块总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>DSH 数据主目录；为空则自动探测（环境变量 DSH_HOME → 常见默认位置）</summary>
    public string HomeOverride { get; set; } = "";

    /// <summary>轮询间隔（毫秒）。投影缓存实时更新，1s 足够且开销极低。</summary>
    public int PollMs { get; set; } = 1000;

    /// <summary>
    /// 空闲多久后收起工作岛（秒）。用于兜住 openStep 在"步与步之间"短暂为 null 的间隙，
    /// 避免灵动岛在连续工作中闪烁。
    /// </summary>
    public int IdleHideSeconds { get; set; } = 8;

    /// <summary>是否显示上下文占用百分比</summary>
    public bool ShowContext { get; set; } = true;

    /// <summary>是否显示会话标题</summary>
    public bool ShowTitle { get; set; } = true;

    /// <summary>是否为已结束的一轮短暂播报"完成"（秒，0 = 关闭）</summary>
    public int DoneFlashSeconds { get; set; } = 5;

    /// <summary>
    /// 同时最多显示几条 DSH 岛。DSH 可开多个会话并发工作，但灵动岛最多只能舒服地
    /// 放 5 条（1 胶囊 + 4 球体），全被 DSH 占满会挤掉通知/媒体。
    /// 超出的会话按"最近活动"排序后丢弃（仍在工作，只是不显示）。
    /// </summary>
    public int MaxIslands { get; set; } = 3;
}

/// <summary>
/// 电源 / 电池（docs §5.8）。
/// 台式机（无电池）整块静默：HasBattery=false 时源不会发任何事件。
/// </summary>
public sealed class PowerSettings
{
    /// <summary>整个模块总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>插拔电源 / 开始充电 / 停止充电时提醒</summary>
    public bool NotifyPlug { get; set; } = true;

    /// <summary>低电量提醒（≤20% 且未充电，一次放电周期只提醒一次）</summary>
    public bool NotifyLow { get; set; } = true;

    /// <summary>充满提醒（接电充满 100% 时，一次充电周期只提醒一次）</summary>
    public bool NotifyFull { get; set; } = true;

    /// <summary>电源类胶囊停留秒数</summary>
    public int TtlSeconds { get; set; } = 4;

    /// <summary>低电量阈值（%）</summary>
    public int LowPercent { get; set; } = 20;
}

/// <summary>
/// 网络：Wi-Fi / 移动热点 / 飞行模式（docs §5.9–§5.11）。
/// 三个信号共用这一组开关，因为它们属于同一个"无线连接"心智模型。
/// </summary>
public sealed class NetworkSettings
{
    /// <summary>整个模块总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Wi-Fi 连接 / 断开 / 切换时提醒</summary>
    public bool Wifi { get; set; } = true;

    /// <summary>
    /// 让 Wi-Fi 岛常驻显示实时网速（上下行）。
    /// 开启：连上 Wi-Fi 后岛不自动收起，胶囊里持续刷新速率；
    /// 关闭：只在"连上 / 断开 / 切换"时短暂弹一次。
    /// </summary>
    public bool PinSpeed { get; set; }

    /// <summary>移动热点开关与设备连接数变化时提醒</summary>
    public bool Hotspot { get; set; } = true;

    /// <summary>飞行模式开关时提醒</summary>
    public bool Airplane { get; set; } = true;

    /// <summary>网络类胶囊停留秒数</summary>
    public int TtlSeconds { get; set; } = 4;
}

/// <summary>麦克风占用（docs §5.12）。与摄像头占用同一套"值级忽略"语义。</summary>
public sealed class MicSettings
{
    /// <summary>整个模块总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>麦克风胶囊停留秒数</summary>
    public int TtlSeconds { get; set; } = 4;
}

/// <summary>
/// 深浅色切换提示（docs §5.13）。
/// 这里的"切换"指**系统外观**变化（设置界面或系统里改深/浅色），
/// 同一个信号用太阳 / 月亮两个图标表达，因此只需要一组开关。
/// </summary>
public sealed class ThemeSwitchSettings
{
    /// <summary>整个模块总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>系统外观切换时在岛上提示（太阳 = 浅色，月亮 = 深色）</summary>
    public bool NotifyOnSwitch { get; set; } = true;

    /// <summary>胶囊停留秒数</summary>
    public int TtlSeconds { get; set; } = 3;
}

public sealed class GeneralSettings
{
    public bool LaunchOnStartup { get; set; }
    /// <summary>启动后自动打开设置窗口（默认开，便于首次配置）</summary>
    public bool OpenSettingsOnStart { get; set; } = true;
    /// <summary>System | On | Off</summary>
    public string ReduceMotion { get; set; } = "System";

    // ---- 自动更新 ----
    /// <summary>启动时自动检查更新</summary>
    public bool CheckUpdateOnStart { get; set; } = true;
    /// <summary>
    /// 发现新版本后自动下载并安装（无需确认）。
    /// 关闭时只在设置页显示「有新版本」，由用户点按钮更新。
    /// </summary>
    public bool AutoInstallUpdate { get; set; }
}

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
    public IslandSettings Island { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
    public ClockSettings Clock { get; set; } = new();
    public NotificationSettings Notification { get; set; } = new();
    public UsageSettings Usage { get; set; } = new();
    public MediaSettings Media { get; set; } = new();
    public GameSettings Game { get; set; } = new();
    public SysSettings Sys { get; set; } = new();
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
                return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
        }
        catch (Exception ex) { App.Log("Settings", ex); }
        return new Settings();
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
    public double Scale { get; set; } = 1.0;

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

public sealed class GeneralSettings
{
    public bool LaunchOnStartup { get; set; }
    /// <summary>启动后自动打开设置窗口（默认开，便于首次配置）</summary>
    public bool OpenSettingsOnStart { get; set; } = true;
    /// <summary>System | On | Off</summary>
    public string ReduceMotion { get; set; } = "System";
}

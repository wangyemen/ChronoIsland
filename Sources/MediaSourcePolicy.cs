using System;
using System.Collections.Generic;

namespace ChronoIsland.Sources;

/// <summary>
/// 媒体会话选择策略（借鉴 FluidBar 的 MediaSnapshotSelectionPolicy）。
/// 音乐应用 > 浏览器 > 其他；浏览器会话降级处理。
/// </summary>
public static class MediaSourcePolicy
{
    // 已知音乐应用 AUMID 片段 → 高优先级
    private static readonly (string Marker, int Priority)[] Known =
    {
        ("Spotify", 120), ("SpotifyAB.SpotifyMusic", 120),
        ("NeteaseCloudMusic", 115), ("cloudmusic", 115), ("QQMusic", 115),
        ("Kugou", 110), ("Kuwo", 110), ("Foobar2000", 110),
        ("MusicBee", 110), ("AIMP", 110), ("iTunes", 105), ("Groove", 105),
        ("Microsoft.ZuneMusic", 105), ("VLC", 100), ("PotPlayer", 100),
        ("MPC-HC", 100), ("wmplayer", 95)
    };

    private static readonly string[] BrowserMarkers =
        { "edge", "msedge", "chrome", "firefox", "browser", "opera", "brave" };

    /// <summary>按 AUMID 给出优先级：音乐应用 100+，浏览器 50，其他 70</summary>
    public static int GetSourcePriority(string? sourceAppUserModelId)
    {
        var id = sourceAppUserModelId ?? "";
        foreach (var (marker, prio) in Known)
            if (id.Contains(marker, StringComparison.OrdinalIgnoreCase)) return prio;
        if (IsBrowserSource(id)) return 50;
        return 70;
    }

    /// <summary>
    /// 是否为"音乐播放器"（歌词只对这类会话生效）。
    /// 判定依据：命中已知音乐应用清单，或优先级 >= 100（音乐应用阈值）。
    /// 浏览器、视频播放器、未知来源一律返回 false —— 浏览器里放视频不该弹歌词。
    /// </summary>
    public static bool IsMusicPlayer(string? sourceAppUserModelId)
    {
        var id = sourceAppUserModelId ?? "";
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (IsBrowserSource(id)) return false;
        foreach (var (marker, prio) in Known)
            if (id.Contains(marker, StringComparison.OrdinalIgnoreCase) && prio >= 100) return true;
        return false;
    }

    public static bool IsBrowserSource(string? sourceAppUserModelId)
    {
        var id = (sourceAppUserModelId ?? "").ToLowerInvariant();
        foreach (var m in BrowserMarkers)
            if (id.Contains(m)) return true;
        return false;
    }

    /// <summary>从 AUMID 推断显示名</summary>
    public static string DisplayName(string? aumid)
    {
        var id = aumid ?? "";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Spotify"] = "Spotify",
            ["NeteaseCloudMusic"] = "网易云音乐",
            ["cloudmusic"] = "网易云音乐",
            ["QQMusic"] = "QQ 音乐",
            ["Kugou"] = "酷狗音乐",
            ["Kuwo"] = "酷我音乐",
            ["Foobar2000"] = "Foobar2000",
            ["Microsoft.ZuneMusic"] = "Groove 音乐",
            ["VLC"] = "VLC",
            ["PotPlayer"] = "PotPlayer"
        };
        foreach (var kv in map)
            if (id.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        if (IsBrowserSource(id)) return "浏览器";
        // AUMID 形如 "App!Package" → 取感叹号前部分
        var bang = id.IndexOf('!');
        var name = bang > 0 ? id[..bang] : id;
        return string.IsNullOrWhiteSpace(name) ? "媒体" : name;
    }
}

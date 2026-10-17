using System;
using System.Collections.Generic;

namespace ChronoIsland.Sources;

/// <summary>歌词同步级别（对应 Lyricify 的 SyncTypes，简化为本项目需要的三种）</summary>
public enum LyricSyncType
{
    /// <summary>无歌词</summary>
    None = 0,
    /// <summary>未同步（纯文本）</summary>
    Unsynced = 1,
    /// <summary>逐行同步（可高亮当前行）</summary>
    LineSynced = 2,
    /// <summary>逐字同步（v0.0.1 按逐行处理）</summary>
    SyllableSynced = 3
}

/// <summary>一行歌词</summary>
public sealed class LyricLine
{
    /// <summary>起始时间（毫秒）</summary>
    public int StartMs { get; set; }
    /// <summary>结束时间（毫秒）；未同步歌词为 null</summary>
    public int? EndMs { get; set; }
    /// <summary>歌词文本</summary>
    public string Text { get; set; } = "";
    /// <summary>翻译行 / 副行（可为空）</summary>
    public string? SubText { get; set; }
}

/// <summary>一次歌词查询的结果</summary>
public sealed class LyricResult
{
    /// <summary>是否成功拿到歌词</summary>
    public bool Success { get; set; }
    /// <summary>同步级别</summary>
    public LyricSyncType SyncType { get; set; } = LyricSyncType.None;
    public List<LyricLine> Lines { get; set; } = new();
    /// <summary>命中来源（QQ音乐 / 网易云 / 酷狗 / 汽水 / LRCLIB）</summary>
    public string Source { get; set; } = "";
    /// <summary>纯音乐标记</summary>
    public bool Instrumental { get; set; }

    /// <summary>查询曲目（用于缓存 key 与调试）</summary>
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
}

/// <summary>缓存条目</summary>
public sealed class LyricCacheEntry
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Source { get; set; } = "";
    public int SyncType { get; set; }
    public bool Instrumental { get; set; }
    public List<LyricLine> Lines { get; set; } = new();
    public long CachedAtUnix { get; set; }
    /// <summary>
    /// 是否"存疑结果"（疑似残缺，只是当时没有更好的可用）。
    /// 存疑条目用更短的 TTL，避免一份残缺歌词被冻结 30 天。
    /// </summary>
    public bool Suspect { get; set; }
}
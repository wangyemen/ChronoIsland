namespace ChronoIsland.Core;

/// <summary>单条岛的展示策略</summary>
public sealed record IslandPolicy(
    /// <summary>存活时长，null = 需显式关闭（如媒体播放中）</summary>
    TimeSpan? Ttl,
    /// <summary>同 SourceKey 合并</summary>
    bool Coalesce = true,
    /// <summary>可否悬浮展开详情卡</summary>
    bool Expandable = true,
    /// <summary>悬停时暂停 TTL</summary>
    bool HoverPauses = true,
    /// <summary>隐藏期间进入待处理队列</summary>
    bool QueueWhenHidden = false);

/// <summary>来源产出的事件（Source 只发事件，不认识 UI）</summary>
public sealed record IslandEvent(
    string Id,
    string SourceKey,
    string Kind,
    int Priority,
    string Title,
    string? Subtitle = null,
    object? Icon = null,
    object? Payload = null,
    IslandPolicy? Policy = null)
{
    /// <summary>
    /// 应用标识，用于查找应用图标（AUMID / 进程名 / "chrome" / "Spotify.exe"）。
    /// 与 SourceKey（用于同类合并）区分开：SourceKey 可能是"应用+标题"的组合键。
    /// </summary>
    public string? AppKey { get; init; }

    /// <summary>
    /// 是否抢占中央胶囊位置。
    /// 例：暂停中的媒体会话已存在时，新的暂停会话不应把播放中的挤走。
    /// </summary>
    public bool StealFocus { get; init; } = true;

    /// <summary>
    /// 来源应用的 AUMID（如 "{GUID}\Path\app.exe" 或 "Microsoft.WindowsStore_xxx!App"）。
    /// 用于"打开"通知时定位并启动应用。
    /// </summary>
    public string? SourceAumid { get; init; }

    /// <summary>来源应用的显示名（如"QQ"、"微信"），用于"打开"时定位应用。</summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>创建时间（默认 now）</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool IsUnread { get; set; } = true;
    public int MergeCount { get; set; } = 1;

    public static IslandPolicy Default(TimeSpan? ttl = null) => new(ttl ?? TimeSpan.FromSeconds(5));
}

public interface IEventSource : IDisposable
{
    string Key { get; }
    int Priority { get; }
    void Start(EventBus bus);
}

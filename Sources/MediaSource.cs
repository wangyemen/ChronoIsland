using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChronoIsland.Core;
using Windows.Media.Control;

namespace ChronoIsland.Sources;

/// <summary>
/// 媒体显示与控制（docs §5.4）。
/// 直接调用 GSMTC（GlobalSystemMediaTransportControlsSessionManager）——
/// TargetFramework 为 net10.0-windows10.0.19041.0，WinRT API 原生可用，无需反射。
/// 关键实现借鉴 FluidBar：遍历全部会话 + 优先级排序 + 浏览器降级 + 时间轴真伪判断。
/// </summary>
public sealed class MediaSource : IEventSource
{
    public string Key => "media";
    public int Priority => 5;

    private EventBus? _bus;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Timer? _poll;
    private bool _available = true;
    private DateTime _lastManualAction = DateTime.MinValue;
    private string? _preferredSourceId;
    private DateTime _lastProgressPush = DateTime.MinValue;
    private DateTime _lastClick = DateTime.MinValue;

    // 去重：同一首歌不重复推送
    private string? _lastKey;
    private DateTime _lastSessionRefresh = DateTime.MinValue;

    // 轮询：Playing 时 1s，否则停摆（省电）
    private const int PlayingPollMs = 1000;
    private const int IdlePollMs = 3000;

    public void Start(EventBus bus)
    {
        _bus = bus;
        _ = Task.Run(async () =>
        {
            try { await InitAsync(); }
            catch (Exception ex) { _available = false; App.Log("Media", ex); }
        });
    }

    private async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (_manager == null) { _available = false; return; }

        // 会话变化 / 播放信息变化 → 立即刷新
        _manager.SessionsChanged += (_, _) => _ = RefreshAsync();
        _manager.CurrentSessionChanged += (_, _) => _ = RefreshAsync();

        await RefreshAsync();

        // 兜底轮询（Playing 时 1s 刷新进度）
        _poll = new Timer(async _ =>
        {
            try { await RefreshAsync(); }
            catch { }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>当前所有会话的快照（Id → 快照），供 UI 轮询</summary>
    private readonly Dictionary<string, MediaSnapshot> _current = new();

    /// <summary>读取全部媒体会话并逐条推送（每个会话一条岛）</summary>
    private async Task RefreshAsync()
    {
        if (!_available || _manager == null) return;
        try
        {
            var snapshots = await ReadAllAsync();
            var seen = new HashSet<string>();

            foreach (var snap in snapshots)
            {
                seen.Add(snap.SourceId);
                _current[snap.SourceId] = snap;

                var key = snap.SourceId;
                bool trackChanged = _lastKeyOf.GetValueOrDefault(key) != $"{snap.Title}|{snap.Artist}";
                bool playingChanged = _lastPlayingOf.GetValueOrDefault(key) != snap.IsPlaying;
                bool progressTick = (DateTime.Now - _lastProgressPush).TotalMilliseconds >= 900;

                if (!trackChanged && !playingChanged && !progressTick) continue;
                _lastKeyOf[key] = $"{snap.Title}|{snap.Artist}";
                _lastPlayingOf[key] = snap.IsPlaying;
                Emit(snap);
            }

            if (snapshots.Count > 0) _lastProgressPush = DateTime.Now;

            // 已消失的会话 → 请求收起
            foreach (var id in _current.Keys.ToList())
            {
                if (seen.Contains(id)) continue;
                _current.Remove(id);
                _lastKeyOf.Remove(id);
                _lastPlayingOf.Remove(id);
                Emit(null, id);
            }
        }
        catch (Exception ex) { App.Log("Media", ex); }
    }

    private readonly Dictionary<string, string> _lastKeyOf = new();
    private readonly Dictionary<string, bool> _lastPlayingOf = new();

    private bool _lastPlaying;

    /// <summary>取指定会话的当前快照（UI 轮询进度用）</summary>
    public MediaSnapshot? GetCurrent(string sourceId)
        => _current.TryGetValue(sourceId, out var s) ? s : null;

    /// <summary>任一正在播放的会话（用于兼容旧调用）</summary>
    public MediaSnapshot? Current => _current.Values.FirstOrDefault(s => s.IsPlaying)
                                     ?? _current.Values.FirstOrDefault();

    /// <summary>
    /// 核心：遍历全部会话，按优先级选出一个有效快照。
    /// 借鉴 FluidBar MediaSessionProvider.ReadAsync 的判断链。
    /// </summary>
    private async Task<List<MediaSnapshot>> ReadAllAsync()
    {
        var result = new List<MediaSnapshot>();
        var sessions = _manager!.GetSessions();

        var ordered = sessions
            .Select(s => new { Session = s, Priority = MediaSourcePolicy.GetSourcePriority(s.SourceAppUserModelId) })
            .OrderByDescending(x => x.Priority)
            .Select(x => x.Session)
            .ToList();

        // GetSessions() 偶尔漏掉当前会话 → 补上
        var current = _manager.GetCurrentSession();
        if (current != null && !ordered.Any(s => s.SourceAppUserModelId == current.SourceAppUserModelId))
            ordered.Insert(0, current);

        foreach (var session in ordered)
        {
            var playback = session.GetPlaybackInfo();
            var sourceId = session.SourceAppUserModelId ?? "";
            var isBrowser = MediaSourcePolicy.IsBrowserSource(sourceId);
            var isHighPriority = MediaSourcePolicy.GetSourcePriority(sourceId) >= 100;
            var status = playback.PlaybackStatus;

            // 非 Playing：仅高优先级（音乐应用）且非浏览器、且未停止时接受
            if (status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                if (!isHighPriority || isBrowser) continue;
                if (status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                         or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed) continue;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties == null || string.IsNullOrWhiteSpace(properties.Title)) continue;

            var timeline = session.GetTimelineProperties();

            // 浏览器：用时间轴判断真伪（Edge 对任何媒体标签页都注册会话，
            // 用户切走后旧会话会残留成 Playing 且位置冻结）
            if (isBrowser)
            {
                var hasDuration = timeline.EndTime > timeline.StartTime;
                var hasProgress = timeline.Position > timeline.StartTime;
                if (!hasDuration && !hasProgress) continue;
            }

            // 陈旧位置外推（Chromium 几秒才推一次 timeline）
            var position = timeline.Position;
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                timeline.LastUpdatedTime != DateTimeOffset.MinValue)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsed.TotalSeconds is > 0 and < 30)
                    position += elapsed;
                if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime)
                    position = timeline.EndTime;
            }

            // 【时间轴语义】统一为"相对量"：
            //   PositionTicks = 已播放时长（扣掉 StartTime 偏移）
            //   EndTicks      = 总时长（EndTime - StartTime）
            // 这样 UI 端 rem = EndTicks - PositionTicks 才是正确的剩余时间。
            // 之前直接存 timeline.Position（绝对量）与 total，StartTime > 0 时
            // 会出现"剩余为负、进度条不满"，表现为快结束时进度条差一截。
            var total = timeline.EndTime - timeline.StartTime;
            var relPos = position - timeline.StartTime;
            if (relPos < TimeSpan.Zero) relPos = TimeSpan.Zero;
            if (total > TimeSpan.Zero && relPos > total) relPos = total;

            int progress = 0;
            if (total > TimeSpan.Zero)
                progress = (int)Math.Clamp(relPos.TotalMilliseconds / total.TotalMilliseconds * 100, 0, 100);

            result.Add(new MediaSnapshot(
                SourceId: sourceId,
                SourceName: MediaSourcePolicy.DisplayName(sourceId),
                Title: properties.Title ?? "",
                Artist: properties.Artist ?? "",
                Album: properties.AlbumTitle ?? "",
                IsPlaying: status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                ProgressPercent: progress,
                PositionTicks: relPos.Ticks,
                EndTicks: total.Ticks,
                SampledAtUtcTicks: DateTime.UtcNow.Ticks));
        }
        return result;
    }

    private void Emit(MediaSnapshot? snap, string? endedSessionId = null)
    {
        if (snap == null)
        {
            var sourceId = endedSessionId ?? _lastKey?.Split('|')[0] ?? "media";
            // 会话结束 → 发送"清除"事件（UI 侧据此收起该岛）
            _bus?.Publish(new IslandEvent(
                Id: "media:" + sourceId,
                SourceKey: "media:" + sourceId,
                Kind: "media",
                Priority: Priority,
                Title: "",
                Policy: new IslandPolicy(TimeSpan.Zero))
            { AppKey = sourceId });
            return;
        }

        _bus?.Publish(new IslandEvent(
            Id: "media:" + snap.SourceId,
            SourceKey: "media:" + snap.SourceId,
            Kind: "media",
            Priority: Priority,
            Title: snap.Title,
            Subtitle: string.IsNullOrWhiteSpace(snap.Artist) ? snap.SourceName : $"{snap.Artist} · {snap.SourceName}",
            Payload: snap,
            // 播放中为常驻型（Ttl = null），停止后由 UI 收起
            Policy: new IslandPolicy(null, Coalesce: true, Expandable: true, HoverPauses: true, QueueWhenHidden: true))
        {
            AppKey = snap.SourceId,
            // 【关键】暂停中的会话不抢中央位置：让正在播放的/新消息保持胶囊
            StealFocus = snap.IsPlaying
        });
    }

    // ---------- 控制 ----------
    /// <summary>
    /// 播放/暂停指定会话（按岛上的会话 ID 精确控制）。
    /// 【关键】必须传入 sourceId：早期版本用优先级"猜"会话，
    /// 导致点浏览器岛的按钮却暂停了 Spotify。
    /// </summary>
    public async Task ToggleAsync(string sourceId)
    {
        var now = DateTime.Now;
        if (now - _lastClick < TimeSpan.FromMilliseconds(200)) return;   // 单飞
        _lastClick = now;
        _lastManualAction = now;
        _preferredSourceId = sourceId;
        await InvokeAsync(sourceId, s => s.TryTogglePlayPauseAsync());
    }

    public Task NextAsync(string sourceId)
    {
        _lastManualAction = DateTime.Now;
        _preferredSourceId = sourceId;
        return InvokeAsync(sourceId, s => s.TrySkipNextAsync());
    }

    public Task PrevAsync(string sourceId)
    {
        _lastManualAction = DateTime.Now;
        _preferredSourceId = sourceId;
        return InvokeAsync(sourceId, s => s.TrySkipPreviousAsync());
    }

    /// <summary>
    /// 该会话是否支持拖动进度（seek）。
    /// GSMTC 的 CanSetPlaybackPosition 为 false 的应用（很多播放器/直播/电台）
    /// 不接受跳转；这类场景下岛上的进度条应当显示为不可拖动，而不是"能拖但没反应"。
    /// </summary>
    public bool CanSeek(string sourceId)
    {
        var session = FindSession(sourceId);
        if (session == null) return false;
        try
        {
            // 总时长必须已知，否则无法把百分比换算成时间点
            var timeline = session.GetTimelineProperties();
            if (timeline.EndTime <= timeline.StartTime) return false;
            // GSMTC 的能力位在 PlaybackInfo.Controls 上（不是 PlaybackInfo 本身）
            return session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// 跳转到指定进度（0–1 之间的时间轴比例）。返回是否成功。
    /// 拖动过程中会高频调用，故不做单飞限制；但要抑制"手动操作"标记，
    /// 否则会把"用户正在拖"当成"用户想切歌"。
    /// </summary>
    public async Task<bool> SeekAsync(string sourceId, double ratio)
    {
        if (_manager == null) return false;
        ratio = Math.Clamp(ratio, 0, 1);
        try
        {
            var session = FindSession(sourceId);
            if (session == null) return false;

            if (!session.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled) return false;

            var timeline = session.GetTimelineProperties();
            var total = timeline.EndTime - timeline.StartTime;
            if (total <= TimeSpan.Zero) return false;

            var target = timeline.StartTime + TimeSpan.FromTicks((long)(total.Ticks * ratio));
            // 少数播放器只允许在 [MinSeekTime, MaxSeekTime] 内跳转
            if (target < timeline.MinSeekTime) target = timeline.MinSeekTime;
            if (timeline.MaxSeekTime > timeline.MinSeekTime && target > timeline.MaxSeekTime)
                target = timeline.MaxSeekTime;

            bool ok = await session.TryChangePlaybackPositionAsync(target.Ticks);
            if (ok)
            {
                // 立刻把内部快照推到目标位置，避免 UI 在轮询到来前回弹
                _preferredSourceId = sourceId;
                _lastManualAction = DateTime.MinValue;   // 不算"切歌"操作
                if (_current.TryGetValue(sourceId, out var snap))
                {
                    _current[sourceId] = snap with
                    {
                        PositionTicks = target.Ticks,
                        EndTicks = total.Ticks,
                        ProgressPercent = (int)Math.Clamp(ratio * 100, 0, 100)
                    };
                }
            }
            return ok;
        }
        catch (Exception ex)
        {
            App.LogInfo("Media", $"seek 失败 {sourceId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>按优先级挑选会话（用户 30 分钟内手动操作过的优先）</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        var sessions = _manager.GetSessions();
        if (sessions == null || sessions.Count == 0) return _manager.GetCurrentSession();

        if (_preferredSourceId != null &&
            DateTime.Now - _lastManualAction < TimeSpan.FromMinutes(30))
        {
            var preferred = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _preferredSourceId);
            if (preferred != null) return preferred;
        }
        return sessions
            .OrderByDescending(s => MediaSourcePolicy.GetSourcePriority(s.SourceAppUserModelId))
            .FirstOrDefault() ?? _manager.GetCurrentSession();
    }

    /// <summary>按会话 ID 找会话；找不到才回退到优先级选择</summary>
    private GlobalSystemMediaTransportControlsSession? FindSession(string? sourceId)
    {
        if (_manager == null) return null;
        var sessions = _manager.GetSessions();
        if (sessions == null || sessions.Count == 0) return _manager.GetCurrentSession();

        if (!string.IsNullOrEmpty(sourceId))
        {
            var exact = sessions.FirstOrDefault(s => s.SourceAppUserModelId == sourceId);
            if (exact != null) return exact;
        }
        return PickSession();
    }

    private async Task InvokeAsync(string sourceId,
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action)
    {
        if (_manager == null) return;
        try
        {
            var session = FindSession(sourceId);
            if (session == null)
            {
                App.LogInfo("Media", $"未找到会话 {sourceId}，控制取消");
                return;
            }
            App.LogInfo("Media", $"控制会话：{session.SourceAppUserModelId}");
            await action(session);
            await Task.Delay(80);
            await RefreshAsync();
        }
        catch (Exception ex) { App.Log("Media", ex); }
    }

    public bool IsAvailable => _available;

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _manager = null;
    }
}

/// <summary>媒体快照（借鉴 FluidBar 的 MediaSnapshot，去掉歌词相关字段）</summary>
public sealed record MediaSnapshot(
    /// <summary>会话唯一标识（AUMID），用于生成稳定的岛 Id</summary>
    string SourceId,
    string SourceName,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    int ProgressPercent,
    long PositionTicks = 0,
    long EndTicks = 0,
    /// <summary>
    /// 本快照的采样时刻（UTC ticks）。GSMTC 的 Position 每 3~4 秒才推一次，
    /// UI 端可用它做"二次外推"，让进度条/歌词在两次采样之间也平滑推进。
    /// </summary>
    long SampledAtUtcTicks = 0);

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChronoIsland.Sources;

/// <summary>
/// 歌词源：跟随当前媒体曲目，异步取歌词并提供按时间查当前行。
/// 设计文档：docs/design-lyrics-0.0.1.md
/// 要点：换歌才发起查询；查询在后台线程不阻塞渲染；当前行二分查找。
/// </summary>
public sealed class LyricsSource : IDisposable
{
    private readonly LyricsProvider _provider = new();
    private readonly object _lock = new();

    private string _curTitle = "";
    private string _curArtist = "";
    private LyricResult? _cur;
    private CancellationTokenSource? _cts;
    private volatile bool _loading;

    public LyricResult? Current { get { lock (_lock) return _cur; } }
    public bool IsLoading => _loading;
    public event Action? Changed;

    /// <summary>曲目变化时调用；同名则忽略，避免重复请求</summary>
    public void UpdateTrack(string title, string artist, string album, int durationMs)
    {
        var (ct, at) = LyricsProvider.Clean(title, artist);
        if (string.IsNullOrWhiteSpace(ct)) return;
        lock (_lock)
        {
            if (ct == _curTitle && at == _curArtist) return;
            _curTitle = ct; _curArtist = at; _cur = null;
        }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _provider.ResetRotation();
        _loading = true;

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await _provider.GetAsync(ct, at, album, durationMs, token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (ct == _curTitle && at == _curArtist) _cur = r;
                }
                _loading = false;
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { _loading = false; }
            catch (Exception ex)
            {
                _loading = false;
                App.LogInfo("Lyrics", "查询异常: " + ex.Message);
            }
        }, token);
    }

    public void Clear()
    {
        _cts?.Cancel();
        lock (_lock) { _cur = null; _curTitle = ""; _curArtist = ""; }
        _loading = false;
        Changed?.Invoke();
    }

    /// <summary>按播放位置查当前行索引；未同步返回 -1（二分查找）</summary>
    public int FindLineIndex(int positionMs, int offsetMs = 0)
    {
        var cur = Current;
        if (cur == null || cur.Lines.Count == 0) return -1;
        if (cur.SyncType != LyricSyncType.LineSynced && cur.SyncType != LyricSyncType.SyllableSynced)
            return -1;

        int t = positionMs + offsetMs;
        var lines = cur.Lines;
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].StartMs <= t) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    public string? CurrentText(int positionMs, int offsetMs = 0)
    {
        int i = FindLineIndex(positionMs, offsetMs);
        if (i < 0) return null;
        var cur = Current;
        return cur != null && i < cur.Lines.Count ? cur.Lines[i].Text : null;
    }

    /// <summary>取上下文行（当前行前后若干行），供展开卡使用</summary>
    public List<(string Text, bool IsCurrent)> GetContext(int positionMs, int before = 1, int after = 2,
                                                          int offsetMs = 0)
    {
        var res = new List<(string, bool)>();
        var cur = Current;
        if (cur == null || cur.Lines.Count == 0) return res;
        int idx = FindLineIndex(positionMs, offsetMs);
        if (idx < 0) return res;
        int from = Math.Max(0, idx - before);
        int to = Math.Min(cur.Lines.Count - 1, idx + after);
        for (int i = from; i <= to; i++)
            res.Add((cur.Lines[i].Text, i == idx));
        return res;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using Lyricify.Lyrics.Searchers.Helpers;
using CompareMatch = Lyricify.Lyrics.Searchers.Helpers.CompareHelper.MatchType;

namespace ChronoIsland.Sources;

/// <summary>
/// 歌词提供者：多源轮转 + 每源配额 + 本地缓存。
/// 对应设计文档 docs/design-lyrics-0.0.1.md §3。
/// 
/// 轮转规则：QQ音乐 ×3 → 网易云 ×3 → 酷狗 ×3 → 汽水 ×3 → LRCLIB ×3 → 循环；
/// 任一步成功即停止并缓存；某源限流/报错则冷却 60s 内跳过。
/// </summary>
public sealed class LyricsProvider
{
    // ---- 轮转配置 ----
    private static readonly Searchers[] _chain =
    {
        Searchers.QQMusic,   // 中文歌首选（用户指定）
        Searchers.Netease,
        Searchers.LRCLIB     // 英文歌 / 兜底
        // TODO: 酷狗 待字段核实后加回
    };
    private const int QuotaPerSource = 3;      // 每源连续请求配额
    private const int GlobalMaxAttempts = 15;  // 全局重试上限
    private const int MinIntervalMs = 800;     // 相邻请求最小间隔
    private const int SourceTimeoutMs = 8000; // 单源超时（防止某源卡死阻塞整条链）
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private int _chainIndex;
    private int _usedInSource;
    private long _lastRequestTicks;
    private readonly Dictionary<Searchers, DateTime> _cooldownUntil = new();
    private readonly string _cacheDir;
    private readonly Dictionary<string, LyricCacheEntry> _memCache = new();

    public LyricsProvider()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChronoIsland", "lyrics");
        try { Directory.CreateDirectory(_cacheDir); } catch { /* 忽略：缓存失败不影响主流程 */ }
    }

    /// <summary>换歌时重置轮转，重新从 QQ音乐 开始</summary>
    public void ResetRotation()
    {
        _chainIndex = 0;
        _usedInSource = 0;
    }

    /// <summary>查询歌词（优先缓存，缓存命中不发起网络请求）</summary>
    public async Task<LyricResult> GetAsync(string title, string artist, string album, int durationMs,
                                           CancellationToken ct = default)
    {
        var (ct2, at2) = Clean(title, artist);
        var key = CacheKey(ct2, at2);
        var cached = LoadCache(key);
        if (cached != null) return FromCache(cached);

        var track = new TrackMetadata
        {
            Title = ct2, Artist = at2, Album = album ?? "", DurationMs = durationMs > 0 ? durationMs : null
        };

        var fail = new LyricResult { Title = ct2, Artist = at2 };
        for (int attempt = 0; attempt < GlobalMaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var src = NextSource();
            if (src == null) break;   // 全部源都在冷却

            await ThrottleAsync(ct).ConfigureAwait(false);
            LyricResult? got = null;
            try
            {
                got = await TrySourceAsync(src.Value, track, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.LogInfo("Lyrics", $"{src} 异常: {ex.Message}");
                MarkCooldown(src.Value);
            }

            if (got is { Success: true })
            {
                SaveCache(key, got);
                return got;
            }
        }
        return fail;
    }

    // ---- 单源尝试：搜索 → 取歌词 → 解析 ----
    private async Task<LyricResult?> TrySourceAsync(Searchers src, TrackMetadata track, CancellationToken ct)
    {
        // README 标准接法：SearchHelper.Search(track, source, minimumMatch)
        // （直接用 GetSearcher().SearchForResult 在部分源上会返回无结果）
        ISearchResult? result = null;
        try
        {
            var multi = new TrackMultiArtistMetadata
            {
                Title = track.Title,
                Artists = new() { track.Artist ?? "" },
                Album = track.Album,
                DurationMs = track.DurationMs
            };
            var call = SearchHelper.Search(multi, src, CompareMatch.High);
            var done = await Task.WhenAny(call, Task.Delay(SourceTimeoutMs, ct)).ConfigureAwait(false);
            if (done != call) { MarkCooldown(src); return null; }   // 该源超时 → 冷却
            result = await call.ConfigureAwait(false);
            if (result == null)
            {
                var call2 = SearchHelper.Search(multi, src, CompareMatch.Medium);
                var done2 = await Task.WhenAny(call2, Task.Delay(SourceTimeoutMs, ct)).ConfigureAwait(false);
                if (done2 == call2) result = await call2.ConfigureAwait(false);
            }
        }
        catch (Exception ex) { App.LogInfo("Lyrics", $"{src} 搜索异常: {ex.Message}"); MarkCooldown(src); }
        if (result == null) return null;

        string? raw = null;
        try
        {
            raw = await FetchRawAsync(src, result).ConfigureAwait(false);
        }
        catch (Exception ex) { App.LogInfo("Lyrics", $"{src} 取歌词失败: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 【关键】自动识别对部分源返回的扩展 LRC（含 [offset:0] 等标签）会返回 null，
        // 因此显式按来源指定格式；QQ音乐 / 网易云 / LRCLIB 均按 Lrc 解析。
        var data = ParseHelper.ParseLyrics(raw!, LyricsRawTypes.Lrc)
                ?? ParseHelper.ParseLyrics(raw!);
        if (data?.Lines == null || data.Lines.Count == 0) return null;

        var lines = new List<LyricLine>();
        foreach (var ln in data.Lines)
        {
            if (string.IsNullOrWhiteSpace(ln.Text)) continue;
            lines.Add(new LyricLine
            {
                StartMs = ln.StartTime ?? 0,
                EndMs = ln.EndTime,
                Text = ln.Text!,
                SubText = string.IsNullOrWhiteSpace(ln.SubLine?.Text) ? null : ln.SubLine!.Text
            });
        }
        if (lines.Count == 0) return null;

        return new LyricResult
        {
            Success = true,
            SyncType = ToSyncType(data.Lines),
            Lines = lines,
            Source = src.ToString(),
            Title = track.Title ?? "",
            Artist = track.Artist ?? ""
        };
    }

    // ---- 按来源取原始歌词文本 ----
    private static async Task<string?> FetchRawAsync(Searchers src, ISearchResult r)
    {
        switch (src)
        {
            case Searchers.LRCLIB:
            {
                var g = await ProviderHelper.LRCLIBApi
                    .Get(r.Title, r.Artist, r.Album, r.DurationMs.HasValue ? r.DurationMs.Value / 1000d : null)
                    .ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(g?.SyncedLyrics) ? g!.SyncedLyrics : g?.PlainLyrics;
            }
            case Searchers.QQMusic:
            {
                if (r is not QQMusicSearchResult q || string.IsNullOrEmpty(q.Mid)) return null;
                var ly = await ProviderHelper.QQMusicApi.GetLyric(q.Mid!).ConfigureAwait(false);
                return ly?.Lyric;                    // QQ音乐字段：Lyric（Trans 为翻译）
            }
            case Searchers.Netease:
            {
                if (r is not NeteaseSearchResult n) return null;
                var ly = await ProviderHelper.NeteaseApi.GetLyric(n.Id).ConfigureAwait(false);
                return ly?.Lrc?.Lyric;
            }
            // TODO(Kugou): SearchLyricsResponse 的歌词字段待核实（非 Content），
            // 且 KRC 需解密。核实后在此启用，届时可加回 _chain。
            // case Searchers.Kugou: ...
            default:
                return null;   // 汽水音乐 v0.0.1 暂不启用
        }
    }

    private static LyricSyncType ToSyncType(List<ILineInfo> lines)
    {
        bool anyTime = lines.Any(l => l.StartTime.HasValue);
        return anyTime ? LyricSyncType.LineSynced : LyricSyncType.Unsynced;
    }

    // ---- 轮转 / 节流 / 冷却 ----
    private Searchers? NextSource()
    {
        for (int i = 0; i < _chain.Length * 2; i++)
        {
            if (_usedInSource >= QuotaPerSource)
            {
                _chainIndex = (_chainIndex + 1) % _chain.Length;
                _usedInSource = 0;
            }
            var src = _chain[_chainIndex];
            _usedInSource++;
            if (_cooldownUntil.TryGetValue(src, out var until) && DateTime.Now < until) continue;
            return src;
        }
        return null;
    }

    private void MarkCooldown(Searchers src) => _cooldownUntil[src] = DateTime.Now + Cooldown;

    private async Task ThrottleAsync(CancellationToken ct)
    {
        long last = Interlocked.Read(ref _lastRequestTicks);
        if (last > 0)
        {
            var wait = MinIntervalMs - (DateTime.UtcNow.Ticks - last) / TimeSpan.TicksPerMillisecond;
            if (wait > 0) await Task.Delay((int)Math.Min(wait, 3000), ct).ConfigureAwait(false);
        }
        Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
    }

    // ---- 标题清洗 ----
    public static (string Title, string Artist) Clean(string? title, string? artist)
    {
        var t = (title ?? "").Trim();
        var a = (artist ?? "").Trim();
        // 去括号内容：(..) [..] 【..】 《..》
        foreach (var (l, r) in new[] { ("(", ")"), ("[", "]"), ("【", "】"), ("《", "》"), ("（", "）") })
        {
            int i, guard = 0;
            while ((i = t.IndexOf(l, StringComparison.Ordinal)) >= 0 && guard++ < 8)
            {
                int e = t.IndexOf(r, i + 1, StringComparison.Ordinal);
                t = e > i ? t.Remove(i, e - i + 1) : t.Remove(i, 1);
            }
        }
        // 去常见后缀词
        foreach (var kw in new[] { "MV", "mv", "Live", "live", "官方", "高清", "完整版", "Official", "official" })
            t = t.Replace(kw, "", StringComparison.Ordinal);
        // "标题 - 艺人" → 拆分
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && string.IsNullOrEmpty(a))
        {
            a = t[(dash + 3)..].Trim();
            t = t[..dash].Trim();
        }
        return (t.Trim(), a.Trim());
    }

    // ---- 缓存 ----
    private static string CacheKey(string title, string artist)
        => (artist + "|" + title).ToLowerInvariant().Replace(" ", "");

    private LyricResult FromCache(LyricCacheEntry e) => new()
    {
        Success = true,
        SyncType = (LyricSyncType)e.SyncType,
        Lines = e.Lines,
        Source = e.Source + "（缓存）",
        Title = e.Title,
        Artist = e.Artist
    };

    private LyricCacheEntry? LoadCache(string key)
    {
        if (_memCache.TryGetValue(key, out var m)) return m;
        try
        {
            var fp = Path.Combine(_cacheDir, SafeFile(key) + ".json");
            if (!File.Exists(fp)) return null;
            var json = File.ReadAllText(fp);
            var e = JsonSerializer.Deserialize<LyricCacheEntry>(json);
            if (e == null) return null;
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(e.CachedAtUnix);
            if (age > TimeSpan.FromDays(30)) return null;
            _memCache[key] = e;
            return e;
        }
        catch { return null; }
    }

    private void SaveCache(string key, LyricResult r)
    {
        var e = new LyricCacheEntry
        {
            Key = key, Title = r.Title, Artist = r.Artist, Source = r.Source,
            SyncType = (int)r.SyncType, Lines = r.Lines,
            CachedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        _memCache[key] = e;
        try
        {
            var fp = Path.Combine(_cacheDir, SafeFile(key) + ".json");
            File.WriteAllText(fp, JsonSerializer.Serialize(e));
        }
        catch { /* 缓存写失败不影响使用 */ }
    }

    private static string SafeFile(string key)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in key)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
    }
}
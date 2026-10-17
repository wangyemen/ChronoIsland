using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lyricify.Lyrics.Decrypter.Krc;
using Lyricify.Lyrics.Decrypter.Qrc;
using Lyricify.Lyrics.Helpers;
using Lyricify.Lyrics.Helpers.General;
using Lyricify.Lyrics.Helpers.Optimization;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Providers.Web;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Searchers.Helpers;
using CompareMatch = Lyricify.Lyrics.Searchers.Helpers.CompareHelper.MatchType;

namespace ChronoIsland.Sources;

/// <summary>
/// 歌词提供者：多源**顺序轮转 + 配额** + 本地缓存。
/// 对应设计文档 designWeb/docs/design-lyrics-0.0.1.md §3.2。
/// 
/// 【轮转而非并发齐发】按 _chain 顺序逐个问，从 QQ音乐 开始；
/// 首选源命中就立刻停止，后面的源完全不打扰（"用 QQ音乐 就不用别的了"）；
/// 首选源没有这首歌时才顺位往下问。首选源连续服务满 QuotaPerSource 首歌后换下一个源。
/// 
/// 为什么不能并发齐发：那等于每首歌都把所有源问一遍，单源请求量不降反升，
/// 正是被限流的根源。轮转把压力摊到各源上（见设计文档 §3.2）。
/// </summary>
public sealed class LyricsProvider
{
    // ---- 源列表 ----
    private static readonly Searchers[] _chain =
    {
        Searchers.QQMusic,    // 中文歌首选（用户指定）
        Searchers.Netease,    // 含 YRC 逐字
        Searchers.Kugou,      // KRC，库内自带解密（Decrypter.Krc）
        Searchers.SodaMusic,  // 汽水，曲目详情取歌词
        Searchers.LRCLIB      // 英文歌 / 兜底
    };
    private const int SourceTimeoutMs = 8000; // 单源超时（防止某源卡死拖累整体）
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    /// <summary>每源连续用满这么多次就换下一个源（设计文档 §3.2 定为 3 次）。</summary>
    private const int QuotaPerSource = 3;

    /// <summary>全局请求次数上限，超过则本次放弃，等下一首歌（设计文档 §3.2）。</summary>
    private const int MaxTotalAttempts = 15;

    /// <summary>相邻两次请求的最小间隔（设计文档 §3.2：≥ 800ms）。</summary>
    private const int MinIntervalMs = 800;

    /// <summary>
    /// 单次查询的**总时间预算**（软预算：只决定还开不开新源，不掐断在飞的源）。
    ///
    /// 【取值依据】必须 ≥ 各源单源超时之和，否则排在后面的源永远轮不到：
    ///   QQ 5s + 网易云 15s + 酷狗 8s + 汽水 8s + LRCLIB 5s = 41s
    ///   再加 4 次限流间隔（0.8s × 4 ≈ 3.2s） ≈ 44.2s → 取 45s。
    ///
    /// 【为什么从 25s/30s 提到 45s】实测《United States of Eurasia》：
    ///   QQ 未收录(5s) → 网易云 未收录(22s) → 轮到酷狗只剩 3s，被旧预算掐断，
    ///   于是这首本来酷狗能命中的歌整首没歌词。同一首歌在更宽松时是能命中的。
    /// 预算一旦小于各源超时之和，"后面的源"就等于形同虚设。
    ///
    /// 【代价可控】只有"所有源都没有"的歌才会跑满；命中即返回。
    /// 且失败会记入负缓存（FailCooldown），同一首歌不会反复跑满。
    /// </summary>
    private const int LookupBudgetMs = 45000;

    /// <summary>
    /// **分源超时**。各源速度差异极大，用同一个超时会毁掉整个轮转：
    /// 实测网易云单次搜索要 14s 以上（历史上还见过 176s / 249s / 319s），
    /// 若和 QQ音乐（1~2s）用同样长的超时，一旦轮到网易云当首选，
    /// 整首歌就会卡在它身上，顺位兜底根本来不及走完。
    /// 因此给网易云更长的单次耐心，其余源收紧，配合总预算兜底。
    /// </summary>
    private static int TimeoutFor(Searchers s) => s switch
    {
        Searchers.Netease => 15000,   // 慢，但确实能查到（需要足够耐心）
        Searchers.Kugou => 8000,     // 搜索 + KRC 解密（Kugou 接口较稳，给足耐心）
        Searchers.SodaMusic => 8000,  // 汽水详情接口（比搜索多一跳）
        _ => 5000                     // QQ音乐 / LRCLIB 通常在 2s 内返回
    };

    /// <summary>
    /// 轮转游标：**跨曲目保留**，这是"QQ音乐用几次后换网易云"的实现关键。
    /// 若每次换歌都归零，就会每首歌都从 QQ音乐 开始，等于把压力全压在同一个源上。
    /// </summary>
    private int _rotationIndex;

    /// <summary>当前源已连续使用的次数；达到 QuotaPerSource 即换源。</summary>
    private int _usedInCurrentSource;

    private DateTime _lastRequestAt = DateTime.MinValue;

    private readonly Dictionary<Searchers, DateTime> _cooldownUntil = new();
    private readonly string _cacheDir;
    private readonly Dictionary<string, LyricCacheEntry> _memCache = new();

    /// <summary>
    /// 负缓存（失败冷却）：某首歌所有源都没命中/超预算后记录时间戳，
    /// 窗口内同名曲目不再重查（避免 900ms 一轮的 UpdateItem 反复打已失败的源）。
    /// 窗口过后自动放行重查 —— 源是间歇性的（实测《畫沙》21:12 全源未命中、
    /// 21:15 命中），必须给重试机会，不能"永远跳过"。
    /// </summary>
    private readonly Dictionary<string, DateTime> _failAt = new();
    private static readonly TimeSpan FailCooldown = TimeSpan.FromSeconds(45);

    /// <summary>本次查询失败/超预算后调用（GetAsync 末尾）。返回是否仍在冷却窗口内。</summary>
    private void MarkFailed(string key) => _failAt[key] = DateTime.UtcNow;

    /// <summary>命中缓存后清掉该 key 的失败标记。</summary>
    private void ClearFail(string key)
    {
        _failAt.Remove(key);
    }

    /// <summary>该 key 是否处于失败冷却窗口内（是则跳过网络查询）。</summary>
    private bool InFailCooldown(string key) =>
        _failAt.TryGetValue(key, out var at) && DateTime.UtcNow - at < FailCooldown;

    public LyricsProvider()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChronoIsland", "lyrics");
        try { Directory.CreateDirectory(_cacheDir); } catch { /* 忽略：缓存失败不影响主流程 */ }
    }

    /// <summary>
    /// 【故意留空】换歌时**不能**重置轮转游标。
    /// 归零会让每首歌都从 QQ音乐 开始，轮转形同虚设，压力全部回到首选源。
    /// 保留该方法只为兼容调用方，内部不改动任何状态。
    /// </summary>
    public void ResetRotation() { }

    /// <summary>查询歌词（优先缓存，缓存命中不发起网络请求）</summary>
    public async Task<LyricResult> GetAsync(string title, string artist, string album, int durationMs,
                                           CancellationToken ct = default)
    {
        var (ct2, at2) = Clean(title, artist);
        var key = CacheKey(ct2, at2);
        var cached = LoadCache(key);
        if (cached != null)
        {
            ClearFail(key);
            return FromCache(cached);
        }
        // 失败冷却窗口内：同名曲目刚全部源未命中/超预算，跳过网络查询，
        // 不再让 900ms 一轮的媒体刷新反复重打已失败的源（修复"永远跳过"的根因）。
        if (InFailCooldown(key))
            return new LyricResult { Title = ct2, Artist = at2 };

        var track = new TrackMetadata
        {
            Title = ct2, Artist = at2, Album = album ?? "", DurationMs = durationMs > 0 ? durationMs : null
        };

        var fail = new LyricResult { Title = ct2, Artist = at2 };

        // 【顺序轮转 + 配额】设计文档 §3.2。
        //
        // ① 一次只问一个源，从"首选源"开始。首选源命中就立刻停止，
        //    后面的源**一次都不请求** —— 这就是"首选 QQ音乐，用 QQ音乐就不用别的了"。
        // ② 首选源**没有这首歌**时，才顺位往下问（否则这首歌永远查不到）。
        // ③ 首选源连续服务满 QuotaPerSource 首歌后，换下一个源当首选，循环轮转。
        //
        // 【为什么不重试同一源】同一个源 + 同一条查询是确定性的，
        // 重试第三次和第一次结果一样，只会白等超时（实测一首歌能耗掉 8 次请求）。
        // 配额衡量的是"这个源连续扛了几首歌"，而不是"一首歌里试了几次"。
        var total = _chain.Length;
        var attempts = 0;
        var preferred = _rotationIndex;      // 本次的首选源
        LyricResult? fallback = null;        // 疑似残缺、留作最后兜底的结果

        // 【配额在这里结算，不能放在循环里】
        // 按"首选源被安排了几首歌"计：命中、未命中、甚至正在冷却被跳过，都算它扛过一次。
        // 若只在真正尝试过时才计数，那么首选源一旦进入冷却就永远不会被换掉，
        // 轮转会卡死在它身上 —— 实测首选位曾连续 9 首歌都停在同一个源。
        // 结算完立刻推进游标，因此下一次查询从下一个源开始。
        _usedInCurrentSource++;
        if (_usedInCurrentSource >= QuotaPerSource) AdvanceSource();

        // 【软预算，不是硬取消】
        // 以前这里 CancelAfter 一个链接令牌，并把它传给每个源的搜索。
        // 后果：QQ 花 5s、网易云花 22s 后，轮到酷狗才跑 2s 就被预算当场取消 ——
        // 一次本来能命中的查询被扔掉。实测日志：
        //   13:04:22 QQMusic 未收录，顺位往下找
        //   13:04:49 查询超预算 30000ms，放弃（已请求 3 次）   ← 酷狗刚起步就被砍
        // 而同一首歌 12:29:18 在没有这套预算时是能命中 Kugou 的。
        //
        // 现在预算只做一件事：**决定还要不要再开一个新源**。
        // 一旦某个源已经在飞，就让它跑完（由各自的单源超时兜底），不中途掐断。
        // 这样既保住了"总时长有上限"的初衷，又不会丢掉快到手的命中。
        var deadline = DateTime.UtcNow.AddMilliseconds(LookupBudgetMs);
        // 【不再建链接令牌】预算已改为软判定，没有需要"取消"的东西。
        // 换歌取消直接靠调用方的 ct；这里保留别名只是让下面的读起来一致。
        var token = ct;

        try
        {
            for (var step = 0; step < total && attempts < MaxTotalAttempts; step++)
            {
                token.ThrowIfCancellationRequested();   // 只在换歌时抛出

                // 已超预算：不再开新源，但已拿到的结果照常返回。
                // 注意这里 break 而不是 throw —— throw 会让下面 fallback/已命中逻辑全跳过。
                if (DateTime.UtcNow >= deadline)
                {
                    App.LogInfo("Lyrics", $"预算 {LookupBudgetMs}ms 到，不再开新源（已请求 {attempts} 次）: {ct2} / {at2}");
                    break;
                }

                var src = _chain[(preferred + step) % total];
                if (IsCoolingDown(src)) continue;      // 限流中的源跳过，不占顺位

                await ThrottleAsync(token).ConfigureAwait(false);  // 相邻请求间隔 ≥ MinIntervalMs

                attempts++;
                LyricResult? got = null;
                try
                {
                    // 搜索阶段用 ct（换歌才取消），不再挂预算令牌 —— 让在飞的源跑完。
                    got = await TrySourceAsync(src, track, ct, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    App.LogInfo("Lyrics", $"{src} 异常: {ex.Message}");
                    MarkCooldown(src);
                    continue;                          // 该源不可用 → 换下一个源
                }

                if (got is { Success: true })
                {
                    // 【残缺结果降级为兜底】
                    // 轮转会让质量较差的源排到前面，而它偶尔只返回一小段（实测
                    // 七里香 由网易云"命中"却只有 5 行、13 秒就结束，全曲约 240 秒）。
                    // 这种结果一旦缓存就是 30 天，会把别处更完整的版本永久挤掉。
                    // 因此判为可疑时不当最终命中，先记作兜底继续往下问；
                    // 只有所有源都给不出更好的，才用它 —— 既修了残缺，也不会让歌彻底没词。
                    if (IsLikelyIncomplete(got, durationMs))
                    {
                        // 多个源都给残缺结果时留最"靠后"的那份：尾行越接近曲末，说明覆盖越全
                        if (fallback == null || LastLineMs(got) > LastLineMs(fallback)) fallback = got;
                        App.LogInfo("Lyrics",
                            $"{src} 结果疑似残缺（{got.Lines.Count} 行，截止 {LastLineMs(got)}ms / 全长 {durationMs}ms），先存疑继续找: {ct2} / {at2}");
                        continue;
                    }

                    App.LogInfo("Lyrics",
                        $"命中 {src}（首选 {_chain[preferred]}，第 {step + 1} 顺位，共请求 {attempts} 次）: {ct2} / {at2}");
                    SaveCache(key, got);
                    return got;
                }
                else if (step == 0)
                {
                    App.LogInfo("Lyrics", $"{src} 未收录，顺位往下找: {ct2} / {at2}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 现在只有"换歌"才会走到这里（预算已改为软判定，不再取消令牌）。
            // 换歌取消要如实抛给调用方，让上层知道这次查询作废、不必记失败。
            if (ct.IsCancellationRequested) throw;
            App.LogInfo("Lyrics", $"查询被取消（已请求 {attempts} 次）: {ct2} / {at2}");
            MarkFailed(key);
            return fail;
        }

        // 所有源都没给出完整结果，才退回到那份存疑的兜底
        if (fallback != null)
        {
            App.LogInfo("Lyrics", $"仅存疑结果可用，采用兜底（{fallback.Lines.Count} 行，来自 {fallback.Source}）: {ct2} / {at2}");
            SaveCache(key, fallback, suspect: true);
            return fallback;
        }

        App.LogInfo("Lyrics", $"全部源均未命中（共请求 {attempts} 次）: {ct2} / {at2}");
        MarkFailed(key);
        return fail;
    }

    /// <summary>最后一行的起始时间（毫秒），空结果返回 0。</summary>
    private static int LastLineMs(LyricResult r) =>
        r.Lines.Count == 0 ? 0 : r.Lines.Max(l => l.StartMs);

    /// <summary>
    /// 判断结果是否**疑似残缺**：歌词在歌曲还没播完时就很早结束。
    /// 
    /// 只做保守判定，宁可放过也不误伤：
    ///  - 不知道歌曲时长（durationMs ≤ 0）时一律不判 —— 无从比较，判了会误伤短歌；
    ///  - 只有"最后一行落在全曲一半之前"才算可疑，正常歌曲尾行都接近结尾；
    ///  - 纯音乐 / 无词曲目本来行数就少，靠时长判断而非行数判断。
    /// </summary>
    private static bool IsLikelyIncomplete(LyricResult r, int durationMs)
    {
        if (r.Lines.Count == 0) return false;
        // 【时长未知时的兜底】暂停/停止的会话拿不到时长（durationMs ≤ 0），
        // 此时原逻辑一律不判残缺，于是「只有一段歌词」的结果（实测《慢慢喜歡你》
        // 首行=末行）会被当成完整版缓存 30 天。行数过少本身就是残缺信号，
        // 这里用行数兜底，宁可存疑也不冻结坏数据。
        if (durationMs <= 0) return r.Lines.Count < 8;
        return LastLineMs(r) < durationMs / 2;
    }

    /// <summary>游标推进到下一个源，并重置该源的连续使用计数。</summary>
    private void AdvanceSource()
    {
        _rotationIndex = (_rotationIndex + 1) % _chain.Length;
        _usedInCurrentSource = 0;
    }

    /// <summary>相邻两次网络请求的最小间隔，防止请求过密被限流（设计文档 §3.2）。</summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow - _lastRequestAt;
        var wait = MinIntervalMs - (int)since.TotalMilliseconds;
        if (wait > 0) await Task.Delay(wait, ct).ConfigureAwait(false);
        _lastRequestAt = DateTime.UtcNow;
    }

    private bool IsCoolingDown(Searchers s) =>
        _cooldownUntil.TryGetValue(s, out var until) && DateTime.Now < until;

    /// <summary>
    /// 【繁简 / 中英名归一化】按"由保守到激进"生成查询变体。
    /// 
    /// 背景：库里 CompareArtist 是纯字符串比对，不做繁简或中英归一化。
    /// 原实现直接把 GSMTC 的原始艺人串丢进搜索，于是
    ///   "Eason Chan" 永远匹配不到曲库里写的「陈奕迅」，
    ///   "A-Mei Chang" 匹配不到「张惠妹」，"Cheer Chen" 匹配不到「陈绮贞」。
    /// 曲名一侧库内有繁简互认（CompareName("最佳損友","最佳损友") = Perfect），所以标题不转也能过。
    /// 
    /// 第 0 个变体与归一化前的行为完全一致，因此不会让原本能命中的歌变差。
    /// </summary>
    private static List<(string Title, List<string> Artists, string Label)> BuildQueryVariants(
        string? title, string? artist)
    {
        var list = new List<(string, List<string>, string)>();
        var seen = new HashSet<string>();
        var t0 = title ?? "";
        var raw = new List<string> { artist ?? "" };

        void Add(string t, List<string> a, string label)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            if (a.Count == 0 || a.All(string.IsNullOrWhiteSpace)) return;   // 空艺人会导致搜索直接失败
            if (!seen.Add(t + "\u0001" + string.Join("\u0002", a))) return;
            list.Add((t, a, label));
        }

        Add(t0, raw, "raw");

        // 艺人英文名 → 中文名。ArtistHelper 内置对照表；已是中文/查不到的（Beyond、Coldplay）
        // 原样返回，属幂等操作，实测不会造成回归。
        List<string> cn;
        try { cn = ArtistHelper.ToChineselizeArtists(new List<string> { artist ?? "" }); }
        catch { cn = raw; }
        if (cn == null || cn.Count == 0) cn = raw;   // 【注意】空艺人时该 API 返回空列表
        Add(t0, cn, "artist-cn");

        // 标题繁体 → 简体
        var tS = t0;
        try { tS = ChineseHelper.T2S(t0); } catch { /* 转换失败则保持原样 */ }
        Add(tS, cn, "artist-cn+title-t2s");

        return list;
    }

    /// <summary>单次查询：先 High 严格，未命中再降级 Medium。</summary>
    private async Task<ISearchResult?> SearchOneAsync(
        Searchers src, TrackMultiArtistMetadata multi, CancellationToken ct)
    {
        var tmo = TimeoutFor(src);
        var call = SearchHelper.Search(multi, src, CompareMatch.High);
        var done = await Task.WhenAny(call, Task.Delay(tmo, ct)).ConfigureAwait(false);
        if (done != call)
        {
            // 【关键】区分两种"没等到"，原实现把它们混为一谈：
            //  · 竞速中别的源先成功 → 本源的 ct 被取消，这是正常现象，绝不能罚冷却；
            //  · 真的超时 → 冷却该源，避免后续每首歌都白等 8 秒。
            if (ct.IsCancellationRequested) return null;
            MarkCooldown(src);
            return null;
        }
        var r = await call.ConfigureAwait(false);
        if (r != null) return r;

        var call2 = SearchHelper.Search(multi, src, CompareMatch.Medium);
        var done2 = await Task.WhenAny(call2, Task.Delay(tmo, ct)).ConfigureAwait(false);
        if (done2 != call2) return null;    // High 阶段已处理过冷却，这里不再重复罚
        return await call2.ConfigureAwait(false);
    }

    /// <summary>
    /// QQ 音乐上一次取回的翻译行（Trans）原文。
    /// 库把原文与翻译分成两个字段返回（不是 sub-line），这里暂存，
    /// 解析时按行号对齐挂到 LyricLine.SubText（设置里的「显示翻译」会用到它）。
    /// </summary>
    private static string? _lastTrans;   // FetchRawAsync 是静态方法，这里也必须是静态

    /// <summary>取回歌词原文；【关键】fetch 也要限时，否则慢源会拖死整体等待。</summary>
    private static async Task<string?> FetchRawTimedAsync(
        Searchers src, ISearchResult result, CancellationToken ct)
    {
        try
        {
            var task = FetchRawAsync(src, result);
            var done = await Task.WhenAny(task, Task.Delay(TimeoutFor(src), ct)).ConfigureAwait(false);
            if (done != task) return null;
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { App.LogInfo("Lyrics", $"{src} 取歌词失败: {ex.Message}"); return null; }
    }

    // ---- 单源尝试：搜索 → 取歌词 → 解析 ----
    private async Task<LyricResult?> TrySourceAsync(Searchers src, TrackMetadata track,
                                                    CancellationToken searchCt, CancellationToken fetchCt)
    {
        // README 标准接法：SearchHelper.Search(track, source, minimumMatch)
        // （直接用 GetSearcher().SearchForResult 在部分源上会返回无结果）
        ISearchResult? result = null;
        try
        {
            // 【并发】三个变体同时发，谁先命中用谁。
            // 不能串行 await：单变体最坏 High(8s)+Medium(8s)=16s，串行三次就是 48s，
            // 那正是当初重写成并发竞速要解决的问题。
            var swVariant = Stopwatch.StartNew();
            var variants = BuildQueryVariants(track.Title, track.Artist);
            var pending = new List<Task<(ISearchResult? R, string Label)>>();
            foreach (var (vTitle, vArtists, vLabel) in variants)
            {
                var multi = new TrackMultiArtistMetadata
                {
                    Title = vTitle,
                    Artists = vArtists,
                    Album = track.Album,
                    DurationMs = track.DurationMs
                };
                var lbl = vLabel;
                pending.Add(Task.Run(async () =>
                {
                    var r = await SearchOneAsync(src, multi, searchCt).ConfigureAwait(false);
                    return (r, lbl);
                }, searchCt));
            }

            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);
                (ISearchResult? R, string Label) got;
                try { got = await finished.ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { continue; }
                if (got.R == null) continue;
                result = got.R;
                if (got.Label != "raw")
                    App.LogInfo("Lyrics",
                        $"{src} 归一化命中({got.Label}) {swVariant.ElapsedMilliseconds}ms: " +
                        $"{(track.Title ?? "")} / {track.Artist ?? ""}");
                break;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { App.LogInfo("Lyrics", $"{src} 搜索异常: {ex.Message}"); MarkCooldown(src); }
        if (result == null) return null;

        // 【关键】取歌词不再挂在总预算上。
        // 网易云搜索本身就要 14s，等它命中时总预算常常只剩 2s，
        // 于是「已经搜到了」的结果被预算掐断丢弃 —— 实测日志：
        //   12:12:10 Netease 归一化命中(14013ms)
        //   12:12:12 查询超预算 25000ms，放弃   ← 搜到却被扔
        // 这正是「同一首歌有时显示歌词、有时不显示」的根因。
        // 现在：搜索阶段受预算约束（控制整体上限），一旦某个源已命中，
        // 取词只用「单源超时」兜底，不再被总预算取消。
        var raw = await FetchRawTimedAsync(src, result, fetchCt).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 【关键】先按各源真实返回的格式解析，不再一律按 Lrc。
        //  · 网易云 YRC（逐字/带 offset 的 LRC）、QQ QRC、酷狗 KRC 各有对应解析器；
        //  · 库的自动识别（ParseHelper.ParseLyrics(raw)）对带 [offset]/[tag] 头的
        //    "完整封装"格式（YrcFull/QrcFull）能正确还原逐字与文本，优先走它。
        // 显式指定格式失败时回落自动识别，最大化解析成功率。
        var rawType = LyricsRawFor(src);
        // 取本次的翻译（QQ 音乐的 Trans），用完即清，避免串到下一首歌。
        var trans = _lastTrans;
        _lastTrans = null;
        LyricsData data = ParseHelper.ParseLyrics(raw!, rawType);
        if (data == null)
            data = ParseHelper.ParseLyrics(raw!);                    // 自动识别兜底
        if (data == null && rawType != LyricsRawTypes.Lrc)
            data = ParseHelper.ParseLyrics(raw!, LyricsRawTypes.Lrc); // 按 LRC 再兜一层
        if (data?.Lines == null || data.Lines.Count == 0) return null;

        // 逐字（Syllable）统一降级为逐行：本项目胶囊/展开卡只高亮到行级，
        // 降级库已实现（SyncDowngrade），逐字时间戳会保留到行首/行尾。
        SyncDowngrade.DowngradeToLineSynced(data.Lines);

        // 【信息行剔除】用库的 InfoLines 识别"作词/作曲/标题/作者"等头行与尾行，
        // 这些行不是歌词，却曾被当成首行缓存 30 天（实测《畫沙》前 12 行全 credit）。
        StripInfoLines(data, track);
        if (data.Lines.Count == 0) return null;

        // 翻译行（QQ 的 Trans）按行号与原文对齐；行号对不上就丢弃，绝不错位。
        var transLines = src == Searchers.QQMusic ? ParseTranslationLines(trans) : null;

        var lines = new List<LyricLine>();
        var idx = 0;
        foreach (var ln in data.Lines)
        {
            idx++;
            if (string.IsNullOrWhiteSpace(ln.Text)) continue;
            if (IsPlaceholderLine(ln.Text!)) continue;              // 剔除"纯音乐"占位文案
            var sub = ln.SubLine?.Text;
            if (string.IsNullOrWhiteSpace(sub) && transLines != null)
            {
                // 翻译文本的过滤顺序必须与原文一致（占位/信息行同样跳过），
                // 否则行号会错位、译文贴到错误的一行上。
                transLines.TryGetValue(idx, out sub);
            }
            lines.Add(new LyricLine
            {
                StartMs = ln.StartTime ?? 0,
                EndMs = ln.EndTime,
                Text = Explicit.Clean(ln.Text!, false), // 还原 [bleep] 等打码词（bool=是否保留打码）
                SubText = string.IsNullOrWhiteSpace(sub) ? null : Explicit.Clean(sub!, false)
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

    /// <summary>
    /// 用库的 InfoLines 识别并剔除"作词 / 作曲 / 标题 / 作者"等信息行（头行 + 尾行）。
    /// 这些行不是歌词，却曾被当成首行写进 30 天缓存（实测《畫沙》前 12 行全 credit）。
    /// 只删纯信息行，保留所有含真人声字的行，宁可留多不可删错。
    /// </summary>
    private static void StripInfoLines(LyricsData data, ITrackMetadata track)
    {
        try
        {
            var flags = InfoLines.CheckInfoLines(data.Lines, track);
            if (flags == null || flags.Count != data.Lines.Count) return;
            var drop = new HashSet<int>();
            for (int i = 0; i < flags.Count; i++)
                if (flags[i]) drop.Add(i);
            data.Lines = data.Lines.Where((ln, i) => !drop.Contains(i)).ToList();
        }
        catch { /* InfoLines 失败不影响主流程，保持原样 */ }
    }

    /// <summary>
    /// 把 QQ 音乐的翻译文本（Trans，一条独立 LRC）解析成「行号 → 译文」表。
    /// 行号从 1 开始，与原文保持一致，便于按行对齐。
    /// 解析不出时间戳（纯文本译文）时返回 null，避免错位。
    /// </summary>
    private static Dictionary<int, string>? ParseTranslationLines(string? trans)
    {
        if (string.IsNullOrWhiteSpace(trans)) return null;
        try
        {
            var d = ParseHelper.ParseLyrics(trans!, LyricsRawTypes.Lrc);
            if (d?.Lines == null || d.Lines.Count == 0) return null;
            var map = new Dictionary<int, string>();
            for (int i = 0; i < d.Lines.Count; i++)
            {
                var t = d.Lines[i].Text;
                if (!string.IsNullOrWhiteSpace(t)) map[i + 1] = t!.Trim();
            }
            return map.Count == 0 ? null : map;
        }
        catch { return null; }
    }

    /// <summary>各源歌词原文的默认解析格式（与 FetchRawAsync 的取数分支一一对应）。</summary>
    private static LyricsRawTypes LyricsRawFor(Searchers src) => src switch
    {
        Searchers.Netease => LyricsRawTypes.Yrc,     // 网易云默认给逐字 YRC；解析器自动兼容 LRC 子集
        Searchers.QQMusic => LyricsRawTypes.Lrc,     // QQ 经 QRC 解密后是带翻译的 LRC
        Searchers.Kugou   => LyricsRawTypes.Krc,     // 酷狗 KRC（先经 Krc.Decrypter 解密）
        _ => LyricsRawTypes.Lrc                       // LRCLIB / 汽水：标准 LRC
    };

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
                // QQ 音乐返回 Lyric（原文）与 Trans（翻译），两条时间轴结构相同。
                // 以前只取 Lyric、把 Trans 丢掉，于是翻译能力白放着没用。
                // 这里把两者都交出去，由调用方按行号对齐挂到 SubText。
                var main = ly?.Lyric;
                if (string.IsNullOrWhiteSpace(main)) return null;
                _lastTrans = ly?.Trans;              // 供本次解析对齐翻译行
                return main;
            }
            case Searchers.Netease:
            {
                if (r is not NeteaseSearchResult n) return null;
                var ly = await ProviderHelper.NeteaseApi.GetLyric(n.Id).ConfigureAwait(false);
                // 逐字 YRC 优先（信息最全，解析器可自动降级为逐行），无则回 LRC。
                return ly?.Yrc?.Lyric ?? ly?.Lrc?.Lyric;
            }
            case Searchers.Kugou:
            {
                // 酷狗：搜索出 hash → GetSearchLyrics 拉候选 → 用候选的 (Id, AccessKey) 取
                // 加密 KRC → 库内 Krc.Decrypter 解密成明文 LRC。
                // 【踩过的坑】之前把 hash 当成 id/accessKey 传两个相同值，端点直接返回空；
                // KRC 密文必须由候选的 Id + AccessKey 换取，缺一不可。
                if (r is not KugouSearchResult k || string.IsNullOrEmpty(k.Hash)) return null;
                var kw = k.Title + " " + string.Join(" ", k.Artists ?? Array.Empty<string>());
                var resp = await ProviderHelper.KugouApi
                    .GetSearchLyrics(kw, k.DurationMs, k.Hash)
                    .ConfigureAwait(false);
                if (resp?.Candidates == null || resp.Candidates.Count == 0) return null;
                // 逐字歌词（KrcType=1/2 为逐字），按评分/时长取最佳候选
                var best = resp.Candidates
                    .OrderByDescending(c => c.KrcType)
                    .ThenByDescending(c => c.Score)
                    .ThenByDescending(c => c.Duration)
                    .FirstOrDefault();
                if (best == null || string.IsNullOrEmpty(best.Id)) return null;
                var enc = await Lyricify.Lyrics.Decrypter.Krc.Helper
                    .GetEncryptedLyricsAsync(best.Id, best.AccessKey)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(enc)) return null;
                try
                {
                    var plain = Lyricify.Lyrics.Decrypter.Krc.Decrypter.DecryptLyrics(enc);
                    if (string.IsNullOrWhiteSpace(plain)) return null;
                    return plain;   // 解密后是逐字 LRC，交给 LyricsRawFor(Kugou)=Krc 解析
                }
                catch { return null; }   // 解密失败 → 不缓存、不污染，顺位往下
            }
            case Searchers.SodaMusic:
            {
                // 汽水：搜索出 track id → 曲目详情里的歌词正文（LRC）。
                if (r is not SodaMusicSearchResult s || string.IsNullOrEmpty(s.Id)) return null;
                var detail = await ProviderHelper.SodaMusicApi.GetDetail(s.Id).ConfigureAwait(false);
                return detail?.Lyric?.Content;   // 主词缺失时返回 null，顺位往下
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 识别各源对"无歌词/纯音乐"返回的占位文案。
    /// 实测 QQ音乐 对《晴天》返回单行「此歌曲为没有填词的纯音乐，请您欣赏」，
    /// 原实现只判 Lines.Count == 0，于是这行被当成真歌词并写入 30 天缓存。
    /// </summary>
    private static bool IsPlaceholderLine(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return false;
        // 只有极短的整行才可能是占位文案，避免误伤正常歌词
        if (t.Length > 60) return false;
        foreach (var kw in PlaceholderKeywords)
            if (t.Contains(kw, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] PlaceholderKeywords =
    {
        "纯音乐", "純音樂", "没有填词", "沒有填詞", "无歌词", "無歌詞",
        "暂无歌词", "暫無歌詞", "Instrumental", "instrumental",
        "请您欣赏", "請您欣賞", "此歌曲为", "此歌曲為"
    };

    private static LyricSyncType ToSyncType(List<ILineInfo> lines)
    {
        bool anyTime = lines.Any(l => l.StartTime.HasValue);
        return anyTime ? LyricSyncType.LineSynced : LyricSyncType.Unsynced;
    }

    // ---- 冷却 ----
    // 并发竞速后不再需要串行轮转与请求节流（各源同时发起，无先后顺序可言），
    // 因此 NextSource / ThrottleAsync 已删除。冷却机制保留：报错的源 60s 内不再参与。
    private void MarkCooldown(Searchers src) => _cooldownUntil[src] = DateTime.Now + Cooldown;

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
            // 【脏缓存自救】历史上有过解析失败写进缓存的结果：
            // 逐字歌词（YRC/QRC）被当成普通 LRC 解析，于是 "<00:00.000>" 这类
            // 时间戳标签原样留在文本里（实测《青花瓷》44 行全是这种）。
            // 读到就当没缓存，重新去源里取，避免一份坏数据被冻 30 天。
            if (HasRawTimestampTags(e.Lines)) return null;
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(e.CachedAtUnix);
            // 存疑结果只留 6 小时：够当下这首歌唱完不重复请求，
            // 但不会让一份残缺歌词挡住以后更好的版本（正常结果仍是 30 天）。
            var ttl = e.Suspect ? TimeSpan.FromHours(6) : TimeSpan.FromDays(30);
            if (age > ttl) return null;
            _memCache[key] = e;
            return e;
        }
        catch { return null; }
    }

    private void SaveCache(string key, LyricResult r, bool suspect = false)
    {
        var e = new LyricCacheEntry
        {
            Key = key, Title = r.Title, Artist = r.Artist, Source = r.Source,
            SyncType = (int)r.SyncType, Lines = r.Lines, Suspect = suspect,
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

    /// <summary>缓存行里是否残留原始时间戳标签（逐字歌词被误当 LRC 解析的特征）。</summary>
    private static bool HasRawTimestampTags(List<LyricLine>? lines)
    {
        if (lines == null) return false;
        foreach (var l in lines)
        {
            var t = l.Text;
            if (string.IsNullOrEmpty(t)) continue;
            // "<00:12.345>" / "<00:12>" 形态
            if (t.Contains("<00:") || t.Contains("<01:") || t.Contains("<02:") ||
                t.Contains("<03:") || t.Contains("<04:") || t.Contains("<05:"))
                return true;
        }
        return false;
    }

    private static string SafeFile(string key)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in key)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
    }
}
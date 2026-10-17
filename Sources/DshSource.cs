using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChronoIsland.Core;

namespace ChronoIsland.Sources;

/// <summary>
/// DeepSeek Harness 工作状态快照（只读 DSH 投影缓存得到）。
/// </summary>
/// <param name="Working">本轮是否还没结束（openStep 非 null，或 turnBoundary.openTurnStartSeq 非 null）</param>
/// <param name="Stepping">是否正在 LLM 流式输出（openStep != null）；
/// false 且 Working = true 表示"正在执行工具 / 等待下一步"</param>
/// <param name="Turn">当前 / 最近一轮编号</param>
/// <param name="Step">当前 / 最近一步编号</param>
/// <param name="Turns">累计轮数</param>
/// <param name="Steps">累计步数</param>
/// <param name="Title">会话标题（首条提问）</param>
/// <param name="Model">当前模型 id</param>
/// <param name="SurfaceTokens">当前上下文已占用 token</param>
/// <param name="ContextWindow">上下文窗口上限</param>
/// <param name="StepElapsedMs">当前步已耗时（毫秒，非工作中为 0）</param>
/// <param name="LastOutputTokens">最近一步输出 token</param>
/// <param name="SessionId">会话 id</param>
public sealed record DshSnapshot(
    bool Working,
    bool Stepping,
    int Turn,
    int Step,
    int Turns,
    int Steps,
    string Title,
    string Model,
    long SurfaceTokens,
    long ContextWindow,
    long StepElapsedMs,
    long LastOutputTokens,
    string SessionId,
    /// <summary>
    /// 投影缓存的 sessionStats.seq（每来一个事件就递增）。
    /// 多会话并存时用它排序：seq 最新的那个是"刚活动过"的会话，排在前面。
    /// 用 seq 而不是文件 mtime，是为了让排序与"谁在动"直接挂钩。
    /// </summary>
    long Seq = 0,
    /// <summary>子代理标签（rows.subagent.identity.label）。非空即表示这是子代理。</summary>
    string SubagentLabel = "",
    /// <summary>子代理模式：continuable | foreground | ...（空 = 非子代理）</summary>
    string SubagentMode = "")
{
    /// <summary>是否是一个子代理会话（DSH 把子代理建成独立 session）</summary>
    public bool IsSubagent => !string.IsNullOrWhiteSpace(SubagentLabel);

    /// <summary>上下文占用比 0~1</summary>
    public double ContextRatio => ContextWindow > 0
        ? Math.Clamp((double)SurfaceTokens / ContextWindow, 0, 1)
        : 0;

    /// <summary>上下文占用百分比（整数）</summary>
    public int ContextPercent => (int)Math.Round(ContextRatio * 100);
}

/// <summary>
/// DeepSeek Harness 工作状态源。
///
/// 【原理】只读 DSH 的会话投影缓存（projection cache）——它是**明文 JSON**，
/// 不是会话日志那种多帧拼接的 zstd，因此 .NET 侧用 System.Text.Json 直接解析即可。
///
/// 路径：&lt;DSH_HOME&gt;\storages\session_projcache\sessions\&lt;sessionId&gt;.json
///
/// 【为何可安全并发读】DSH 对该文件是原子落盘：实测 68 个缓存文件、500ms 高频轮询
/// 120 次，0 次 JSON 解析失败，且目录中从未出现 .tmp / .lock 伴随文件。
///
/// 【如何判断"正在工作"】主判据是 turnBoundary.openTurnStartSeq 非 null（或
/// lastStepBoundary.kind == "start"），它覆盖整个 turn（含工具执行阶段）。
/// openStep 只在 LLM 流式输出时为真，单看它会把"正在跑工具"误判成已结束。
///
/// 【多任务】DSH 可同时开多个会话各自跑任务，因此本源对每个工作中的会话
/// 各发一条岛：Id / SourceKey 均为 "dsh:&lt;会话id&gt;"，互不合并、各自独立收起。
///
/// 【如何选中会话】取全部"工作中且新鲜"的会话；若都不在工作中，
/// 单条模式（设置页 / 诊断）才退化为取 mtime 最新的那个。
/// </summary>
public sealed class DshSource : IEventSource
{
    public string Key => "dsh";

    /// <summary>优先级 4：高于媒体(3) —— DSH 工作中是用户当前最关心的状态。</summary>
    public int Priority => 4;

    private EventBus? _bus;
    private Settings _settings = new();
    private System.Threading.Timer? _timer;
    private readonly object _gate = new();

    // ---- 状态机（按会话独立） ----
    // key = 会话 id（短 id）。每个工作中的 DSH 会话在灵动岛上各占一条岛，
    // 所以这里不能只存一份状态，必须按会话分开 tracking。
    private readonly Dictionary<string, SessionState> _states = new(StringComparer.Ordinal);
    private bool _diagLogged;

    /// <summary>单个 DSH 会话的岛状态</summary>
    private sealed class SessionState
    {
        public DshSnapshot? Last;
        public DshSnapshot? Published;
        public DateTime LastWorkingAt = DateTime.MinValue;
        public bool AnnouncedDone;
    }

    // UI 侧读实时状态（无源实例引用时用）
    private static readonly object StaticLock = new();
    private static DshSnapshot? _staticSnapshot;
    private static List<DshSnapshot> _staticWorking = new();
    public static DshSnapshot? Current { get { lock (StaticLock) return _staticSnapshot; } }

    /// <summary>全部正在工作的会话（供设置页 / 诊断显示）</summary>
    public static List<DshSnapshot> WorkingList
    {
        get { lock (StaticLock) return new List<DshSnapshot>(_staticWorking); }
    }

    public void Start(EventBus bus)
    {
        _bus = bus;
        int ms = Math.Clamp(_settings.Dsh.PollMs, 200, 10000);
        _timer = new System.Threading.Timer(_ => Poll(), null, 0, ms);
    }

    public void Configure(Settings s)
    {
        _settings = s ?? new Settings();
        if (_timer != null)
        {
            int ms = Math.Clamp(_settings.Dsh.PollMs, 200, 10000);
            _timer.Change(0, ms);
        }
    }

    /// <summary>手动唤出（冒烟测试 / 托盘点击）</summary>
    public void Poke() => Poll();

    // ================= 主轮询 =================

    private void Poll()
    {
        try
        {
            if (!_settings.Dsh.Enabled)
            {
                lock (StaticLock) { _staticSnapshot = null; _staticWorking = new List<DshSnapshot>(); }
                return;
            }

            var working = ReadWorkingSessions();

            // 上限保护：会话再多也只显示前 MaxIslands 条（按最近活动排序），
            // 否则会把通知 / 媒体挤出灵动岛。
            int cap = Math.Clamp(_settings.Dsh.MaxIslands, 1, 6);
            if (working.Count > cap) working = working.GetRange(0, cap);

            lock (_gate)
            {
                lock (StaticLock)
                {
                    _staticWorking = working;
                    _staticSnapshot = working.Count > 0 ? working[0] : ReadActiveSession();
                }

                // 本轮出现过的工作会话
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var snap in working)
                {
                    var id = KeyOf(snap);
                    seen.Add(id);
                    if (!_states.TryGetValue(id, out var st))
                    {
                        st = new SessionState();
                        _states[id] = st;
                    }

                    st.Last = snap;
                    st.LastWorkingAt = DateTime.Now;
                    st.AnnouncedDone = false;
                    Evaluate(id, st);
                }

                // 本轮没再出现的会话：已经结束，走各自的收尾逻辑。
                // ToList 避免在遍历中修改字典。
                foreach (var kv in _states.ToList())
                {
                    if (seen.Contains(kv.Key)) continue;
                    // 【关键】必须先把 Last 置空。
                    // 否则 st.Last 永远停在最后那份 Working=true 的快照上，
                    // Evaluate 里的 `snap.Working || graceActive` 会短路成 true，
                    // 于是一条已结束的会话会被无限期地反复重发「工作中」——
                    // 表现为任务早就结束了，灵动岛却一直亮着。
                    kv.Value.Last = null;
                    Evaluate(kv.Key, kv.Value);
                }
            }
        }
        catch (Exception ex) { App.Log("Dsh", ex); }
    }

    /// <summary>该会话对应的岛 Id（每条会话一条独立岛，避免被合并成一条）</summary>
    private static string KeyOf(DshSnapshot snap) => "dsh:" + (snap.SessionId ?? "");

    /// <summary>依据状态机决定某个会话的岛是刷新 / 播报完成 / 收起</summary>
    private void Evaluate(string id, SessionState st)
    {
        var snap = st.Last;

        // 宽限期：openStep 为 null 时给 IdleHideSeconds 缓冲，
        // 避免"步与步之间"的瞬时 null 让岛闪烁。
        bool graceActive = snap != null &&
            (DateTime.Now - st.LastWorkingAt).TotalSeconds < _settings.Dsh.IdleHideSeconds;

        // snap == null 表示这条会话已经不在"工作中"列表里了（真的结束了）。
        // 此时不能提前 return —— 否则下面播报完成 / 收起的逻辑永远走不到。
        if (snap != null && (snap.Working || graceActive))
        {
            if (st.Published == null || !st.Published.Working || HasVisibleChange(st.Published, snap))
            {
                PublishWorking(id, snap);
                st.Published = snap;
            }
            return;
        }

        // 空闲：之前若在 working，播报一次"完成"后静默
        if (st.Published is { Working: true } && !st.AnnouncedDone)
        {
            st.AnnouncedDone = true;
            int flash = _settings.Dsh.DoneFlashSeconds;
            if (flash > 0) PublishDone(id, st.Published);
            else PublishRetract(id);
        }
        st.Published = snap;
    }

    /// <summary>工作态下真正影响观感的字段是否变了</summary>
    private static bool HasVisibleChange(DshSnapshot a, DshSnapshot b)
        => a.Step != b.Step || a.Turn != b.Turn || a.Title != b.Title
        || a.ContextPercent != b.ContextPercent || a.Model != b.Model;

    private void PublishWorking(string id, DshSnapshot snap)
    {
        if (!_diagLogged)
        {
            _diagLogged = true;
            App.LogInfo("Dsh", $"已接入 DSH 状态：会话 {Short(snap.SessionId)}「{snap.Title}」");
        }

        // 【多任务】Id 与 SourceKey 都带上会话 id：
        //   · Id 不同 → 不会被当成"同一条岛就地更新"
        //   · SourceKey 不同 → 不会被 EventBus 的同类合并吞掉
        _bus?.Publish(new IslandEvent(
            Id: id,
            SourceKey: id,
            Kind: "dsh",
            Priority: Priority,
            Title: BuildTitle(snap),
            Subtitle: BuildSubtitle(snap),
            Payload: snap,
            // 常驻（Ttl = null）：DSH 在跑就一直在，直到状态机判定空闲后发出收起事件。
            // 必须用 null 而非 TimeSpan.Zero —— UI 判据是 Policy?.Ttl is TimeSpan。
            Policy: new IslandPolicy(null, Coalesce: true, Expandable: true, QueueWhenHidden: false))
        { AppKey = "dsh" });
    }

    private void PublishDone(string id, DshSnapshot snap)
    {
        App.LogInfo("Dsh", $"DSH 本轮结束（会话 {Short(snap.SessionId)}：第 {snap.Turn} 轮，共 {snap.Steps} 步）");
        _bus?.Publish(new IslandEvent(
            Id: id,
            SourceKey: id,
            Kind: "dsh",
            Priority: Priority,
            Title: "DSH 已完成",
            Subtitle: $"第 {snap.Turn} 轮 · 共 {snap.Steps} 步",
            Payload: snap with { Working = false, Stepping = false, StepElapsedMs = 0 },
            Policy: new IslandPolicy(
                TimeSpan.FromSeconds(_settings.Dsh.DoneFlashSeconds),
                Coalesce: true, Expandable: true))
        { AppKey = "dsh" });
    }

    /// <summary>请求 UI 立即收起该岛（空闲静默）</summary>
    private void PublishRetract(string id)
    {
        _bus?.Publish(new IslandEvent(
            Id: id,
            SourceKey: id,
            Kind: "dsh-retract",
            Priority: 0,
            Title: "",
            Policy: new IslandPolicy(TimeSpan.Zero, Coalesce: false, Expandable: false))
        { AppKey = "dsh" });
    }

    private string BuildTitle(DshSnapshot snap)
    {
        if (!_settings.Dsh.ShowTitle || string.IsNullOrWhiteSpace(snap.Title))
            return "DSH 工作中";
        return snap.Title;
    }

    private string BuildSubtitle(DshSnapshot snap)
    {
        // 区分"LLM 在输出"与"正在执行工具"：两者都算工作中，但用户看到的措辞不同。
        var parts = new List<string>
        {
            snap.Stepping
                ? $"第 {Math.Max(snap.Turn, 1)} 轮 · 步 {snap.Step}"
                : $"第 {Math.Max(snap.Turn, 1)} 轮 · 执行中"
        };
        if (_settings.Dsh.ShowContext && snap.ContextWindow > 0)
            parts.Add($"上下文 {snap.ContextPercent}%");
        if (!string.IsNullOrWhiteSpace(snap.Model))
            parts.Add(ShortModel(snap.Model));
        return string.Join(" · ", parts);
    }

    /// <summary>trae/deepseek-v4.1-flash → deepseek-v4.1-flash（去掉 provider 前缀）</summary>
    private static string ShortModel(string model)
    {
        var i = model.LastIndexOf('/');
        return i >= 0 && i < model.Length - 1 ? model[(i + 1)..] : model;
    }

    private static string Short(string id)
        => string.IsNullOrEmpty(id) ? "?" : (id.Length > 12 ? id[..12] : id);

    // ================= 读取投影缓存 =================

    /// <summary>
    /// 【僵尸会话防线】判定"工作中"时，缓存文件的 mtime 与"全部会话里最新的 mtime"
    /// 之间允许落后的秒数。
    ///
    /// 为什么用"相对新鲜度"而不是绝对时长：投影缓存只在 **step 边界**落盘，
    /// 因此一个正在跑长任务（长时间 LLM 流式输出 / 长时间执行工具）的会话，
    /// 它的 mtime 本身也可能静止好几分钟。用绝对上限（比如"必须 120s 内写过"）
    /// 会把这类真正在工作的会话误判成空闲。
    ///
    /// 但 mtime 静止同样出现在**僵尸会话**上：实测本机 c9558c32 的
    /// openTurnStartSeq / lastStepBoundary=start 始终没被清理，已挂起 11 天。
    ///
    /// 两者的可靠区别是**相对位置**：真正在工作的会话一定是全库里最新的那几个
    /// （它就是当前活动会话，其它文件不会同时在写）；僵尸则落后了几个数量级。
    /// 所以规则是"mtime 与新最新的差不超过该阈值才算数"。
    /// </summary>
    internal static readonly int WorkingFreshnessToleranceSeconds = 180;

    /// <summary>
    /// 扫描全部会话缓存，返回（快照, mtime, 是否新鲜）列表。
    /// 新鲜度基准是"全库最新 mtime"，见 <see cref="WorkingFreshnessToleranceSeconds"/>。
    /// 读坏的文件直接跳过，不影响其他会话。
    /// </summary>
    internal static List<(DshSnapshot Snap, DateTime Mtime, bool Fresh)> ScanSessions()
    {
        var result = new List<(DshSnapshot, DateTime, bool)>();
        var dir = SessionsDir();
        if (dir == null || !Directory.Exists(dir)) return result;

        string[] files;
        try { files = Directory.GetFiles(dir, "*.json"); }
        catch { return result; }

        var parsed = new List<(DshSnapshot Snap, DateTime Mtime)>();
        DateTime newestMtime = DateTime.MinValue;

        foreach (var f in files)
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(f); } catch { continue; }

            DshSnapshot? snap;
            try { snap = ParseCache(f); }
            catch { continue; }               // 单个文件坏了不影响其他会话
            if (snap == null) continue;

            parsed.Add((snap, mtime));
            if (mtime > newestMtime) newestMtime = mtime;
        }

        foreach (var (snap, mtime) in parsed)
        {
            bool fresh = (newestMtime - mtime).TotalSeconds <= WorkingFreshnessToleranceSeconds;
            result.Add((snap, mtime, fresh));
        }
        return result;
    }

    /// <summary>
    /// 全部"正在工作"的会话（含新鲜度过滤），按 mtime 从新到旧排序。
    ///
    /// 【多任务】DSH 允许同时开多个会话窗口各自跑任务，因此这里返回的是**列表**而非单个：
    /// 每个工作中的会话都会在灵动岛上占一条独立的岛。
    /// </summary>
    public static List<DshSnapshot> ReadWorkingSessions()
    {
        var list = new List<DshSnapshot>();
        foreach (var (snap, mtime, fresh) in ScanSessions())
            if (snap.Working && fresh) list.Add(snap);

        // 按缓存写入时间从新到旧：最新活动的会话排在前面（也优先占据中央胶囊）
        list.Sort((a, b) => b.Seq.CompareTo(a.Seq));
        return list;
    }

    /// <summary>解析出当前活动会话的快照；无缓存 / 读失败返回 null</summary>
    public static DshSnapshot? ReadActiveSession()
    {
        var all = ScanSessions();
        if (all.Count == 0) return null;

        DshSnapshot? working = null;
        DateTime workingMtime = DateTime.MinValue;
        DshSnapshot? newest = null;
        DateTime newestSeen = DateTime.MinValue;

        foreach (var (snap, mtime, fresh) in all)
        {
            if (snap.Working && fresh && mtime > workingMtime)
            {
                workingMtime = mtime;
                working = snap;
            }
            if (mtime > newestSeen) { newestSeen = mtime; newest = snap; }
        }

        return working ?? newest ?? all[0].Snap;
    }

    /// <summary>解析单个投影缓存文件</summary>
    private static DshSnapshot? ParseCache(string path)
    {
        // 共享读：DSH 可能正在写；用 FileShare.ReadWrite 拿到当前内容。
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;
        if (!root.TryGetProperty("record", out var rec)) return null;
        if (!rec.TryGetProperty("rows", out var rows)) return null;

        var stats = Row(rows, "sessionStats");
        int turns = 0, steps = 0, turn = 0, step = 0;
        bool stepOpen = false;      // openStep != null：只在 LLM 流式阶段为真
        bool turnOpen = false;      // openTurnStartSeq != null：整个 turn 未结束（含工具执行）
        bool working = false;
        long stepElapsed = 0;
        long seq = 0;
        string subLabel = "", subMode = "";

        if (stats.HasValue)
        {
            var v = stats.Value;
            turns = Int(v, "turns");
            steps = Int(v, "steps");
            // seq 取自行的外层字段（rows.<name>.seq，而非 val 内部）
            if (rows.TryGetProperty("sessionStats", out var ssRow) &&
                ssRow.ValueKind == JsonValueKind.Object)
                seq = Long(ssRow, "seq");
            if (v.TryGetProperty("openStep", out var os) && os.ValueKind == JsonValueKind.Object)
            {
                stepOpen = true;
                turn = Int(os, "turn");
                step = Int(os, "step");
                long started = Long(os, "startTime");
                if (started > 0) stepElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - started;
            }
        }

        // 【核心判据】turnBoundary.openTurnStartSeq 非 null = 本轮 turn 还没结束。
        //
        // openStep 只在"LLM 正在流式输出"时非 null；一旦模型把工具调用吐完、进入
        // 工具执行阶段（可能几十秒），openStep 就清成 null —— 若只看它，会误判成
        // "任务完成"。实测 38s 工具执行期间 openTurnStartSeq 恒为 6116 不变。
        //
        // 另一个等价信号是 lastStepBoundary.kind == "start"（step 未闭合），两者取或。
        var tbRow = Row(rows, "turnBoundary");
        if (tbRow.HasValue)
        {
            if (Long(tbRow.Value, "openTurnStartSeq") > 0) turnOpen = true;
            if (tbRow.Value.TryGetProperty("lastStepBoundary", out var lsb) &&
                lsb.ValueKind == JsonValueKind.Object &&
                Str2(lsb, "kind") == "start") turnOpen = true;
        }

        // 【子代理】DSH 把每个子代理建成独立 session（实测 11 个 childId 全部
        // 有各自的投影缓存文件），所以子代理干活时本身就是一条 Working 会话，
        // 天然会出独立的岛。问题在于它的 title 多半继承自父会话，看不出"这是个
        // 子代理"——因此这里取出 identity.label / mode 供 UI 标注。
        var subRow = Row(rows, "subagent");
        if (subRow.HasValue && subRow.Value.TryGetProperty("identity", out var idEl) &&
            idEl.ValueKind == JsonValueKind.Object)
        {
            subLabel = Str2(idEl, "label");
            subMode = Str2(idEl, "mode");
        }
        working = stepOpen || turnOpen;

        // 不在步中时：用 turnBoundary.lastTurn 兜底"第几轮"，
        // 并用 tokenUsage.last.step 兜底"第几步"——否则空闲态会显示成"步 0"。
        // 注意 steps 是"累计步数"（跨轮累加），不能拿来当本轮步号。
        if (!stepOpen)
        {
            if (tbRow.HasValue) turn = Int(tbRow.Value, "lastTurn");

            var usage = Row(rows, "tokenUsage");
            if (usage.HasValue && usage.Value.TryGetProperty("last", out var lu0) &&
                lu0.ValueKind == JsonValueKind.Object)
                step = Int(lu0, "step");
        }

        var cp = Row(rows, "contextPressure");
        long surface = 0, window = 0;
        if (cp.HasValue) { surface = Long(cp.Value, "surfaceTokens"); window = Long(cp.Value, "contextWindow"); }

        var tu = Row(rows, "tokenUsage");
        long lastOut = 0;
        if (tu.HasValue && tu.Value.TryGetProperty("last", out var last) &&
            last.ValueKind == JsonValueKind.Object &&
            last.TryGetProperty("buckets", out var bk) && bk.ValueKind == JsonValueKind.Object)
            lastOut = Long(bk, "outputTokens");

        string title = "";
        if (rows.TryGetProperty("title", out var trow) && trow.ValueKind == JsonValueKind.Object)
            title = Str2(trow, "val");

        string model = "";
        var ms = Row(rows, "modelSelection");
        if (ms.HasValue && ms.Value.TryGetProperty("lastUsed", out var lu) &&
            lu.ValueKind == JsonValueKind.Object)
        {
            var p = Str2(lu, "provider");
            var m = Str2(lu, "model");
            model = !string.IsNullOrEmpty(m) ? m : p;
        }

        var sid = Path.GetFileNameWithoutExtension(path);
        if (sid.StartsWith("session-", StringComparison.OrdinalIgnoreCase)) sid = sid["session-".Length..];

        return new DshSnapshot(working, stepOpen, turn, step, turns, steps, title, model,
            surface, window, Math.Max(stepElapsed, 0), lastOut, sid, seq, subLabel, subMode);
    }

    // ---- JSON 小工具（容忍字段缺失 / 类型不符） ----

    private static JsonElement? Row(JsonElement rows, string name)
    {
        if (!rows.TryGetProperty(name, out var row)) return null;
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (row.TryGetProperty("val", out var val) && val.ValueKind == JsonValueKind.Object)
            return val;
        return null;
    }

    private static int Int(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var i) ? i : 0;

    private static string Str2(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? (v.GetString() ?? "") : "";

    // ================= 路径探测 =================

    /// <summary>
    /// DSH 主目录：优先设置覆盖 → 环境变量 DSH_HOME → 常见默认位置。
    /// </summary>
    public static string? HomeDir()
    {
        var candidates = new List<string>();
        var custom = App.CurrentSettings.Dsh.HomeOverride;
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(custom);

        var env = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        candidates.Add(@"D:\dsh\home");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            candidates.Add(Path.Combine(userProfile, ".dsh", "home"));

        foreach (var c in candidates)
        {
            try { if (Directory.Exists(c)) return c; } catch { }
        }
        return null;
    }

    private static string? SessionsDir()
    {
        var home = HomeDir();
        return home == null ? null : Path.Combine(home, "storages", "session_projcache", "sessions");
    }

    public void Dispose() => _timer?.Dispose();
}

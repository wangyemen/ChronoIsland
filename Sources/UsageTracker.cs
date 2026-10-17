using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ChronoIsland.Core;

namespace ChronoIsland.Sources;

/// <summary>
/// 前台应用使用时长统计（docs §5.3）。
/// WinEventHook 采集 + 空闲判定 + 会话合并 + jsonl 本地存储。
/// </summary>
public sealed class UsageTracker : IEventSource
{
    public string Key => "usage";
    public int Priority => 3;

    // ---- Win32 ----
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eMin, uint eMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    private static uint IdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        return (uint)((Environment.TickCount - lii.dwTime) / 1000);
    }

    // ---- 配置 ----
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    { "ApplicationFrameHost", "ShellExperienceHost", "TextInputHost", "dwm", "SearchHost",
      "StartMenuExperienceHost", "ChronoIsland", "LockApp", "WinLogon" };
    private const int MinSessionSeconds = 3;
    private const int MergeGapSeconds = 30;
    private const int IdleThresholdSeconds = 300;
    private const int FlushEverySeconds = 30;

    private EventBus? _bus;
    private IntPtr _hook = IntPtr.Zero;
    private readonly WinEventDelegate _delegate;
    private readonly object _gate = new();

    private string? _currentPath;
    private DateTime _currentStart;
    private DateTime _lastFlush = DateTime.Now;
    private DateTime _lastEmit = DateTime.MinValue;

    public UsageTracker()
    {
        _delegate = OnForegroundChanged;
    }

    public void Start(EventBus bus)
    {
        _bus = bus;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _delegate, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundChanged(IntPtr h, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            if (Ignored.Contains(name)) return;

            var now = DateTime.Now;
            lock (_gate)
            {
                // 空闲不计入
                if (IdleSeconds() > IdleThresholdSeconds) { CloseSession(null); return; }

                if (_currentPath == name)
                {
                    // 同一进程：30s 内视为连续，不做处理
                    return;
                }
                CloseSession(now);
                _currentPath = name;
                _currentStart = now;

                if (now - _lastFlush > TimeSpan.FromSeconds(FlushEverySeconds)) { Flush(); _lastFlush = now; }
                if (now - _lastEmit > TimeSpan.FromMinutes(30)) { _lastEmit = now; EmitToday(); }
            }
        }
        catch { /* 单模块异常不影响全局 */ }
    }

    private void CloseSession(DateTime? now)
    {
        if (_currentPath == null) return;
        var end = now ?? DateTime.Now;
        var dur = end - _currentStart;
        if (dur.TotalSeconds >= MinSessionSeconds)
            Append(new UsageSession(_currentPath, _currentStart, end));
        _currentPath = null;
    }

    // ---- 存储：%LOCALAPPDATA%\ChronoIsland\usage-YYYY-MM.jsonl ----
    private static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChronoIsland");

    private static string FileFor(DateTime d) =>
        Path.Combine(StoreDir, $"usage-{d:yyyy-MM}.jsonl");

    private void Append(UsageSession s)
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            File.AppendAllText(FileFor(s.Start), JsonSerializer.Serialize(s) + "\n", Encoding.UTF8);
        }
        catch (Exception ex) { App.Log("Usage", ex); }
    }

    private void Flush() { /* AppendAllText 即时落盘；如需缓冲在此实现 */ }

    /// <summary>读取某天聚合结果 → 进程名到时长</summary>
    public static Dictionary<string, TimeSpan> Load(DateTime day)
    {
        var result = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = FileFor(day);
            if (!File.Exists(file)) return result;
            foreach (var line in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                UsageSession? s;
                try { s = JsonSerializer.Deserialize<UsageSession>(line); } catch { continue; }
                if (s == null || s.Start.Date != day.Date) continue;
                var dur = s.End - s.Start;
                result[s.ProcessName] = result.TryGetValue(s.ProcessName, out var v) ? v + dur : dur;
            }
        }
        catch (Exception ex) { App.Log("Usage", ex); }
        return result;
    }

    /// <summary>
    /// 本周（周一至周日）的聚合结果。
    /// 一周最多跨两个月边界，因此按月文件分别打开，但只扫描一遍会话。
    /// </summary>
    public static List<UsageDay> LoadWeek(DateTime anyDayInWeek)
    {
        var start = StartOfWeek(anyDayInWeek);
        var days = new List<UsageDay>();
        for (int i = 0; i < 7; i++) days.Add(new UsageDay(start.AddDays(i)));

        try
        {
            var files = days.Select(d => FileFor(d.Date)).Distinct().ToList();
            foreach (var file in files)
            {
                if (!File.Exists(file)) continue;
                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    UsageSession? s;
                    try { s = JsonSerializer.Deserialize<UsageSession>(line); } catch { continue; }
                    if (s == null) continue;
                    var idx = days.FindIndex(d => d.Date == s.Start.Date);
                    if (idx < 0) continue;
                    days[idx].Add(s.ProcessName, s.End - s.Start);
                }
            }
        }
        catch (Exception ex) { App.Log("Usage", ex); }
        return days;
    }

    /// <summary>所在周的周一 00:00（周一为一周之首）</summary>
    public static DateTime StartOfWeek(DateTime d)
    {
        int delta = ((int)d.DayOfWeek + 6) % 7;   // 周一=0 … 周日=6
        return d.Date.AddDays(-delta);
    }

    private void EmitToday()
    {
        var agg = Load(DateTime.Today);
        var total = agg.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        if (total <= TimeSpan.Zero) return;
        var top = agg.OrderByDescending(kv => kv.Value).FirstOrDefault();
        _bus?.Publish(new IslandEvent(
            Id: "usage",
            SourceKey: "usage",
            Kind: "usage",
            Priority: Priority,
            Title: $"今日已用 {(int)total.TotalHours}h {total.Minutes}m",
            Subtitle: top.Key is null ? "" : $"{top.Key} · {(int)top.Value.TotalHours}h {top.Value.Minutes}m",
            // 【常驻不自动消失】使用时长是「随时想看随时看」的统计岛。
            // 之前 TTL 只有 4 秒：启动时弹一下，用户还没来得及悬停就被收走，
            // 于是周视图永远点不开（表现为「找不到周视图」）。
            // Ttl 必须传【null】而不是 TimeSpan.Zero ——
            // UI 侧判断是 `if (ev.Policy?.Ttl is TimeSpan)`，
            // Zero 仍会被当成有 TTL 并立刻收起，只有 null 才是真正的常驻。
            Policy: new IslandPolicy(null, Expandable: true))
        { AppKey = top.Key ?? "usage" });
    }

    /// <summary>手动唤出（托盘/快捷键）</summary>
    public void Poke() { lock (_gate) CloseSession(DateTime.Now); EmitToday(); }

    public void Dispose()
    {
        lock (_gate) CloseSession(DateTime.Now);
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
    }
}

public sealed record UsageSession(string ProcessName, DateTime Start, DateTime End);

/// <summary>某一天的使用时长聚合（供周视图树状图使用）</summary>
public sealed class UsageDay
{
    public UsageDay(DateTime date) { Date = date.Date; }
    public DateTime Date { get; }
    public Dictionary<string, TimeSpan> Apps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan Total => Apps.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
    /// <summary>是否已到当天 24:00 之后（未来日期不显示数据）</summary>
    public bool IsFuture => Date > DateTime.Today;
    /// <summary>当天是否已经开始过（今天及之前）</summary>
    public bool HasStarted => Date <= DateTime.Today;
    public void Add(string app, TimeSpan dur)
    {
        if (dur <= TimeSpan.Zero) return;
        Apps[app] = Apps.TryGetValue(app, out var v) ? v + dur : dur;
    }
}

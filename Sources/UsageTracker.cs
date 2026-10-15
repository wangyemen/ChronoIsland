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
            Policy: new IslandPolicy(TimeSpan.FromSeconds(4), Expandable: true))
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

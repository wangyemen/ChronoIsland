using System;
using System.Threading;
using ChronoIsland.Core;

namespace ChronoIsland.Sources;

/// <summary>
/// 时间日期。默认不常驻：跨小时、跨天、唤醒时浮现（docs §5.2）。
/// 秒更新对齐到下一秒，避免累积漂移。
/// </summary>
public sealed class ClockSource : IEventSource
{
    public string Key => "clock";
    public int Priority => 1;

    private EventBus? _bus;
    private System.Threading.Timer? _timer;
    private int _lastHour = -1;
    private int _lastDay = -1;

    public void Start(EventBus bus)
    {
        _bus = bus;
        _lastHour = DateTime.Now.Hour;
        _lastDay = DateTime.Now.Day;
        ScheduleNext();
    }

    /// <summary>手动唤出（如托盘点击）</summary>
    public void Poke() => Emit();

    private void ScheduleNext()
    {
        var now = DateTime.Now;
        // 对齐到下一个整分钟（跨小时/跨天必然落在整分）
        var next = now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);
        var due = next - now;
        if (due < TimeSpan.FromSeconds(1)) due = TimeSpan.FromSeconds(1);
        _timer = new System.Threading.Timer(_ => Tick(), null, due, Timeout.InfiniteTimeSpan);
    }

    private void Tick()
    {
        var now = DateTime.Now;
        bool hourChanged = now.Hour != _lastHour;
        bool dayChanged = now.Day != _lastDay;
        _lastHour = now.Hour; _lastDay = now.Day;

        if (hourChanged || dayChanged) Emit();
        ScheduleNext();
    }

    private void Emit()
    {
        var now = DateTime.Now;
        var wk = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" }[(int)now.DayOfWeek];
        _bus?.Publish(new IslandEvent(
            Id: "clock",
            SourceKey: "clock",
            Kind: "clock",
            Priority: Priority,
            Title: now.ToString("HH:mm"),
            Subtitle: $"{now.Month}月{now.Day}日 {wk}",
            Policy: new IslandPolicy(TimeSpan.FromSeconds(3), Coalesce: true, Expandable: true))
        { AppKey = "clock" });
    }

    public void Dispose() => _timer?.Dispose();
}

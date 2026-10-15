using System.Collections.Concurrent;

namespace ChronoIsland.Core;

/// <summary>
/// 事件总线：Source（后台线程）→ 队列 → 单一 UI 线程消费。
/// 支持优先级、同类合并、全局限流。
/// </summary>
public sealed class EventBus
{
    private readonly ConcurrentQueue<IslandEvent> _queue = new();
    private readonly object _gate = new();
    private Action<IslandEvent>? _sink;
    private readonly Dictionary<string, (IslandEvent Ev, DateTime At)> _recent = new();
    private readonly TimeSpan _coalesceWindow = TimeSpan.FromMilliseconds(200);
    private DateTime _lastEmit = DateTime.MinValue;
    private const double MaxEventsPerSecond = 8;

    /// <summary>注册 UI 侧消费者（应投递到 UI 线程）</summary>
    public void Subscribe(Action<IslandEvent> sink) { lock (_gate) _sink = sink; }

    /// <summary>Source 调用：发布事件</summary>
    public void Publish(IslandEvent ev)
    {
        lock (_gate)
        {
            // 同类 200ms 内合并，防止洪峰
            if (ev.Policy?.Coalesce != false)
            {
                var now = DateTime.UtcNow;
                if (_recent.TryGetValue(ev.SourceKey, out var prev) && now - prev.At < _coalesceWindow)
                {
                    _recent[ev.SourceKey] = (ev, now);
                    return;                       // 丢弃，等待窗口末尾那条
                }
                _recent[ev.SourceKey] = (ev, now);
            }

            // 全局限流 8 条/秒
            var elapsed = DateTime.UtcNow - _lastEmit;
            if (elapsed < TimeSpan.FromSeconds(1.0 / MaxEventsPerSecond))
            {
                _ = Task.Delay((int)(1000.0 / MaxEventsPerSecond - elapsed.TotalMilliseconds))
                        .ContinueWith(_ => Deliver(ev));
                return;
            }
            _lastEmit = DateTime.UtcNow;
        }
        Deliver(ev);
    }

    private void Deliver(IslandEvent ev)
    {
        Action<IslandEvent>? sink;
        lock (_gate) sink = _sink;
        sink?.Invoke(ev);
    }

    /// <summary>取出全部待处理事件（UI 线程调用）</summary>
    public bool TryDequeue(out IslandEvent ev) => _queue.TryDequeue(out ev!);

    /// <summary>清空最近合并缓存</summary>
    public void Reset()
    {
        lock (_gate) { _recent.Clear(); }
    }
}

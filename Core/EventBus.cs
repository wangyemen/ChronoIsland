using System;
using System.Collections.Concurrent;

namespace ChronoIsland.Core;

/// <summary>
/// 事件总线：Source（后台线程）→ 队列 → 单一 UI 线程消费。
/// 支持优先级、同类合并、全局限流。
///
/// 【0.0.2 线程模型收口】
/// 此前 <see cref="Deliver"/> 直接在**发布者线程**上回调 sink ——
/// 现在恰好所有 Source 都在 UI 线程发事件，所以没出问题；
/// 但这是一个**没有写在契约里的隐性依赖**：DSH 源的轮询定时器、媒体回调、
/// 以及（后续的）插件，任何一处换成后台线程，渲染就会跨线程访问被破坏。
///
/// 因此这里新增 <see cref="Marshaler"/>：宿主（IslandWindow）把它设成
/// <c>a =&gt; Dispatcher.InvokeAsync(a)</c>，总线保证 **sink 永远在 UI 线程执行**。
/// 它是幂等的 —— 已经在 UI 线程时 Dispatcher.InvokeAsync 会同步执行（同优先级排队），
/// 语义上仍然是"最终在 UI 线程"，这正是我们要的不变量。
/// </summary>
public sealed class EventBus
{
    private readonly object _gate = new();
    private Action<IslandEvent>? _sink;
    private readonly Dictionary<string, (IslandEvent Ev, DateTime At)> _recent = new();
    private TimeSpan _coalesceWindow = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 同类合并窗口。默认 200ms（防洪峰）；自检把它设为 0 关掉合并，
    /// 这样"连续两次采样"不会被合并逻辑吃掉，差分行为才测得准。
    /// 生产路径永远不碰它 —— 合并是防抖手段，不是可选行为。
    /// </summary>
    internal TimeSpan CoalesceWindow
    {
        get => _coalesceWindow;
        set => _coalesceWindow = value;
    }

    /// <summary>全局限流开关（仅自检用：关掉后才能一秒内连发多条断言）</summary>
    internal bool RateLimitEnabled { get; set; } = true;
    private DateTime _lastEmit = DateTime.MinValue;
    private const double MaxEventsPerSecond = 8;

    /// <summary>
    /// UI 线程投递器。由宿主设置；未设置时退化为"发布者线程直调"（仅诊断/测试场景）。
    /// 契约：传入的 Action 最终会在 UI 线程上执行一次。
    /// </summary>
    public Action<Action>? Marshaler { get; set; }

    /// <summary>注册 UI 侧消费者。调用方必须先设置 <see cref="Marshaler"/>。</summary>
    public void Subscribe(Action<IslandEvent> sink) { lock (_gate) _sink = sink; }

    /// <summary>Source 调用：发布事件（可在任意线程）</summary>
    public void Publish(IslandEvent ev)
    {
        if (ev == null) return;

        bool immediate;
        lock (_gate)
        {
            immediate = true;

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
            var minGap = TimeSpan.FromSeconds(1.0 / MaxEventsPerSecond);
            if (RateLimitEnabled && elapsed < minGap)
            {
                // 【0.0.2 修正】这里必须同时推进 _lastEmit：
                // 以前只调度延时、不更新时间戳，于是同一窗口内的每条事件都按
                // 同一个剩余时长延时，随后**一起**落地 —— 限流形同虚设（只是把洪峰推迟）。
                // 现在每调度一条就把时间戳往前推一个间隔，洪峰被真正摊平成 8 条/秒。
                int delayMs = (int)Math.Ceiling((minGap - elapsed).TotalMilliseconds);
                if (delayMs < 1) delayMs = 1;
                _lastEmit = DateTime.UtcNow.AddMilliseconds(delayMs);
                immediate = false;
                _ = Task.Delay(delayMs).ContinueWith(_ => Deliver(ev), TaskScheduler.Default);
            }
            else
            {
                _lastEmit = DateTime.UtcNow;
            }
        }

        if (immediate) Deliver(ev);
    }

    /// <summary>
    /// 把事件交给 sink。**必须始终经由此方法**，这样"强制 UI 线程"这条
    /// 不变量只有一处实现，不会因为新代码绕路而失效。
    /// </summary>
    private void Deliver(IslandEvent ev)
    {
        Action<IslandEvent>? sink;
        lock (_gate) sink = _sink;
        if (sink == null) return;

        var marshaler = Marshaler;
        if (marshaler == null)
        {
            // 未接宿主的退化路径（诊断器/测试）：直调，但仍要兜住异常，
            // 否则异常会顺着 Timer 回调冒泡成"未处理异常"。
            try { sink(ev); }
            catch (Exception ex) { Log.Error("EventBus", ex); }
            return;
        }

        try
        {
            marshaler(() =>
            {
                try { sink(ev); }
                catch (Exception ex) { Log.Error("EventBus", ex); }
            });
        }
        catch (Exception ex)
        {
            // Marshaler 本身失败（例如 Dispatcher 已关闭）：写日志，绝不上抛
            Log.Error("EventBus", ex);
        }
    }
}

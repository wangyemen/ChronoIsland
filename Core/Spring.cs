using System;
using System.Diagnostics;

namespace ChronoIsland.Core;

/// <summary>
/// 阻尼谐振子弹簧。半隐式欧拉，固定步长 1/120s。
/// 掉帧时自动拆子步，避免大 dt 下发散（与原型 HTML 同一求解器）。
/// </summary>
public sealed class Spring
{
    /// <summary>固定步长（秒）</summary>
    public const double FixedDelta = 1.0 / 120.0;
    private const double Epsilon = 1e-6;
    private const int MaxSubSteps = 64;
    private const double SettlePosition = 0.05;
    private const double SettleVelocity = 0.05;

    public Spring(double value, double stiffness, double damping)
    {
        Value = value; Target = value; Stiffness = stiffness; Damping = damping;
    }

    /// <summary>当前值</summary>
    public double Value { get; private set; }
    /// <summary>目标值</summary>
    public double Target { get; private set; }
    /// <summary>速度</summary>
    public double Velocity { get; private set; }
    public double Stiffness { get; private set; }
    public double Damping { get; private set; }

    /// <summary>能量，用于诊断</summary>
    public double Energy => 0.5 * Velocity * Velocity + 0.5 * Stiffness * (Value - Target) * (Value - Target);

    /// <summary>是否已收敛</summary>
    public bool IsSettled =>
        Math.Abs(Value - Target) < SettlePosition && Math.Abs(Velocity) < SettleVelocity;

    /// <summary>设置目标（不打断当前运动）</summary>
    public void To(double target) => Target = target;

    /// <summary>瞬移到目标并清零速度</summary>
    public void Snap(double value)
    {
        Value = Target = value; Velocity = 0;
    }

    /// <summary>切换刚度/阻尼（用于阶段切换）</summary>
    public void SetParameters(double stiffness, double damping)
    {
        Stiffness = stiffness; Damping = damping;
    }

    /// <summary>
    /// 推进 dt 秒。内部拆分为 ≤1/120s 的子步，保证掉帧时稳定。
    /// </summary>
    public void Step(double dt)
    {
        if (dt <= 0) return;
        double remaining = dt;
        int guard = 0;
        while (remaining > Epsilon && guard++ < MaxSubSteps)
        {
            double h = Math.Min(remaining, FixedDelta);
            remaining -= h;
            double a = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
        }
        if (double.IsNaN(Value) || double.IsInfinity(Value) || double.IsNaN(Velocity) || double.IsInfinity(Velocity))
        {
            // 兜底：绝不飞出屏幕
            Value = Target; Velocity = 0;
        }
    }
}

/// <summary>弹簧预设（与 docs/design-0.0.1.md §3.2 冻结值一致）</summary>
public static class SpringPresets
{
    /// <summary>宽度变化、内容替换 — 轻微回弹</summary>
    public static (double K, double C) Gentle => (170, 22);
    /// <summary>折叠成点（阶段 A）— 快、微过冲</summary>
    public static (double K, double C) Snappy => (300, 26);
    /// <summary>水珠拉伸/吸附（阶段 B）— 黏、带过冲</summary>
    public static (double K, double C) Gooey => (220, 18);
    /// <summary>详情卡展开 — 柔和</summary>
    public static (double K, double C) Float => (140, 20);
    /// <summary>堆叠推挤/补位 — 几乎无过冲</summary>
    public static (double K, double C) Push => (200, 24);
    /// <summary>手动忽略/滑出 — 干脆</summary>
    public static (double K, double C) Dismiss => (260, 30);
}

/// <summary>
/// 全局弹簧驱动器：单个 CompositionTarget.Rendering 驱动所有弹簧。
/// 不使用 DispatcherTimer / Storyboard。
/// </summary>
public sealed class SpringEngine
{
    private static SpringEngine? _instance;
    public static SpringEngine Instance => _instance ??= new SpringEngine();

    private readonly List<Spring> _springs = new();
    private readonly List<Action> _tickers = new();
    private bool _running;
    private long _lastTicks;

    private SpringEngine() { }

    /// <summary>注册一个弹簧，由引擎统一驱动</summary>
    public void Register(Spring spring)
    {
        lock (_springs) { if (!_springs.Contains(spring)) _springs.Add(spring); }
        EnsureRunning();
    }

    public void Unregister(Spring spring)
    {
        lock (_springs) _springs.Remove(spring);
    }

    /// <summary>注册每帧回调（如颈缩路径重算）</summary>
    public void RegisterTicker(Action tick)
    {
        lock (_tickers) { if (!_tickers.Contains(tick)) _tickers.Add(tick); }
        EnsureRunning();
    }

    public void UnregisterTicker(Action tick)
    {
        lock (_tickers) _tickers.Remove(tick);
    }

    /// <summary>当前活跃弹簧数（诊断用）</summary>
    public int ActiveCount { get { lock (_springs) return _springs.Count(s => !s.IsSettled); } }

    private void EnsureRunning()
    {
        if (_running) return;
        _running = true;
        _lastTicks = Stopwatch.GetTimestamp();
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
    }

    private void StopIfIdle()
    {
        // 栈空时彻底停摆，CPU ≈ 0
        lock (_springs) lock (_tickers)
        {
            if (_springs.Count == 0 && _tickers.Count == 0) return;
        }
        return; // 保守：不自动摘事件，避免频繁挂/摘
    }

    /// <summary>
    /// 手动推进一帧（供高优先级定时器兜底调用）。
    /// CompositionTarget.Rendering 在窗口后台时会节流，需要这个入口保证弹簧收敛。
    /// </summary>
    public void Tick()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        Advance(dt);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;
        Advance(dt);
    }

    

    private void Advance(double dt)
    {
        if (dt > 0.05) dt = 0.05;         // 掉帧上限保护
        if (dt <= 0) return;

        Spring[] snapshot;
        Action[] ticks;
        lock (_springs) snapshot = _springs.ToArray();
        lock (_tickers) ticks = _tickers.ToArray();

        foreach (var s in snapshot) s.Step(dt);
        foreach (var t in ticks) t();
    }
}

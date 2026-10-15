using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ChronoIsland.Animation;
using ChronoIsland.Core;

namespace ChronoIsland.UI;

/// <summary>
/// 岛主窗口：透明·置顶·无边框。渲染集群（中央胶囊 + 左右球体）。
/// 栈空时窗口隐藏、停止一切定时器（docs §6 后台静默）。
/// </summary>
public partial class IslandWindow : Window
{
    private readonly ClusterStack _stack = new();
    private readonly Dictionary<string, Border> _visuals = new();
    private readonly DispatcherTimer _autoReadTimer;
    private string? _lastLayoutSignature;
    private bool _gameHidden;

    public IslandWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // 自动已读计时（5s，悬停暂停）
        _autoReadTimer = new DispatcherTimer(DispatcherPriority.Normal)
        { Interval = TimeSpan.FromMilliseconds(200) };
        _autoReadTimer.Tick += OnAutoReadTick;
    }

    private readonly Core.EventBus _bus = new();
    private Sources.ClockSource? _clock;
    private Sources.UsageTracker? _usage;
    private Sources.MediaSource? _media;
    private Sources.SysStateSource? _sys;   // 键位 + 会话解锁（docs §5.5）
    private static readonly double s_progressTrackWidth = 388;


    /// <summary>展开卡歌词区高度（当前行 + 上下各 3 行 = 7 行），超出用滚动条</summary>
    private static readonly double s_cardLyricMaxHeight = 220;
    private MediaCardParts? _cardParts;
    /// <summary>最近一次卡片内按钮操作的时间（用于屏蔽冒泡）</summary>
    private DateTime _lastCardAction = DateTime.MinValue;

    // ================= 悬停穿透状态机 =================
    /// <summary>鼠标是否在岛的实际区域内</summary>
    private bool _pointerOverIsland;
    /// <summary>鼠标进入岛的时刻（用于计算放行延时）</summary>
    private DateTime _pointerEnterAt = DateTime.MinValue;
    /// <summary>最近一次"检测到鼠标在岛内"的时刻（用于抖动容错）</summary>
    private DateTime _lastInsideAt = DateTime.MinValue;
    private DateTime _lastHitLog = DateTime.MinValue;
    /// <summary>离开判定宽限：连续这么久都没命中才认为真的离开了</summary>
    private static readonly TimeSpan LeaveGrace = TimeSpan.FromMilliseconds(250);
    /// <summary>当前是否处于"可交互"状态（不穿透）</summary>
    private bool _interactive;
    /// <summary>当前窗口是否已设为穿透（null = 尚未同步过）</summary>
    private bool? _clickThrough;
    /// <summary>Alt 键是否按下（强制穿透）</summary>
    private bool _altDown;
    /// <summary>状态巡检定时器</summary>
    private DispatcherTimer? _hitTestTimer;
    private DispatcherTimer? _renderTimer;
    private DispatcherTimer? _topmostTimer;

    /// <summary>媒体卡的实时更新句柄（进度条 / 时间 / 播放图标）</summary>
    private sealed class MediaCardParts
    {
        public Border? ProgressFill;
        public Border? Track;
        public double TrackWidth = 388;
        public TextBlock? PosText;
        public TextBlock? RemainText;
        /// <summary>播放/暂停按钮的图标宿主（换图标用）</summary>
        public Border? PlayGlyphHost;
        /// <summary>当前播放按钮图标名</summary>
        public string PlayGlyphName = "";
        /// <summary>进度条圆点（拖动时跟随指针）</summary>
        public Border? Knob;
        /// <summary>该会话是否支持拖动进度；false 时进度条只读、不可拖</summary>
        public bool CanSeek;
        /// <summary>用户正在拖动 → 暂停轮询回写，避免"边拖边弹回"</summary>
        public bool Scrubbing;
        /// <summary>拖动结束后要提交的目标比例（0–1）</summary>
        public double PendingRatio = -1;
    }

    /// <summary>
    /// 每帧刷新媒体卡的可变部分（进度条 / 时间 / 播放按钮图标）。
    /// 卡片是静态构建的，必须在这里持续更新，否则进度条永远不动。
    /// </summary>
    private void RefreshMediaCard()
    {
        var parts = _cardParts;
        if (parts?.ProgressFill == null) return;
        // 用户正在拖动进度 → 不回写位置，否则会和指针"抢"控制权
        if (parts.Scrubbing) return;

        // 按当前展开的会话取快照（支持多会话）
        var active = _stack.Active;
        if (active == null || active.Event.Kind != "media") return;
        var snap = _media?.GetCurrent(active.Event.AppKey ?? active.Event.SourceKey);
        if (snap == null) return;

        double pct = Math.Clamp(snap.ProgressPercent, 0, 100);
        parts.ProgressFill.Width = Math.Max(2, parts.TrackWidth * pct / 100.0);

        var pos = TimeSpan.FromTicks(Math.Max(0, snap.PositionTicks));
        var end = TimeSpan.FromTicks(Math.Max(0, snap.EndTicks));
        if (parts.PosText != null)
            parts.PosText.Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:00}";
        if (parts.RemainText != null)
        {
            if (end > TimeSpan.Zero)
            {
                var rem = end - pos;
                parts.RemainText.Text = $"-{(int)rem.TotalMinutes}:{rem.Seconds:00}";
            }
            else parts.RemainText.Text = "直播";
        }
        // 播放/暂停图标切换（Lucide）
        string want = snap.IsPlaying ? "pause" : "play";
        if (parts.PlayGlyphHost != null && parts.PlayGlyphName != want)
        {
            parts.PlayGlyphName = want;
            parts.PlayGlyphHost.Child = Native.SvgIcons.CreateCentered(want, 17,
                Brushes.White, 1.35);
        }
    }

    /// <summary>
    /// 刷新展开卡的歌词区：当前行 + 前后上下文，当前行高亮加粗。
    /// 只在行索引变化时重建（用签名比对），不做每帧重绘。
    /// </summary>
    private void RefreshCardLyric(Sources.MediaSnapshot? snap)
    {
        var cur = _cardLyricText;
        var prev = _cardLyricPrev;
        var next = _cardLyricNext;
        var clip = _cardLyricClip;
        if (cur == null || clip == null) return;

        // 只有音乐播放器才显示歌词
        var ctx = new System.Collections.Generic.List<(int Index, string Text, bool IsCurrent)>();
        if (snap != null && _settings.Media.ShowLyrics && Sources.MediaSourcePolicy.IsMusicPlayer(snap.SourceId))
        {
            int posMs = (int)(snap.PositionTicks / TimeSpan.TicksPerMillisecond);
            ctx = _lyrics.GetContextFull(posMs, 1, 1, _lyricOffsetMs, false);
        }

        if (ctx.Count == 0)
        {
            if (_cardLyricSig != "")
            {
                _cardLyricSig = "";
                if (prev != null) prev.Text = "";
                if (next != null) next.Text = "";
                cur.Text = "";
            }
            clip.Visibility = Visibility.Collapsed;
            return;
        }
        clip.Visibility = Visibility.Visible;

        var sig = string.Join("|", ctx.Select(c => c.Index + ":" + c.Text));
        if (sig.Length > 0 && sig == _cardLyricSig) return;   // 行未变，不重建
        _cardLyricSig = sig;

        // 三行：上一行（淡化）/ 当前行（高亮）/ 下一行（淡化）
        string curText = "", prevText = "", nextText = "";
        foreach (var item in ctx)
        {
            if (item.IsCurrent) curText = item.Text;
            else if (prevText.Length == 0 && curText.Length == 0) prevText = item.Text;   // 当前行之前
            else nextText = item.Text;                                                    // 当前行之后
        }
        if (curText.Length == 0) curText = ctx[0].Text;   // 兜底

        if (prev != null) { prev.Text = prevText; prev.Visibility = prevText.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
        cur.Text = curText;
        if (next != null) { next.Text = nextText; next.Visibility = nextText.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }

        // 当前行超宽时水平滚动（单向一轮，停留-滚动-停留）
        cur.RenderTransform = Transform.Identity;
        if (!_settings.Media.LyricAutoScroll) return;
        double avail = clip.ActualWidth;
        if (avail <= 0) return;
        double need = MeasureText(curText, 13.5, true);
        if (need <= avail - 1) return;
        double shift = need - avail + 8;
        double sec = Math.Clamp(shift / 35.0, 2.0, 8.0);
        var tx = new TranslateTransform();
        cur.RenderTransform = tx;
        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(sec + 2.4),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0,
            System.Windows.Media.Animation.KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0,
            System.Windows.Media.Animation.KeyTime.FromPercent(0.12)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(-shift,
            System.Windows.Media.Animation.KeyTime.FromPercent(0.88)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(-shift,
            System.Windows.Media.Animation.KeyTime.FromPercent(1.0)));
        tx.BeginAnimation(TranslateTransform.XProperty, anim);
    }


    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowChromeHelper.MakeToolWindow(this);
        WindowChromeHelper.CenterTop(this);

        // 多显示器（含虚拟显示器）环境下，确保岛落在"主显示器"上
        // ——否则 GetCursorPos 的物理坐标与岛所在屏幕对不上，悬停判定失效
        WindowChromeHelper.MoveToPrimaryMonitor(this);
        SpringEngine.Instance.RegisterTicker(Render);
        App.LogInfo("Island", "渲染 ticker 已注册");

        // 【启动入场队列】打开后这段时间内到达的事件先排队，按优先级逐条弹出，
        // 避免时钟 / 使用时长 / 媒体 / 键位在启动瞬间一起"滴落"糊成一片。
        StartupQueueActive = true;

        // 事件源接线
        _bus.Subscribe(ev => OnEvent(ev));
        _clock = new Sources.ClockSource();
        _clock.Start(_bus);

        // 时钟在启动时补一条"当前状态"（跨小时/跨天判定要等下一个整点，
        // 启动时先给用户看一眼当前时间与日期）
        _clock.Poke();

        var notif = new Sources.NotificationSource();
        notif.Start(_bus);
        // 权限检查是异步的：等待结果后再决定是否抑制原生弹窗
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            // 等待权限检查结果（BackgroundSource 内部异步初始化）
            for (int i = 0; i < 20; i++)
            {
                if (notif.HasAccess)
                {
                    Dispatcher.Invoke(() =>
                    {
                        // 横幅抑制：Win11 上第三方程序无法拦截通知横幅
                        // （横幅非 Win32 窗口；注册表键已失效；官方 API 需包标识）
                        // 因此这里只记录状态，不修改任何系统设置。
                        App.LogInfo("Notification",
                            "通知已接管至灵动岛（系统横幅不受第三方程序控制，属 Windows 平台限制）");
                    });
                    return;
                }
                System.Threading.Thread.Sleep(250);
            }
            App.LogInfo("Notification", "权限未就绪，暂不抑制原生通知");
        });

        _usage = new Sources.UsageTracker();
        _usage.Start(_bus);
        // 使用时长在启动时也播报一次（今日已用 X 小时）
        _usage.Poke();

        _media = new Sources.MediaSource();
        _media.Start(_bus);

        // 系统状态：键位翻转 + 会话解锁（docs §5.5）
        _sys = new Sources.SysStateSource();
        _sys.Configure(_settings);
        _sys.Start(_bus);

        Native.FullscreenDetector.FullscreenChanged += OnFullscreenChanged;
        Native.FullscreenDetector.Start();

        // 初始就设为穿透：岛默认不挡任何东西
        // 用 SourceInitialized（HwndSource 建好、句柄此刻可用）而非 Loaded：
        // WPF Window 并没有公开的 Created 事件；Loaded 要等第一次 Show() 才触发，
        // 而本窗口是 Hidden 创建的 → _clickThrough 永远得不到初始同步，
        // 悬停达标时 SetClickThrough(false) 因 _clickThrough==null!=false 才真正写入，
        // 但此前窗口一直是未同步的穿透态 —— 表现为"停留 0.4s 后详情卡能展开、却永远点不动"。
        // SourceInitialized 在构造后立即触发，句柄可用，是同步基准态的正确时机。
        SourceInitialized += (_, _) => SetClickThrough(true);

        // 【自适应】显示器接入/拔出、分辨率或缩放变化时，重新定位到主显示器顶部，
        // 保证窗口宽度 / 物理中心与命中测试坐标系始终一致（"放到岛的左边/右边才触发"的根因之一）
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;

        // 【关键】用高优先级定时器兜底驱动弹簧与渲染。
        // 原因：CompositionTarget.Rendering 在窗口未激活/后台时会被 WPF 节流甚至暂停，
        // 导致弹簧不收敛（岛停在半透明、坐标不归位），悬停判定随之失效。
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += (_, _) => { SpringEngine.Instance.Tick(); };
        _renderTimer.Start();

        // 悬停穿透：每 60ms 巡检一次鼠标位置与 Alt 状态
        _hitTestTimer = new DispatcherTimer(DispatcherPriority.Input)
        { Interval = TimeSpan.FromMilliseconds(60) };
        _hitTestTimer.Tick += (_, _) => UpdateHitTest();
        _hitTestTimer.Start();

        // 自检：启动后打印窗口穿透样式，确认策略真的生效
        var diag = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        int diagCount = 0;
        diag.Tick += (_, _) =>
        {
            if (++diagCount > 4) { diag.Stop(); return; }
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int ex = WindowChromeHelper.GetExStyle(hwnd);
                bool through = (ex & 0x20) != 0;
                App.LogInfo("HitTest",
                    $"[{diagCount}] 穿透={(through ? "开" : "关")} IsVisible={IsVisible} " +
                    $"岛数={_stack.Items.Count} 定时器={(_hitTestTimer != null)} " +
                    $"悬停留痕={IslandMetrics.HoverActivateMs}ms | {DescribeRects()}");
            }
            catch (Exception ex2) { App.Log("HitTest", ex2); }
        };
        diag.Start();

        // 周期性重新置顶：其他程序（截图工具、全屏应用）可能把岛压到后面
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (!IsVisible || _gameHidden) return;
            try { WindowChromeHelper.SetTopmostNoActivate(this); } catch { }
        };
        _topmostTimer.Start();

        // 几何：把设置同步到 metrics（顶端距离 / 宽度上限 / 缩放）
        IslandMetrics.Apply(_settings);

        // 主题：跟随设置 + 系统变化时刷新灵动岛
        AppTheme.Changed += _ => Dispatcher.InvokeAsync(ApplyVisualTheme);
        ApplyVisualTheme();

        // 监听系统主题切换（"跟随系统"模式下实时响应）
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // 窗口销毁时解绑所有 SystemEvents 静态事件，避免泄漏（IslandWindow 常驻单例，
        // 但托盘退出 / 测试场景会反复创建，解绑是保险）
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        };

        Hide();                       // 默认空态：不可见
    }

    /// <summary>手动唤出时钟（冒烟测试 / 托盘点击）</summary>
    public void PokeClock() => _clock?.Poke();

    /// <summary>注入一条测试通知（M5 之前用于验证集群）</summary>
    public void DebugPush(string app, string title, string sub)
    {
        _bus.Publish(new Core.IslandEvent(
            Id: Guid.NewGuid().ToString("N"),
            SourceKey: app,
            Kind: "notification",
            Priority: 4,
            Title: title,
            Subtitle: sub,
            Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(5))));
    }

    // ---------- 启动入场队列（docs §2.6）----------
    // 启动瞬间多个源会同时发事件（时钟、时长、媒体、键位…）。
    // 若一次性全部 DropletAnimator 生成，会同时弹出、互相挤掉，体验很糟。
    // 因此启动阶段先进队列，按 Priority 从高到低排队，逐条入场（默认间隔 900ms），
    // 期间新到的普通事件仍然实时插入。
    private readonly List<IslandEvent> _startupQueue = new();
    private DispatcherTimer? _startupPump;
    private bool _startupDraining;

    /// <summary>启动阶段：所有新事件进队列排队入场</summary>
    public bool StartupQueueActive { get; set; }

    /// <summary>启动时把事件放进队列；同一 Id 只保留最后一条（媒体每秒刷新会刷屏）</summary>
    private void EnqueueStartup(IslandEvent ev)
    {
        // 同 Id 去重：媒体源每 250ms 就会发一次刷新事件，
        // 若不去重队列里会塞进几十条同一条岛。
        for (int i = _startupQueue.Count - 1; i >= 0; i--)
            if (_startupQueue[i].Id == ev.Id) _startupQueue.RemoveAt(i);

        _startupQueue.Add(ev);
        App.LogInfo("Island", $"启动队列：+{ev.Kind}/{ev.SourceKey}（排队 {_startupQueue.Count} 条）");
        EnsureStartupPump();
    }

    private void EnsureStartupPump()
    {
        if (_startupPump != null) return;
        _startupPump = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _startupPump.Tick += (_, _) => PumpStartupQueue();
        _startupPump.Start();
    }

    /// <summary>取队首（优先级高者先出），入场一条后按剩余数量决定是否继续</summary>
    private void PumpStartupQueue()
    {
        if (_startupDraining) return;
        if (_startupQueue.Count == 0)
        {
            _startupPump?.Stop();
            StartupQueueActive = false;
            App.LogInfo("Island", "启动入场队列已放完，恢复实时响应");
            return;
        }

        _startupDraining = true;
        try
        {
            // 队首取 Priority 最大者（同优先级则保持先来后到）
            var bestIdx = 0;
            for (int i = 1; i < _startupQueue.Count; i++)
                if (_startupQueue.ElementAt(i).Priority > _startupQueue.ElementAt(bestIdx).Priority)
                    bestIdx = i;

            var ev = _startupQueue[bestIdx];
            _startupQueue.RemoveAt(bestIdx);
            App.LogInfo("Island", $"启动入场：{ev.Kind}/{ev.SourceKey}（剩 {_startupQueue.Count} 条）");
            // 媒体：入栈时同步曲目给歌词源
            if (ev.Kind == "media" && ev.Payload is Sources.MediaSnapshot ms0)
            {
                int d0 = (int)(Math.Max(0, ms0.EndTicks) / TimeSpan.TicksPerMillisecond);
                _lyrics.UpdateTrack(ms0.Title, ms0.Artist, ms0.Album, d0);
            }

            AdmitIsland(ev);
        }
        finally { _startupDraining = false; }
    }

    /// <summary>真正生成一条岛（原本 OnEvent 里的主体逻辑）</summary>
    private void AdmitIsland(IslandEvent ev)
    {
        var item = CreateItem(ev);
        var prev = _stack.Active;

        // 不抢焦点的事件（暂停中的媒体）不夺走中央位置
        if (!ev.StealFocus && prev != null)
        {
            _stack.Add(item);
            item.Role = IslandRole.Sphere;
            _ = DropletAnimator.EmergeAsync(item);
            ApplyLayout(force: true);
            EnsureVisible();
            return;
        }

        _stack.Add(item);

        // 旧胶囊"退位"为球体
        if (prev != null)
        {
            prev.Role = IslandRole.Sphere;
            prev.SetPreset(SpringPresets.Push, prev.Width, prev.Height, prev.X);
            prev.Width.To(IslandMetrics.SphereSize);
            prev.ContentOpacity.To(1);
        }

        _ = DropletAnimator.EmergeAsync(item);
        ApplyLayout(force: true);
        EnsureVisible();

        if (ev.Policy?.Ttl is TimeSpan) item.TtlStart = DateTime.Now;
        _autoReadTimer.Start();
    }

    // ---------- 事件入口（由 Source 通过 EventBus 投递到 UI 线程）----------
    public void OnEvent(IslandEvent ev)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (_gameHidden)
            {
                if (ev.Policy?.QueueWhenHidden == true) PendingQueue.Instance.Enqueue(ev);
                App.LogInfo("Island", $"隐藏期间收到的 {ev.Kind} 事件已暂存：{ev.Title}");
                return;
            }

            // 【就地更新】同一 Id 的事件（如媒体每秒刷新）应替换内容，而不是新增一条岛
            var existing = _stack.Items.FirstOrDefault(i => i.Event.Id == ev.Id);
            if (existing != null)
            {
                UpdateItem(existing, ev);
                return;
            }

            // 媒体"会话结束"事件：Title 为空 → 收起该岛
            if (ev.Kind == "media" && string.IsNullOrEmpty(ev.Title))
            {
                var media = _stack.Items.FirstOrDefault(i => i.Event.Id == "media");
                if (media != null && !media.IsDying) Dismiss(media);
                return;
            }

            // 启动阶段：先进队列按优先级逐条入场，避免一次性全部弹出
            if (StartupQueueActive)
            {
                EnqueueStartup(ev);
                return;
            }

            AdmitIsland(ev);
        });
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_settings.Theme.Mode != "System") return;
        Dispatcher.InvokeAsync(() => App.ApplyTheme("System"));
    }

    /// <summary>
    /// 显示器设置变化（接拔屏 / 改分辨率 / 改缩放）→ 重新定位到主显示器顶部。
    /// 延迟 1.5s 触发：Windows 在分辨率/DPI 变化时会反复广播，且此刻 GetMonitorInfo
    /// 可能尚未就绪；稍等让系统布局稳定后再重定位最可靠。
    /// </summary>
    private DispatcherTimer? _displayDebounce;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_displayDebounce == null)
            {
                _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _displayDebounce.Tick += (_, _) =>
                {
                    _displayDebounce.Stop();
                    RedeployToPrimaryMonitor();
                };
            }
            _displayDebounce.Stop();
            _displayDebounce.Start();
        });
    }

    /// <summary>锁屏/解锁/恢复会话时，显示器配置可能变化，同样重新定位</summary>
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) Dispatcher.InvokeAsync(RedeployToPrimaryMonitor);
    }

    /// <summary>把窗口重新对齐到当前主显示器顶部（物理像素）</summary>
    private void RedeployToPrimaryMonitor()
    {
        try
        {
            App.LogInfo("Island", "显示器/会话变化 → 重新定位到主显示器");
            WindowChromeHelper.MoveToPrimaryMonitor(this);
            _lastLayoutSignature = null;
            ApplyLayout(force: true);
        }
        catch (Exception ex) { App.Log("Island", ex); }
    }

    /// <summary>胶囊内歌词控件引用（用于每帧更新文本 / 滚动）</summary>
    private TextBlock? _pillLyricText;
    private Grid? _pillLyricRow;
    private FrameworkElement? _pillLyricHost;
    private string _lastLyricText = "";
    private double _lyricScrollShift;      // 需要滚动时的位移
    private bool _lyricNeedScroll;

    /// <summary>歌词源（跟随当前媒体曲目）</summary>
    private readonly Sources.LyricsSource _lyrics = new();
    private int _lyricOffsetMs = 0;                 // 用户微调的歌词偏移（毫秒）

    /// <summary>展开卡歌词容器与其滚动宿主</summary>
    /// <summary>用户上次手动滚动歌词的时间（4 秒内不自动跟随，避免上滚被拉回）</summary>
    private DateTime _lyricUserScrollAt = DateTime.MinValue;
    /// <summary>卡片内容滚动宿主（超出高度上限时滚动而非裁切）</summary>
    private ScrollViewer? _cardScrollHost;
    /// <summary>歌词内容变化后需重新实测卡片高度（歌词异步晚到）</summary>
    private bool _cardNeedsRemeasure;
    /// <summary>展开卡：标题右侧的歌词控件（含横向滚动）</summary>
    private TextBlock? _cardLyricText;
    private TextBlock? _cardLyricPrev;
    private TextBlock? _cardLyricNext;
    private StackPanel? _cardLyricPanel;
    private Grid? _cardLyricClip;
    private string _cardLyricSig = "";
    /// <summary>卡片展开时刻（用于关闭按钮防误触）</summary>
    private DateTime _cardExpandedAt = DateTime.MinValue;

    private Core.Settings _settings = Core.Settings.Load();

    /// <summary>设置变化时重新应用几何（顶端距离 / 宽度上限 / 缩放）</summary>
    public void ApplyGeometrySettings(Core.Settings s)
    {
        _lyricOffsetMs = s.Media.LyricOffsetMs;
        IslandMetrics.Apply(s);
        foreach (var item in _stack.Items)
            item.MeasuredWidth = MeasurePillWidth(item.Event);
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
    }

    /// <summary>
    /// 图库自检：构建一张详情卡但不进入集群（供 IconGallery 离屏渲染用）。
    /// </summary>
    public FrameworkElement ProbeCard(string kind, ThemeTokens? themeOverride = null)
    {
        object snap = kind switch
        {
            "camera" => new Sources.CameraSnapshot(true, "HD Webcam"),
            "unlock" => new Sources.UnlockSnapshot(720, DateTime.Now, IntPtr.Zero),
            "media" => new Sources.MediaSnapshot("Spotify", "Spotify", "夜航西飞", "Sodagreen", "动感生活", true, 32, 24_000_000L, 304_000_000L),
            _ => new Sources.SysSnapshot("capslock", true)
        };
        var ev = new Core.IslandEvent(
            Id: kind, SourceKey: "probe", Kind: kind, Priority: 2,
            Title: kind == "unlock" ? "已解锁" : kind == "camera" ? "摄像头已启用"
                 : kind == "media" ? "夜航西飞" : "大写锁定",
            Subtitle: kind == "unlock" ? "进入桌面" : kind == "camera" ? "HD Webcam"
                    : kind == "media" ? "Sodagreen" : "开",
            Payload: snap,
            Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(6)))
        { AppKey = "sys" };
        // BuildCard 内部取 AppTheme.Current，这里临时换成传入的主题，
        // 保证离屏渲染出来的配色与真实运行时一致（浅色模式必须用浅色 token）。
        var prev = AppTheme.Current;
        if (themeOverride != null) AppTheme.SetCurrentForProbe(themeOverride);
        try
        {
            return WrapWithClose(BuildCard(ev));
        }
        finally
        {
            AppTheme.SetCurrentForProbe(prev);
        }
    }

    /// <summary>设置变化时把新配置推给各数据源（系统状态模块的开关 / TTL 立即生效）</summary>
    public void ApplySourceSettings(Core.Settings s)
    {
        _settings = s;
        _sys?.Configure(s);
    }

    /// <summary>手动弹一条系统状态示例岛（设置页预览用）</summary>
    public void PreviewSysState(string signal)
    {
        object snap = signal switch
        {
            "unlock" => new Sources.UnlockSnapshot(720, DateTime.Now, IntPtr.Zero),
            "camera" => new Sources.CameraSnapshot(true, "Integrated Camera"),
            _ => (object)new Sources.SysSnapshot(signal, Sources.SysStateSource.IsOn(signal))
        };
        OnEvent(new Core.IslandEvent(
            Id: signal, SourceKey: "sys", Kind: signal == "unlock" ? "unlock" : signal == "camera" ? "camera" : "sys",
            Priority: 2,
            Title: signal switch
            {
                "unlock" => "已解锁",
                "camera" => "摄像头已启用",
                "numlock" => "数字键盘",
                "scrolllock" => "滚动锁定",
                _ => "大写锁定"
            },
            Subtitle: signal == "unlock" ? "进入桌面"
                : signal == "camera" ? "Integrated Camera"
                : ((Sources.SysSnapshot)snap).On ? "开" : "关",
            Payload: snap,
            Policy: new Core.IslandPolicy(TimeSpan.FromSeconds(8), Coalesce: true, Expandable: true))
        { AppKey = "sys" });
    }

    /// <summary>
    /// 预览模式：只显示一条示例岛，跟随几何设置实时变化，不播放生成/收起动画。
    /// 进入预览时清空现有岛；退出预览恢复真实岛。
    /// </summary>
    public void EnterPreview()
    {
        if (_previewMode) return;
        _previewMode = true;

        // 清空现有岛（立即移除，不做收起动画）
        foreach (var it in _stack.Items.ToList())
        {
            it.UnregisterAll();
            Stage.Children.Remove(it.Visual);
            _stack.Remove(it);
        }
        _stack.Clear();

        // 造 3 条示例岛：中间胶囊 + 左右各 1 球 —— 便于直观验证"是否在同一条直线上"
        var mainEv = new IslandEvent(
            Id: "preview", SourceKey: "preview", Kind: "media", Priority: 0,
            Title: "记得", Subtitle: "A-Mei Chang · Spotify")
        {
            AppKey = "Spotify.exe",
            SourceDisplayName = "Spotify",
            Payload = new Sources.MediaSnapshot("Spotify.exe", "Spotify", "记得", "A-Mei Chang",
                "album", true, 35, TimeSpan.FromSeconds(45).Ticks, TimeSpan.FromSeconds(240).Ticks)
        };
        var leftEv = new IslandEvent("preview-left", "chrome", "notification", 0,
            "Google Chrome", "www.example.com") { AppKey = "chrome" };
        var rightEv = new IslandEvent("preview-right", "QQ", "notification", 0,
            "QQ", "新消息") { AppKey = "QQ" };

        var items = new[] { CreateItem(mainEv), CreateItem(leftEv), CreateItem(rightEv) };
        _stack.Add(items[0]);          // 中间成为胶囊
        _stack.Add(items[1]);          // 球体
        _stack.Add(items[2]);          // 球体

        // 立即就位，不播放任何动画
        foreach (var it in items)
        {
            it.Width.Snap(it.MeasuredWidth);
            it.Height.Snap(IslandMetrics.IslandHeight);
            it.X.Snap(0);
            it.Y.Snap(IslandMetrics.TopMargin);
            it.Opacity.Snap(1);
            it.ScaleX.Snap(1);
            it.ScaleY.Snap(1);
            it.ContentOpacity.Snap(1);
        }
        items[0].Role = IslandRole.Pill;
        items[0].ContentKey = "pill";

        _lastLayoutSignature = null;
        ApplyLayout(force: true);

        // 让球体立刻到位（跳过弹簧）
        var map = _stack.ComputeSlots();
        foreach (var it in items)
        {
            if (!map.TryGetValue(it.Event.Id, out var slot)) continue;
            it.X.Snap(slot.X);
            it.Width.Snap(slot.Width);
            it.Height.Snap(slot.Height);
            it.Role = slot.Role;
            it.ContentKey = slot.Role == IslandRole.Sphere ? "sphere" : "pill";
        }

        EnsureVisible();
        App.LogInfo("Island", "进入预览模式（胶囊 + 左右球体）");
    }

    /// <summary>退出预览：移除示例岛，恢复真实显示</summary>
    public void ExitPreview()
    {
        if (!_previewMode) return;
        _previewMode = false;
        foreach (var it in _stack.Items.ToList())
        {
            it.UnregisterAll();
            Stage.Children.Remove(it.Visual);
            _stack.Remove(it);
        }
        _stack.Clear();
        _lastLayoutSignature = null;
        Hide();
        App.LogInfo("Island", "退出预览模式");
    }

    private bool _previewMode;

    /// <summary>兼容旧调用</summary>
    public void PreviewMedia() => EnterPreview();

    /// <summary>把当前主题应用到所有已存在的岛（深浅色切换时立即生效）</summary>
    private void ApplyVisualTheme()
    {
        var t = AppTheme.Current;
        foreach (var item in _stack.Items)
        {
            var b = item.Visual;
            b.Background = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new(((SolidColorBrush)t.IslandTop).Color, 0),
                    new(((SolidColorBrush)t.IslandBottom).Color, 1)
                }
            };
            b.BorderBrush = t.Stroke;
            if (b.Effect is System.Windows.Media.Effects.DropShadowEffect ds)
                ds.Opacity = t.ShadowOpacity;

            // 内容重建（文字/图标颜色来自 token）
            var content = BuildContent(item.Event);
            if (content != null && b.Child is ContentPresenter cp) cp.Content = content;
        }
        NeckPath.Fill = t.IslandTop;
        PlusBadge.BorderBrush = t.Stroke;
        PlusText.Foreground = t.TextSecondary;
    }

    /// <summary>就地更新一条已存在的岛（媒体刷新、通知合并等）</summary>
    private void UpdateItem(IslandItem item, IslandEvent ev)
    {
        if (item.IsDying) return;
        item.ReplaceEvent(ev);

        // 媒体：把当前曲目同步给歌词源（内部做了同名去重，不会重复请求）
        if (ev.Kind == "media" && ev.Payload is Sources.MediaSnapshot ms)
        {
            // 只有音乐播放器才查歌词（浏览器放视频不查，省流量也避免误匹配）
            if (Sources.MediaSourcePolicy.IsMusicPlayer(ms.SourceId))
            {
                int durMs = (int)(Math.Max(0, ms.EndTicks) / TimeSpan.TicksPerMillisecond);
                _lyrics.UpdateTrack(ms.Title, ms.Artist, ms.Album, durMs);
            }
        }

        // 【关键】媒体进度推送（约 900ms 一次）不要重建内容：
        // 重建会造出新的 TextBlock（空文本），而歌词"文本未变"就不再赋值，
        // 于是每行歌词显示约 900ms 后变空白 —— 表现为"消失时间固定且不正确"。
        // 只有曲目 / 播放态真正变化时才重建；进度由 RefreshMediaCard 每帧更新。
        bool needRebuild = true;
        if (ev.Kind == "media" && ev.Payload is Sources.MediaSnapshot msig)
        {
            var sig = msig.Title + "|" + msig.Artist + "|" + msig.IsPlaying;
            needRebuild = item.MediaSig != sig;
            item.MediaSig = sig;
        }

        if (needRebuild)
        {
            // 【关键】按当前形态重建：展开态必须继续用卡片模板，
            // 否则暂停事件会把进度条/控制按钮打回胶囊（表现为"暂停后按钮消失"）
            bool keepCard = item.ContentKey == "card";
            var content = keepCard ? BuildCard(ev) : BuildContent(ev);
            if (content != null && item.Visual.Child is ContentPresenter cp)
                cp.Content = content;
        }

        // 媒体暂停 → 启动自动收起计时
        if (ev.Kind == "media" && ev.Payload is Sources.MediaSnapshot snap)
        {
            // 暂停后是否收起由设置决定；关闭时保留暂停态显示
            item.HideAt = (snap.IsPlaying || !_settings.Media.HideWhenPaused)
                ? null
                : DateTime.Now.AddSeconds(Math.Max(3, _settings.Media.PausedHideSeconds));
        }

        // 宽度可能变化（歌名长度不同）
        item.MeasuredWidth = MeasurePillWidth(ev);
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
    }

    private IslandItem CreateItem(IslandEvent ev)
    {
        var content = BuildContent(ev);

        var tk = AppTheme.Current;
        var b = new Border
        {
            CornerRadius = new CornerRadius(IslandMetrics.IslandHeight / 2),
            Background = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1),
                GradientStops = new GradientStopCollection
                {
                    new(((SolidColorBrush)tk.IslandTop).Color, 0),
                    new(((SolidColorBrush)tk.IslandBottom).Color, 1)
                }
            },
            BorderBrush = tk.Stroke,
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            RenderTransformOrigin = new System.Windows.Point(0.5, 1.0),   // 底部锚定，向上吸附
        };
        b.Effect = new System.Windows.Media.Effects.DropShadowEffect
        { BlurRadius = 24, Opacity = tk.ShadowOpacity, ShadowDepth = 0, Direction = 270 };

        var scale = new ScaleTransform(1, 1);
        var tg = new TransformGroup();
        tg.Children.Add(scale);
        b.RenderTransform = tg;

        if (content != null)
            b.Child = new ContentPresenter { Content = content };
        b.MouseLeftButtonUp += (_, _) => OnItemClick(ev.Id);
        b.MouseEnter += (_, _) => OnItemEnter(ev.Id);
        b.MouseLeave += (_, _) => OnItemLeave(ev.Id);

        Stage.Children.Add(b);
        _visuals[ev.Id] = b;

        var item = new IslandItem(ev, b);
        item.MeasuredWidth = MeasurePillWidth(ev);
        return item;
    }

    /// <summary>
    /// 详情卡右上角的关闭按钮（所有卡片共用）。
    /// 悬浮自动展开和点击展开这两种来源的卡片都能一键收起；
    /// 关闭后同一条岛在本次悬停中不再自动展开（鼠标移开再回来才会恢复）。
    /// </summary>
    private Border BuildCloseButton(ThemeTokens t)
    {
        var close = new Border
        {
            Width = 26, Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 10, 0),   // 与卡片 16/14 内边距对齐
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "关闭（本次悬停不再自动展开）",
            Child = Native.SvgIcons.CreateCentered("close", 14, t.TextSecondary, 1.25)
        };
        close.MouseEnter += (_, _) => close.Background = t.RowSurface;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;               // 不要冒泡到"切换当前岛"
            _lastCardAction = DateTime.Now;
            CloseExpandedCard();
        };
        return close;
    }

    /// <summary>
    /// 把卡片内容包一层，右上角叠放关闭按钮。
    /// 卡片 body 自带 16,14,16,14 的内边距，这里不再重复加边距，
    /// 只把关闭按钮按同样的 inset 摆到右上角。
    /// </summary>
    /// <summary>展开卡的可用内容宽度（去掉圆角裁切影响，按 CardWidth 计）</summary>
    private double cwForCard() => IslandMetrics.CardWidth;

    /// <summary>
    /// 在与真实布局相同的宽度下离屏 Measure 一张详情卡，返回它想要的高度。
    /// 离屏容器宽高都设为 double.PositiveInfinity，让 WPF 按内容自然撑开。
    /// 夹在 [CardMinHeight, CardMaxHeight]，避免极端内容把卡撑爆或塌成一坨。
    /// </summary>
        private static double MeasureCardContent(FrameworkElement content, double width)
    {
        try
        {
            // 量【整棵内容树】（含外层 Grid/Border），不要绕过 ScrollViewer：
            // 歌词区靠 ScrollViewer.Height 撑开（0 或 220），绕过它就测不到那 220，
            // 卡片高度会永远停在"无歌词"的 174 → 歌词区被裁、进度条按钮错位。
            // 【必须 InvalidateMeasure】：WPF 对已测量过的元素不会重新测量，
            // 改了子元素 Height 后不失效就仍返回旧值。
            InvalidateTree(content);

            var host = new Border
            {
                Width = Math.Max(1, width),
                Height = double.PositiveInfinity,
                Child = content
            };
            host.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
            var h = host.DesiredSize.Height;

            double scale = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
            return double.IsNaN(h) || double.IsInfinity(h) || h <= 0
                ? 0
                : Math.Clamp(h * scale,
                             IslandMetrics.CardMinHeight * scale,
                             IslandMetrics.CardMaxHeight * scale);
        }
        catch (Exception) { return 0; }
    }


    private FrameworkElement WrapWithClose(FrameworkElement body)
    {
        // 【自适应高度 + 上限 + 滚动条】
        // 卡片高度由内容实测决定（自适应），但上限受 CardMaxHeight 约束；
        // 超出上限时用 ScrollViewer 滚动，而不是让 Border 把内容裁掉
        // —— 之前通知卡底部按钮被裁、歌词看不见，就是硬裁切造成的。
        // 滚动条：默认 Hidden（不占宽度、不显示），只在鼠标进入卡片时才细显示。
        // 之前用 Auto，Windows 默认滚动条又粗又显眼，横在卡片里很难看。
        // 【不套外层 ScrollViewer】
        // 给整张卡片套 ScrollViewer 会造成「两层滚动嵌套」：
        // 外层（卡片）与内层（歌词区）争抢滚轮，表现为"只能下滚、上滚没反应"。
        // 现在卡片高度按屏幕动态上限自适应，内容超限时由内层（歌词区）自己滚。
        var host = new Grid();
        host.Children.Add(body);
        host.Children.Add(BuildCloseButton(AppTheme.Current));
        return host;
    }

    /// <summary>
    /// 按内容实测自适应高度：
    /// 内容 <= 上限 → 卡片随内容生长；内容 > 上限 → 卡在上限，内部滚动。
    /// </summary>
    private double AdaptiveCardHeight(FrameworkElement content, double width)
    {
        double h = MeasureCardContent(content, width);   // 已夹在 [Min, Max] 内
        return h;
    }

    /// <summary>
    /// 详情卡（展开态）。
    /// 无论来自"悬浮自动展开"还是"点击展开"，都在右上角挂同一个关闭按钮。
    /// </summary>
    private FrameworkElement? BuildCard(IslandEvent ev)
    {
        _cardParts = null;                 // 旧引用作废，避免更新已移除的元素
        _cardLyricSig = "";
        _cardLyricText = null;             // 歌词引用作废（卡片会被重建）
        _cardLyricPanel = null;
        _cardLyricClip = null;
        _cardScrollHost = null;
        _cardLyricSig = "";
        var body = ev.Kind switch
        {
            "media" when ev.Payload is Sources.MediaSnapshot snap => BuildMediaCard(snap),
            "sys" when ev.Payload is Sources.SysSnapshot snap => BuildSysCard(snap),
            "camera" when ev.Payload is Sources.CameraSnapshot cam => BuildCameraCard(cam),
            "unlock" when ev.Payload is Sources.UnlockSnapshot snap => BuildUnlockCard(snap),
            _ => BuildNotificationCard(ev)
        };
        return body == null ? null : WrapWithClose(body);
    }

    /// <summary>键位详情卡（docs §5.5）：Lucide 键帽 + 名称 + [稍后提醒][忽略][键盘设置]</summary>
    private FrameworkElement BuildSysCard(Sources.SysSnapshot snap)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        // 标题行：键帽图标 + 名称 + 存活提示
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Child = Native.SvgIcons.CreateKeycap(SignalIcon(snap.Signal), 26, snap.On, t)
        });
        var hi = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hi.Children.Add(new TextBlock
        { Text = SignalName(snap.Signal), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = t.TextPrimary });
        hi.Children.Add(new TextBlock
        { Text = $"状态快照 · {Math.Max(2, _settings.Sys.TtlSeconds)} s 后自动收起", FontSize = 11, Foreground = t.TextTertiary });
        head.Children.Add(hi);
        root.Children.Add(head);

        // 键位矩阵：三个键的实时开/关
        var matrix = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var sig in new[] { "capslock", "numlock", "scrolllock" })
        {
            bool on = Sources.SysStateSource.IsOn(sig);
            var tile = new Border
            {
                Width = 104, Height = 60, CornerRadius = new CornerRadius(13),
                Background = t.RowSurface, BorderBrush = on ? t.Accent : t.Stroke,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, sig == "scrolllock" ? 0 : 8, 0)
            };
            var sp = new StackPanel { Margin = new Thickness(11, 9, 11, 9) };
            sp.Children.Add(new Border
            {
                Height = 20,
                Child = Native.SvgIcons.CreateKeycap(SignalIcon(sig), 20, on, t)
            });
            var vr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            vr.Children.Add(new Ellipse
            { Width = 7, Height = 7, Fill = on ? t.Success : t.TextTertiary, VerticalAlignment = VerticalAlignment.Center });
            vr.Children.Add(new TextBlock
            {
                Text = on ? "开" : "关", FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = on ? t.TextPrimary : t.TextTertiary, Margin = new Thickness(6, 0, 0, 0)
            });
            sp.Children.Add(vr);
            tile.Child = sp;
            matrix.Children.Add(tile);
        }
        root.Children.Add(matrix);

        // 按钮行
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        string sig0 = snap.Signal;
        bool sigOn = snap.On;

        acts.Children.Add(MakeTextButton("稍后提醒", "bell", t, false, () =>
        {
            Sources.SysStateSource.SnoozeValue(sig0, sigOn);   // 120s 内同值不再弹
            DismissActive();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", "bell-off", t, false, () =>
        {
            Sources.SysStateSource.IgnoreValue(sig0, sigOn);    // 值级忽略，60s 过期
            DismissActive();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("键盘设置", "settings", t, true, async () =>
        {
            DismissActive();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:easeofaccesskeyboard")
                { UseShellExecute = true });
            }
            catch (Exception ex) { App.Log("Sys", ex); }
            await Task.CompletedTask;
        }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>摄像头详情卡（docs §5.6）：隐私提醒 + [打开相机权限设置][忽略]</summary>
    private FrameworkElement BuildCameraCard(Sources.CameraSnapshot snap)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
            Background = snap.Busy ? t.Success : t.RowSurface,
            Margin = new Thickness(0, 0, 11, 0),
            Child = new Grid { Children =
            {
                Native.SvgIcons.CreateCentered(snap.Busy ? "camera" : "camera-off", 19,
                    snap.Busy ? Brushes.White : t.TextSecondary, 1.15)
            } }
        });
        var hi = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hi.Children.Add(new TextBlock
        { Text = snap.Busy ? "摄像头已启用" : "摄像头已关闭", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = t.TextPrimary });
        hi.Children.Add(new TextBlock
        {
            Text = snap.Busy ? "有程序正在使用你的摄像头" : "已不再被任何程序占用",
            FontSize = 12, Foreground = t.TextSecondary, Margin = new Thickness(0, 3, 0, 0)
        });
        head.Children.Add(hi);
        root.Children.Add(head);

        // 设备条：图标 + 设备名 + "正在录制"提示
        var devRow = new StackPanel { Orientation = Orientation.Horizontal };
        devRow.Children.Add(new Border
        {
            Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Native.SvgIcons.CreateCentered("monitor", 16, t.TextSecondary, 1.15)
        });
        devRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(snap.DeviceName) ? "摄像头" : snap.DeviceName,
            FontSize = 11.5, Foreground = t.TextSecondary, VerticalAlignment = VerticalAlignment.Center
        });
        if (snap.Busy)
        {
            devRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(7), Background = t.RowSurface,
                BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
                Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(7, 1, 7, 1),
                Child = new TextBlock
                { Text = "正在录制", FontSize = 10, Foreground = t.Warning }
            });
        }
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = t.RowSurface,
            BorderBrush = t.Stroke, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(11, 8, 11, 8),
            Child = devRow
        });

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("稍后提醒", "bell", t, false, () =>
        {
            Sources.SysStateSource.SnoozeValue("camera", snap.Busy);
            DismissActive();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", "bell-off", t, false, () =>
        {
            Sources.SysStateSource.IgnoreValue("camera", snap.Busy);
            DismissActive();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("相机权限", "settings", t, true, async () =>
        {
            DismissActive();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-webcam")
                { UseShellExecute = true });
            }
            catch (Exception ex) { App.Log("Sys", ex); }
            await Task.CompletedTask;
        }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>解锁详情卡（docs §5.5）：欢迎回来 + 离开时长 + [快速聚焦上次应用][忽略]</summary>
    private FrameworkElement BuildUnlockCard(Sources.UnlockSnapshot snap)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "\u26B6", FontSize = 30, Foreground = t.Success,
            VerticalAlignment = VerticalAlignment.Center
        });
        var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        string away = snap.AwaySeconds > 60
            ? $"{(int)snap.AwaySeconds / 60} 分 {snap.AwaySeconds % 60} 秒"
            : $"{snap.AwaySeconds} 秒";
        info.Children.Add(new TextBlock { Text = "欢迎回来", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = t.TextPrimary });
        info.Children.Add(new TextBlock { Text = $"离开 {away} · {snap.At:HH:mm}", FontSize = 12, Foreground = t.TextSecondary, Margin = new Thickness(0, 3, 0, 0) });
        row.Children.Add(info);
        root.Children.Add(row);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        acts.Children.Add(MakeTextButton("快速聚焦上次应用", t, true, async () =>
        {
            bool ok = Sources.SysStateSource.FocusLastWindow(snap.LastForegroundHwnd);
            if (!ok) App.LogInfo("Sys", "快速聚焦失败（上次窗口可能已关闭）");
            DismissActive();
            await Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("忽略", t, false, () => { DismissActive(); return Task.CompletedTask; }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>媒体详情卡：封面 + 歌名 + 进度条 + ⏮ ⏯ ⏭ + 来源</summary>
    private FrameworkElement BuildMediaCard(Sources.MediaSnapshot snap)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        _cardParts = new MediaCardParts();

        // 顶部：封面 + 左侧元信息 + 右侧歌词（借用标题右侧空白区，不占额外高度）
        // 布局： [封面] 标题/作者/来源  |  当前行歌词（超出则自动滚动）
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 封面
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 标题列
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 歌词列

        var coverHost = new Grid { Width = 56, Height = 56, Margin = new Thickness(0, 0, 12, 0) };
        var ic = Native.AppIconExtractor.GetIcon(snap.SourceId);
        if (ic != null)
            coverHost.Children.Add(new Image
            {
                Source = ic, Width = 56, Height = 56, Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, 56, 56), 12, 12)
            });
        else
            coverHost.Children.Add(new Border
            { Width = 56, Height = 56, CornerRadius = new CornerRadius(12), Background = t.AccentGradient });
        Grid.SetColumn(coverHost, 0);
        top.Children.Add(coverHost);

        var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(new TextBlock
        {
            Text = snap.Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = t.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(snap.Artist))
            meta.Children.Add(new TextBlock
            { Text = snap.Artist, FontSize = 12, Foreground = t.TextSecondary, Margin = new Thickness(0, 2, 0, 0) });
        meta.Children.Add(new TextBlock
        { Text = snap.SourceName, FontSize = 11, Foreground = t.TextTertiary, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(meta, 1);
        top.Children.Add(meta);

        // ---- 歌词列（标题右侧，显示 2~3 行，当前行高亮）----
        // 利用标题右侧原本的空白，不增加卡片高度，因此永远不会被裁。
        // 超出宽度时：先按行截断（TextTrimming），必要时整行水平滚动。
        var lyWrap = new Grid
        {
            Margin = new Thickness(24, 0, 4, 0),        // 与标题拉开距离（之前太靠左）
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 210                              // 更宽的歌词区
        };
        lyWrap.ClipToBounds = true;
        var lyStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // 当前行（高亮）
        var lyCur = new TextBlock
        {
            FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = t.TextPrimary,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        // 上下文行（淡化）
        var lyNext = new TextBlock
        {
            FontSize = 11.5, Foreground = t.TextTertiary, Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var lyPrev = new TextBlock
        {
            FontSize = 11.5, Foreground = t.TextTertiary, Margin = new Thickness(0, 0, 0, 2),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        lyStack.Children.Add(lyPrev);
        lyStack.Children.Add(lyCur);
        lyStack.Children.Add(lyNext);
        lyWrap.Children.Add(lyStack);
        _cardLyricPrev = lyPrev;
        _cardLyricText = lyCur;
        _cardLyricNext = lyNext;
        _cardLyricPanel = lyStack;
        _cardLyricClip = lyWrap;
        Grid.SetColumn(lyWrap, 2);
        top.Children.Add(lyWrap);

        root.Children.Add(top);

        // 进度条：支持拖动 seek。
        // 命中区比视觉高度高（12px），符合"细进度条也要好点"的手感；
        // 不支持 seek 的会话（直播 / 电台 / 部分播放器）直接禁用，视觉上变灰且光标不变手型。
        bool canSeek = _media?.CanSeek(snap.SourceId) ?? false;
        double trackW = s_progressTrackWidth;
        var track = new Border
        {
            Height = 12, Background = Brushes.Transparent, Cursor =
                canSeek ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow,
            Margin = new Thickness(0, 8, 0, 0),
            IsHitTestVisible = canSeek
        };
        var trackInner = new Grid();          // 4px 视觉条居中
        var rail = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center,
            Background = t.RowSurface
        };
        var fill = new Border
        {
            Height = 4, CornerRadius = new CornerRadius(2), Background = t.AccentGradient,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(2, trackW * snap.ProgressPercent / 100.0)
        };
        var knob = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
            Background = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-5, 0, 0, 0), Opacity = 0
        };
        trackInner.Children.Add(rail);
        trackInner.Children.Add(fill);
        trackInner.Children.Add(knob);
        track.Child = trackInner;
        _cardParts!.ProgressFill = fill;
        _cardParts.Knob = knob;
        _cardParts.TrackWidth = trackW;
        _cardParts.CanSeek = canSeek;

        if (canSeek)
        {
            AttachScrub(track, trackW, snap.SourceId);
        }
        else
        {
            // 不支持跳转（直播 / 电台 / 部分播放器）：进度条只读，用次级色区分
            fill.Background = t.TextTertiary;
            knob.Visibility = Visibility.Collapsed;
        }
        root.Children.Add(track);

        // 时间轴
        var tl = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        tl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pos = TimeSpan.FromTicks(Math.Max(0, snap.PositionTicks));
        var end = TimeSpan.FromTicks(Math.Max(0, snap.EndTicks));
        var left = new TextBlock
        { Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:00}", FontSize = 11, Foreground = t.TextSecondary };
        var right = new TextBlock
        {
            Text = end > TimeSpan.Zero ? $"-{(int)(end - pos).TotalMinutes}:{(end - pos).Seconds:00}" : "直播",
            FontSize = 11, Foreground = t.TextSecondary
        };
        Grid.SetColumn(right, 1);
        tl.Children.Add(left); tl.Children.Add(right);
        root.Children.Add(tl);
        _cardParts!.PosText = left;
        _cardParts.RemainText = right;
        _cardParts.Track = track;

        // 控制按钮 ⏮ ⏯ ⏭
        var ctl = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        // 控制按钮必须绑定到"这条岛对应的会话"，否则会误控其它播放器
        var sid = snap.SourceId;
        ctl.Children.Add(MakeIconControlButton("prev", () => _media?.PrevAsync(sid), t, 34));
        var playBtn = MakeIconControlButton(snap.IsPlaying ? "pause" : "play",
            () => _media?.ToggleAsync(sid), t, 42, true);
        ctl.Children.Add(playBtn);
        ctl.Children.Add(MakeIconControlButton("next", () => _media?.NextAsync(sid), t, 34));
        root.Children.Add(ctl);

        _cardParts!.PlayGlyphHost = playBtn.Child as Border;
        _cardParts.PlayGlyphName = snap.IsPlaying ? "pause" : "play";
        return root;
    }

    /// <summary>
    /// 给进度条挂上拖动 seek。
    /// 拖动中：立即把填充宽度和圆点移到指针位置并暂停轮询回写（Scrubbing），
    ///         松手才真正下发 TrySetPlaybackPosition —— 避免"边拖边被服务器位置弹回"。
    /// 拖动时不置 IsHitTestVisible=false，否则一按下就丢失捕获导致无法继续拖。
    /// </summary>
    private void AttachScrub(Border track, double trackW, string sourceId)
    {
        var parts = _cardParts;
        if (parts == null) return;

        void ApplyRatio(double ratio)
        {
            ratio = Math.Clamp(ratio, 0, 1);
            double w = Math.Max(2, trackW * ratio);
            if (parts.ProgressFill != null) parts.ProgressFill.Width = w;
            if (parts.Knob != null) parts.Knob.Margin = new Thickness(w - 5, 0, 0, 0);
        }

        double RatioAt(Point p)
        {
            // 命中区比视觉轨道左右各宽 5px，按轨道宽度换算比例并夹紧
            if (trackW <= 0) return 0;
            double x = p.X - 5.0;
            return Math.Clamp(x / trackW, 0, 1);
        }

        track.MouseLeftButtonDown += (_, e) =>
        {
            _lastCardAction = DateTime.Now;         // 屏蔽"切换当前岛"的冒泡
            e.Handled = true;
            if (parts.Knob != null) parts.Knob.Opacity = 1;
            parts.Scrubbing = true;
            double r = RatioAt(e.GetPosition(track));
            ApplyRatio(r);
            parts.PendingRatio = r;
            track.CaptureMouse();
        };

        track.MouseMove += (_, e) =>
        {
            if (!parts.Scrubbing) return;
            e.Handled = true;
            double r = RatioAt(e.GetPosition(track));
            ApplyRatio(r);
            parts.PendingRatio = r;
            UpdateScrubTimeLabels(r);
        };

        track.MouseLeftButtonUp += async (_, e) =>
        {
            if (!parts.Scrubbing) return;
            e.Handled = true;
            parts.Scrubbing = false;
            if (parts.Knob != null) parts.Knob.Opacity = 0;
            try { track.ReleaseMouseCapture(); } catch (Exception) { /* 已释放 */ }

            double target = parts.PendingRatio;
            parts.PendingRatio = -1;
            if (target < 0) return;
            try
            {
                bool ok = await _media!.SeekAsync(sourceId, target);
                if (!ok) ApplyRatio(parts.ProgressFill?.Width / trackW ?? 0);  // 失败则回滚
            }
            catch (Exception ex) { App.Log("Media", ex); }
        };

        // 拖出轨道外松手也要结束拖动，避免圆点一直挂着
        track.LostMouseCapture += (_, _) =>
        {
            if (!parts.Scrubbing) return;
            parts.Scrubbing = false;
            if (parts.Knob != null) parts.Knob.Opacity = 0;
        };
    }

    /// <summary>拖动过程中同步刷新左侧"已播放"与右侧"剩余"时间文本</summary>
    private void UpdateScrubTimeLabels(double ratio)
    {
        var parts = _cardParts;
        if (parts == null) return;
        var activeItem = _stack.Active;
        var snap = _media?.GetCurrent(activeItem?.Event.AppKey ?? activeItem?.Event.SourceKey);
        double totalSec = snap != null ? Math.Max(0, snap.EndTicks) / (double)TimeSpan.TicksPerSecond : 0;
        if (totalSec <= 0) return;
        double pos = totalSec * Math.Clamp(ratio, 0, 1);
        if (parts.PosText != null)
            parts.PosText.Text = $"{(int)pos / 60}:{(int)pos % 60:00}";
        if (parts.RemainText != null)
        {
            double rem = Math.Max(0, totalSec - pos);
            parts.RemainText.Text = $"-{(int)rem / 60}:{(int)rem % 60:00}";
        }
    }

    /// <summary>
    /// Lucide 图标版圆形控制按钮（媒体 ⏮ ⏯ ⏭ 用）。
    ///
    /// 图标尺寸按"按钮直径"而不是 viewBox 比例来定，保证在 34 / 42 两种尺寸下
    /// 三角形、竖条都清晰可辨（之前用 size*0.40 太小，实心三角几乎看不见）。
    /// 颜色全部取自 ThemeTokens，浅色 / 深色模式各自适配：
    ///   主按钮 = 强调色渐变底 + 白图标；次按钮 = 透明底 + 主文字色（深浅都有足够对比）
    /// </summary>
    private Border MakeIconControlButton(string icon, Func<Task> action, ThemeTokens t,
        double size, bool primary = false)
    {
        // Lucide viewBox 24 → 目标图标边长；实心图形（play/pause）可再放大一点
        double iconSize = size * (primary ? 0.52 : 0.56);
        var fg = primary ? Brushes.White : t.TextPrimary;

        var btn = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Margin = new Thickness(10, 0, 10, 0),
            Background = primary ? (Brush)t.AccentGradient : Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = Native.SvgIcons.CreateCentered(icon, iconSize, fg, primary ? 1.3 : 1.2)
        };
        btn.MouseEnter += (_, _) => btn.Background = primary
            ? (Brush)t.AccentGradient : t.RowSurface;
        btn.MouseLeave += (_, _) => btn.Background = primary
            ? (Brush)t.AccentGradient : Brushes.Transparent;
        btn.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;                       // 不要触发"切换当前岛"
            _lastCardAction = DateTime.Now;         // 屏蔽冒泡
            try { await action(); } catch (Exception ex) { App.Log("Media", ex); }
        };
        return btn;
    }

    private Border MakeControlButton(string glyph, Func<Task> action, ThemeTokens t,
        double size, bool primary = false)
    {
        var btn = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Margin = new Thickness(10, 0, 10, 0),
            Background = primary ? t.AccentGradient : Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = primary ? 15 : 13,
                Foreground = primary ? Brushes.White : t.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        btn.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;                       // 不要触发"切换当前岛"
            _lastCardAction = DateTime.Now;         // 屏蔽冒泡
            try { await action(); } catch (Exception ex) { App.Log("Media", ex); }
        };
        return btn;
    }

    /// <summary>
    /// 球体内容：36px 正圆内仅居中显示 18px 应用图标（docs §2.4）。
    /// 关键：外层容器必须是固定尺寸且不参与拉伸（Border.Child 默认拉伸会导致图标溢出）。
    /// </summary>
    private static FrameworkElement BuildSphere(IslandEvent ev)
    {
        double size = IslandMetrics.SphereSize;      // 36
        double iconSize = 18;

        // 外层用 Stretch 填满球体容器，内部元素各自居中
        var wrap = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true
        };

        // 系统状态球体：显示 Lucide 键帽 / 状态图标，而非应用图标（docs §5.5 / §5.6）
        if (ev.Kind == "sys" && ev.Payload is Sources.SysSnapshot ss)
            return Native.SvgIcons.CreateSphere(SignalIcon(ss.Signal), 19,
                ss.On ? AppTheme.Current.Accent : AppTheme.Current.TextSecondary);

        if (ev.Kind == "camera" && ev.Payload is Sources.CameraSnapshot cs)
            return Native.SvgIcons.CreateSphere(cs.Busy ? "camera" : "camera-off", 19,
                cs.Busy ? AppTheme.Current.Success : AppTheme.Current.TextSecondary);

        if (ev.Kind == "unlock")
            return Native.SvgIcons.CreateSphere("unlock", 19, AppTheme.Current.Success);

        var icon = Native.AppIconExtractor.GetIcon(ev.AppKey ?? ev.SourceKey);
        if (icon != null)
        {
            var img = new Image
            {
                Source = icon,
                Width = iconSize, Height = iconSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 用 Border 做圆角裁剪（Image 的 Clip 在某些情况下不生效）
            var clip = new Border
            {
                Width = iconSize, Height = iconSize,
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = img,
                ClipToBounds = true
            };
            wrap.Children.Add(clip);
        }
        else
        {
            wrap.Children.Add(new Border
            {
                Width = iconSize, Height = iconSize,
                CornerRadius = new CornerRadius(5),
                Background = AppTheme.Current.AccentGradient,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        return wrap;
    }

    /// <summary>通知详情卡</summary>
    private FrameworkElement BuildNotificationCard(IslandEvent ev)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        { Child = BuildAppIcon(ev.AppKey ?? ev.SourceKey, 32, 9), Margin = new Thickness(0, 0, 10, 0) });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = Sources.MediaSourcePolicy.DisplayName(ev.AppKey ?? ev.SourceKey),
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = t.TextPrimary
        });
        info.Children.Add(new TextBlock
        { Text = ev.CreatedAt.ToString("HH:mm"), FontSize = 11, Foreground = t.TextSecondary });
        head.Children.Add(info);
        root.Children.Add(head);

        root.Children.Add(new TextBlock
        {
            Text = ev.Title, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = t.TextPrimary, Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(ev.Subtitle))
            root.Children.Add(new TextBlock
            {
                Text = ev.Subtitle, FontSize = 12.5, Foreground = t.TextSecondary,
                Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
                MaxHeight = 60
            });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

        // "打开"按钮：仅当该应用支持 COM 激活时才显示（与 Windows 原生通知一致）。
        // 不支持的应用（如 Chrome 类未注册 CustomActivator 的）不显示按钮，避免点了没反应。
        bool canOpen = Native.AppLauncher.CanActivateByCom(ev.SourceAumid ?? "");
        if (canOpen)
        {
            actions.Children.Add(MakeTextButton("打开", t, true, () =>
            {
                LaunchSourceApp(ev);
                DismissActive();
                return Task.CompletedTask;
            }));
        }
        else
        {
            App.LogInfo("Island", $"「{ev.SourceDisplayName ?? ev.SourceKey}」不支持 COM 激活，隐藏「打开」按钮");
        }

        // "忽略"：仅从岛上移除（系统那条早已被移除）
        actions.Children.Add(MakeTextButton("忽略", t, canOpen ? false : true, () =>
        {
            DismissActive();
            return Task.CompletedTask;
        }));
        root.Children.Add(actions);
        return root;
    }

    private Border MakeTextButton(string text, ThemeTokens t, bool primary, Func<Task> action)
        => MakeTextButton(text, null, t, primary, action);

    /// <summary>带 Lucide 图标的文字按钮（icon 为 null 时退化为纯文字）</summary>
    private Border MakeTextButton(string text, string? icon, ThemeTokens t, bool primary, Func<Task> action)
    {
        var fg = primary ? Brushes.White : t.TextPrimary;
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!string.IsNullOrEmpty(icon))
        {
            content.Children.Add(new Border
            {
                Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0),
                Child = Native.SvgIcons.CreateCentered(icon, 14, fg, 1.1)
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Foreground = fg,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal
        });

        var b = new Border
        {
            Height = 28, CornerRadius = new CornerRadius(14),
            Padding = new Thickness(13, 0, 13, 0),
            Margin = new Thickness(0, 0, 8, 0),
            Background = primary ? t.AccentGradient : t.RowSurface,
            BorderBrush = primary ? null : t.Stroke,
            BorderThickness = primary ? new Thickness(0) : new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = content
        };
        b.MouseEnter += (_, _) => b.Background = primary
            ? t.AccentGradient
            : new SolidColorBrush(Color.FromArgb(38, 106, 162, 255));
        b.MouseLeave += (_, _) => b.Background = primary ? t.AccentGradient : t.RowSurface;
        b.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            _lastCardAction = DateTime.Now;
            try { await action(); } catch (Exception ex) { App.Log("Island", ex); }
        };
        return b;
    }

    /// <summary>
    /// 打开通知的来源应用。
    /// 优先用 AUMID 解析（Windows 通知的 AUMID 常形如
    /// "{GUID}\WindowsPowerShell\v1.0\powershell.exe"，直接就是 exe 路径）。
    /// </summary>
    /// <summary>
    /// 打开通知的来源应用 —— 只用 COM 激活（与 Windows 原生通知同一机制）。
    /// 不支持 COM 的应用（如 Chrome）不猜测 exe，直接提示失败。
    /// </summary>
    private static void LaunchSourceApp(IslandEvent ev)
    {
        var aumid = ev.SourceAumid ?? "";
        var displayName = ev.SourceDisplayName ?? ev.AppKey ?? ev.SourceKey;

        if (Native.AppLauncher.ActivateComOnly(aumid, out var reason))
        {
            App.LogInfo("Island", $"已打开来源应用：{displayName}");
            return;
        }

        App.LogInfo("Island", $"打开失败（{displayName}）：{reason}");
        NotifyOpenFailed(displayName, reason);
    }

    /// <summary>提示无法打开（用岛自身弹一条提示，不弹系统框）</summary>
    private static void NotifyOpenFailed(string appName, string reason)
    {
        try
        {
            var main = Application.Current.Windows.OfType<IslandWindow>().FirstOrDefault();
            main?.DebugPush("ChronoIsland", "无法打开应用",
                $"{appName}：{reason}");
        }
        catch { }
    }

    /// <summary>尝试启动一个可执行文件或 shell 目标</summary>
    private static bool TryStart(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target, UseShellExecute = true
            });
            App.LogInfo("Island", $"已启动：{target}");
            return true;
        }
        catch (Exception ex)
        {
            App.LogInfo("Island", $"启动失败 {target}：{ex.Message}");
            return false;
        }
    }

    /// <summary>推测可能的进程名（AUMID 尾段 / appKey / 显示名映射）</summary>
    private static IEnumerable<string> CandidateProcessNames(string aumid, string appKey, string displayName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // AUMID 直接映射（Chrome → chrome 等）
        foreach (var c in AumidCandidates(aumid))
            if (!c.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && seen.Add(c))
                yield return c;

        // AUMID 尾段（去掉 .exe）
        if (!string.IsNullOrEmpty(aumid))
        {
            var tail = aumid.Split('\\', '!').LastOrDefault() ?? "";
            tail = System.IO.Path.GetFileNameWithoutExtension(tail);
            if (!string.IsNullOrWhiteSpace(tail) && seen.Add(tail)) yield return tail;
        }

        if (!string.IsNullOrWhiteSpace(appKey) && seen.Add(appKey)) yield return appKey;

        // 显示名 → 进程名映射（覆盖国内常用 IM）
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["QQ"] = new[] { "QQ" },
            ["腾讯QQ"] = new[] { "QQ" },
            ["微信"] = new[] { "WeChat", "Weixin" },
            ["WeChat"] = new[] { "WeChat", "Weixin" },
            ["企业微信"] = new[] { "WXWork" },
            ["WeCom"] = new[] { "WXWork" },
            ["钉钉"] = new[] { "DingTalk" },
            ["DingTalk"] = new[] { "DingTalk" },
            ["飞书"] = new[] { "Feishu", "Lark" },
            ["Feishu"] = new[] { "Feishu", "Lark" },
            ["Lark"] = new[] { "Feishu", "Lark" },
            ["Telegram"] = new[] { "Telegram" },
            ["Discord"] = new[] { "Discord" },
            ["Slack"] = new[] { "slack" },
            ["Spotify"] = new[] { "Spotify" },
            ["网易云音乐"] = new[] { "cloudmusic" },
            ["QQ音乐"] = new[] { "QQMusic" }
        };
        foreach (var kv in map)
        {
            if (displayName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                appKey.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var n in kv.Value)
                    if (seen.Add(n)) yield return n;
            }
        }

        // 显示名本身可能就是进程名
        if (!string.IsNullOrWhiteSpace(displayName) && seen.Add(displayName)) yield return displayName;
    }

    /// <summary>
    /// 从 AUMID 解析可启动的目标。Windows 通知的 AUMID 有三种形态：
    ///   ① 绝对路径： "D:/App/app.exe"
    ///   ② GUID 前缀："{GUID}\WindowsPowerShell\v1.0\powershell.exe"（相对 Windows 目录）
    ///   ③ 纯标识符："Chrome" / "Windows.Defender.SecurityCenter" / "Package!AppId"
    /// 这里只负责 ①②；③ 交给 CandidateProcessNames 按名字匹配进程。
    /// </summary>
    private static string? ExtractExePath(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;

        var idx = aumid.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return null;

        var raw = aumid[..(idx + 4)];

        // 形态 ②：去掉开头的 {GUID}        if (raw.StartsWith("{"))
        {
            var close = raw.IndexOf('}');
            if (close > 0 && close + 1 < raw.Length)
                raw = raw[(close + 1)..].TrimStart('\\');
        }

        // 已是绝对路径（含盘符或 UNC）→ 直接返回
        if (System.IO.Path.IsPathRooted(raw)) return raw.Replace('/', '\\');

        // 相对路径 → 相对 Windows 目录补全
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var full = System.IO.Path.Combine(win, raw);
        if (System.IO.File.Exists(full)) return full;

        // 也试试 System32 与 Program Files
        foreach (var b in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            var c = System.IO.Path.Combine(b, System.IO.Path.GetFileName(raw));
            if (System.IO.File.Exists(c)) return c;
        }

        return raw;      // 交给调用方做存在性检查
    }

    /// <summary>形态 ③：把 AUMID 映射为可能的应用标识（供 shell:AppsFolder 或进程匹配）</summary>
    private static IEnumerable<string> AumidCandidates(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) yield break;

        // "Chrome" → "chrome"
        var lower = aumid.ToLowerInvariant();
        if (lower == "chrome" || lower.Contains("chrome")) { yield return "chrome"; }
        if (lower.Contains("msedge") || lower == "edge") yield return "msedge";
        if (lower.Contains("firefox")) yield return "firefox";
        if (lower.Contains("explorer")) yield return "explorer";

        // UWP：直接给出 shell:AppsFolder 目标
        if (aumid.Contains('!')) yield return "shell:AppsFolder\\" + aumid;
    }

    private void DismissActive()
    {
        var a = _stack.Active;
        if (a != null && !a.IsDying) Dismiss(a);
    }

    /// <summary>按事件类型构建胶囊内容</summary>
    private FrameworkElement? BuildContent(IslandEvent ev) => ev.Kind switch
    {
        "media" when ev.Payload is Sources.MediaSnapshot snap => BuildMediaPill(snap),
        "clock" => BuildClockPill(ev),
        "usage" => BuildUsagePill(ev),
        "sys" when ev.Payload is Sources.SysSnapshot snap => BuildSysPill(snap, ev),
        "camera" when ev.Payload is Sources.CameraSnapshot cam => BuildCameraPill(cam),
        "unlock" => BuildUnlockPill(ev),
        _ => BuildNotificationPill(ev)
    };

    /// <summary>键位 / 摄像头胶囊（docs §5.5）：Lucide 键帽图标 + 名称 + 开/关</summary>
    private static FrameworkElement BuildSysPill(Sources.SysSnapshot snap, IslandEvent ev)
        => BuildTogglePill(SignalIcon(snap.Signal), SignalName(snap.Signal), snap.On);

    /// <summary>摄像头胶囊（docs §5.6）</summary>
    private static FrameworkElement BuildCameraPill(Sources.CameraSnapshot snap)
        => BuildTogglePill(snap.Busy ? "camera" : "camera-off", "摄像头", snap.Busy,
            snap.Busy ? AppTheme.Current.Success : AppTheme.Current.TextSecondary,
            string.IsNullOrWhiteSpace(snap.DeviceName) ? null : snap.DeviceName);

    /// <summary>键位信号 → Lucide 图标名</summary>
    private static string SignalIcon(string signal) => signal switch
    {
        "capslock" => "capslock",
        "numlock" => "numlock",
        "scrolllock" => "scrolllock",
        "camera" => "camera",
        _ => "keyboard"
    };

    /// <summary>键位信号 → 中文名</summary>
    private static string SignalName(string signal) => signal switch
    {
        "capslock" => "大写锁定",
        "numlock" => "数字键盘",
        "scrolllock" => "滚动锁定",
        "camera" => "摄像头",
        _ => signal
    };

    /// <summary>通用的"图标 + 名称 + 状态"胶囊（系统状态类共用）</summary>
    private static FrameworkElement BuildTogglePill(string icon, string name, bool on,
        Brush? stateColor = null, string? tail = null)
    {
        var t = AppTheme.Current;
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new Border
        {
            Margin = new Thickness(11, 0, 9, 0),
            Child = Native.SvgIcons.CreateKeycap(icon, 22, on, t)
        });

        panel.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = t.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });

        panel.Children.Add(new TextBlock
        {
            Text = on ? "开" : "关",
            FontSize = 11.5,
            Margin = new Thickness(7, 0, 0, 0),
            Foreground = stateColor ?? (on ? t.Accent : t.TextTertiary),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(tail))
            panel.Children.Add(new TextBlock
            {
                Text = tail,
                FontSize = 11,
                Margin = new Thickness(7, 0, 12, 0),
                Foreground = t.TextTertiary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130
            });

        return panel;
    }

    /// <summary>解锁胶囊（docs §5.5）：⛶ + 已解锁 · 进入桌面 + 时间</summary>
    private static FrameworkElement BuildUnlockPill(IslandEvent ev)
    {
        var t = AppTheme.Current;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Border
        {
            Width = 17, Height = 17, Margin = new Thickness(12, 0, 9, 0),
            Child = Native.SvgIcons.CreateCentered("unlock", 17, t.Success, 1.1)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "已解锁 · 进入桌面", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = t.TextPrimary, VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = ev.CreatedAt.ToString("HH:mm"), FontSize = 11, Margin = new Thickness(8, 0, 12, 0),
            Foreground = t.TextTertiary, VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    /// <summary>通用应用图标方块（24×24，圆角 6）；取不到图标时用渐变占位</summary>
    private static FrameworkElement BuildAppIcon(string sourceKey, double size = 24, double radius = 6)
    {
        var host = new Grid { Width = size, Height = size, Margin = new Thickness(0) };
        var img = Native.AppIconExtractor.GetIcon(sourceKey);
        if (img != null)
        {
            host.Children.Add(new Image
            {
                Source = img,
                Width = size, Height = size,
                Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius)
            });
        }
        else
        {
            host.Children.Add(new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(radius),
                Background = new LinearGradientBrush(
                    ((SolidColorBrush)AppTheme.Current.Accent).Color, ((SolidColorBrush)AppTheme.Current.Accent).Color, 45)
            });
        }
        return host;
    }

    /// <summary>通知胶囊：应用图标 + 标题 + 正文一行</summary>
    private static FrameworkElement BuildNotificationPill(IslandEvent ev)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Border { Child = BuildAppIcon(ev.AppKey ?? ev.SourceKey), Margin = new Thickness(12, 0, 8, 0) });

        var title = new TextBlock
        {
            Text = ev.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Current.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        panel.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(ev.Subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = ev.Subtitle, FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0),
                Foreground = AppTheme.Current.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        return panel;
    }

    /// <summary>时钟胶囊</summary>
    private static FrameworkElement BuildClockPill(IslandEvent ev)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Border
        {
            Width = 16, Height = 16, Margin = new Thickness(12, 0, 9, 0),
            Child = Native.SvgIcons.CreateCentered("clock", 16, AppTheme.Current.TextPrimary, 1.1)
        });
        panel.Children.Add(new TextBlock
        {
            Text = ev.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Current.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(ev.Subtitle))
            panel.Children.Add(new TextBlock
            {
                Text = ev.Subtitle, FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
                Foreground = AppTheme.Current.TextTertiary,
                VerticalAlignment = VerticalAlignment.Center
            });
        return panel;
    }

    /// <summary>使用时长胶囊</summary>
    private static FrameworkElement BuildUsagePill(IslandEvent ev)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Border
        {
            Width = 16, Height = 16, Margin = new Thickness(12, 0, 9, 0),
            Child = Native.SvgIcons.CreateCentered("timer", 16, AppTheme.Current.Success, 1.1)
        });
        panel.Children.Add(new TextBlock
        {
            Text = ev.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Current.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    /// <summary>媒体胶囊：封面 + 歌名 + 艺术家 + 波形</summary>
    private FrameworkElement BuildMediaPill(Sources.MediaSnapshot snap)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 封面：优先来源应用图标（Chrome / Spotify …），取不到再用渐变块
        var coverHost = new Grid { Width = 24, Height = 24, Margin = new Thickness(12, 0, 8, 0) };
        var appIcon = Native.AppIconExtractor.GetIcon(snap.SourceId);
        if (appIcon != null)
        {
            coverHost.Children.Add(new Image
            {
                Source = appIcon, Width = 24, Height = 24,
                Stretch = Stretch.UniformToFill,
                Clip = new RectangleGeometry(new Rect(0, 0, 24, 24), 6, 6)
            });
        }
        else
        {
            coverHost.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
                Background = new LinearGradientBrush(
                    ((SolidColorBrush)AppTheme.Current.Accent).Color, ((SolidColorBrush)AppTheme.Current.Accent).Color, 45)
            });
        }
        Grid.SetColumn(coverHost, 0);
        grid.Children.Add(coverHost);

        // ---------- 右侧文字列：上行（标题+作者+波形） / 下行（歌词）----------
        // 布局：图标左固定不动，歌词与歌名作者统一排在图标右侧
        var textCol = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0)
        };

        // 上行：标题 + 作者（横向）+ 波形推到最右
        var metaRow = new Grid();
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = snap.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Current.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);
        metaRow.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(snap.Artist))
        {
            var artist = new TextBlock
            {
                Text = snap.Artist, FontSize = 11, Margin = new Thickness(7, 0, 0, 0),
                Foreground = AppTheme.Current.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(artist, 1);
            metaRow.Children.Add(artist);
        }
        // 波形：放在 metaRow 第 3 列（右端），由 BuildMediaPill 尾部统一添加
        textCol.Children.Add(metaRow);

        // 下行：歌词（有歌词时才显示；宽度自适应 / 超宽滚动在 ApplyLyric 中处理）
        var lyricRow = new Grid { Height = 15, Margin = new Thickness(0, 1, 0, 0) };
        var lyricText = new TextBlock
        {
            FontSize = 11, Foreground = AppTheme.Current.TextSecondary,
            TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new System.Windows.Point(0, 0.5)
        };
        lyricRow.Children.Add(lyricText);
        lyricRow.ClipToBounds = true;
        textCol.Children.Add(lyricRow);

        _pillLyricText = lyricText;
        _pillLyricRow = lyricRow;
        _pillLyricHost = textCol;
        _lastLyricText = "";        // 新元素是空的：强制下一帧重新赋值，避免歌词空白
        _lyricNeedScroll = false;

        Grid.SetColumn(textCol, 1);
        grid.Children.Add(textCol);

        // 波形：Playing 时动画，Paused 时静止
        // 波形：明确固定宽度，右 Margin 与岛内边距(12)对齐，
        // 并设 HorizontalAlignment=Right —— 保证永远贴在标题行右端、不出界。
        var wave = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 0, 0),
            Width = 22,                                  // 4×2.5 + 3×2(间距) ≈ 16，留余量
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true                         // 动画放大时不溢出
        };
        for (int i = 0; i < 4; i++)
        {
            var bar = new Border
            {
                Width = 2.5, CornerRadius = new CornerRadius(1.25),
                Background = AppTheme.Current.Accent,
                Height = snap.IsPlaying ? 10 : 4,
                Margin = new Thickness(0.8, 0, 0.8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (snap.IsPlaying)
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    4, 13, TimeSpan.FromMilliseconds(900))
                { BeginTime = TimeSpan.FromMilliseconds(i * 150), AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
            wave.Children.Add(bar);
        }
        // 【波形位置】放在胶囊【右侧、整体垂直居中】。
        // 用 Grid 叠一层：波形独立于文字列，按整个胶囊高度居中，
        // 不受标题行高度影响（之前放在 metaRow 里，行矮就显得贴顶）。
        Grid.SetColumn(wave, 2);            // 岛的第 3 列（右端）
        wave.VerticalAlignment = VerticalAlignment.Center;
        wave.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(wave);
        return grid;
    }

    /// <summary>
    /// 用 FormattedText 精确测量文本宽度（歌词需要真实宽度做三段式自适应，
    /// 字数估算会导致歌词被裁或留白不准）。
    /// </summary>
    private static double MeasureText(string text, double fontSize, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var ft = new FormattedText(
                text, System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                             bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                fontSize, Brushes.White, VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);
            return ft.Width;
        }
        catch { return text.Length * fontSize * 0.55; }
    }

    private double MeasurePillWidth(IslandEvent ev)
    {
        if (ev.Kind is "sys" or "camera")
            // 键帽(22) + 键名 + 开/关：固定估算，不随 Title 长度变化
            return Math.Clamp(22 + 20 + ev.Title.Length * 7.2 + 8 + 22 + 24,
                IslandMetrics.MinWidth, IslandMetrics.MaxWidth);
        if (ev.Kind == "unlock")
            return Math.Clamp(22 + 20 + ev.Title.Length * 7.2 + 8 + ev.Subtitle!.Length * 6.0 + 24,
                IslandMetrics.MinWidth, IslandMetrics.MaxWidth);
        if (ev.Kind == "media")
        {
            // 媒体胶囊宽度：精确测量（标题+作者+波形）与（歌词）取较大者，
            // 实现三段式第②步"歌词放不下时先把岛加宽"
            var snap = ev.Payload as Sources.MediaSnapshot;
            string? lyric = null;
            if (snap != null && _settings.Media.ShowLyrics && Sources.MediaSourcePolicy.IsMusicPlayer(snap.SourceId))
            {
                int posMs = (int)(snap.PositionTicks / TimeSpan.TicksPerMillisecond);
                lyric = _lyrics.CurrentText(posMs, _lyricOffsetMs);
            }
            if (snap != null) return MediaPillWidth(snap, lyric);
            return Math.Clamp(56 + ev.Title.Length * 7.2 +
                (string.IsNullOrEmpty(ev.Subtitle) ? 0 : ev.Subtitle.Length * 6.0 + 8),
                IslandMetrics.MinWidth, IslandMetrics.MaxWidth);
        }

        // 0.0.1 用字数估算；后续用 FormattedText 精确测量
        double w = 24;                     // 图标 + 间距
        w += ev.Title.Length * 7.2;
        if (!string.IsNullOrEmpty(ev.Subtitle)) w += ev.Subtitle.Length * 6.0 + 8;
        w += IslandMetrics.PaddingX * 2;
        return Math.Clamp(w, IslandMetrics.MinWidth, IslandMetrics.MaxWidth);
    }

    /// <summary>
    /// 把卡片里的滚动条改造成"细、圆角、低对比"样式，融入玻璃卡。
    /// Windows 默认滚动条约 17px 宽且带箭头按钮，放在卡片里非常突兀。
    /// </summary>
        private static void ApplyThinScrollBar(DependencyObject root)
    {
        // 用自定义模板替换 WPF 默认滚动条（默认约 17px 宽 + 上下箭头按钮，很丑）。
        // 目标：6px 宽胶囊滑块、无箭头、无轨道底色、低对比，融入玻璃卡。
        try
        {
            foreach (var sb in FindAll<System.Windows.Controls.Primitives.ScrollBar>(root))
            {
                if (sb.Tag as string == "glass") continue;
                sb.Tag = "glass";

                var t = AppTheme.Current;
                var thumb = new System.Windows.Controls.Primitives.Thumb
                {
                    Width = 6,
                    MinHeight = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Template = BuildPillThumbTemplate(t)
                };
                var track = new System.Windows.Controls.Primitives.Track
                {
                    Orientation = Orientation.Vertical,
                    Thumb = thumb,
                    IsDirectionReversed = true
                };
                var tpl = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ScrollBar));
                tpl.VisualTree = new FrameworkElementFactory(typeof(Grid));
                var gf = tpl.VisualTree;
                var trk = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Track));
                trk.Name = "PART_Track";
                trk.SetValue(System.Windows.Controls.Primitives.Track.OrientationProperty, Orientation.Vertical);
                trk.SetValue(System.Windows.Controls.Primitives.Track.IsDirectionReversedProperty, true);
                var th = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Thumb));
                th.SetValue(FrameworkElement.WidthProperty, 6.0);
                th.SetValue(FrameworkElement.MinHeightProperty, 28.0);
                th.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                th.SetValue(System.Windows.Controls.Primitives.Thumb.TemplateProperty, BuildPillThumbTemplate(t));
                trk.AppendChild(th);
                gf.AppendChild(trk);
                sb.Template = tpl;
                sb.Width = 6;
                sb.Background = Brushes.Transparent;
            }
        }
        catch { /* 样式失败不影响功能，退化为默认滚动条 */ }
    }

    /// <summary>胶囊形滑块模板：圆角 3、半透明、悬停变亮</summary>
    private static ControlTemplate BuildPillThumbTemplate(ThemeTokens t)
    {
        var tpl = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        b.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(70, 128, 128, 138)));
        var trigger = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(130, 110, 110, 125)), "b"));
        // 命名 Border 以便触发器定位
        b.Name = "b";
        tpl.VisualTree = b;
        tpl.Triggers.Add(trigger);
        return tpl;
    }



    /// <summary>递归使整棵子树的测量失效（WPF 不会自动重测已测量过的元素）</summary>
    private static void InvalidateTree(DependencyObject? root)
    {
        if (root == null) return;
        if (root is UIElement ue) ue.InvalidateMeasure();
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) InvalidateTree(VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>查找所有指定类型的后代</summary>
    private static IEnumerable<T> FindAll<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) yield return t;
            foreach (var r in FindAll<T>(c)) yield return r;
        }
    }

    /// <summary>在子元素中查找指定类型的第一个后代</summary>
    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>
    /// 媒体胶囊目标宽度：把歌词宽度需求算进去（三段式第②步 先加宽）。
    /// 返回未缩放宽度（渲染时乘 Scale）。
    /// </summary>
    private double MediaPillWidth(Sources.MediaSnapshot snap, string? lyric)
    {
        double icon = 12 + 24 + 8;
        // 波形实际占宽：4 根柱(2.5×4=10) + 3 个间距(2×3=6) + 与标题的间隔(6)，
        // 再加右内边距 12 → 共 34。之前只留 14，导致波形被挤出胶囊右边界。
        double wave = 12 + 10 + 6 + 6;
        double titleW = MeasureText(snap.Title, 12.5, true);
        double artistW = string.IsNullOrWhiteSpace(snap.Artist) ? 0 : MeasureText(snap.Artist!, 11) + 7;
        double metaW = titleW + artistW + wave;
        double lyricW = string.IsNullOrWhiteSpace(lyric) ? 0 : MeasureText(lyric!, 11);

        double w = icon + Math.Max(metaW, lyricW);
        double scale = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
        double cap = IslandMetrics.MaxWidth / scale;
        return Math.Clamp(w, IslandMetrics.MinWidth, Math.Min(cap, IslandMetrics.MaxWidth));
    }

    /// <summary>
    /// 更新胶囊内歌词行：三段式宽度自适应
    ///   ① 放得下 -> 完整显示
    ///   ② 超出但未达上限 -> 岛加宽（宽度由 MediaPillWidth 负责）
    ///   ③ 到上限仍放不下 -> 自动滚动
    /// </summary>
    private void UpdatePillLyric(Sources.MediaSnapshot? snap)
    {
        var tb = _pillLyricText;
        var row = _pillLyricRow;
        if (tb == null || row == null) return;

        string? text = null;
        int posMs = 0;
        // 歌词只对音乐播放器生效：浏览器/视频播放器不显示歌词
        if (snap != null && _settings.Media.ShowLyrics && Sources.MediaSourcePolicy.IsMusicPlayer(snap.SourceId))
        {
            posMs = (int)(snap.PositionTicks / TimeSpan.TicksPerMillisecond);
            text = _lyrics.CurrentText(posMs, _lyricOffsetMs);
        }

        // 无歌词 / 未同步 / 纯音乐：隐藏第二行，胶囊回单行
        if (string.IsNullOrWhiteSpace(text))
        {
            if (_lastLyricText != "")
            {
                _lastLyricText = "";
                StopLyricScroll(tb);
                row.Visibility = Visibility.Collapsed;
            }
            return;
        }
        row.Visibility = Visibility.Visible;

        // ---- 行切换：只在换行时重建文本与动画（不要每帧重建，否则动画永远停在起点）----
        bool lineChanged = text != _lastLyricText;
        if (lineChanged)
        {
            _lastLyricText = text!;
            tb.Text = text;
            StopLyricScroll(tb);
        }

        // ---- 三段式宽度判断（缩放感知）----
        double scale = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
        double need = MeasureText(text!, 11);
        double avail = row.ActualWidth;
        if (avail <= 0) avail = Math.Max(60, IslandMetrics.MaxWidth / scale - 56);
        double capW = IslandMetrics.MaxWidth / scale - 56;

        bool fits = need <= avail - 1 || need <= capW;
        if (fits)
        {
            if (_lyricNeedScroll) { StopLyricScroll(tb); _lyricNeedScroll = false; }
            return;
        }

        // ---- ③ 到上限仍放不下：自动滚动 ----
        if (!_settings.Media.LyricAutoScroll)
        {
            if (_lyricNeedScroll) { StopLyricScroll(tb); _lyricNeedScroll = false; }
            return;
        }
        if (!lineChanged && _lyricNeedScroll) return;   // 动画已在本行运行，别重启

        _lyricNeedScroll = true;
        double shift = need - avail + 8;
        _lyricScrollShift = shift;

        // 在本行剩余停留时间内单向滚完一轮（停留-滚动-停留），不来回、不固定周期。
        // 行时长来自 LRC 的下一行 StartMs（EndMs 常为 null）。
        int span = _lyrics.GetCurrentLineSpanMs(posMs, _lyricOffsetMs);
        double scrollSec = Math.Clamp(shift / 45.0, 1.5, 8.0);        // 按距离定滚动时长
        double holdSec = 1.2;                                          // 首尾各停留
        double totalSec = Math.Max(scrollSec + holdSec * 2, span / 1000.0 - 0.3);
        holdSec = Math.Max(0.35, (totalSec - scrollSec) / 2);

        var tx = new TranslateTransform();
        tb.RenderTransform = tx;
        double holdFrac = holdSec / totalSec;
        double scrollFrac = scrollSec / totalSec;
        // 关键帧：停留 → 单向滚到底 → 停留（不 AutoReverse）
        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(totalSec),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
            0, System.Windows.Media.Animation.KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
            0, System.Windows.Media.Animation.KeyTime.FromPercent(holdFrac)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
            -shift, System.Windows.Media.Animation.KeyTime.FromPercent(holdFrac + scrollFrac)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
            -shift, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)));
        tx.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>停止并复位歌词滚动（换行 / 恢复单行时调用）</summary>
    private static void StopLyricScroll(TextBlock tb)
    {
        if (tb.RenderTransform is TranslateTransform tx)
        {
            tx.BeginAnimation(TranslateTransform.XProperty, null);
            tx.X = 0;
        }
        tb.RenderTransform = Transform.Identity;
    }


    // ---------- 每帧渲染 ----------
    private DateTime _lastSelfHeal = DateTime.MinValue;

    private void Render()
    {
        // 自愈：若隐藏状态与实际全屏状态不符，强制校正（防止状态卡死）
        if (DateTime.Now - _lastSelfHeal > TimeSpan.FromSeconds(2))
        {
            _lastSelfHeal = DateTime.Now;
            if (_gameHidden && !Native.FullscreenDetector.IsFullscreenNow())
            {
                App.LogInfo("Island", "自愈：检测到已非全屏但隐藏标记仍为真 → 强制复位");
                OnFullscreenChanged(false);
            }
        }

        if (_stack.TotalCount == 0)
        {
            // 空态：窗口保持隐藏（不占屏幕），但保留 ticker 以便事件到达时立即显示
            if (IsVisible && !_gameHidden) Hide();
            return;
        }
        ApplyLayout();
        RefreshMediaCard();          // 进度条 / 时间 / 播放图标 每帧更新
        UpdatePillLyric(_stack.Active?.Event.Payload as Sources.MediaSnapshot);
        RefreshCardLyric(_stack.Active?.Event.Payload as Sources.MediaSnapshot);

        // 【自适应中心】cx 必须与 IsPointerOverAnyIsland 的命中坐标系严格一致：
        // 命中测试用 GetWindowRect（物理像素）/ DpiScale 反推逻辑坐标，
        // 若这里用 ActualWidth（WPF 内部宽，会被 CenterTop 旧值污染 / 跨屏拖动后滞后），
        // 视觉中心与命中中心错位 → "鼠标要放到岛左边/右边才触发"。
        // 直接用窗口实际物理宽度 / DPI 取中心，任意屏幕 / DPI / 拖动 / 缩放下都不跑偏。
        double cx = ActualWidth / 2.0;   // 兜底（窗口刚创建时）
        try
        {
            var hwnd0 = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd0 != IntPtr.Zero && WindowChromeHelper.GetWindowRect(hwnd0, out var wr0))
            {
                var dpi0 = VisualTreeHelper.GetDpi(this);
                double sc0 = dpi0.DpiScaleX <= 0 ? 1.0 : dpi0.DpiScaleX;
                cx = (wr0.Right - wr0.Left) / sc0 / 2.0;
            }
        }
        catch { /* 保持 ActualWidth 兜底 */ }
        var slots = _stack.ComputeSlots();

        foreach (var item in _stack.Items.ToList())
        {
            if (!slots.TryGetValue(item.Event.Id, out var slot)) continue;
            var b = item.Visual;

            // 【居中保证】所有几何量都乘 Scale：
            //   · 宽度/高度乘 Scale  → 元素实际占位随缩放变化
            //   · X 偏移乘 Scale     → 侧列球体跟随缩放一起外扩
            //   · 左边界 = 屏幕中轴 + 缩放后偏移 - 缩放后宽度/2
            // 这样无论怎么缩放，元素的"中心"永远落在 cx + off*scale 上，绝不会跑偏。
            double scale = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
            double w = item.Width.Value * scale;
            double h = item.Height.Value * scale;
            double off = item.X.Value * scale;

            b.Width = w;
            b.Height = h;
            Canvas.SetLeft(b, cx + off - w / 2.0);       // 以中心定位
            Canvas.SetTop(b, item.Y.Value * scale);
            b.CornerRadius = new CornerRadius(Math.Min(h / 2.0, IslandMetrics.CardRadius * scale));

            // 悬停等待期：加一圈高亮描边，暗示"再停一下就能操作"
            if (_pointerOverIsland && !_interactive && !item.IsDying)
            {
                b.BorderBrush = AppTheme.Current.Accent;
                b.BorderThickness = new Thickness(1.5);
            }
            else
            {
                b.BorderBrush = AppTheme.Current.Stroke;
                b.BorderThickness = new Thickness(1);
            }
            // 透明度 = 岛的弹簧透明度 × 悬停提示系数
            b.Opacity = Math.Clamp(item.Opacity.Value * item.HoverAlpha, 0, 1);

            // 形变（水珠阶段的挤压拉伸）仍用 RenderTransform，不影响布局
            if (b.RenderTransform is TransformGroup g && g.Children[0] is ScaleTransform st)
            {
                st.ScaleX = item.ScaleX.Value;
                st.ScaleY = item.ScaleY.Value;
            }

            // 三种形态：球体（侧列） / 胶囊（当前） / 详情卡（展开）
            bool wantCard = _stack.IsExpanded && item.Event.Id == _stack.Active?.Event.Id;
            bool wantSphere = item.Role == IslandRole.Sphere && !wantCard;
            var wantedKey = wantCard ? "card" : (wantSphere ? "sphere" : "pill");
            if (item.ContentKey != wantedKey)
            {
                item.ContentKey = wantedKey;
                var c = wantCard ? BuildCard(item.Event)
                      : wantSphere ? BuildSphere(item.Event)
                      : BuildContent(item.Event);
                if (c != null && b.Child is ContentPresenter cp) cp.Content = c;
                if (!wantCard) _cardParts = null;   // 收起后不再刷新进度

                // 展开卡：按内容实测高度，供 ClusterStack 计算目标高度。
                // 【关键】实测到的是"未缩放"的内容高度，而渲染时
                //   b.Height = item.Height.Value * Scale
                // 内容区实际可用高度 = 内容高 × Scale，所以这里必须乘回去，
                // 否则整体缩放 >1（如 1.35×）会把底部按钮裁掉。
                // 歌词晚到 / 内容高度变化 → 需要重新实测（此时卡片内容不必重建）
                if (wantCard && _cardNeedsRemeasure)
                {
                    _cardNeedsRemeasure = false;
                    var existing = (b.Child as ContentPresenter)?.Content as FrameworkElement;
                    if (existing != null)
                    {
                        double sc2 = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
                        double m2 = MeasureCardContent(existing, cwForCard() / sc2);
                        if (m2 > 0 && Math.Abs(m2 - item.MeasuredCardHeight) > 0.5)
                        {
                            item.MeasuredCardHeight = m2;
                            _lastLayoutSignature = null;
                        }
                    }
                }
                if (wantCard && c != null)
                {
                    // 宽度也要按缩放还原：内容是在"放大后的框"里排版的
                    double sc = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
                    double measured = MeasureCardContent(c, cwForCard() / sc);
                    // 纯自适应：高度 = 内容实测（歌词区用 MaxHeight 自适应，内容少就矮）
                    if (measured > 0 && Math.Abs(measured - item.MeasuredCardHeight) > 0.5)
                    {
                        item.MeasuredCardHeight = measured;
                        _lastLayoutSignature = null;   // 强制下一帧重算槽位
                    }
                }
            }

            // 内容对齐：胶囊/卡片的内容必须填满（Stretch），
            // 球体的居中由 BuildSphere 内部固定尺寸的 Grid 自行处理。
            // 不在此处动态切换对齐 —— 切换时机难以把握，会污染胶囊布局。
            if (b.Child is ContentPresenter cp2)
            {
                cp2.HorizontalAlignment = HorizontalAlignment.Stretch;
                cp2.VerticalAlignment = VerticalAlignment.Stretch;
            }

            b.Visibility = item.Opacity.Value < 0.02 ? Visibility.Collapsed : Visibility.Visible;
            b.IsHitTestVisible = item.Opacity.Value > 0.5 && !item.IsDying;

            // 颈缩（仅当前有 neck 的岛，通常是消散/生成中的那条）
            if (item.Neck.Value > 0.3)
            {
                double nx = cx + item.X.Value * scale;
                double topY = item.Y.Value * scale + h * (1 - item.ScaleY.Value);
                NeckPath.Data = DropletShape.BuildNeck(nx, topY, item.Neck.Value * scale);
                NeckPath.Opacity = Math.Clamp(item.Opacity.Value, 0, 1) * 0.9;
            }
        }

        // +N 指示：定位在最右侧球体之外，与岛保持同一条水平线
        PositionPlusBadge(cx, IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale);
    }

    /// <summary>把 +N 徽标放在最右侧可见球体的右边</summary>
    private void PositionPlusBadge(double cx, double scale)
    {
        int over = _stack.OverflowCount;
        if (over <= 0) { PlusBadge.Visibility = Visibility.Collapsed; return; }

        PlusText.Text = "+" + over;
        PlusBadge.Visibility = Visibility.Visible;

        var map = _stack.ComputeSlots();
        var act = _stack.Active;
        double cw = act != null && map.TryGetValue(act.Event.Id, out var s)
            ? s.Width : IslandMetrics.SphereSize;

        // 最右球体的偏移 + 一个球体直径 + 间距
        double lastRight = IslandMetrics.SphereOffset(cw, IslandMetrics.MaxSideSpheres - 1, 1);
        double badgeOff = lastRight + IslandMetrics.SphereSize / 2 + IslandMetrics.Gap + 19;

        double bw = 38 * scale, bh = 20 * scale;
        PlusBadge.Width = bw;
        PlusBadge.Height = bh;
        PlusBadge.CornerRadius = new CornerRadius(10 * scale);

        Canvas.SetLeft(PlusBadge, cx + badgeOff * scale - bw / 2);
        Canvas.SetTop(PlusBadge, IslandMetrics.TopMargin * scale
            + (IslandMetrics.IslandHeight * scale - bh) / 2);
    }

    /// <summary>
    /// 应用布局目标。每帧设目标（弹簧自己决定怎么走），
    /// 弹簧预设只在角色变化时切换——避免覆盖 emerge/collapse 的阶段参数。
    /// </summary>
    private void ApplyLayout(bool force = false)
    {
        var slots = _stack.ComputeSlots();
        var sig = string.Join("|", _stack.Items.Select(i => i.Event.Id + slots.GetValueOrDefault(i.Event.Id).Role))
                  + "#" + _stack.Active?.Event.Id + "#" + (_stack.IsExpanded ? "e" : "c");
        bool changed = force || sig != _lastLayoutSignature;
        _lastLayoutSignature = sig;

        foreach (var item in _stack.Items)
        {
            if (item.IsDying) continue;
            if (!slots.TryGetValue(item.Event.Id, out var slot)) continue;

            if (slot.Offscreen)
            {
                item.Opacity.To(0);
                item.X.To(slot.X);
                item.Width.To(slot.Width);
                item.Height.To(slot.Height);
                continue;
            }
            item.Opacity.To(1);
            item.X.To(slot.X);
            item.Width.To(slot.Width);
            item.Height.To(slot.Height);
            item.Role = slot.Role;
            // 球体内容就是图标本身，必须保持可见（之前置 0 导致球体空白）
            item.ContentOpacity.To(1);

            if (!changed) continue;
            switch (slot.Role)
            {
                case IslandRole.Sphere: item.SetPreset(SpringPresets.Push, item.Width, item.Height, item.X); break;
                case IslandRole.Card: item.SetPreset(SpringPresets.Float, item.Width, item.Height); break;
                default: item.SetPreset(SpringPresets.Snappy, item.Width, item.Height); break;
            }
        }

        // +N
        int over = _stack.OverflowCount;
        if (over > 0)
        {
            PlusText.Text = "+" + over;
            PlusBadge.Visibility = Visibility.Visible;
        }
        else PlusBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 悬停穿透状态机（方案一）：
    ///   未悬停        → 穿透（岛不挡任何东西）
    ///   悬停 < 放行时间 → 仍穿透，仅高亮描边提示"再停一下可交互"
    ///   悬停 ≥ 放行时间 → 不透明 + 拦截，可展开详情卡
    ///   按住 Alt      → 强制穿透（保险）
    ///   鼠标离开      → 立即收回 + 恢复穿透
    ///
    /// 用轮询而非 MouseMove 事件：窗口穿透时不接收鼠标事件，
    /// 必须靠 GetCursorPos 主动查询才能感知"鼠标进入"。
    /// </summary>
    private int _hitTick;

    private void UpdateHitTest()
    {
        _hitTick++;
        if (_hitDebug && _hitTick % 10 == 1)
            HitDiag($"tick={_hitTick} IsVisible={IsVisible} game={_gameHidden} items={_stack.Items.Count}");

        // 窗口隐藏 / 游戏隐藏时：保持穿透（这样窗口一旦显示就已是穿透状态）
        if (!IsVisible || _gameHidden)
        {
            SetClickThrough(true);
            return;
        }

        // 1. Alt 状态
        bool alt = (WindowChromeHelper.GetKeyState(0x12) & 0x8000) != 0;   // VK_MENU
        if (alt != _altDown)
        {
            _altDown = alt;
            if (alt) App.LogInfo("Island", "Alt 按下 → 强制穿透");
            else App.LogInfo("Island", "Alt 松开 → 恢复正常穿透策略");
        }

        // 2. 鼠标是否落在某条岛的矩形内
        bool over = IsPointerOverAnyIsland();

        // 3. 状态转移（带宽限，防止边缘抖动误判离开）
        if (over)
        {
            _lastInsideAt = DateTime.Now;
            if (!_pointerOverIsland)
            {
                _pointerOverIsland = true;
                _pointerEnterAt = DateTime.Now;
            }
        }
        else if (_pointerOverIsland)
        {
            // 连续 LeaveGrace 都没命中才算真的离开
            if (DateTime.Now - _lastInsideAt > LeaveGrace)
            {
                _pointerOverIsland = false;
                _pointerEnterAt = DateTime.MinValue;
                if (_interactive) OnPointerLeft();
            }
        }

        // 4. 计算目标状态
        bool wantInteractive;
        if (_altDown && IslandMetrics.AltForceThrough)
            wantInteractive = false;                       // Alt 强制穿透
        else if (!IslandMetrics.HoverActivate)
            wantInteractive = over;                        // 未启用策略：悬停即可交互
        else
            wantInteractive = over && _pointerEnterAt != DateTime.MinValue
                && (DateTime.Now - _pointerEnterAt).TotalMilliseconds >= IslandMetrics.HoverActivateMs;

        // 5. 应用
        if (wantInteractive != _interactive) SetInteractive(wantInteractive);
        SetClickThrough(!_interactive);

        // 6. 半透明提示（悬停等待期）
        UpdateHoverVisual();

        // 7. 状态日志（诊断模式每 300ms 无条件打印；正常模式仅状态变化时打印）
        if ((DateTime.Now - _lastHitLog).TotalMilliseconds > 300)
        {
            _lastHitLog = DateTime.Now;
            HitDiag($"在岛内={over} 停留={(_pointerEnterAt == DateTime.MinValue ? 0 : (DateTime.Now - _pointerEnterAt).TotalMilliseconds):0}ms " +
                $"可交互={_interactive} 穿透={!_interactive} Alt={_altDown} | 岛数={_stack.Items.Count} {DescribeRects()}");
        }
    }

    /// <summary>
    /// 鼠标是否落在任意一条岛的实际矩形内。
    ///
    /// 【坐标系】统一用物理像素：
    ///   · GetCursorPos           → 物理像素
    ///   · GetWindowRect(hwnd)    → 物理像素
    ///   · Canvas.GetLeft/Width   → 逻辑像素（DIP），需乘 DpiScale
    /// 之前混用 PointToScreen（逻辑）与 GetCursorPos（物理），
    /// 在 125%/150% 缩放的屏幕上区域会错位，导致"有几率才触发"。
    /// </summary>
    private bool IsPointerOverAnyIsland()
    {
        try
        {
            if (!WindowChromeHelper.GetCursorPos(out var pt)) return false;

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return false;
            if (!WindowChromeHelper.GetWindowRect(hwnd, out var wr)) return false;

            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            foreach (var item in _stack.Items)
            {
                if (item.IsDying || item.Opacity.Value < 0.3) continue;
                var v = item.Visual;
                if (v.Visibility != Visibility.Visible) continue;

                double cx0 = Canvas.GetLeft(v), cy0 = Canvas.GetTop(v);
                double ww = v.Width, hh = v.Height;
                if (double.IsNaN(ww) || double.IsNaN(hh) || ww <= 0 || hh <= 0) continue;

                // 逻辑 → 物理；外扩热区防边缘抖动
                const double padDip = 6;
                double x0 = wr.Left + (cx0 - padDip) * sx;
                double y0 = wr.Top + (cy0 - padDip) * sy;
                double x1 = wr.Left + (cx0 + ww + padDip) * sx;
                double y1 = wr.Top + (cy0 + hh + padDip) * sy;

                if (_hitDebug)
                    App.LogInfo("HitDbg",
                        $"鼠标({pt.X},{pt.Y}) 窗口({wr.Left},{wr.Top}) dpi={sx:0.##} " +
                        $"岛[{x0:0},{y0:0} → {x1:0},{y1:0}] 命中={pt.X >= x0 && pt.X <= x1 && pt.Y >= y0 && pt.Y <= y1}");

                if (pt.X >= x0 && pt.X <= x1 && pt.Y >= y0 && pt.Y <= y1) return true;
            }
        }
        catch (Exception ex) { if (_hitDebug) App.Log("HitDbg", ex); }
        return false;
    }

    /// <summary>把各岛的物理矩形描述出来（诊断用，可保留）</summary>
    private string DescribeRects()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !WindowChromeHelper.GetWindowRect(hwnd, out var wr)) return "";
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            var sb = new System.Text.StringBuilder();
            sb.Append($"窗口({wr.Left},{wr.Top}) dpi={sx:0.##} ");
            foreach (var it in _stack.Items)
            {
                var v = it.Visual;
                double x = Canvas.GetLeft(v), y = Canvas.GetTop(v);
                sb.Append($"[{it.Event.Id[..Math.Min(6, it.Event.Id.Length)]} " +
                          $"{wr.Left + x * sx:0},{wr.Top + y * sx:0} " +
                          $"{v.Width * sx:0}x{v.Height * sx:0} vis={v.Visibility} op={v.Opacity:0.00}] ");
            }
            if (WindowChromeHelper.GetCursorPos(out var pt)) sb.Append($"鼠标({pt.X},{pt.Y})");
            return sb.ToString();
        }
        catch (Exception ex) { return "ERR:" + ex.Message; }
    }

    /// <summary>命中测试诊断开关（环境变量 CHRONO_HIT_DEBUG=1）</summary>
    private static readonly bool _hitDebug =
        Environment.GetEnvironmentVariable("CHRONO_HIT_DEBUG") == "1";

    /// <summary>把一行诊断写到独立文件（避免日志缓冲/刷屏问题）</summary>
    private static void HitDiag(string msg)
    {
        if (!_hitDebug) return;
        try
        {
            var p = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChronoIsland", "hitdebug.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            System.IO.File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n",
                System.Text.Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>进入/退出可交互状态</summary>
    private void SetInteractive(bool on)
    {
        _interactive = on;
        App.LogInfo("Island", on ? "鼠标停留达标 → 岛可交互" : "鼠标离开/Alt → 岛恢复穿透");

        // 进入可交互 → 展开详情卡；退出 → 收回胶囊
        var active = _stack.Active;
        if (active != null && active.Event.Id != "preview")
        {
            bool expandable = active.Event.Policy?.Expandable != false;
            _stack.SetExpanded(on && expandable);
            _lastLayoutSignature = null;
            ApplyLayout(force: true);
        }
    }

    /// <summary>鼠标离开时的处理</summary>
    private void OnPointerLeft()
    {
        _stack.SetExpanded(false);
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
    }

    /// <summary>悬停等待期给一个"可以交互"的视觉提示（半透明）</summary>
    private void UpdateHoverVisual()
    {
        double target = 1.0;
        if (_pointerOverIsland && !_interactive)
            target = 0.55;      // 等待期：半透明，表示"当前穿透，再停一下可交互"
        else if (_altDown && IslandMetrics.AltForceThrough)
            target = 0.35;      // Alt 强制穿透：更淡，明确示意穿透中

        foreach (var item in _stack.Items)
        {
            if (item.IsDying) continue;
            item.HoverAlpha = target;
        }
    }

    /// <summary>切换窗口穿透（首次调用强制写入，避免初始状态不同步）</summary>
    private void SetClickThrough(bool through)
    {
        if (_clickThrough == through) return;
        _clickThrough = through;
        try
        {
            WindowChromeHelper.SetClickThrough(this, through);
            App.LogInfo("HitTest", through ? "点击穿透：开" : "点击穿透：关");
        }
        catch (Exception ex) { App.Log("HitTest", ex); }
    }

    private void EnsureVisible()
    {
        if (!IsVisible) Show();
        // 显示后立即重新置顶，避免被其他窗口挡住
        WindowChromeHelper.SetTopmostNoActivate(this);
    }

    // ---------- 交互 ----------
    private string? _hoverId;

    // 悬浮自动展开（docs §2.5）
    // 需求：鼠标悬停可自动展开详情卡；用户也可以手动点击展开。
    // 两者共用同一张卡，卡上带"关闭"按钮 —— 关闭后本次悬停不再自动展开。
    private DispatcherTimer? _hoverExpandTimer;
    private string? _hoverExpandId;
    /// <summary>本次悬停会话内已被用户手动关闭：不再自动展开</summary>
    private bool _hoverExpandSuppressed;
    /// <summary>上一次展开是"点出来的"还是"悬浮出来的" —— 决定关闭按钮的行为</summary>
    private bool _expandedByClick;
    /// <summary>上一条悬停停留的岛（用于判断是否换了岛）</summary>
    private string? _lastHoverId;

    /// <summary>岛被点击：非当前项 → 切换为当前；当前项 → 切换展开/收起</summary>
    private void OnItemClick(string id)
    {
        // 刚刚处理过卡片内的按钮点击 → 本次忽略（防止按钮点击冒泡又触发展开切换）
        if ((DateTime.Now - _lastCardAction).TotalMilliseconds < 400)
        {
            App.LogInfo("Island", "忽略按钮点击的冒泡");
            return;
        }

        // 200ms 内重复点击忽略，防止双击把展开又切回去
        var now = DateTime.Now;
        if ((now - _lastClickAt).TotalMilliseconds < 200) return;
        _lastClickAt = now;

        if (_stack.Active?.Event.Id == id)
        {
            if (_stack.IsExpanded) CloseExpandedCard();
            else
            {
                // 用户点击展开：清掉"已关闭"锁定，让这张卡能一直留着
                _hoverExpandSuppressed = false;
                _expandedByClick = true;
                _hoverExpandTimer?.Stop();
                _stack.SetExpanded(true);
            }
        }
        else { _stack.SetActive(id); }
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
    }

    /// <summary>节流：避免快速连点导致状态混乱</summary>
    private DateTime _lastClickAt = DateTime.MinValue;

    /// <summary>悬浮自动展开是否开启</summary>
    private bool HoverExpandDelayMsIsOn => _settings.Island.HoverExpandMs > 0;

    /// <summary>悬浮自动展开是否开启</summary>
    private bool HoverExpandOn => _settings.Island.HoverExpandMs > 0;

    /// <summary>悬浮停留多久后自动展开详情卡（ms）</summary>
    private int HoverExpandDelayMs => Math.Max(0, _settings.Island.HoverExpandMs);

    private void OnItemEnter(string id)
    {
        _hoverId = id;

        // 换了另一条岛 -> 解除"已关闭"锁定
        if (_lastHoverId != id)
        {
            _lastHoverId = id;
            _hoverExpandSuppressed = false;
        }

        EnsureHoverExpandTimer();

        if (!HoverExpandOn || _hoverExpandSuppressed) return;
        if (_stack.Active?.Event.Id != id) return;      // 只对当前胶囊生效
        if (_stack.IsExpanded) return;                 // 已经展开了就不必再等

        _hoverExpandId = id;
        _hoverExpandTimer!.Stop();
        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(HoverExpandDelayMs);
        _hoverExpandTimer.Start();
    }

    /// <summary>惰性创建悬浮展开计时器（只有真的需要时才建）</summary>
    private void EnsureHoverExpandTimer()
    {
        if (_hoverExpandTimer != null) return;
        _hoverExpandTimer = new DispatcherTimer();
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer!.Stop();
            if (_hoverExpandSuppressed) return;
            if (_hoverExpandId == null || _hoverExpandId != _hoverId) return;
            if (_stack.Active?.Event.Id != _hoverExpandId) return;
            if (_stack.IsExpanded) return;

            _expandedByClick = false;                   // 标记为"悬浮展开"
            _cardExpandedAt = DateTime.Now;             // 记录展开时刻（防误触）
            _stack.SetExpanded(true);
            _lastLayoutSignature = null;
            ApplyLayout(force: true);
            App.LogInfo("Island",
                $"悬浮 {HoverExpandDelayMs}ms -> 自动展开详情卡");
        };
    }

    private void OnItemLeave(string id)
    {
        if (_hoverId == id) _hoverId = null;
        _hoverExpandTimer?.Stop();
        _hoverExpandId = null;
    }

    /// <summary>详情卡上的关闭按钮：收起，并抑制本次悬停的再次自动展开</summary>
    private void CloseExpandedCard()
    {
        if (!_stack.IsExpanded) return;
        // 【防误触】展开瞬间，卡片右上角的关闭按钮可能正好"长"到鼠标下方，
        // 导致刚展开就被点掉（日志表现为"展开"与"点击关闭"同一秒发生）。
        // 展开后 500ms 内的关闭点击一律忽略。
        if (DateTime.Now - _cardExpandedAt < TimeSpan.FromMilliseconds(500)) return;
        _hoverExpandSuppressed = true;      // 同一岛上不再自动弹
        _hoverExpandTimer?.Stop();
        _hoverExpandId = null;
        _stack.SetExpanded(false);
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
        App.LogInfo("Island", "点击关闭 → 收起详情卡（本次悬停不再自动展开）");
    }

    /// <summary>关闭后要能再次触发：鼠标先离开再回来</summary>
    private void ResetHoverExpandLatch()
    {
        if (_hoverId == null) _hoverExpandSuppressed = false;
    }

    private void OnAutoReadTick(object? sender, EventArgs e)
    {
        var active = _stack.Active;
        if (active == null || active.IsDying) return;
        if (_hoverId != null) return;                     // 悬停暂停 TTL
        if (_stack.IsExpanded) return;

        // 媒体暂停：仅当用户开启"暂停后自动收起"时才计时
        var expiring = _stack.Items.FirstOrDefault(i => i.HideAt != null && !i.IsDying
                                                        && DateTime.Now >= i.HideAt);
        if (expiring != null)
        {
            expiring.HideAt = null;
            if (_settings.Media.HideWhenPaused) Dismiss(expiring);
        }

        var ttl = active.Event.Policy?.Ttl;
        if (ttl == null) return;
        if (active.TtlStart == null) { active.TtlStart = DateTime.Now; return; }
        if (DateTime.Now - active.TtlStart.Value < ttl.Value) return;

        Dismiss(active);
    }

    /// <summary>全屏/游戏：错开 60ms 依次收起（多米诺水珠）；恢复后回放</summary>
    private void OnFullscreenChanged(bool fullscreen)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            _gameHidden = fullscreen;
            if (fullscreen)
            {
                var list = _stack.Items.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    var it = list[i];
                    if (i > 0) await System.Threading.Tasks.Task.Delay(60);
                    _ = CollapseAndRemove(it);
                }
            }
            else
            {
                // 回放暂存事件（最多 5 条，间隔 120ms）
                App.LogInfo("Island", $"退出全屏，开始回放暂存（{Core.PendingQueue.Instance.Count} 条）");
                for (int i = 0; i < 5; i++)
                {
                    var ev = Core.PendingQueue.Instance.Dequeue();
                    if (ev == null) break;
                    OnEvent(ev);
                    await System.Threading.Tasks.Task.Delay(120);
                }
            }
        });
    }

    public void Dismiss(IslandItem item) => _ = CollapseAndRemove(item);

    private async System.Threading.Tasks.Task CollapseAndRemove(IslandItem item)
    {
        await DropletAnimator.CollapseAsync(item);
        await System.Threading.Tasks.Task.Delay(DropletAnimator.CollapseMs - 620);
        Dispatcher.Invoke(() =>
        {
            Stage.Children.Remove(item.Visual);
            _visuals.Remove(item.Event.Id);
            _stack.Remove(item);
            _lastLayoutSignature = null;
            ApplyLayout(force: true);
            if (_stack.TotalCount == 0)
            {
                _autoReadTimer.Stop();
                Hide();                                   // 回到空态：隐藏 + 停摆
            }
        });
    }
}

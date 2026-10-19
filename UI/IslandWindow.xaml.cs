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
    private Sources.DshSource? _dsh;        // DeepSeek Harness 工作状态（只读投影缓存）
    /// <summary>电池 / Wi-Fi / 热点 / 飞行模式 / 麦克风（docs §5.8–§5.12）</summary>
    private Sources.SystemStatusSource? _system;
    /// <summary>深浅色切换（docs §5.13）</summary>
    private Sources.ThemeSwitchSource? _themeSwitch;
    /// <summary>
    /// 进度条轨道的"未缩放"排版宽度。
    /// 卡片内容在未缩放坐标系里排版（CardWidth 宽 - 左右各 16 内边距），
    /// 由 WrapWithClose 的 LayoutTransform 整体放大到外框，因此这里必须用未缩放口径。
    /// 之前写死 388 恰好等于这个值，但 CardWidth 一旦变化就会失配；改为按公式推导。
    /// </summary>
    private static double ProgressTrackWidth => Math.Max(40, IslandMetrics.CardWidth - 32);


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
        /// <summary>轨道宽度（未缩放排版口径，每帧由 ProgressTrackWidth 赋值）</summary>
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

        // 进度条：直接用「已播放 / 总时长」算比例，比整数百分比更精确
        // （之前用 int 百分比，末尾可能差 1% 导致进度条差一小截）。
        // 快结束时（剩余 < 1s）吸附满格，避免观感上"总差一点"。
        // 【二次外推】GSMTC 的 Position 每 3~4 秒才推一次，快照因此是"陈旧"的。
        // 用快照采样时刻 SampledAtUtcTicks 补上到此刻流逝的时间，
        // 进度条与歌词才能连续推进，而不是每次跳一大格。
        var posTicks = Math.Max(0, snap.PositionTicks);
        var endTicks = Math.Max(0, snap.EndTicks);
        if (snap.IsPlaying && snap.SampledAtUtcTicks > 0)
        {
            var sinceSampled = DateTime.UtcNow.Ticks - snap.SampledAtUtcTicks;
            // 上限 10s：防止系统休眠/长时间挂起后突然跳到结尾
            if (sinceSampled > 0 && sinceSampled < TimeSpan.TicksPerSecond * 10)
            {
                posTicks += sinceSampled;
                if (endTicks > 0 && posTicks > endTicks) posTicks = endTicks;
            }
        }
        double frac;
        if (endTicks > 0)
        {
            frac = posTicks / (double)endTicks;
            if (endTicks - posTicks < TimeSpan.TicksPerSecond) frac = 1.0;
        }
        else frac = Math.Clamp(snap.ProgressPercent / 100.0, 0, 1);

        parts.ProgressFill.Width = Math.Max(2, parts.TrackWidth * Math.Clamp(frac, 0, 1));

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
            int posMs = (int)CurrentPositionMs(snap);
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

        // 【逐行滚动】三行都不再用省略号（见 BuildMediaCard 的说明），
        // 所以每一行都要各自把超出部分滚出来，否则上下文行会被直接裁掉。
        // 先统一清掉旧动画，再按需重新挂。
        double avail = clip.ActualWidth;
        ResetLineScroll(prev);
        ResetLineScroll(cur);
        ResetLineScroll(next);
        if (!_settings.Media.LyricAutoScroll || avail <= 0) return;

        ScrollLineIfNeeded(prev, prevText, 11.5, avail);
        ScrollLineIfNeeded(cur, curText, 13.5, avail);
        ScrollLineIfNeeded(next, nextText, 11.5, avail);
    }

    /// <summary>清掉一行的横向滚动动画与位移（换行时调用）。</summary>
    private static void ResetLineScroll(TextBlock? tb)
    {
        if (tb == null) return;
        if (tb.RenderTransform is TranslateTransform tx)
        {
            tx.BeginAnimation(TranslateTransform.XProperty, null);
            tx.X = 0;
        }
    }

    /// <summary>
    /// 单行需要时挂上「停留-滚动-停留」的横向动画。
    /// 判定依据是文本实测宽度 vs 可用宽（由调用方传入，避免每行各量一次容器）。
    /// </summary>
    private void ScrollLineIfNeeded(TextBlock? tb, string text, double fontSize, double avail)
    {
        if (tb == null || string.IsNullOrEmpty(text) || avail <= 0) return;
        double need = MeasureText(text, fontSize, fontSize > 12);
        if (need <= avail - 1) return;      // 放得下 → 不滚

        double shift = need - avail + 8;    // 末尾留 8px 呼吸，保证最后一个字可见
        double sec = Math.Clamp(shift / 35.0, 2.0, 8.0);
        var tx = new TranslateTransform();
        tb.RenderTransform = tx;
        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(sec + 2.4),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        // 停留 12% → 滚到 88% → 停留到底（单向一轮，便于读完一整句）
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


    /// <summary>
    /// 展开卡标题：宽度不够时横向滚动（停留 → 单向滚到底 → 停留）。
    ///
    /// 【为什么改成滚动而不是省略号】
    /// 标题列与歌词列现在各占一半（1* : 1*），长歌名放不下。
    /// 以前是 CharacterEllipsis 截断（"United States of Eurasia (..."），
    /// 看不到完整歌名。改成跑马灯后能看全，且不占用更多宽度。
    ///
    /// 只在标题变化时重算并重启动画（用签名比对），避免每帧重启导致抖动。
    /// </summary>
    private void ApplyCardTitleScroll()
    {
        var tb = _cardTitleText;
        var clip = _cardTitleClip;
        if (tb == null || clip == null) return;

        var text = tb.Text ?? "";
        if (text == _cardTitleSig) return;   // 标题没变，保持现有动画
        _cardTitleSig = text;

        // 重置上一次的动画与位移
        tb.RenderTransform = Transform.Identity;
        if (string.IsNullOrEmpty(text)) return;

        double avail = clip.ActualWidth;
        // 布局尚未完成时 ActualWidth 为 0：留到下一帧再量，避免误判"放得下"
        if (avail <= 0) { _cardTitleSig = ""; return; }

        double need = MeasureText(text, 14, true);
        if (need <= avail - 1) return;       // 放得下 → 不滚动

        // 超出部分 + 右侧留白，滚到末尾时能看到最后一个字
        double shift = need - avail + 8;
        // 速度 ~35 DIP/s，2~8 秒走完一轮（与歌词滚动同一手感）
        double sec = Math.Clamp(shift / 35.0, 2.0, 8.0);
        var tx = new TranslateTransform();
        tb.RenderTransform = tx;
        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(sec + 2.4),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        // 停留 12% → 滚动到 88% → 停留到底（与歌词滚动同一节奏）
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
        // 【0.0.2 线程闸门】先把总线与 UI 线程绑定，再订阅。
        // 顺序不能反：Marshaler 未设置时总线会退化为"发布者线程直调"（诊断器路径）。
        _bus.Marshaler = a => Dispatcher.InvokeAsync(a);
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

        // DeepSeek Harness 工作状态：只读其投影缓存（明文 JSON），不停 DSH 侧任何代码
        _dsh = new Sources.DshSource();
        _dsh.Configure(_settings);
        _dsh.Start(_bus);

        // 系统状态：电源 / Wi-Fi 与网速 / 移动热点 / 飞行模式 / 麦克风（docs §5.8–§5.12）
        _system = new Sources.SystemStatusSource();
        _system.Configure(_settings);
        _system.Start(_bus);

        // 深浅色切换提示（docs §5.13）：外观变化本身就在 UI 线程，这里只需转投一次队列
        _themeSwitch = new Sources.ThemeSwitchSource { Marshaler = a => Dispatcher.InvokeAsync(a) };
        _themeSwitch.Configure(_settings);
        _themeSwitch.Start(_bus);

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

            // 【数据源也必须释放】SystemStatusSource 持有 1s 轮询定时器、
            // ThemeSwitchSource 订阅了 AppTheme.Changed 静态事件 ——
            // 不释放的话，窗口关掉后定时器还在跑、主题变化还在往已销毁的窗口推事件
            // （自检/测试里反复创建 IslandWindow 时会不断累积）。
            try { _system?.Dispose(); } catch { }
            try { _themeSwitch?.Dispose(); } catch { }
            try { _sys?.Dispose(); } catch { }
            try { _clock?.Dispose(); } catch { }
            try { _dsh?.Dispose(); } catch { }
            try { _media?.Dispose(); } catch { }
            try { _usage?.Dispose(); } catch { }
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
            // 【与 UpdateItem 同一道闸】只有音乐播放器才查歌词。启动队列是独立路径，
            // 以前漏了这道判断，浏览器里放的视频也会被拿去查歌词（白费请求且可能匹配错歌）。
            if (ev.Kind == "media" && ev.Payload is Sources.MediaSnapshot ms0)
            {
                if (Sources.MediaSourcePolicy.IsMusicPlayer(ms0.SourceId))
                {
                    int d0 = (int)(Math.Max(0, ms0.EndTicks) / TimeSpan.TicksPerMillisecond);
                    _lyrics.UpdateTrack(ms0.Title, ms0.Artist, ms0.Album, d0);
                }
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

    // ---------- 事件入口 ----------
    /// <summary>
    /// 事件入口 —— **整个程序唯一的线程闸门**（0.0.2 收口）。
    ///
    /// 【契约】本方法只允许在 UI 线程上执行，且**不再自己派发**：
    ///   · Source 侧：<see cref="Core.EventBus.Marshaler"/> 由 OnLoaded 设为
    ///     <c>a =&gt; Dispatcher.InvokeAsync(a)</c>，总线保证 sink 一定落在 UI 线程；
    ///   · 宿主内部直调（PreviewSysState / 全屏退出回放）本身就在 UI 线程。
    ///
    /// 以前这里再包一层 Dispatcher.InvokeAsync，导致"到底谁负责 marshal"含糊不清，
    /// 也掩盖了总线在发布者线程直调的事实 —— 只要有任意一个 Source 换到后台线程，
    /// 渲染就会跨线程访问。现在闸门只有一处，且写进了契约。
    /// </summary>
    public void OnEvent(IslandEvent ev)
    {
        if (ev == null) return;

        if (_gameHidden)
        {
            if (ev.Policy?.QueueWhenHidden == true) PendingQueue.Instance.Enqueue(ev);
            App.LogInfo("Island", $"隐藏期间收到的 {ev.Kind} 事件已暂存：{ev.Title}");
            return;
        }

        // ===== 先处理"终止"语义，再做"就地更新" =====
        //
        // 【0.0.2 修正 —— 这两条的判定顺序此前是反的】
        // 两个终止事件的 Id 与在岛上那条**完全相同**：
        //   · 媒体结束：Id = "media:<SourceId>"，与播放中那条同 Id（MediaSource.Emit）
        //   · DSH 收起：Id = "dsh:<会话id>"，与工作中那条同 Id（DshSource.PublishRetract）
        // 而"同 Id 就地更新"的判断原本排在它们前面 → 终止事件会被当成一次普通刷新走
        // UpdateItem，岛根本不会被收起，只是内容被换成空胶囊并**永久留在屏幕上**。
        // DshSource 里那句注释（"必须在'同 Id 就地更新'之前判断"）本来就是对的，
        // 只是代码没照着写；这里把顺序与注释对齐。
        // 主动作是收起、不是刷新，所以必须放在就地更新之前。

        // 媒体"会话结束"事件：Title 为空 → 收起该岛
        if (ev.Kind == "media" && string.IsNullOrEmpty(ev.Title))
        {
            // 多会话：按事件自带的 Id 精确收起（旧代码写死 "media"，
            // 而实际 Id 一直是 "media:<SourceId>"，那个回退分支从来没有命中过）。
            var media = _stack.Items.FirstOrDefault(i => i.Event.Id == ev.Id);
            if (media != null && !media.IsDying) Dismiss(media);
            return;
        }

        // DSH 空闲收起（多任务：每条会话一个独立 Id）
        if (ev.Kind == "dsh-retract")
        {
            var old = _stack.Items.FirstOrDefault(i => i.Event.Id == ev.Id);
            if (old != null && !old.IsDying) Dismiss(old);
            return;
        }

        // 【就地更新】同一 Id 的事件（如媒体每秒刷新）应替换内容，而不是新增一条岛
        var existing = _stack.Items.FirstOrDefault(i => i.Event.Id == ev.Id);
        if (existing != null)
        {
            UpdateItem(existing, ev);
            return;
        }

        // 启动阶段：先进队列按优先级逐条入场，避免一次性全部弹出
        if (StartupQueueActive)
        {
            EnqueueStartup(ev);
            return;
        }

        AdmitIsland(ev);
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

    /// <summary>
    /// 胶囊内歌词行的滚动状态，按 event.Id 记录。
    ///
    /// 【为什么按 item 存】它描述的是"某条 item 的歌词行此刻滚到哪了"。
    /// 以前是窗口级共享字段，与 <c>_pillGrid / _pillLyricText</c> 一样，
    /// 会被其它媒体会话的刷新读走/改写（见 <see cref="MediaSignature"/> 的说明）。
    /// 顺带解决了一个隐藏问题：切到另一条媒体岛时，新胶囊会继承旧岛的
    /// "正在滚动"标记，于是新歌词行永远不重新挂滚动动画。
    /// </summary>
    private readonly Dictionary<string, LyricScrollState> _lyricScroll = new();

    private sealed class LyricScrollState
    {
        /// <summary>本行是否已经挂过滚动动画</summary>
        public bool NeedScroll;
    }

    /// <summary>歌词源（跟随当前媒体曲目）</summary>
    private readonly Sources.LyricsSource _lyrics = new();
    private int _lyricOffsetMs = 0;                 // 用户微调的歌词偏移（毫秒）

    /// <summary>展开卡歌词容器与其滚动宿主</summary>
    /// <summary>用户上次手动滚动歌词的时间（4 秒内不自动跟随，避免上滚被拉回）</summary>
    private DateTime _lyricUserScrollAt = DateTime.MinValue;
    /// <summary>卡片内容滚动宿主（超出高度上限时滚动而非裁切）</summary>
    private ScrollViewer? _cardScrollHost;
    /// <summary>展开卡：标题（放不下时横向滚动，占宽上限 = 整卡一半）</summary>
    private TextBlock? _cardTitleText;
    /// <summary>标题的裁剪宿主（滚动时超出的部分靠它裁掉）</summary>
    private Grid? _cardTitleClip;
    /// <summary>标题滚动签名（标题变了才重算，避免每帧重启动画）</summary>
    private string _cardTitleSig = "";

    /// <summary>展开卡：标题右侧的歌词控件（含横向滚动）</summary>
    private TextBlock? _cardLyricText;
    private TextBlock? _cardLyricPrev;
    private TextBlock? _cardLyricNext;
    private StackPanel? _cardLyricPanel;
    private Grid? _cardLyricClip;
    private string _cardLyricSig = "";
    /// <summary>卡片展开时刻（用于关闭按钮防误触）</summary>
    private DateTime _cardExpandedAt = DateTime.MinValue;

    /// <summary>上次校正展开卡高度的时刻（节流用）</summary>

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
    public FrameworkElement ProbeCard(string kind, ThemeTokens? themeOverride = null,
        Sources.MediaSnapshot? mediaOverride = null, Core.IslandEvent? eventOverride = null)
    {
        // 系统状态类岛有自己的一套样例（电源 / 网络 / 麦克风 / 外观），
        // 由分部类构造 —— 这里传入完整事件，卡片模板只认 Payload 与 Title。
        if (eventOverride != null)
        {
            var prev0 = AppTheme.Current;
            if (themeOverride != null) AppTheme.SetCurrentForProbe(themeOverride);
            try { return BuildCard(eventOverride)!; }
            finally { AppTheme.SetCurrentForProbe(prev0); }
        }

        object snap = kind switch
        {
            "media" when mediaOverride != null => mediaOverride,
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
            // BuildCard 内部已经 WrapWithClose 过，这里不要再包一层：
            // 重复包裹会让关闭按钮叠两次、且测量口径与真实渲染不一致。
            return BuildCard(ev)!;
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
        _system?.Configure(s);
        _themeSwitch?.Configure(s);
    }

    /// <summary>手动弹一条系统状态示例岛（设置页预览用）</summary>
    public void PreviewSysState(string signal)
    {
        // 新增的系统状态类（电源 / 网络 / 麦克风 / 外观）走统一预览入口，
        // 它们的样例数据在分部类里构造 —— 这里的 switch 保持只处理键位 / 解锁 / 摄像头。
        if (PreviewSystemStatus(signal)) return;

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
            // 【必须按当前形态重建】不能一律 BuildContent —— 那会把侧列球体灌成胶囊，
            // 且 ContentKey 不同步，Render 的换装守卫会认为"已经对了"而永不再修。
            var content = BuildContentFor(item);
            if (content != null && b.Child is ContentPresenter cp) cp.Content = content;
        }
        _wifiPillSpeedText = null;      // 内容重建后旧引用作废，由新胶囊重新登记
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
            // 【关键】按当前形态重建：展开态继续用卡片模板、侧列球体继续用球体模板。
            // 仅用 ContentKey == "card" 判断是不够的 —— 那会把球体 item 当成胶囊重建，
            // 于是球体里被塞进一个胶囊（图标偏右 6px、文字/波形溢出球外）。
            // BuildContentFor 会重新判定形态并同步 ContentKey。
            var content = BuildContentFor(item);
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
        item.ContentKey = "pill";
        ItemContentBuilt(item, content);
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
    /// <summary>
    /// 取媒体的"当前播放位置（毫秒）"，对 GSMTC 的陈旧快照做二次外推。
    /// GSMTC 的 Position 每 3~4 秒才推一次；歌词与进度条都依赖它，
    /// 不外推就会卡在固定的一行 / 进度条不涨。
    /// </summary>
    private static long CurrentPositionMs(Sources.MediaSnapshot? snap)
    {
        if (snap == null) return 0;
        long pos = Math.Max(0, snap.PositionTicks);
        long end = Math.Max(0, snap.EndTicks);
        if (snap.IsPlaying && snap.SampledAtUtcTicks > 0)
        {
            long since = DateTime.UtcNow.Ticks - snap.SampledAtUtcTicks;
            if (since > 0 && since < TimeSpan.TicksPerSecond * 10)
            {
                pos += since;
                if (end > 0 && pos > end) pos = end;
            }
        }
        return pos / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// 展开卡内容的排版宽度（= 外框真实宽度 = CardWidth × Scale）。
    /// 交给 MeasureCardContent 时不能预先除以 Scale：WPF 自己会为 LayoutTransform
    /// 做这步除法，重复除会让内容按 CardWidth/Scale² 排版，导致右侧留白。
    /// </summary>
    private double cwForCard() =>
        IslandMetrics.CardWidth * (IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale);

    /// <summary>
    /// 实测卡片内容所需高度（未缩放逻辑值）。
    /// 调用方必须传入【尚未挂载到视觉树】的元素 —— 已挂载的元素其
    /// DesiredSize.Height 会被父级拉伸，测出来的不是内容真实需求。
    ///
    /// 【两个必须做对的点】
    /// 1) 用【定宽容器】Measure，而不是直接 Measure content：
    ///    卡片根节点是 StackPanel，其 DesiredSize 取自子元素自然宽度（实测仅 202），
    ///    而非外框给定的 CardWidth(420)。直接测会让文字按 202 宽换行、撑出 174 高；
    ///    真实渲染时被拉伸到 420 宽其实只要约 116 —— 差值正是底部那片空白。
    /// 2) LayoutTransform 由 WPF 自行处理：它会把约束除以缩放比再交给子树，
    ///    并把 DesiredSize 再乘回缩放比。因此约束直接传外框真实宽度
    ///    （CardWidth × Scale），结果再除以 Scale 折回未缩放口径。
    /// </summary>
    private static double MeasureCardContent(FrameworkElement content, double width)
    {
        try
        {
            double sc = 1.0;
            if (content.LayoutTransform is ScaleTransform sct && sct.ScaleX > 0)
                sc = sct.ScaleX;

            double w = Math.Max(1, width);
            var probe = new Border { Width = w, Child = content };
            try
            {
                probe.Measure(new Size(w, double.PositiveInfinity));
                double h = probe.DesiredSize.Height / sc;
                if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0) return 0;
                // 返回未缩放口径：缩放由渲染端施加（b.Height = item.Height × Scale），
                // 这里再乘一次就会被缩放两次，卡片过高、下方重新出现空白。
                return Math.Clamp(h, IslandMetrics.CardMinHeight, IslandMetrics.CardMaxHeight);
            }
            finally
            {
                // 解绑：调用方随后要把 content 挂到 ContentPresenter 上，
                // WPF 不允许元素同时有两个父级。
                probe.Child = null;
            }
        }
        catch { return 0; }
    }






    private FrameworkElement WrapWithClose(FrameworkElement body, ScaleTransform? contentScale = null)
    {
        // 【自适应高度 + 上限 + 滚动条】
        // 卡片高度由内容实测决定（自适应），但上限受 CardMaxHeight 约束；
        // 超出上限时用 ScrollViewer 滚动，而不是让 Border 把内容裁掉
        // —— 之前通知卡底部按钮被裁、歌词看不见，就是硬裁切造成的。
        // 滚动条：默认 Hidden（不占宽度、不显示），只在鼠标进入卡片时才细显示。
        // 之前用 Auto，Windows 默认滚动条又粗又显眼，横在卡片里很难看。
        // 【缩放放在 host 上】body 与关闭按钮一起按 Scale 放大，
        // 于是 16/14 的内边距、圆角、进度条宽度全部在未缩放坐标系里自洽，
        // 视觉上正好铺满 item.Width × Scale 的外框（否则下方会留大片空白）。
        // contentScale 由 BuildCard 传入（已构造好，供 MeasureCardContent 反推口径）。
        var host = new Grid();
        if (contentScale != null) host.LayoutTransform = contentScale;
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
        _cardTitleText = null;             // 标题引用作废（同上）
        _cardTitleClip = null;
        _cardTitleSig = "";
        _cardScrollHost = null;
        _cardLyricSig = "";
        var body = ev.Kind switch
        {
            "media" when ev.Payload is Sources.MediaSnapshot snap => BuildMediaCard(snap),
            "sys" when ev.Payload is Sources.SysSnapshot snap => BuildSysCard(snap),
            "camera" when ev.Payload is Sources.CameraSnapshot cam => BuildCameraCard(cam),
            "unlock" when ev.Payload is Sources.UnlockSnapshot snap => BuildUnlockCard(snap),
            "usage" => BuildUsageCard(ev),
            "dsh" when ev.Payload is Sources.DshSnapshot ds => BuildDshCard(ds),
            // 系统状态类（电源 / 网络 / 麦克风 / 外观）统一走分部类，返回 null 时回落通知卡
            _ => BuildSystemCard(ev) ?? BuildNotificationCard(ev)
        };
        if (body == null) return null;
        // 【卡片专用缩放】详情卡内容按【未缩放】的 CardWidth 排版，再整体放大 Scale。
        // 之前只放大外框（b.Width = item.Width × Scale）而内容仍按 1.0 排版，导致：
        //   ① 内容只占外框的 1/Scale（1.5 缩放下约 67%），下方留下一大片空白；
        //   ② 内容按未缩放宽度排版却要撑满被放大的外框，百分比 / 宽度全部失配 ——
        //      进度条按写死的 388px 算比例，实际轨道已变成 598px，最多只能走到 64.9%，
        //      表现为"进度条永远走不满"。
        // 改用 LayoutTransform 后内容与外框同比缩放，宽度、比例、截断重新自洽。
        // 变换挂在 WrapWithClose 返回的 host 上，body 自身不带变换，
        // 这样 MeasureCardContent 只需按 host 的变换反推排版宽度，逻辑单一。
        double sc = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
        return WrapWithClose(body, new ScaleTransform(sc, sc));
    }

    /// <summary>
    /// DSH 工作状态详情卡：状态行 + 进度/上下文指标 + 会话标题 + [打开 DSH][收起]。
    /// 不显示"打开应用"的 COM 激活路径 —— DSH 不是可被 COM 激活的应用，
    /// 直接跳它的 Web GUI 更符合直觉。
    /// </summary>
    private FrameworkElement BuildDshCard(Sources.DshSnapshot snap)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        bool working = snap.Working;

        // 头部：状态图标 + 标题 + 副标题
        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Child = Native.SvgIcons.CreateKeycap(
                working ? "sparkles" : "check", 26, working, t)
        });
        var hi = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hi.Children.Add(new TextBlock
        {
            Text = (snap.IsSubagent ? "DSH 子代理" : "DeepSeek Harness")
                + (working ? " 工作中" : " 已完成"),
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = t.TextPrimary
        });
        hi.Children.Add(new TextBlock
        {
            Text = (snap.IsSubagent && !string.IsNullOrWhiteSpace(snap.SubagentLabel)
                    ? snap.SubagentLabel + " · "
                    : "")
                + (working
                    ? snap.Stepping
                        ? $"第 {Math.Max(snap.Turn, 1)} 轮 · 第 {snap.Step} 步"
                        : $"第 {Math.Max(snap.Turn, 1)} 轮 · 执行中"
                    : $"第 {Math.Max(snap.Turn, 1)} 轮 · 共 {snap.Steps} 步"),
            FontSize = 11, Foreground = t.TextTertiary
        });
        head.Children.Add(hi);
        // 子代理徽标：卡片里同样要能看出这是子代理而不是主会话
        if (snap.IsSubagent)
            head.Children.Add(new Border
            {
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = BuildSubagentTag(t, snap.SubagentMode)
            });
        root.Children.Add(head);

        // 指标矩阵：轮次 / 步数 / 上下文
        var matrix = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        void AddTile(string label, string value, bool last)
        {
            var tile = new Border
            {
                Width = 104, Height = 60, CornerRadius = new CornerRadius(13),
                Background = t.RowSurface, BorderBrush = t.Stroke,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, last ? 0 : 8, 0)
            };
            var sp = new StackPanel { Margin = new Thickness(11, 9, 11, 9) };
            sp.Children.Add(new TextBlock
            { Text = label, FontSize = 10.5, Foreground = t.TextTertiary });
            sp.Children.Add(new TextBlock
            {
                Text = value, FontSize = 16, FontWeight = FontWeights.Bold,
                Foreground = t.TextPrimary, Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            tile.Child = sp;
            matrix.Children.Add(tile);
        }
        AddTile("轮次", Math.Max(snap.Turn, 1).ToString(), false);
        AddTile("步数", working ? snap.Step.ToString() : snap.Steps.ToString(), false);
        AddTile("上下文", snap.ContextWindow > 0 ? snap.ContextPercent + "%" : "—", true);
        root.Children.Add(matrix);

        // 上下文进度条
        if (snap.ContextWindow > 0)
        {
            var track = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(3), Background = t.Track,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var grid = new Grid();
            grid.Children.Add(track);
            var fill = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(3),
                Background = snap.ContextPercent >= 85 ? t.Danger
                           : snap.ContextPercent >= 60 ? t.Warning : t.AccentGradient,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, 388 * snap.ContextRatio)
            };
            grid.Children.Add(fill);
            root.Children.Add(grid);
            root.Children.Add(new TextBlock
            {
                Text = $"{snap.SurfaceTokens:N0} / {snap.ContextWindow:N0} tokens",
                FontSize = 10.5, Foreground = t.TextTertiary, Margin = new Thickness(0, 5, 0, 0)
            });
        }

        // 会话标题
        if (!string.IsNullOrWhiteSpace(snap.Title))
            root.Children.Add(new TextBlock
            {
                Text = snap.Title,
                FontSize = 12.5, Foreground = t.TextSecondary,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 40,
                Margin = new Thickness(0, 12, 0, 0)
            });

        // 模型
        if (!string.IsNullOrWhiteSpace(snap.Model))
            root.Children.Add(new TextBlock
            {
                Text = "模型 " + snap.Model,
                FontSize = 10.5, Foreground = t.TextTertiary, Margin = new Thickness(0, 6, 0, 0)
            });

        // 按钮行
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        acts.Children.Add(MakeTextButton("打开 DSH", "monitor", t, true, () =>
        {
            DismissActive();
            OpenDshWebUi();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("收起", "close", t, false, () =>
        {
            DismissActive();
            return Task.CompletedTask;
        }));
        root.Children.Add(acts);
        return root;
    }

    /// <summary>打开 DSH 的 Web GUI（127.0.0.1:19387）；未运行则什么都不做并记日志。</summary>
    private static void OpenDshWebUi()
    {
        try
        {
            var url = Environment.GetEnvironmentVariable("DSH_WEB_URL");
            if (string.IsNullOrWhiteSpace(url)) url = "http://127.0.0.1:19387";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log("Dsh", ex); }
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
        // 【布局：封面 | 右侧上下两行】
        //   上行：标题 + 艺人 + 来源（独占整宽，长歌名放得下）
        //   下行：歌词（独占整宽，比挤在半边时宽敞）
        //
        // 【为什么不再左右分栏】之前试过标题 1/3、歌词 2/3 的左右分栏，
        // 左右分栏时（标题 1/3 ≈ 107px）像 "Exogenesis: Symphony Pt. 2"（实测 332.9px）
        // 这种长歌名怎么都放不下，收起态能显示全、展开后反而被压成 "jenesis: Sy"。
        // 改成上下两行后标题拿回整宽，长歌名不再被压缩；歌词也拿回整宽。
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 封面
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 右侧内容

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

        // 右侧内容容器：上排标题块、下排歌词块，各占整宽。
        var textArea = new StackPanel
        {
            // 【必须撑满】垂直 StackPanel 默认按子元素内容宽收缩；
            // 不显式 Stretch 的话，titleClip/lyWrap 量到的 ActualWidth 等于内容宽，
            // 跑马灯会误判"放得下"，超长文本还会溢出卡片右边缘。
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };

        // 标题块：独占整行，长歌名放得下；仍保留跑马灯兜底极端长名。
        var meta = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        // 标题的裁剪宿主：只有它裁掉溢出部分，滚动才看起来是"跑马灯"而不是溢出。
        var titleClip = new Grid
        {
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch   // 占满整行，裁剪宽度才等于可用宽
        };
        var titleText = new TextBlock
        {
            Text = snap.Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = t.TextPrimary,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new System.Windows.Point(0, 0.5)
        };
        titleClip.Children.Add(titleText);
        _cardTitleText = titleText;
        _cardTitleClip = titleClip;
        _cardTitleSig = "";        // 新元素：强制下一帧重新判定是否需要滚动
        meta.Children.Add(titleClip);
        if (!string.IsNullOrWhiteSpace(snap.Artist))
            meta.Children.Add(new TextBlock
            { Text = snap.Artist, FontSize = 12, Foreground = t.TextSecondary, Margin = new Thickness(0, 2, 0, 0) });
        meta.Children.Add(new TextBlock
        { Text = snap.SourceName, FontSize = 11, Foreground = t.TextTertiary, Margin = new Thickness(0, 2, 0, 0) });
        textArea.Children.Add(meta);

        // ---- 歌词块（标题下方，独占整宽，显示 2~3 行，当前行高亮）----
        // 与标题左对齐（不再额外缩进），宽度拿满，长歌词有更多可见空间。
        // 【不能设 HorizontalAlignment.Left】那样 lyWrap 宽度会收缩到内容宽，
        // ClipToBounds 就按内容宽裁剪，RefreshCardLyric 量到的 avail 永远够用，
        // 长歌词的横向滚动会彻底失效。必须撑满整列。
        var lyWrap = new Grid
        {
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,   // 同上：裁剪宽度 = 可用宽
            VerticalAlignment = VerticalAlignment.Center
        };
        lyWrap.ClipToBounds = true;
        var lyStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // 【三行歌词都不设 TextTrimming】
        // 以前用 CharacterEllipsis：超宽会先在末尾打 "…"，可 Rolling 又要把
        // 超出部分滚出来 —— 两者互相矛盾。实测看到的是
        //   "n you didn't love that girl…"：既被滚掉开头、又被省略号砍掉结尾，
        // 完整歌词永远看不全，滚动等于白做。
        // 现在改成 None（不裁字），溢出部分由外层 lyWrap.ClipToBounds 裁掉，
        // 再靠 ApplyLyric 的横向滚动把剩余内容送进来 —— 职责清晰：
        //   裁剪 = 谁超出容器；滚动 = 如何看到剩下的。
        var lyCur = new TextBlock
        {
            FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = t.TextPrimary,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        // 上下文行（淡化）
        var lyNext = new TextBlock
        {
            FontSize = 11.5, Foreground = t.TextTertiary, Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var lyPrev = new TextBlock
        {
            FontSize = 11.5, Foreground = t.TextTertiary, Margin = new Thickness(0, 0, 0, 2),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None,
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
        textArea.Children.Add(lyWrap);

        // 右侧内容整体放进第 1 列（第 0 列是封面）
        Grid.SetColumn(textArea, 1);
        top.Children.Add(textArea);

        root.Children.Add(top);

        // 进度条：支持拖动 seek。
        // 命中区比视觉高度高（12px），符合"细进度条也要好点"的手感；
        // 不支持 seek 的会话（直播 / 电台 / 部分播放器）直接禁用，视觉上变灰且光标不变手型。
        bool canSeek = _media?.CanSeek(snap.SourceId) ?? false;
        double trackW = ProgressTrackWidth;
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

        // 系统状态类（电源 / 网络 / 麦克风 / 外观）：Lucide 状态图标
        var systemSphere = BuildSystemSphere(ev);
        if (systemSphere != null) return systemSphere;

        // DSH 工作状态：球体内用 sparkles / check，不依赖应用图标
        if (ev.Kind == "dsh" && ev.Payload is Sources.DshSnapshot ds)
            return Native.SvgIcons.CreateSphere(ds.Working ? "sparkles" : "check", 19,
                ds.Working ? AppTheme.Current.Accent : AppTheme.Current.Success);
        if (ev.Kind == "dsh-retract")
            return Native.SvgIcons.CreateSphere("sparkles", 19, AppTheme.Current.TextSecondary);

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

    /// <summary>
    /// 该 item 此刻应该装的形态（判定口径必须与 Render 里完全一致）。
    /// 【为什么需要它】<see cref="ApplyVisualTheme"/> 与 <see cref="UpdateItem"/> 原先一律
    /// 直接调用 BuildContent —— 它对任何 event 都只返回「胶囊」。侧列球体的 item 因此被灌进
    /// 胶囊内容，而 ContentKey 仍是 "sphere"，Render 的换装守卫（ContentKey != wantedKey）
    /// 会认为"已经是对的"而永不修复 → 球体里长期装着一个 230px 宽的胶囊：
    /// 封面按 margin(12,0,8,0) 定位，在 36px 圆里偏右 6px，文字/波形整片溢出球外。
    /// </summary>
    private string WantedContentKey(IslandItem item)
    {
        if (_stack.IsExpanded && item.Event.Id == _stack.Active?.Event.Id) return "card";
        return item.Role == IslandRole.Sphere ? "sphere" : "pill";
    }

    /// <summary>
    /// 按 item 当前应有的形态重建内容，并把 <see cref="IslandItem.ContentKey"/> 同步为
    /// 实际装进去的形态。所有「就地重建内容」的地方都必须走它，禁止直接调 BuildContent，
    /// 否则内容形态与 ContentKey 会脱节，且 Render 再也修不回来。
    /// </summary>
    private FrameworkElement? BuildContentFor(IslandItem item)
    {
        var key = WantedContentKey(item);
        var el = key switch
        {
            "card" => BuildCard(item.Event),
            "sphere" => BuildSphere(item.Event),
            _ => BuildContent(item.Event)
        };
        if (el != null) item.ContentKey = key;
        // 子树换了 → 同步（或作废）它的引用与波形柱子。
        // 必须放在 ContentKey 同步之后：ItemContentBuilt 要靠它判断装的是不是胶囊。
        ItemContentBuilt(item, el);
        return el;
    }

    // ---------- 仅用于自检（不参与运行时逻辑）----------
    /// <summary>喂一条真事件并走一帧真实渲染，供 MediaIsolationDiag 端到端验证波形</summary>
    internal void FeedForProbe(Core.IslandEvent ev)
    {
        StartupQueueActive = false;      // 不排队，直接入场
        OnEvent(ev);
        EnsureVisible();                 // 窗口隐藏时事件会被丢弃，这里强制显示
        Render();                        // 真实渲染帧（弹簧 + 波形刷新都在里面）
    }

    internal IReadOnlyList<IslandItem> ProbeItems => _stack.Items;
    internal IslandItem? ProbeActive => _stack.Active;
    internal List<Border>? ProbeWaveBars(string id)
        => _mediaSig.TryGetValue(id, out var mi) ? mi.Bars : null;
    internal Grid? ProbeLyricRow(string id)
        => _mediaSig.TryGetValue(id, out var mi) ? mi.LyricRow : null;

    /// <summary>仅供 DshRenderDiag 渲染自检使用（不参与运行时逻辑）</summary>
    internal static FrameworkElement BuildDshPillPublic(Sources.DshSnapshot? s) => BuildDshPill(s);

    /// <summary>仅供 DshRenderDiag 渲染自检使用（不参与运行时逻辑）</summary>
    internal static FrameworkElement BuildSpherePublic(IslandEvent ev) => BuildSphere(ev);

    /// <summary>
    /// 胶囊宽度公式（<see cref="MeasurePillWidth"/>）——仅供 PillWidthDiag 自检使用。
    ///
    /// 自检要用它来当"胶囊应该有多宽"的基准：把真实内容挂进同宽容器里量需求，
    /// 需求 &gt; 公式即溢出被裁，需求 &lt; 公式即右侧多出空白。
    /// </summary>
    internal double MeasurePillWidthPublic(IslandEvent ev) => MeasurePillWidth(ev);

    /// <summary>胶囊内容（<see cref="BuildContent"/>）——仅供 PillWidthDiag 自检使用</summary>
    internal FrameworkElement? BuildContentPublic(IslandEvent ev) => BuildContent(ev);

    /// <summary>文本实测宽（<see cref="MeasureText"/>）——仅供 PillWidthDiag 自检使用</summary>
    internal static double MeasureTextPublic(string text, double size, bool bold)
        => MeasureText(text, size, bold);

    /// <summary>仅供 DshRenderDiag 渲染自检使用（不参与运行时逻辑）</summary>
    internal FrameworkElement BuildDshCardPublic(Sources.DshSnapshot s) => BuildDshCard(s);

    /// <summary>按事件类型构建胶囊内容</summary>
    private FrameworkElement? BuildContent(IslandEvent ev) => ev.Kind switch
    {
        "media" when ev.Payload is Sources.MediaSnapshot snap => BuildMediaPill(snap),
        "clock" => BuildClockPill(ev),
        "usage" => BuildUsagePill(ev),
        "sys" when ev.Payload is Sources.SysSnapshot snap => BuildSysPill(snap, ev),
        "camera" when ev.Payload is Sources.CameraSnapshot cam => BuildCameraPill(cam),
        "unlock" => BuildUnlockPill(ev),
        "dsh" when ev.Payload is Sources.DshSnapshot ds => BuildDshPill(ds),
        "dsh-retract" => BuildDshPill(null),
        // 系统状态类（电源 / 网络 / 麦克风 / 外观）统一走分部类，返回 null 时回落通知胶囊
        _ => BuildSystemPill(ev) ?? BuildNotificationPill(ev)
    };

    /// <summary>
    /// DSH 工作状态胶囊：状态图标 + 会话标题 + 轮/步 + 上下文占用。
    /// <paramref name="snap"/> 为 null 时表示"收起"（源判定空闲），返回空占位。
    /// </summary>
    private static FrameworkElement BuildDshPill(Sources.DshSnapshot? snap)
    {
        var t = AppTheme.Current;
        if (snap == null)
            return new Grid { Width = 1, Height = 1 };

        bool working = snap.Working;
        var accent = working ? t.Accent : t.Success;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 状态图标：工作中 = sparkles（强调色）；已完成 = check（成功色）
        // 【左内边距 12】此前图标直抵胶囊左边缘，而 MeasurePillWidth 的 "dsh" 分支
        // 一直按 PaddingX*2 计左右内边距 —— 公式给宽了 24、排版却没占位，
        // 于是胶囊右侧凭空多出 24px 空白。两边必须一致，这里补上左内边距。
        row.Children.Add(new Border
        {
            Margin = new Thickness(IslandMetrics.PaddingX, 0, 0, 0),
            Child = Native.SvgIcons.CreateCentered(
                working ? "sparkles" : "check", 18, accent, 1.2)
        });

        // 【子代理徽标】DSH 把每个子代理建成独立 session，所以子代理干活时本身
        // 就是一条 Working 会话、天然出独立岛；但它的 title 多半继承自父会话，
        // 看起来像「主会话卡住了」。加徽标让用户一眼看出是子代理在干活。
        if (snap.IsSubagent)
            row.Children.Add(BuildSubagentTag(t, snap.SubagentMode));


        var text = new StackPanel
        {
            // 左间距 8（与公式的 "+ 8" 对应）+ 右内边距 12（与公式的 PaddingX 对应）
            Margin = new Thickness(8, 0, IslandMetrics.PaddingX, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // 【文字列定宽，而不是各自设 MaxWidth】
        // 此前两行各自 MaxWidth = MaxWidth - 120：胶囊被 MaxWidth 截断时，
        // 两行都按"刚好等于上限"排版 → 又落入"内容宽 == 可用宽"必然出省略号的坑，
        // 且两行右端不齐。改为共用一个"文本列宽 = 胶囊上限 - 图标 - 间距 - 左右内边距"，
        // 由 MeasurePillWidth 的同名公式决定，两边永远一致。
        double textColW = DshPillTextColWidth();
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(snap.Title) ? "DSH 工作中" : snap.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = t.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = textColW
        });
        text.Children.Add(new TextBlock
        {
            Text = DescribeDsh(snap),
            FontSize = 11,
            Foreground = t.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = textColW
        });
        row.Children.Add(text);

        return row;
    }

    /// <summary>子代理徽标（"子代理" + 模式简写），仅子代理会话显示</summary>
    /// <summary>子代理徽标宽度（与 BuildSubagentTag 的排版保持一致）</summary>
    private static double SubagentTagWidth(string mode)
    {
        var txt = string.IsNullOrWhiteSpace(mode) ? "子代理" : "子代理·" + mode;
        // 左间距 8 + 左右内边距 6+6
        return 8 + 12 + MeasureText(txt, 9.5, true);
    }

    private static FrameworkElement BuildSubagentTag(ThemeTokens t, string mode)
    {
        var txt = string.IsNullOrWhiteSpace(mode) ? "子代理" : "子代理·" + mode;
        var bd = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            Background = t.Accent,
            VerticalAlignment = VerticalAlignment.Center
        };
        bd.Child = new TextBlock
        {
            Text = txt,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };
        return bd;
    }

    /// <summary>DSH 副标题：轮/步 · 上下文 % · 模型</summary>
    private static string DescribeDsh(Sources.DshSnapshot snap)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (snap.Working)
            // Stepping=false 且 Working=true = 正在跑工具（openStep 已清空但 turn 未结束）
            parts.Add(snap.Stepping
                ? $"第 {Math.Max(snap.Turn, 1)} 轮 · 步 {snap.Step}"
                : $"第 {Math.Max(snap.Turn, 1)} 轮 · 执行中");
        else
            parts.Add($"第 {Math.Max(snap.Turn, 1)} 轮 · 共 {snap.Steps} 步");

        if (snap.ContextWindow > 0) parts.Add($"上下文 {snap.ContextPercent}%");
        if (!string.IsNullOrWhiteSpace(snap.Model))
        {
            var m = snap.Model;
            var i = m.LastIndexOf('/');
            if (i >= 0 && i < m.Length - 1) m = m[(i + 1)..];
            parts.Add(m);
        }
        return string.Join(" · ", parts);
    }

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

        // 【右内边距 12】此前"开/关"之后没有任何右留白，内容直抵胶囊圆角边缘。
        // 宽度公式（TogglePillWidth）已把这 12 计入，这里补上，两边必须一致。
        //
        // 【有 tail 时右内边距交给 tail 承担】否则会出现 12 + 7 = 19 的间距，
        // 与宽度公式算的 7 对不上（公式与排版脱节 → 尾部凭空少一截）。
        panel.Children.Add(new TextBlock
        {
            Text = on ? "开" : "关",
            FontSize = 11.5,
            Margin = new Thickness(7, 0, string.IsNullOrWhiteSpace(tail) ? 12 : 0, 0),
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

        // 【标题与正文都设了 TextTrimming】
        // 因此宽度公式（MeasurePillWidth 的默认分支）为它们各留了 EllipsisSlack。
        // 但正文右边的 8 是"与标题的间距"，省略号必须出现在它**之外**，
        // 所以正文自己的右内边距与标题一致（都是 12），保持右边线对齐。
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
                Text = ev.Subtitle, FontSize = 11.5, Margin = new Thickness(8, 0, 12, 0),
                Foreground = AppTheme.Current.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        else
        {
            // 无正文时右内边距由标题承担
            title.Margin = new Thickness(0, 0, 12, 0);
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
        // 【右内边距 12】此前时间后面没有任何右留白，内容直抵胶囊圆角边缘。
        // MeasurePillWidth 的 "clock" 分支已把这 12 计入宽度，两边必须一致，
        // 否则胶囊右侧会凭空多出一块空白。
        panel.Children.Add(new TextBlock
        {
            Text = ev.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Current.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
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
        // 【右内边距 12】与 MeasurePillWidth 的 "usage" 分支保持一致
        panel.Children.Add(new TextBlock
        {
            Text = ev.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.Current.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        return panel;
    }

    /// <summary>
    /// 使用时长详情卡（展开态）：本周使用时长树状图。
    /// 结构：
    ///   ① 顶部一行汇总（本周合计 + 日均）
    ///   ② 每天一个节点：日期 + 柱状条 + 当天合计
    ///      柱内再按应用拆分（树状图的第二层），条宽按占比，
    ///      只显示当天用得最多的前 3 个应用，其余归入"其他"。
    /// </summary>
    private FrameworkElement BuildUsageCard(IslandEvent ev)
    {
        var t = AppTheme.Current;
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        var week = Sources.UsageTracker.LoadWeek(DateTime.Today);
        var total = week.Where(d => d.HasStarted).Aggregate(TimeSpan.Zero, (a, d) => a + d.Total);
        var elapsedDays = week.Count(d => d.HasStarted);
        var avg = elapsedDays > 0 ? total / elapsedDays : TimeSpan.Zero;

        // ---- 顶部汇总 ----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Background = t.AccentGradient, Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Native.SvgIcons.CreateCentered("timer", 14, Brushes.White, 1.3)
        });
        var headTxt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headTxt.Children.Add(new TextBlock
        {
            Text = $"本周已用 {FmtH(total)}", FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = t.TextPrimary
        });
        headTxt.Children.Add(new TextBlock
        {
            Text = $"{week[0].Date:MM/dd} – {week[6].Date:MM/dd} · 已过 {elapsedDays} 天 · 日均 {FmtH(avg)}",
            FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
            Foreground = t.TextSecondary
        });
        Grid.SetColumn(headTxt, 1);
        head.Children.Add(headTxt);
        root.Children.Add(head);

        // ---- 每天一行：周一 → 周日 固定 7 行，没有数据的日子照样占位 ----
        // 条宽以"本周最长的一天"为基准（而不是全周平均或固定值），
        // 因此周初几天没数据也不会压缩后面的条形，分析不受影响。
        double maxTotal = week.Count > 0 ? week.Max(d => d.Total.TotalMinutes) : 0;
        if (maxTotal <= 0) maxTotal = 1;   // 避免除零

        // 【必须显式建 7 个 RowDefinition】Grid 不定义行时所有子元素都叠在第 0 行，
        // 表现为"7 天的条全部重叠在顶部"（曾只看到一根条 + 一天的字）。
        var weekGrid = new Grid();
        for (int i = 0; i < week.Count; i++) weekGrid.RowDefinitions.Add(new RowDefinition());
        // 【条形按比例填充，不再传宽度】BuildWeekRow 内部用 GridLength(x, Star) 分配，
        // 容器多宽都自适应 —— 不需要（也不应该）在这里算一个像素宽度传进去。
        // 曾经这里算 cardBarMax 传入，但算出来的值和真实列宽永远对不上：
        // 卡宽被 ClusterStack.Clamp 到 MaxWidth（不是 CardWidth）、再叠加 LayoutTransform
        // 的缩放口径，条形溢出被裁 →「树状图展示不全」。
        for (int i = 0; i < week.Count; i++)
        {
            var row = BuildWeekRow(week[i], maxTotal, t);
            Grid.SetRow(row, i);
            weekGrid.Children.Add(row);
        }
        root.Children.Add(weekGrid);

        // 【忽略按钮】使用时长岛 Ttl=null 是常驻的（改TTL 会导致"还没悬停就消失"），
        // 但常驻岛必须有手动关闭入口，否则用户无法让它离开屏幕 ——
        // 表现为"没有忽略按钮，导致使用时长岛常驻"。
        var acts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        acts.Children.Add(MakeTextButton("忽略", t, true, () =>
        {
            DismissActive();
            return Task.CompletedTask;
        }));
        acts.Children.Add(MakeTextButton("数据目录", t, false, () =>
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ChronoIsland");
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception ex) { App.Log("Usage", ex); }
            return Task.CompletedTask;
        }));
        root.Children.Add(acts);

        return root;
    }

    /// <summary>
    /// 周视图中的一天：周几 + 堆叠条（按应用） + 合计时长。
    /// 【不需要传容器宽度】条形按比例填充本列，容器多宽都自适应
    /// （灵动岛卡 / 设置页共用此方法，两边列宽差很多）。
    /// </summary>
    public static FrameworkElement BuildWeekRow(Sources.UsageDay d, double maxTotal, ThemeTokens t)
    {
        var row = new Grid { Height = 22, Margin = new Thickness(0, 1.5, 0, 1.5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });   // 周几
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 条
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });  // 合计

        bool future = d.IsFuture;
        bool today = d.Date == DateTime.Today;
        string[] wd = { "一", "二", "三", "四", "五", "六", "日" };
        row.Children.Add(new TextBlock
        {
            Text = wd[((int)d.Date.DayOfWeek + 6) % 7],   // 周一=0 … 周日=6
            FontSize = 11,
            FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = future ? t.TextTertiary : (today ? t.Accent : t.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center
        });

        // 条形容器：固定行高。ClipToBounds 仅作兜底（比例布局本就不会溢出）。
        var barHost = new Grid { Height = 18, VerticalAlignment = VerticalAlignment.Center };
        barHost.ClipToBounds = true;
        if (d.Total > TimeSpan.Zero)
        {
            double scale = d.Total.TotalMinutes / maxTotal;   // 当天条宽占本列的比例（0..1）
            var top = d.Apps.OrderByDescending(kv => kv.Value).Take(3).ToList();
            TimeSpan restTotal = d.Total - top.Aggregate(TimeSpan.Zero, (a, kv) => a + kv.Value);
            var brushes = new Brush[] { t.Accent, t.Success, t.Warning,
                                       new SolidColorBrush(Color.FromArgb(120, 140, 140, 152)) };

            // 【全比例布局，不写死像素】两层 GridLength(x, Star)：
            //   外层 track = [条形][留白]，比例 scale : (1-scale) → 条形占本列多少由 scale 决定
            //   内层 segGrid = 各应用按占比分列                    → 条内分段比例由数据决定
            // WPF 在布局时按【列的真实像素宽】分配，因此容器多宽、缩放多少、
            // 卡片被 Clamp 到多窄，条形都精确填满应有的比例，绝不会溢出被裁。
            //
            // 之前是先算 totalW = barMax × scale 再给每段写死 Width(px)，barMax 由调用方传：
            // 只要传的值和真实列宽对不上（卡宽被 ClusterStack.Clamp 到 MaxWidth 而非 CardWidth、
            // 设置页 ActualWidth 含滚动条与页面边距……），条就溢出被 ClipToBounds 裁掉，
            // 表现为「树状图展示不全」——末尾的应用分段整个消失。
            var segs = new List<(double Share, Brush Bg, string Tip)>();
            int i = 0;
            foreach (var kv in top)
            {
                double share = kv.Value.TotalMinutes / Math.Max(0.001, d.Total.TotalMinutes);
                segs.Add((share, brushes[i % brushes.Length], $"{kv.Key} · {FmtH(kv.Value)}"));
                i++;
            }
            if (restTotal > TimeSpan.Zero)
            {
                double share = restTotal.TotalMinutes / Math.Max(0.001, d.Total.TotalMinutes);
                segs.Add((share, brushes[3], $"其他 {top.Count} 个应用 · {FmtH(restTotal)}"));
            }

            var track = new Grid();
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(scale, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - scale), GridUnitType.Star) });

            var segGrid = new Grid();
            foreach (var sg in segs)
                segGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sg.Share, GridUnitType.Star) });

            // 【圆角按"首段只圆左、末段只圆右"分配】
            // 之前写的是 i==0 四角全圆、其余全方，两个毛病：
            //   · 首段右角也圆了，但它的右边还接着别的段 → 交界处圆角接方角，有道小缺口；
            //   · 末段（没有"其他"灰段时就是最后一个应用）四角全方 → 条形末端该圆却是方的。
            // 只有单段时首=末，四角全圆（正好是一颗胶囊）。
            for (int s = 0; s < segs.Count; s++)
            {
                bool first = s == 0, last = s == segs.Count - 1;
                var seg = new Border
                {
                    Height = 18, Background = segs[s].Bg,
                    // CornerRadius(topLeft, topRight, bottomRight, bottomLeft)
                    CornerRadius = new CornerRadius(
                        first ? 3 : 0,     // 左上
                        last ? 3 : 0,      // 右上
                        last ? 3 : 0,      // 右下
                        first ? 3 : 0)     // 左下
                };
                seg.ToolTip = segs[s].Tip;
                Grid.SetColumn(seg, s);
                segGrid.Children.Add(seg);
            }
            Grid.SetColumn(segGrid, 0);
            track.Children.Add(segGrid);
            barHost.Children.Add(track);
        }
        else
        {
            barHost.Children.Add(new Border
            {
                Width = d.HasStarted ? 3 : 0, Height = 3,
                Background = t.RowSurface, CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left
            });
        }
        Grid.SetColumn(barHost, 1);
        row.Children.Add(barHost);

        // 【必须显式指定第 2 列】不写 Grid.SetColumn 会默认落在第 0 列，
        // 于是「合计」压在「周次」底下（同一列叠在一起），
        // 右侧列宽再大也永远是空白 —— 表现为时长根本没显示。
        var durText = new TextBlock
        {
            Text = future || !d.HasStarted ? "—" : FmtH(d.Total),
            FontSize = 10.5,
            // 定宽不换行：宁可省略也不撑破列宽压到左侧「周次」上
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = future || !d.HasStarted ? t.TextTertiary : (today ? t.Accent : t.TextSecondary),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(durText, 2);
        row.Children.Add(durText);
        return row;
    }

    /// <summary>时长格式化：不足 1 小时用「Xm」，否则「Xh Ym」</summary>
    private static string FmtH(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero) return "0m";
        int h = (int)ts.TotalHours;
        int m = ts.Minutes;
        return h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }

    // ---------- 波形 ----------

    /// <summary>波形一根柱子在一个周期内的往返时长（毫秒）。四根依次错开 1/4 周期。</summary>
    private const double WavePeriodMs = 1800.0;
    /// <summary>柱子最低/最高高度（DIP）</summary>
    private const double WaveBarMin = 4.0;
    private const double WaveBarMax = 13.0;

    /// <summary>
    /// 波形的相位时钟（秒）。**全进程只有一个，与任何子树的生命周期无关。**
    ///
    /// 【为什么必须是静态的】柱高 = f(相位)，相位必须只由"现在几点"决定。
    /// 只要它跟某棵树、某个动画或者某次重建绑定，重建就会把整块波形拉回起点
    /// （旧实现就是如此：动画 BeginTime 从重建那刻重新起算）。
    /// 用 <see cref="Stopwatch"/> 取单调时钟，不受系统时间调整影响。
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch WaveClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// 第 <paramref name="i"/> 根柱子在当前相位下的高度。
    ///
    /// 【纯函数】同一时刻调用任意次、在任意一根柱子上调用，结果都相同 ——
    /// 这正是"重建不再闪"的根据：新建的四根柱子第一次渲染就落在正确高度上。
    /// 相位取 <see cref="WavePeriodMs"/> 的模，因此时间再长也不会丢精度。
    /// </summary>
    private static double WaveBarHeight(int i, bool playing)
    {
        if (!playing) return WaveBarMin;
        double phase = (WaveClock.Elapsed.TotalMilliseconds + i * WavePeriodMs / 4.0) % WavePeriodMs;
        double tri = phase < WavePeriodMs / 2
            ? phase / (WavePeriodMs / 2)               // 0 → 1
            : (WavePeriodMs - phase) / (WavePeriodMs / 2);   // 1 → 0
        return WaveBarMin + tri * (WaveBarMax - WaveBarMin);
    }

    /// <summary>
    /// 每帧刷新波形柱高（由 <see cref="Render"/> 调用）。
    ///
    /// 只有"当前胶囊里装着的、正在播放的媒体"才刷新：侧列球体、展开卡、
    /// 以及已经不在视觉树上的旧子树一律跳过 —— 后者的引用已由
    /// <see cref="ItemContentRemoved"/> 摘除。
    /// </summary>
    private void UpdateWaveBars()
    {
        // 【为什么按 event.Id 查表，而不是把引用挂在 IslandItem 上】
        // UpdateItem 是"就地替换事件"，IslandItem 实例在一条岛的整个生命周期里
        // 都是同一个；而条目的 Id 也就是 event.Id，同样稳定。
        // 两者的区别只在岛被收起时：条目随 Class 移除，表项也一并删掉（见
        // CollapseAndRemove）—— 用表能顺手把不再需要的引用清干净，不留悬挂引用。
        var active = _stack.Active;
        if (active == null || active.ContentKey != "pill") return;

        var mi = _mediaSig.GetValueOrDefault(active.Event.Id);
        if (mi?.Bars is not { Count: 4 } bars) return;

        // 播放态从"最新快照"取，而不是用建树时的旧快照：
        // 这样点暂停后波形立刻静止，不必等下一次整树重建。
        bool playing = (active.Event.Payload as Sources.MediaSnapshot)?.IsPlaying ?? false;
        for (int i = 0; i < 4; i++)
        {
            double h = WaveBarHeight(i, playing);
            if (Math.Abs(bars[i].Height - h) < 0.05) continue;
            bars[i].Height = h;
        }
    }

    /// <summary>
    /// 媒体胶囊 id（= 这条岛的事件 Id）。与 <c>MediaSource.Emit</c> 的
    /// <c>"media:" + SourceId</c> 同口径，是"这条胶囊的子树"的索引键。
    /// </summary>
    private static string MediaEventId(Sources.MediaSnapshot snap) => "media:" + snap.SourceId;

    /// <summary>
    /// 一条媒体胶囊"当前装着的子树"的引用。按 event.Id 存放 ——
    /// A 会话的刷新因此永远不会摸到 B 会话的列宽。
    ///
    /// 【曾经的写法与症状】这些引用原本是窗口级的共享字段
    /// （<c>_pillGrid / _pillTextCol / _pillLyricText / _pillLyricRow</c>），
    /// 指向"最后一次 BuildMediaPill 建出来的那棵树"。而 <c>ApplyMediaPillColumns</c>
    /// 会被**任意一条**媒体事件调用（UpdateItem 对每条 media item 都执行
    /// MeasurePillWidth → MediaPillWidth），于是两条媒体会话并存时，
    /// 后刷新那条的列宽被写进了**正在显示的另一条**胶囊里。
    /// </summary>
    private sealed class MediaSignature
    {
        public Grid? Grid;
        public FrameworkElement? TextCol;
        public TextBlock? LyricText;
        public Grid? LyricRow;
        /// <summary>当前这一句歌词（空串 = 无歌词行）</summary>
        public string LastLyricText = "";
        /// <summary>波形宿主与四根柱子</summary>
        public StackPanel? Wave;
        public List<Border>? Bars;
    }

    private readonly Dictionary<string, MediaSignature> _mediaSig = new();

    /// <summary>取（必要时创建）某条媒体胶囊的子树引用表</summary>
    private MediaSignature MediaSigFor(string id)
    {
        if (!_mediaSig.TryGetValue(id, out var mi))
        {
            mi = new MediaSignature();
            _mediaSig[id] = mi;
        }
        return mi;
    }

    /// <summary>
    /// 内容被构建出来时登记它携带的"子树引用"。非媒体内容会清掉旧引用 ——
    /// 换装成球体 / 详情卡后，旧的胶囊子树已经不在视觉树上，引用必须一并作废。
    /// </summary>
    private void ItemContentBuilt(IslandItem item, FrameworkElement? content)
    {
        if (item.Event.Kind == "media" && item.ContentKey == "pill"
            && content is Grid g && item.Event.Payload is Sources.MediaSnapshot snap)
        {
            var mi = MediaSigFor(MediaEventId(snap));
            mi.Grid = g;      // BuildMediaPill 内部已把 TextCol/歌词/柱子登记到同一张表上
        }
        else if (item.Event.Kind == "media")
        {
            ItemContentRemoved(item);
        }
        SyncSystemContentRefs(item, content);
    }

    /// <summary>
    /// 这条 item 的媒体子树已脱离视觉树（换装 / 收起 / 移除）。
    /// 只清引用，不删表项：<c>MediaSig</c> 与"上次签名"必须留着，
    /// 否则重新装回胶囊时会被当成新内容而触发一次多余重建。
    /// </summary>
    private void ItemContentRemoved(IslandItem item)
    {
        if (item.Event.Kind != "media") return;
        if (!_mediaSig.TryGetValue(item.Event.Id, out var mi)) return;
        mi.Grid = null;
        mi.TextCol = null;
        mi.LyricText = null;
        mi.LyricRow = null;
        mi.Wave = null;
        mi.Bars = null;
        mi.LastLyricText = "";
    }

    /// <summary>
    /// 把文字列宽同步到**指定那条**媒体胶囊的第 1 列与其 textCol。
    /// 唯一调用者是 <see cref="MediaPillWidth"/>，保证两件事同时成立：
    ///   · "容器宽 = 三列之和"这一不变量（对每条岛各自成立）；
    ///   · 宽度只落在自己的子树上 —— 两条媒体岛并存时互不影响。
    /// <paramref name="id"/> 对应的子树尚未建好时（例如它此刻是球体）静默跳过：
    /// 等它装回胶囊时 BuildMediaPill 会按当时的宽度重新建列，不会留下错宽度。
    /// </summary>
    private void ApplyMediaPillColumns(string id, double textW)
    {
        if (!_mediaSig.TryGetValue(id, out var mi)) return;
        if (mi.Grid != null && mi.Grid.ColumnDefinitions.Count >= 2)
            mi.Grid.ColumnDefinitions[1].Width = new GridLength(textW);
        if (mi.TextCol != null) mi.TextCol.Width = textW;
    }

    /// <summary>媒体胶囊：封面 + 歌名 + 艺术家 + 波形</summary>
    private FrameworkElement BuildMediaPill(Sources.MediaSnapshot snap)
    {
        var grid = new Grid();
        // 【三列宽度全部显式给定，不用 Auto / Star】
        // Grid 的 Auto 列永远不会缩小：标题一长就把 Star 列挤成 0，
        // TextTrimming 因此永远不触发（文本被硬裁，而不是省略号），
        // 内容还会直接溢出胶囊右边界。
        // 这里把各列宽度算成固定像素值，文字列因此有确定的可用宽度，
        // 超长标题会正确省略、超宽歌词会正确触发滚动。
        // 歌词宽度取「全曲代表性宽度」而非"当前这句"，保证容器与列宽始终一致（见 TrackLyricWidth）
        // 【列宽必须与容器宽同源】
        // 先按 MediaPillWidth 算出"最终生效的容器宽"，再由它反推文字列宽。
        // 以前这里独立调用 MediaPillTextWidth，和 MediaPillWidth 各算一遍；
        // 而歌词宽度依赖当前播放位置，两次调用可能落在不同歌词行，
        // 于是 columns[1] 与容器宽失配 → 文字列溢出到波形列上。
        double totalW = MediaPillWidth(snap, TrackLyricWidth(snap));   // 内部已同步列宽
        double textW = Math.Max(60, totalW - PillCoverColW - PillWaveColW - PillTailPad);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PillCoverColW) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(textW) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PillWaveColW + PillTailPad) });

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

        // ---------- 右侧文字列：上行（标题+作者） / 下行（歌词）----------
        // 布局：图标左固定不动，歌词与歌名作者统一排在图标右侧。
        // 【宽度必须受控】文字列是定宽列：外层 Grid 已按 textW 给了确定宽度，
        // 这里再钉一个 MaxWidth 兜底，绝不让内容顶出胶囊右边界。
        var textCol = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        textCol.Width = textW;
        textCol.ClipToBounds = true;

        // 上行：标题 + 作者（横向）。
        // 【标题列必须能被压缩】用 Star 而不是 Auto：Auto 列不会收缩，
        // 标题过长时会挤掉作者并溢出容器（此时 TextTrimming 根本不触发）。
        // Star 列会被压缩到可用宽度，省略号才能正常出现。
        //
        // 【但整行宽度必须夹到"元信息自身所需"】否则 Star 列会把 textW 里所有余量
        // 全部吞掉并推给最右端：作者是 Auto 列 → 被顶到 textCol 右缘，
        // 标题字形左对齐 → 中间就空出一大片。而 textW = Max(metaW, lyricW) + 18，
        // 余量随歌词长度变化，所以"标题↔作者间距"会随当时那句歌词忽大忽小；
        // 长歌词时作者还会紧贴波形（列宽 22+6+8 零余量），看起来就是"挤了波形"。
        //
        // 夹到 metaW 后：Star 列宽 == 标题自身宽，作者紧跟标题（间距恒为 margin 的 7px），
        // 余量全部留在本行右端（作者与波形之间）—— 既恒定又给波形留出呼吸空间。
        // 夹到 textW 后：标题超长时照常省略，作者仍紧跟其后，不会溢出。
        double titleW = MeasureText(snap.Title, 12.5, true);
        double artistW = string.IsNullOrWhiteSpace(snap.Artist) ? 0 : MeasureText(snap.Artist!, 11) + 7;
        double metaW = titleW + artistW;

        // 【本行宽 = 元信息 + EllipsisSlack，而不是恰好等于 metaW】
        // WPF 判断"要不要加省略号"时，会把末尾 "…" 字形的宽度也一起算进可用宽度里。
        // 因此内容宽度与可用宽度**恰好相等**时它仍会截断 —— 表现为
        // 「量得下却显示成 Leehom W…」。余量必须留出至少一个省略号字宽。
        //
        // 余量放在【作者与波形之间】，而不是作者与标题之间：
        // 作者仍是 Auto 列紧跟标题（间距恒为 Margin 的 7），
        // 多出来的宽度全部堆在本行右端，不改变既有排版观感。
        double metaRowW = Math.Min(textW, metaW + EllipsisSlack);
        var metaRow = new Grid { Width = metaRowW, HorizontalAlignment = HorizontalAlignment.Left };
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
                VerticalAlignment = VerticalAlignment.Center,
                // 作者宽度上限 = 整行实际可用 - 标题所需宽 - 7 间距。
                // 标题优先占满，作者按剩余空间收缩（省略号随之出现）。
                // 【下限不能给 44】metaRow 现已夹到 metaW + EllipsisSlack，若标题很长、
                // 真实剩余不足 44，44 的下限会让 Auto 列抢走超过可用的宽度，把标题额外压扁；
                // 这里只用一个小下限防止完全塌成 0。
                // 【与 metaRowW 同一口径】两处必须一致，否则作者列可用宽度会
                // 超过本行实际宽度 → 作者被挤出胶囊右边界。
                MaxWidth = Math.Max(16, metaRowW - titleW - 7)
            };
            Grid.SetColumn(artist, 1);
            metaRow.Children.Add(artist);
        }
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

        Grid.SetColumn(textCol, 1);
        grid.Children.Add(textCol);

        // 【关键】引用与列宽全部按 item 记账，且"先记账、后同步列宽"。
        // ApplyMediaPillColumns 要靠 event.Id 反查"这条 item 的"子树，所以引用
        // 必须在调用之前就位 —— 旧代码里"构建早期 _pillGrid 还是旧的/空的，
        // 引用就位后再同步一次"的那次兜底调用，正是把**别的** item 的列宽
        // 写进来的入口（两条媒体岛并存时，标题截断与波形位置就会来回跳）。
        var id = MediaEventId(snap);
        var mi = MediaSigFor(id);
        mi.Grid = grid;
        mi.TextCol = textCol;
        mi.LyricText = lyricText;
        mi.LyricRow = lyricRow;
        mi.LastLyricText = "";       // 新元素是空的：强制下一帧重新赋值，避免歌词空白
        ApplyMediaPillColumns(id, Math.Max(60, totalW - PillCoverColW - PillWaveColW - PillTailPad));

        // ---------- 波形 ----------
        // 位置：胶囊右侧、整体垂直居中。用 Grid 叠一层（独立于文字列），
        // 按整个胶囊高度居中，不受标题行高度影响（以前放在 metaRow 里，行矮就显得贴顶）。
        // 宽度明确固定，右 Margin 与岛内边距(12)对齐，并贴右 —— 永远不越界。
        var wave = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // 右 Margin 必须等于 PillTailPad：胶囊宽度里已按它预留了留白，
            // 波形本身放在第 2 列（宽 PillWaveColW = 22 + 6）刚好填满。
            Margin = new Thickness(6, 0, PillTailPad, 0),
            Width = 22,                                  // 4×2.5 + 3×2(间距) ≈ 16，留余量
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 【0.0.2 修正 · 波形"闪"的真正来源】
        //
        // 旧做法给每根柱子挂一个 DoubleAnimation，BeginTime 错开 0/150/300/450ms。
        // 这种动画一旦子树被重建就会**从零重新起拍**：重建瞬间四根柱子一起归位，
        // 再依次长起来 —— 看起来就是波形整块塌一下（图一 ↔ 图二）。
        // 而重建在播放期间是周期性发生的（媒体每秒推送 + 任何需要重建的变更）。
        // 当时用"按播放位置注入初始高度"来补偿，但那个相位和动画自己的时间轴
        // （从重建时刻起算）根本不是同一个东西，等于补偿完又被动画覆盖。
        //
        // 现在改为【一个动画都不挂】：柱高由 Render() 每帧按"频谱相位"直写。于是
        //   · 重建后不需要任何相位补偿 —— 根本没有需要补偿的时间轴；
        //   · 相位只有一个来源（见 WaveBarHeight 的静态时钟），同一时刻永远
        //     算出同一组高度，与"这条子树是什么时候建的"完全无关；
        //   · 暂停时直接写 4，波形自然静止，不需要额外分支。
        // 顺带解决旧做法的另一个隐患：窗口隐藏时 WPF 会停掉时钟，恢复显示后
        // 动画时间轴已经跑偏，四根柱子的相对相位会对不上。
        var bars = new List<Border>(4);
        for (int i = 0; i < 4; i++)
        {
            bars.Add(new Border
            {
                Width = 2.5, CornerRadius = new CornerRadius(1.25),
                Background = AppTheme.Current.Accent,
                Height = WaveBarHeight(i, snap.IsPlaying),
                Margin = new Thickness(0.8, 0, 0.8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        foreach (var bar in bars) wave.Children.Add(bar);
        Grid.SetColumn(wave, 2);            // 岛的第 3 列（右端）
        grid.Children.Add(wave);

        // 【无条件登记】重建后换上新的一组柱子，首建时也必须有引用 ——
        // 否则 UpdateWaveBars 拿不到 bars 直接返回，波形会**冻住不再动**。
        // （这里曾经错写成 `if (mi.Wave != null)`，而首建时它恰好是 null。）
        mi.Wave = wave;
        mi.Bars = bars;
        return grid;
    }

    /// <summary>
    /// 测量一段文本排版后的自然宽度 —— **与 TextBlock 的实际排版同源**。
    ///
    /// 【为什么不用 FormattedText】这是本项目踩过最深的一个坑，分两层：
    ///
    /// ① <see cref="FormattedText"/> 不做字体回退。胶囊里的 TextBlock 没显式设
    ///    FontFamily，走的是 SystemFonts.MessageFontFamily（Segoe UI），
    ///    Segoe UI **不含任何中文字形**，渲染时 WPF 会自动回退到雅黑把中文画出来；
    ///    而 FormattedText 只按给定的 Typeface 量、不回退，中文被按缺字宽度量出来。
    ///    实测歌名「心跳」12.5px：只给 Segoe UI → 13.8 DIP（实际应约 25），
    ///    少了近一半。少量一半意味着所有含中文的胶囊宽度被系统性低估：
    ///      · 胶囊太窄 → 内容越过右边界被硬裁（图二：时钟的「10月16日 周四」少一截）
    ///      · 内容列太窄 → 标题/作者被省略号截断（图一：「Leehom W…」）
    ///
    /// ② 即使补上字体列表，FormattedText 的字形推进/舍入仍与 TextBlock 的
    ///    排版引擎有 3–8 DIP 的出入，恰好落在"要不要省略号"的判定边界上。
    ///    用它算宽就会出现"公式说放得下、实际还是被截"。
    ///
    /// 【现在怎么做】直接用 TextBlock 自己 Measure：同一个控件类型、同一套字体回退、
    /// 同一套排版引擎，量出来的就是它真正会占的宽度，因此公式与排版不可能再脱节。
    /// 代价是每次测量要建一个临时 TextBlock —— 这些测量只发生在
    /// 事件到达 / 歌词换行 / 卡片构建时，不是每帧热路径。
    ///
    /// 【无约束是关键】宽度给 PositiveInfinity，TextTrimming 才不会触发，
    /// 量到的正是"内容不被截断所需的宽度"。
    /// </summary>
    private static double MeasureText(string text, double fontSize, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None,
                // 与胶囊内的 TextBlock 对齐：同样的默认字体 + 同样的行距行为
                LineHeight = double.NaN,
                LineStackingStrategy = LineStackingStrategy.MaxHeight,
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = tb.DesiredSize.Width;
            return w > 0 && !double.IsInfinity(w) ? w : EstimateWidth(text, fontSize);
        }
        catch
        {
            // 兜底：按"拉丁 0.55em / CJK 1em"估算
            return EstimateWidth(text, fontSize);
        }
    }

    /// <summary>
    /// 无字体环境下的粗略估算：CJK 与全角标点按 1.0em，其余按 0.55em。
    /// 仅在 <see cref="MeasureText"/> 抛异常时兜底，正常路径不会走到。
    /// </summary>
    private static double EstimateWidth(string text, double fontSize)
    {
        double w = 0;
        foreach (char c in text)
            w += IsWideChar(c) ? fontSize : fontSize * 0.55;
        return w;
    }

    /// <summary>是否 CJK / 全角字符（全角标点也算，它们同样是 1em 宽）</summary>
    private static bool IsWideChar(char c)
        => (c >= '\u1100' && c <= '\u115F')   // 谚文字母
        || (c >= '\u2E80' && c <= '\uA4CF')   // CJK 部首、假名、汉字
        || (c >= '\uAC00' && c <= '\uD7A3')   // 谚文音节
        || (c >= '\uF900' && c <= '\uFAFF')   // CJK 兼容表意
        || (c >= '\uFE30' && c <= '\uFE6F')   // CJK 兼容形式
        || (c >= '\uFF00' && c <= '\uFF60')   // 全角 ASCII
        || (c >= '\uFFE0' && c <= '\uFFE6');  // 全角符号

    /// <summary>
    /// 胶囊（中央位）的目标宽度 —— **未缩放口径**，渲染时乘 <see cref="IslandMetrics.Scale"/>。
    ///
    /// 【铁律】本函数的每一个加项都必须与对应的 <c>Build*Pill()</c> 里的
    /// 元素排版一一对应（图标尺寸、Margin、字号、TextTrimming）。
    /// 一旦两边脱节，症状分两种，都会被用户当成"岛的长度不对"：
    ///   · 算得比排版窄 → 内容越过胶囊右边界被硬裁（图二：时钟「10月16日 周四」少一截）
    ///   · 算得比排版宽 → 右侧凭空多出一块空白
    ///
    /// 【曾经踩过的坑】旧实现用「字数 × 7.2」估算宽度，而排版用 FormattedText 实测：
    /// 中文与拉丁数字的字宽差异可达 2 倍，于是估算系统性偏窄 → 右侧被裁。
    /// 现已全部改为 FormattedText 实测。
    ///
    /// 【省略号余量】凡该位置设了 TextTrimming，就额外加 <see cref="EllipsisSlack"/>：
    /// WPF 判断是否加省略号时会把 "…" 字形算进可用宽度，余量为 0 时"量得下也被截"。
    /// </summary>
    private double MeasurePillWidth(IslandEvent ev)
    {
        // 系统状态类（电源 / 网络 / 麦克风 / 外观）的宽度在分部类里算，
        // 口径与该文件里的 StatusPill 排版严格一一对应。
        var systemW = MeasureSystemPillWidth(ev);
        if (systemW is double sw) return Math.Clamp(sw, IslandMetrics.MinWidth, IslandMetrics.MaxWidth);

        // 各胶囊的右内边距（与 Build*Pill 中最后一个元素的右 Margin 一致）：
        //   0 → 内容紧贴右边缘，几乎不留白（历史上的做法，观感尚可但显拥挤）
        //   1 → 补一个右内边距，让左右对称（内边距不足的胶囊宽度同样要补）
        double w = ev.Kind switch
        {
            // ---- 媒体：走三列口径，必须与 BuildMediaPill 完全一致 ----
            "media" when ev.Payload is Sources.MediaSnapshot ms
                => MediaPillWidth(ms, TrackLyricWidth(ms)),

            // ---- DSH：图标 18 + 文字左边距 8 + 两行取宽 + 左右内边距 ----
            // BuildDshPill 的图标是 CreateCentered(18) → 实际占位 18，两行都有
            // TextTrimming → 取宽者再加 EllipsisSlack。
            // 子代理徽标也要计入宽度（BuildSubagentTag：字号 9.5 半粗 + 左右各 6 内边距 + 左间距 8）
            "dsh" when ev.Payload is Sources.DshSnapshot ds
                => 18 + 8 + (ds.IsSubagent ? SubagentTagWidth(ds.SubagentMode) : 0) + Math.Max(
                        MeasureText(ds.Title.Length > 0 ? ds.Title : "DSH 工作中", 12.5, true),
                        MeasureText(DescribeDsh(ds), 11))
                   + EllipsisSlack + IslandMetrics.PaddingX * 2,

            // dsh-retract：BuildDshPill(null) 返回 1×1 空占位，按正圆收
            "dsh-retract" => IslandMetrics.MinWidth,

            // ---- 键位 / 摄像头：BuildTogglePill ----
            // 键帽 22 + 左右 Margin(11+9) + 名称 13 + 间距 7 + 状态"开/关" 11.5 + 右内边距 12
            "sys" or "camera" => TogglePillWidth(
                ev.Kind == "camera" ? "摄像头" : SignalNameOf(ev),
                tail: ev.Kind == "camera" && ev.Payload is Sources.CameraSnapshot cam
                        && !string.IsNullOrWhiteSpace(cam.DeviceName) ? cam.DeviceName : null),

            // ---- 解锁：BuildUnlockPill ----
            // 图标 17 + Margin(12+9) + 固定文案 + 间距 8 + 时间 11 + 右内边距 12
            "unlock" => 12 + 17 + 9
                        + MeasureText("已解锁 · 进入桌面", 13, true)
                        + 8 + MeasureText(ev.CreatedAt.ToString("HH:mm"), 11)
                        + 12,

            // ---- 时钟：BuildClockPill ----
            // 图标 16 + Margin(12+9) + 时间 13 SemiBold + 间距 8 + 日期 11 + 右内边距 12
            "clock" => 12 + 16 + 9
                     + MeasureText(ev.Title, 13, true)
                     + (string.IsNullOrWhiteSpace(ev.Subtitle) ? 0
                        : 8 + MeasureText(ev.Subtitle!, 11))
                     + 12,

            // ---- 使用时长：BuildUsagePill ----
            // 图标 16 + Margin(12+9) + 标题 13 SemiBold + 右内边距 12
            "usage" => 12 + 16 + 9 + MeasureText(ev.Title, 13, true) + 12,

            // ---- 通知（默认）：BuildNotificationPill ----
            // 图标 24 + Margin(12+8) + 标题 13 SemiBold + 间距 8 + 正文 11.5 + 右内边距 12
            //
            // 【EllipsisSlack 只加一次，不是每段各加一次】
            // 标题与正文都设了 TextTrimming，但它们是**同一行上的左右两段**：
            // 排版时 WPF 对两段各自算可用宽，只要这一行整体还有余量，两段就都不截。
            // 曾经给标题、正文各加 12 → 公式比排版宽出整整 24 DIP，
            // 表现为通知胶囊右侧凭空多出一块空白（自检里显示为差 -24）。
            // 整行只留一份余量即可，两段都安全。
            _ => 12 + 24 + 8
               + MeasureText(ev.Title, 13, true)
               + (string.IsNullOrWhiteSpace(ev.Subtitle) ? 0
                  : 8 + MeasureText(ev.Subtitle!, 11.5))
               + EllipsisSlack + 12
        };
        return Math.Clamp(w, IslandMetrics.MinWidth, IslandMetrics.MaxWidth);
    }

    /// <summary>
    /// DSH 胶囊文字列在【胶囊宽度上限】下能分到的宽度。
    ///
    /// 与 <see cref="MeasurePillWidth"/> 的 "dsh" 分支同源：
    /// 上限 = 左内边距 12 + 图标 18 + 间距 8 + 文字列 + 右内边距 12。
    /// 两行共用它 → 行宽恒定、右端对齐，且不会因"刚好等于上限"而被省略号误伤。
    /// </summary>
    private static double DshPillTextColWidth()
        => Math.Max(40, IslandMetrics.MaxWidth - 12 - 18 - 8 - 12);

    /// <summary>键位事件的信号名（与 BuildSysPill 用的 SignalName 同口径）</summary>
    private static string SignalNameOf(IslandEvent ev)
        => SignalName(ev.Payload is Sources.SysSnapshot ss ? ss.Signal : "keyboard");

    /// <summary>
    /// BuildTogglePill 的宽度，与 <see cref="BuildTogglePill"/> 的排版逐项对应：
    ///   键帽 22 + 左 11 + 右 9 + 名称 13 SemiBold + 间距 7 + 状态 11.5
    ///   + 尾巴：无 tail 则 12 右内边距；有 tail 则 间距 7 + tail(≤130) + 右内边距 12
    /// tail 设了 MaxWidth=130，因此它超长时会省略号 → 按 130 封顶计入。
    /// </summary>
    private double TogglePillWidth(string name, string? tail)
    {
        double w = 11 + 22 + 9                       // 键帽 + 左右 Margin
                 + MeasureText(name, 13, true)       // 名称
                 + 7 + MeasureText("开", 11.5);      // 间距 + 状态（开/关同宽，中文一字）
        if (!string.IsNullOrWhiteSpace(tail))
            w += 7 + Math.Min(MeasureText(tail!, 11), 130) + 12;  // 间距 + tail + 右内边距
        else
            w += 12;                                 // 右内边距
        return w;
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

    /// <summary>歌词宽度缓存键：曲目 + 当前句，换曲或换句才失效</summary>
    private string? _lyricWidthKey;
    private double _lyricWidthCache;

    /// <summary>
    /// 胶囊宽度收缩的滞回阈值（未缩放 DIP）。
    /// 新的一句比当前宽度窄不足这个值时**不缩**，避免每句都抖一下。
    /// 只有明显变短（长句 → 短句）才真正收窄。
    /// </summary>
    private const double WidthShrinkHysteresis = 36;

    /// <summary>
    /// 本曲"代表性歌词宽度"（未缩放口径）——用于决定胶囊文字列要多宽。
    ///
    /// 【为什么不用"当前这句"】旧实现取 <c>CurrentText(当前进度)</c>，于是：
    ///   · 胶囊宽度只在切歌那一刻按"当时恰好显示的那句"算一次，之后永不重算
    ///     （UpdatePillLyric 不写 MeasuredWidth，重建又受 MediaSig 守卫限制），
    ///     即 width 被冻结在一句随机歌词上；
    ///   · 而 <see cref="MeasurePillWidth"/>（容器宽）每次媒体事件都重算，
    ///     容器与内部列宽因此脱节：容器宽 > 列宽 → 波形右侧空出一块；
    ///     容器宽 &lt; 列宽 → 内容越过胶囊右边缘（"挤了波形"）。
    ///
    /// 改为取全曲歌词行的第 80 百分位宽度：
    ///   · 同一首歌内恒定 → 容器与列宽永远一致，也不会每秒抖动；
    ///   · 换曲才变 → 与 MediaSig 的重建策略天然一致；
    ///   · 用 p80 而非最大值 → 避免一句超长歌词把胶囊永远顶到上限 420。
    /// </summary>
    private double TrackLyricWidth(Sources.MediaSnapshot snap)
    {
        if (!_settings.Media.ShowLyrics || !Sources.MediaSourcePolicy.IsMusicPlayer(snap.SourceId))
            return 0;

        var res = _lyrics.Current;
        if (res?.Lines == null || res.Lines.Count == 0) return 0;

        // 【按"当前这句"取宽，而不是全曲 p80】
        // 以前取全曲第 80 百分位宽：同一首歌内恒定，但代价是
        // 一旦歌里有长句，胶囊就被永久撑到那个宽度 —— 当前句很短时
        // 右侧留一大片空白（实测《Underwater》标题短、歌词短，胶囊却很长）。
        //
        // 现在跟随当前播放位置的那句歌词，但**按档位量化**（见下方 quantized）：
        // 宽度只在"短句档 / 长句档"之间切换，一首歌最多变两次，
        // 不会再出现每句都改容器宽、把波形在内的整条胶囊反复拽动的现象。
        int posMs = (int)CurrentPositionMs(snap);
        int idxNow = _lyrics.FindLineIndex(posMs, _lyricOffsetMs);
        string curText = idxNow >= 0 && idxNow < res.Lines.Count ? res.Lines[idxNow].Text : "";
        if (string.IsNullOrWhiteSpace(curText))
        {
            // 还没到第一句（前奏）：用全曲 p80 撑住基础宽度，避免胶囊忽胖忽瘦
            return BaseLyricWidth(res);
        }
        if (curText.Length > 60) return BaseLyricWidth(res);   // 异常长句不参与定宽

        double need = MeasureText(curText, 11);

        // 【0.0.2 修正 · 波形闪烁的根因】
        // 宽度必须按"档位"取整，不能按每句歌词的实测宽度逐句变化。
        //
        // 症状：波形位置反复横跳（用户看到的就是"一闪一闪"）。
        // 链路：媒体快照每 ~900ms 刷新 → 每帧 Render 都调本函数 →
        //   宽度逐句变化 → MediaPillWidth 把 columns[1] 改掉 →
        //   容器宽变化 → 整条胶囊的弹簧在动画中被反复改目标 →
        //   内部的"文字列 + 波形"在旧坐标与新坐标之间来回跳。
        //
        // 只做"单句宽度"的量化还不够：每句都会落进相邻两档，宽度照样变。
        // 所以取【全曲 p80】作为档位锚点，再按当前句相对它的大小取整：
        //   · 同一首歌内最多 3 个宽度值（基础档 / 长句档 / 上限档）；
        //   · 仍能跟随歌曲（歌短则岛短，长句歌才变宽），不会永久撑在上限；
        //   · 换档是低频事件（一首歌 ≤ 3 次），动画期间护栏可以放心把它挡掉。
        double baseW = BaseLyricWidth(res);
        if (baseW <= 0) baseW = need;
        // 只有三档，且每档都是定值 —— 绝不能把 need 本身当结果返回，
        // 否则长句之间又会逐句变化，等于没量化。
        //   ① need ≤ 0.92×base → base（短句：不缩，避免忽胖忽瘦）
        //   ② 0.92 ~ 1.08×base → base（同一档）
        //   ③ need > 1.08×base  → 1.45×base（长句：固定加宽档）
        double quantized = need > baseW * 1.08 ? baseW * 1.45 : baseW;

        // 缓存键含"这一档"，同档内不失效
        string key = snap.SourceId + "|" + res.Title + "|" + res.Artist + "|" + res.Source
                   + "|" + (int)Math.Round(quantized);
        // 【纯函数：同一输入必得同一输出】
        // 之前这里带副作用（写 _lyricWidthCache 并有滞回），而 
        // BuildMediaPill（构建内容）与 MediaPillWidth（算容器宽）会在不同时刻
        // 各调一次 —— 两次结果不同就会出现"容器变宽但内部文字列没变"，
        // 表现为胶囊变宽、歌词仍被裁在词中间，甚至整块溢出屏幕。
        // 现在只按"当前句"的实测宽度返回，滞回交给调用方（TryResizePillForLyric）处理。
        if (key == _lyricWidthKey) return _lyricWidthCache;

        _lyricWidthKey = key;
        _lyricWidthCache = quantized;
        return quantized;
    }

    /// <summary>
    /// 全曲歌词的第 80 百分位宽度 —— 只在"还没到第一句"或"当前句异常长"时兜底，
    /// 保证胶囊有个稳定的基础宽度，不会因为前奏静音而塌成最小宽。
    /// </summary>
    private double BaseLyricWidth(Sources.LyricResult res)
    {
        var widths = new List<double>(res.Lines.Count);
        foreach (var ln in res.Lines)
        {
            var t = ln.Text;
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (t.Length > 60) continue;
            widths.Add(MeasureText(t, 11));
        }
        if (widths.Count == 0) return 0;
        widths.Sort();
        int idx = (int)Math.Floor((widths.Count - 1) * 0.8);
        return widths[Math.Clamp(idx, 0, widths.Count - 1)];
    }

    /// <summary>
    /// 换歌词行后，按新的"当前句"重算并应用胶囊宽度。
    ///
    /// 【为什么不用 UpdateItem 重建】那条路径会重建整个 ContentPresenter（闪一下）。
    /// 这里只更新 MeasuredWidth 再请求一次布局，弹簧会平滑地把宽度过渡过去。
    /// 只有宽度真的变了（超过 1px）才动，避免每句都触发一次布局。
    /// </summary>
    private void TryResizePillForLyric(Sources.MediaSnapshot? snap)
    {
        // 【0.0.2 新增护栏：弹簧未落定时不改目标宽度】
        // 这是"波形一闪一闪"的另一半原因（见 TrackLyricWidth 的注释）：
        // 换行恰好落在胶囊宽度弹簧还在收敛的过程中时，item.Width.To(newW) 会
        // 把正在跑的动画直接改向，内部的「文字列 + 波形」跟着在两组列宽之间跳。
        // 与其压制症状，不如让换档等弹簧站稳：宽度不是每句都必须立刻变的东西，
        // 晚 200ms 换档完全看不出来，但在动画中途换档非常显眼。
        if (!IsPillWidthSettled())
        {
            if (_lyricResizePending is null)
            {
                _lyricResizePending = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(120) };
                _lyricResizePending.Tick += (_, _) =>
                {
                    _lyricResizePending!.Stop();
                    _lyricResizePending = null;
                    TryResizePillForLyric(_stack.Active?.Event.Payload as Sources.MediaSnapshot);
                };
            }
            _lyricResizePending.Start();
            return;
        }
        _lyricResizePending?.Stop();
        _lyricResizePending = null;

        if (snap == null) return;
        var item = _stack.Active;
        if (item == null || item.Event.Kind != "media") return;
        if (item.Role != IslandRole.Pill) return;          // 只有胶囊态跟随
        if (item.Event.Payload is not Sources.MediaSnapshot ms) return;

        // MeasurePillWidth 内部已经同步好三列宽度（见 MediaPillWidth），
        // 这里只负责判断"总宽是否真的变了"并请求一次重排。
        double w = MeasurePillWidth(item.Event);

        // 【死区放大到 4px】原阈值 1px 意味着"几乎任何歌词换行都算变化"。
        // 宽度做量化之后（见 TrackLyricWidth），同档内 w 恒定，这里不会被频繁触发；
        // 死区再放到 4px，是为了吸收浮点误差与档位边界的抖动。
        if (Math.Abs(w - item.MeasuredWidth) < 4.0) return;

        item.MeasuredWidth = w;
        _lastLayoutSignature = null;

        // 下一帧布局：此时新歌词已写入，宽度是按这一档算的
        Dispatcher.BeginInvoke(new Action(() => ApplyLayout(force: true)),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// 胶囊宽度弹簧是否已经落定（可安全接受新的目标宽度）。
    /// 直接用引擎自己的收敛判据 <see cref="Core.Spring.IsSettled"/>，
    /// 不再另立一套阈值 —— 两套阈值不一致时，"引擎认为还在动、我们却改了目标"
    /// 这种错配会重新引入抖动。
    /// </summary>
    private bool IsPillWidthSettled() => _stack.Active?.Width.IsSettled ?? true;

    /// <summary>等宽度弹簧落定后再换档的延时器（见 TryResizePillForLyric）</summary>
    private DispatcherTimer? _lyricResizePending;

    /// <summary>媒体胶囊三列的固定宽度（未缩放口径）</summary>
    private const double PillCoverColW = 12 + 24 + 8;   // 左内边距 + 封面 + 间距
    private const double PillWaveColW = 22 + 6;         // 波形 + 左间距
    private const double PillTailPad = 8;               // 右侧留白

    /// <summary>
    /// 文本列必须额外预留的"省略号余量"（未缩放 DIP）。
    ///
    /// WPF 的 TextBlock 在判断"是否要加省略号"时，会把末尾 "…" 字形的宽度
    /// 也算进可用宽度里。因此
    ///     内容宽度 == 可用宽度   →   仍然显示 "…"
    /// 也就是"量得下却还是被截"。
    ///
    /// 【影响范围】任何设了 <see cref="TextTrimming.CharacterEllipsis"/> 的定宽文本列：
    /// 媒体胶囊的标题 / 作者行、通知胶囊的标题与正文、DSH 胶囊的两行文字。
    /// 凡是"按内容实测算宽"的地方都要加上它，否则宽度虽算对了仍会被截。
    /// </summary>
    private const double EllipsisSlack = 12;

    /// <summary>
    /// 媒体胶囊的文字列宽度：标题行与歌词行取较宽者，并夹在上限内。
    /// 【BuildMediaPill 的列宽 与 MediaPillWidth 的胶囊宽度 必须都走这里】
    /// 否则两处各算各的，就会出现「列比容器宽」→ 内容溢出胶囊（曾实测溢出 78px）。
    /// </summary>
    /// <param name="lyricW">
    /// 已经测好的歌词宽度，由 <see cref="TrackLyricWidth"/> 提供 —— 不接受"当前这句歌词"，
    /// 以确保同一首歌内、以及"列宽"与"容器宽"两处调用得到完全相同的值。
    /// 传 0 表示不计歌词（无歌词 / 非音乐播放器 / 未开启歌词）。
    /// </param>
    private double MediaPillTextWidth(Sources.MediaSnapshot snap, double lyricW)
    {
        double scale = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
        double cap = IslandMetrics.MaxWidth / scale;
        double maxText = cap - PillCoverColW - PillWaveColW - PillTailPad;

        double titleW = MeasureText(snap.Title, 12.5, true);
        double artistW = string.IsNullOrWhiteSpace(snap.Artist) ? 0 : MeasureText(snap.Artist!, 11) + 7;
        double metaW = titleW + artistW;

        // 【余量不能太小】实测：标题需要 98.34 DIP、只分到 101.13（余 2.8）时，
        // WPF 依然会显示省略号 —— 它判断是否裁剪时会把省略号字形宽度算进去。
        // 因此至少留一个中文字宽（约 12.5 DIP）的呼吸空间，否则会出现
        // 「明明量得下却还是被截」。metaW 已含 artist 的 +7 间距，lyricW 不含。
        //
        // 【下限 96 而不是 60】60 太窄：波形列(28) + 左侧间距(6) 会贴着文字，
        // 歌词短到 "So come on" 时胶囊整体收缩，波形就被挤到文字上。
        // 96 能保证「标题/歌词 + 波形」之间始终有呼吸空间。
        return Math.Clamp(Math.Max(metaW, lyricW) + 18, 96, maxText);
    }

    /// <summary>
    /// 媒体胶囊目标宽度：把歌词宽度需求算进去（三段式第②步 先加宽）。
    /// 返回未缩放宽度（渲染时乘 Scale）。
    /// </summary>
    private double MediaPillWidth(Sources.MediaSnapshot snap, double lyricW)
    {
        // 三列口径与 BuildMediaPill 的 Grid 完全一致：
        //   封面列 PillCoverColW + 文字列 textW + 波形列(PillWaveColW) + 右留白(PillTailPad)
        double scale = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
        double cap = IslandMetrics.MaxWidth / scale;
        // 下限不能用 MinWidth(36)：那是"正圆收起"的几何值，
        // 套在三列媒体胶囊上会让容器比三列之和还窄。
        double mediaMin = PillCoverColW + PillWaveColW + PillTailPad + 96;

        double total = Math.Clamp(
            PillCoverColW + PillWaveColW + PillTailPad + MediaPillTextWidth(snap, lyricW),
            mediaMin, Math.Min(cap, IslandMetrics.MaxWidth));

        // 【关键：由最终总宽反推文字列宽】
        // 以前容器宽和 columns[1] 是各自独立算出来的，两端还会在不同时刻调用
        // TrackLyricWidth（它依赖当前播放位置）—— 构建内容时按第 5 句定宽，
        // 进度走到第 6 句后 MeasurePillWidth 又按第 6 句重算容器，
        // 于是 textCol.Width 大于 columns[1] 宽，StackPanel 直接溢出到隔壁列，
        // 把波形盖住（用户看到的就是"挤波形"）。
        // 现在文字列 = 总宽 - 三列固定宽，三列之和恒等于容器宽，永不溢出。
        double finalTextW = Math.Max(60, total - PillCoverColW - PillWaveColW - PillTailPad);
        ApplyMediaPillColumns(MediaEventId(snap), finalTextW);
        return total;
    }

    /// <summary>
    /// 更新胶囊内歌词行：三段式宽度自适应
    ///   ① 放得下 -> 完整显示
    ///   ② 超出但未达上限 -> 岛加宽（宽度由 MediaPillWidth 负责）
    ///   ③ 到上限仍放不下 -> 自动滚动
    /// </summary>
    private void UpdatePillLyric(Sources.MediaSnapshot? snap)
    {
        if (snap == null) return;
        var item = _stack.Active;
        if (item == null || item.Event.Id != MediaEventId(snap)) return;

        // 【必须从"这条 item 自己的"引用表取控件】
        // 以前取的是窗口级的 _pillLyricText / _pillLyricRow，那指向"最后一次
        // BuildMediaPill 建出来的"那棵树 —— 两条媒体岛并存时（浏览器 + 音乐软件），
        // 刷新当前胶囊会写到另一条会话的歌词行上（或写到一棵已脱离视觉树的旧子树上）。
        // 按 item 取之后，A 的刷新永远只摸 A 自己的子树。
        var mi = _mediaSig.GetValueOrDefault(item.Event.Id);
        var tb = mi?.LyricText;
        var row = mi?.LyricRow;
        if (tb == null || row == null || mi == null) return;
        var scroll = LyricScrollFor(item.Event.Id);

        string? text = null;
        int posMs = 0;
        // 歌词只对音乐播放器生效：浏览器/视频播放器不显示歌词
        if (_settings.Media.ShowLyrics && Sources.MediaSourcePolicy.IsMusicPlayer(snap.SourceId))
        {
            posMs = (int)(snap.PositionTicks / TimeSpan.TicksPerMillisecond);
            text = _lyrics.CurrentText(posMs, _lyricOffsetMs);
        }

        // 无歌词 / 未同步 / 纯音乐：隐藏第二行，胶囊回单行
        if (string.IsNullOrWhiteSpace(text))
        {
            // 【不再用 `LastLyricText != ""` 当"是否已经折叠"的判据】
            // 新树建出来时 LastLyricText 就是 ""，于是这条分支一次都不会执行 ——
            // 浏览器（永远没有歌词）的歌词行就一直留着，白占 15px+1 行高，
            // 把标题从"单行居中"顶高约 8px。改为按 Visibility 判断，与状态一致。
            if (row.Visibility != Visibility.Collapsed || scroll.NeedScroll)
            {
                mi.LastLyricText = "";
                StopLyricScroll(tb);
                scroll.NeedScroll = false;
                row.Visibility = Visibility.Collapsed;
            }
            return;
        }
        row.Visibility = Visibility.Visible;

        // ---- 行切换：只在换行时重建文本与动画（不要每帧重建，否则动画永远停在起点）----
        bool lineChanged = text != mi.LastLyricText;
        if (lineChanged)
        {
            mi.LastLyricText = text!;
            tb.Text = text;
            StopLyricScroll(tb);
            scroll.NeedScroll = false;

            // 【换句 → 重算胶囊宽度】
            // 宽度改为跟随"当前这句"（见 TrackLyricWidth），因此换句后必须让
            // 容器重新量一次，否则 _lyricWidthCache 更新了、界面却还停在旧宽度。
            // 这里只改 MeasuredWidth 并请求一帧布局，不重建内容（重建会闪）。
            // 仅在播放器胶囊处于 Pill 形态时处理：收起/球体态不需要跟随。
            TryResizePillForLyric(snap);
        }

        // ---- 三段式宽度判断（缩放感知）----
        double scale = IslandMetrics.Scale <= 0 ? 1.0 : IslandMetrics.Scale;
        double need = MeasureText(text!, 11);
        double avail = row.ActualWidth;
        // avail 是"当前实际可用宽度"，可能已被外层裁剪（MusicPill 是 Auto 列，
        // 不一定等于胶囊上限），因此只在它是 0 时才回退到上限推算。
        if (avail <= 0) avail = Math.Max(60, IslandMetrics.MaxWidth / scale - 56);

        // 【只按实际可用宽度判断是否能放下】
        // 之前写成 need <= avail - 1 || need <= capW：只要歌词不超过胶囊理论上限
        // 就判定"放得下"，于是返回、不滚动 —— 可当前宽度已经被外层挤窄，
        // 歌词其实是被裁掉的（表现为"歌词显示不全且不会滚动"）。
        // 能不能放下只看实际宽度 avail；放不下就交给下面的自动滚动。
        bool fits = need <= avail - 1;
        if (fits)
        {
            if (scroll.NeedScroll) { StopLyricScroll(tb); scroll.NeedScroll = false; }
            return;
        }

        // ---- ③ 到上限仍放不下：自动滚动 ----
        if (!_settings.Media.LyricAutoScroll)
        {
            if (scroll.NeedScroll) { StopLyricScroll(tb); scroll.NeedScroll = false; }
            return;
        }
        if (!lineChanged && scroll.NeedScroll) return;   // 动画已在本行运行，别重启

        scroll.NeedScroll = true;
        double shift = need - avail + 8;

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

    /// <summary>取（必要时创建）某条媒体岛的歌词滚动状态</summary>
    private LyricScrollState LyricScrollFor(string id)
    {
        if (!_lyricScroll.TryGetValue(id, out var st))
        {
            st = new LyricScrollState();
            _lyricScroll[id] = st;
        }
        return st;
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
            _system?.SetIslandVisible(false);
            if (IsVisible && !_gameHidden) Hide();
            return;
        }
        _system?.SetIslandVisible(true);
        ApplyLayout();
        RefreshMediaCard();          // 进度条 / 时间 / 播放图标 每帧更新
        RefreshWifiLive();           // Wi-Fi 胶囊 / 卡片的实时速率（常驻播报）
        UpdatePillLyric(_stack.Active?.Event.Payload as Sources.MediaSnapshot);
        RefreshCardLyric(_stack.Active?.Event.Payload as Sources.MediaSnapshot);
        UpdateWaveBars();            // 媒体波形（纯函数直写高度，重建不会打乱相位）
        ApplyCardTitleScroll();      // 标题超宽时横向滚动（列宽 = 整卡一半）

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

                // 【先测高，再挂载】c 尚未进入视觉树时 Measure 得到的是
                // 内容真实需求；挂到 ContentPresenter 之后再测，DesiredSize
                // 会被父级拉伸，测出虚高值 —— 表现为卡片下方永远留大片空白。
                if (wantCard && c != null)
                {
                    // 【未缩放口径】WrapWithClose 给整张卡片加了 LayoutTransform(Scale)，
                    // WPF 会先按未缩放尺寸给 DesiredSize，再放大成布局尺寸。
                    // 所以这里传 CardWidth 测到的就是未缩放高度，与 ClusterStack 的
                    // 上限口径一致，不会二次缩放。
                    double m = MeasureCardContent(c, cwForCard());
                    if (m > 0) item.MeasuredCardHeight = m;
                }
                // 卡片重建 → 旧的 Wi-Fi 卡片引用作废（新卡会在构建时重新登记）
                _wifiCardParts = null;

                if (c != null && b.Child is ContentPresenter cp) cp.Content = c;
                ItemContentBuilt(item, c);
                if (!wantCard) _cardParts = null;
                _lastLayoutSignature = null;   // 强制下一帧重算槽位
            }

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

            // 3b. 指针停在某颗**球体**上 -> 走"球体 -> 胶囊"提升
            // 【为什么放在这里】半透明等待期（HoverActivateMs 内）岛是 WS_EX_TRANSPARENT，
            // 收不到 MouseEnter，所以判定必须落在主动轮询的命中测试里。
            // 内部自带落定防抖（停留不足 SphereHoverSettleMs 视为横向扫过，不提升）。
            PollSpherePromotion(IslandUnderCursor());
        }
        else
        {
            PollSpherePromotion(null);

            // 连续 LeaveGrace 都没命中才算真的离开
            if (_pointerOverIsland && DateTime.Now - _lastInsideAt > LeaveGrace)
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

    /// <summary>
    /// 指针当前落在哪一条岛上（null = 都没落中）。
    /// 与 <see cref="IsPointerOverAnyIsland"/> 同一套物理矩形 + 外扩热区口径。
    ///
    /// 【为什么需要它】半透明等待期（HoverActivateMs 内）岛是 WS_EX_TRANSPARENT，
    /// **收不到任何 WPF 鼠标事件**，所以挂在 MouseEnter 上的球体提升在这段时间里不会触发。
    /// 而命中轮询是主动 GetCursorPos 探测，穿透状态下照样能跑 ——
    /// 于是把"球体 -> 胶囊"的判定搬到这里，等待期内也能响应。
    /// </summary>
    private string? IslandUnderCursor()
    {
        try
        {
            if (!WindowChromeHelper.GetWindowRect(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle, out var wr)) return null;
            if (!WindowChromeHelper.GetCursorPos(out var pt)) return null;
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
            double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

            string? best = null;
            double bestArea = double.MaxValue;   // 取面积最小（最贴合）的那条，避免大胶囊盖住小球
            const double padDip = 6;
            foreach (var item in _stack.Items)
            {
                if (item.IsDying || item.Opacity.Value < 0.3) continue;
                var v = item.Visual;
                if (v.Visibility != Visibility.Visible) continue;
                double cx0 = Canvas.GetLeft(v), cy0 = Canvas.GetTop(v);
                double ww = v.Width, hh = v.Height;
                if (double.IsNaN(ww) || double.IsNaN(hh) || ww <= 0 || hh <= 0) continue;

                double x0 = wr.Left + (cx0 - padDip) * sx;
                double y0 = wr.Top + (cy0 - padDip) * sy;
                double x1 = wr.Left + (cx0 + ww + padDip) * sx;
                double y1 = wr.Top + (cy0 + hh + padDip) * sy;
                if (pt.X < x0 || pt.X > x1 || pt.Y < y0 || pt.Y > y1) continue;

                double area = ww * hh;
                if (area < bestArea) { bestArea = area; best = item.Event.Id; }
            }
            return best;
        }
        catch { return null; }
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

        // 【球体 -> 胶囊 -> 详情卡】由命中轮询驱动（PollSpherePromotion），
        // 不在这里做 —— 半透明等待期内收不到 MouseEnter。

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

    // ---- 球体 -> 胶囊 提升的落定（防抖） ----
    private string? _sphereHoverId;
    private DateTime _sphereHoverSince = DateTime.MinValue;
    private string? _sphereSettleId;
    private DispatcherTimer? _sphereSettleTimer;
    /// <summary>
    /// 本轮是否已经提升过球体。为 true 时不再接受任何新的提升，
    /// 直到指针完全离开岛所在那一行（由 PollSpherePromotion(null) 清除）——
    /// 这是阻断"提升 -> 重排 -> 另一球体落进光标 -> 再提升"自激循环的关键。
    /// </summary>
    private bool _spherePromoted;

    /// <summary>指针在球体上停留多久才真正提升为胶囊（毫秒）。偏小会误触发，偏大会显得没反应。</summary>
    private int SphereHoverSettleMs => 120;

    /// <summary>
    /// 当前状态下是否允许"悬浮球体 -> 胶囊"：
    /// 半透明穿透期看 HoverSphereInPassive；已放行可交互期看 HoverSphereInInteractive。
    /// 两者都关 = 完全不响应悬浮球体（只有点击才切换）。
    /// </summary>
    private bool HoverSphereEnabledNow
        => _interactive ? _settings.Island.HoverSphereInInteractive
                        : _settings.Island.HoverSphereInPassive;

    private void EnsureSphereSettleTimer()
    {
        if (_sphereSettleTimer != null) return;
        _sphereSettleTimer = new DispatcherTimer();
        _sphereSettleTimer.Tick += (_, _) =>
        {
            _sphereSettleTimer!.Stop();
            var id = _sphereSettleId;
            _sphereSettleId = null;
            if (id == null) return;
            PromoteSphereToPill(id);
        };
    }
    /// <summary>
    /// 命中轮询驱动：指针停在 id 这颗球上够久就提升为胶囊；离开则取消落定。
    /// </summary>
    private void PollSpherePromotion(string? hoverId)
    {
        if (string.IsNullOrEmpty(hoverId))
        {
            _sphereHoverId = null;
            _sphereSettleTimer?.Stop();
            _sphereSettleId = null;
            _spherePromoted = false;      // 指针已离开整行 -> 重新武装
            return;
        }

        // 【两个状态各自独立开关】
        //   · 被动（半透明穿透）：HoverSphereInPassive
        //   · 主动（已放行、可交互）：HoverSphereInInteractive
        // 两者都关 = 完全不响应悬浮球体（回到只有点击才切换的行为）。
        if (!HoverSphereEnabledNow) return;

        var item = _stack.Items.FirstOrDefault(i => i.Event.Id == hoverId && !i.IsDying);
        if (item == null || item.Role != IslandRole.Sphere) return;
        if (_sphereHoverId == hoverId) return;   // 已在落定中

        // 【必须先离开才能再次提升】上一条提升后中央胶囊变宽、其余岛重排，
        // 别的球体很可能正好落到静止的光标下方 —— 若继续允许提升就会自激：
        // 提升 -> 重排 -> 另一球体落进光标 -> 再提升 -> 再重排（实测 14 秒抖 26 次）。
        // 纯"光标移动过"的闸不够（重排本身就会让光标相对各球体移动）。
        // 因此要求：提升过一次之后，必须**完全离开岛所在那一行**才会重新武装。
        if (_spherePromoted && hoverId != null) return;

        _sphereHoverId = hoverId;
        _sphereHoverSince = DateTime.Now;
        EnsureSphereSettleTimer();
        _sphereSettleTimer!.Stop();
        _sphereSettleId = hoverId;
        _sphereSettleTimer.Interval = TimeSpan.FromMilliseconds(SphereHoverSettleMs);
        _sphereSettleTimer.Start();
    }

    /// <summary>真正把球体提升为中央胶囊，并接上"胶囊 -> 详情卡"的展开计时</summary>
    private void PromoteSphereToPill(string id)
    {
        var item = _stack.Items.FirstOrDefault(i => i.Event.Id == id && !i.IsDying);
        if (item == null || item.Role != IslandRole.Sphere) return;
        if (_stack.Active?.Event.Id == id) return;   // 已是中央胶囊

        // 记下提升时的光标位置：之后光标不动就不允许再次提升（阻断自激）
        _spherePromoted = true;   // 本轮已提升，必须先离开整行才会重新武装

        _stack.SetActive(id);
        _lastLayoutSignature = null;
        ApplyLayout(force: true);
        App.LogInfo("Island", $"悬浮球体停留达标 -> 提升为胶囊（{id}）");

        // 提升后接上展开：胶囊 -> 详情卡
        // 【关键】_hoverId 原本只由 MouseEnter 赋值，而等待期内收不到该事件；
        // 这里显式补上，否则计时器 Tick 里的 `id != _hoverId` 会直接 return，卡片永远不展开。
        _hoverId = id;
        EnsureHoverExpandTimer();
        if (!HoverExpandOn || _hoverExpandSuppressed) return;
        if (_stack.IsExpanded) return;
        _hoverExpandId = id;
        _hoverExpandTimer!.Stop();
        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(HoverExpandDelayMs);
        _hoverExpandTimer.Start();
    }

    private void OnItemLeave(string id)
    {
        if (_hoverId == id) _hoverId = null;
        _hoverExpandTimer?.Stop();
        _hoverExpandId = null;
        // 离开即取消"落定"，避免指针已经走了却还把球体提成胶囊
        if (_sphereHoverId == id) _sphereHoverId = null;
        _sphereSettleTimer?.Stop();
        _sphereSettleId = null;
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
            ItemContentRemoved(item);
            _mediaSig.Remove(item.Event.Id);
            _lyricScroll.Remove(item.Event.Id);
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

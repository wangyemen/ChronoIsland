using System;
using ChronoIsland.Core;
using ChronoIsland.UI;

namespace ChronoIsland.Sources;

/// <summary>
/// 深浅色切换快照（docs §5.13）。
/// 太阳 = 已切到浅色，月亮 = 已切到深色 —— 同一个信号的两个值，因此共用一条岛。
/// </summary>
public sealed record ThemeSnapshot(bool Dark, string ModeLabel);

/// <summary>
/// 深色 / 浅色切换提示源（docs §5.13）。
///
/// 【为什么需要一个"源"】外观切换是纯 UI 事件，但它与键位翻转、摄像头占用属于
/// 同一类"你刚才动了一下系统状态，给你确认"的信号，理应走同一条事件通道 ——
/// 这样堆叠、值级忽略、TTL、球体图标全都自动复用，不需要另立一套特例。
///
/// 【触发点】订阅 <see cref="AppTheme.Changed"/>，只在"深/浅真的翻转了"时发事件：
///   · 同色系内刷新（例如跟随系统时改缩放、切材质）不弹；
///   · 启动时的首次应用不弹（那是初始化，不是用户操作）。
///
/// 【为什么用太阳 / 月亮两个图标】它们表达的是"现在是哪种外观"，
/// 一个图标只描述一半状态；两个图标共用一个岛，读起来就是"月亮 → 深色"。
/// </summary>
public sealed class ThemeSwitchSource : IEventSource
{
    public string Key => "theme";
    /// <summary>优先级 1：外观确认属于"顺手一看"，不该压过通知与状态提醒。</summary>
    public int Priority => 1;

    private EventBus? _bus;
    private Settings _settings = new();
    private bool? _lastDark;

    /// <summary>
    /// UI 线程投递器。主题变化事件是在 ApplyTheme 内部触发的，
    /// 此刻灵动岛正在遍历自己的岛列表重建内容 —— 直接在回调里发事件会
    /// 一边遍历一边改列表（InvalidOperationException）。因此统一转投到 UI 线程排一次队。
    /// </summary>
    public Action<Action>? Marshaler { get; set; }

    public void Start(EventBus bus)
    {
        _bus = bus;
        _lastDark = AppTheme.Current.IsDark;      // 建立基线：启动那次不算切换
        AppTheme.Changed += OnThemeChanged;
    }

    public void Configure(Settings s) => _settings = s ?? new Settings();

    private void OnThemeChanged(ThemeTokens t)
    {
        try
        {
            var s = _settings;
            if (!s.ThemeSwitch.Enabled) { _lastDark = t.IsDark; return; }

            bool dark = t.IsDark;
            if (_lastDark == null) { _lastDark = dark; return; }
            if (_lastDark == dark) return;            // 同色系刷新，不弹
            _lastDark = dark;

            if (!s.ThemeSwitch.NotifyOnSwitch) return;

            int ttl = Math.Max(2, s.ThemeSwitch.TtlSeconds);
            var snap = new ThemeSnapshot(dark, ModeLabel(s.Theme.Mode, dark));
            App.LogInfo("Theme", $"外观切换 → {(dark ? "深色" : "浅色")}，生成岛（{ttl}s）");

            void Publish() => _bus?.Publish(new IslandEvent(
                Id: "theme",
                SourceKey: "theme",
                Kind: "theme",
                Priority: Priority,
                Title: dark ? "已切换到深色" : "已切换到浅色",
                Subtitle: snap.ModeLabel,
                Payload: snap,
                Policy: new IslandPolicy(TimeSpan.FromSeconds(ttl), Coalesce: true,
                    Expandable: true, HoverPauses: true, QueueWhenHidden: false))
            { AppKey = "theme", StealFocus = true });

            if (Marshaler != null) Marshaler(Publish);
            else Publish();
        }
        catch (Exception ex) { App.Log("Theme", ex); }
    }

    /// <summary>副标题：跟着系统 / 手动固定，比只写"深色"更有信息量</summary>
    private static string ModeLabel(string mode, bool dark) => mode switch
    {
        "Dark" => "手动固定 · 深色",
        "Light" => "手动固定 · 浅色",
        _ => dark ? "跟随系统 · 深色" : "跟随系统 · 浅色"
    };

    public void Dispose() => AppTheme.Changed -= OnThemeChanged;
}

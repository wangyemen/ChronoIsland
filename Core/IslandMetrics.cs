namespace ChronoIsland.Core;

/// <summary>
/// 几何基线（docs §1.1）。
/// 标 * 的项可在设置中调整，运行时可改（由 Settings 同步）。
/// </summary>
public static class IslandMetrics
{
    // ---- 固定值 ----
    public const double IslandHeight = 36;
    public const double MinWidth = 36;              // = 正圆，收起终点几何
    public const double CardWidth = 420;
    public const double CardMinHeight = 96;
    /// <summary>
    /// 详情卡高度上限（未缩放逻辑值）。
    /// 媒体卡 = 封面行 + 进度条 + 时间 + 控制按钮 + 歌词区，内容较高；
    /// 通知卡正文长时也会超过 400。取 460 兼顾两者，
    /// 超出部分由卡片内滚动条承载，不会无限增长。
    /// </summary>
    public const double CardMaxHeight = 460;
    public const double CardRadius = 28;
    public const double Gap = 8;
    public const double SphereSize = 36;
    /// <summary>侧列每层可见球体数（左右各 2 → 共 5 个可见）</summary>
    public const int MaxSideSpheres = 2;
    public static int MaxVisible => 1 + MaxSideSpheres * 2;
    public const double PaddingX = 12;
    public const double IconTextGap = 8;
    public const double HoverPadding = 8;
    public const int HoverLeaveGraceMs = 200;

    // ---- 可调值（由 Settings 同步，* 表示设置界面可改）----
    /// <summary>* 距屏幕顶部距离</summary>
    public static double TopMargin { get; set; } = 8;
    /// <summary>* 岛宽度上限</summary>
    public static double MaxWidth { get; set; } = 420;
    /// <summary>* 整体缩放（0.75 – 1.5）</summary>
    public static double Scale { get; set; } = 1.0;
    /// <summary>* 悬停放行时间（毫秒）：进入后先穿透，超时才可交互</summary>
    public static int HoverActivateMs { get; set; } = 400;
    /// <summary>* 是否启用悬停激活策略</summary>
    public static bool HoverActivate { get; set; } = true;
    /// <summary>* Alt 强制穿透</summary>
    public static bool AltForceThrough { get; set; } = true;

    /// <summary>应用设置中的可调项</summary>
    public static void Apply(Settings s)
    {
        TopMargin = s.Island.Top;
        MaxWidth = s.Island.MaxWidth;
        Scale = s.Island.Scale <= 0 ? 1.0 : s.Island.Scale;
        HoverActivateMs = Math.Clamp(s.Island.HoverActivateMs, 100, 3000);
        HoverActivate = s.Island.HoverActivate;
        AltForceThrough = s.Island.AltForceThrough;
    }

    /// <summary>圆角恒等于半高 → 永远是胶囊</summary>
    public static double RadiusFor(double height) => height / 2.0;

    /// <summary>第 tier 层球体的中心偏移（相对屏幕中轴）</summary>
    public static double SphereOffset(double pillWidth, int tier, int direction)
    {
        double base_ = pillWidth / 2.0 + Gap + SphereSize / 2.0;
        return direction * (base_ + tier * (SphereSize + Gap));
    }
}

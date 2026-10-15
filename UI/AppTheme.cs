using System;
using System.Windows.Media;

namespace ChronoIsland.UI;

/// <summary>
/// 主题 token 中心（docs §1.2）。
/// 灵动岛与设置界面共用同一份颜色定义，全部为不透明色。
/// </summary>
public sealed class ThemeTokens
{
    // ---- 表面层级（全部不透明，层次靠明度差表达）----
    /// <summary>窗口底 / 最外层</summary>
    public required Brush WindowSurface { get; init; }
    /// <summary>分组卡（在窗口底之上）</summary>
    public required Brush PanelSurface { get; init; }
    /// <summary>设置行（在分组卡之上）</summary>
    public required Brush RowSurface { get; init; }
    /// <summary>岛本体渐变端</summary>
    public required Brush IslandTop { get; init; }
    public required Brush IslandBottom { get; init; }

    // ---- 描边与分隔 ----
    public required Brush Stroke { get; init; }
    public required Brush Separator { get; init; }

    // ---- 文字 ----
    public required Brush TextPrimary { get; init; }
    public required Brush TextSecondary { get; init; }
    public required Brush TextTertiary { get; init; }

    // ---- 强调与语义色 ----
    public required Brush Accent { get; init; }
    public required Brush AccentGradient { get; init; }
    public required Brush Success { get; init; }
    public required Brush Warning { get; init; }
    public required Brush Danger { get; init; }

    /// <summary>开关/滑块的轨道底色</summary>
    public required Brush Track { get; init; }
    public double ShadowOpacity { get; init; } = 0.35;
    public bool IsDark { get; init; } = true;

    private static SolidColorBrush S(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex)!);

    private static Brush Grad(string a, string b) => new LinearGradientBrush
    {
        StartPoint = new System.Windows.Point(0, 0),
        EndPoint = new System.Windows.Point(1, 1),
        GradientStops = new GradientStopCollection
        {
            new((Color)ColorConverter.ConvertFromString(a)!, 0),
            new((Color)ColorConverter.ConvertFromString(b)!, 1)
        }
    };

    /// <summary>深色：窗口 #101016 → 分组 #1A1A23 → 行 #24242F</summary>
    public static ThemeTokens Dark { get; } = new()
    {
        IsDark = true,
        ShadowOpacity = 0.45,
        WindowSurface = S("#FF101016"),
        PanelSurface = S("#FF1A1A23"),
        RowSurface = S("#FF24242F"),
        IslandTop = S("#FF1C1C25"),
        IslandBottom = S("#FF0E0E13"),
        Stroke = S("#FF2E2E3A"),
        Separator = S("#FF26262F"),
        TextPrimary = S("#FFF5F6F8"),
        TextSecondary = S("#FFA8ADB7"),
        TextTertiary = S("#FF767C8A"),
        Accent = S("#FF5E9BFF"),
        AccentGradient = Grad("#FF5E9BFF", "#FFB27BFF"),
        Success = S("#FF3DDC97"),
        Warning = S("#FFFFC75A"),
        Danger = S("#FFFF6B6B"),
        Track = S("#FF2C2C38")
    };

    /// <summary>浅色：窗口 #FFFFFF → 分组 #F5F6FA → 行 #EBEDF3</summary>
    public static ThemeTokens Light { get; } = new()
    {
        IsDark = false,
        ShadowOpacity = 0.16,
        WindowSurface = S("#FFFFFFFF"),
        PanelSurface = S("#FFF5F6FA"),
        RowSurface = S("#FFEBEDF3"),
        IslandTop = S("#FFFFFFFF"),
        IslandBottom = S("#FFF0F2F7"),
        Stroke = S("#FFDCE0E8"),
        Separator = S("#FFE8EBF0"),
        TextPrimary = S("#FF1A1B1F"),
        TextSecondary = S("#FF5B6070"),
        TextTertiary = S("#FF8B90A0"),
        Accent = S("#FF2F6BFF"),
        AccentGradient = Grad("#FF2F6BFF", "#FF8B4DFF"),
        Success = S("#FF12A87A"),
        Warning = S("#FFD99000"),
        Danger = S("#FFD64545"),
        Track = S("#FFDDE1E9")
    };
}

/// <summary>主题解析与广播</summary>
public static class AppTheme
{
    private static ThemeTokens _current = ThemeTokens.Dark;
    public static ThemeTokens Current => _current;

    /// <summary>主题变化时通知所有窗口刷新</summary>
    public static event Action<ThemeTokens>? Changed;

    /// <summary>离屏渲染自检用：临时切换当前主题（不影响真实运行的 ApplyTheme 流程）</summary>
    internal static void SetCurrentForProbe(ThemeTokens t) => _current = t;

    public static ThemeTokens Resolve(string mode) => mode switch
    {
        "Light" => ThemeTokens.Light,
        "Dark" => ThemeTokens.Dark,
        _ => IsSystemLight() ? ThemeTokens.Light : ThemeTokens.Dark
    };

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 1;
        }
        catch { return false; }
    }

    public static void Apply(string mode)
    {
        _current = Resolve(mode);
        Changed?.Invoke(_current);
    }
}

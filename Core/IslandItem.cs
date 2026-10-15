using System.Windows.Controls;

namespace ChronoIsland.Core;

/// <summary>岛的角色</summary>
public enum IslandRole
{
    /// <summary>中央胶囊（当前消息）</summary>
    Pill,
    /// <summary>侧列球体（未查看的旧消息）</summary>
    Sphere,
    /// <summary>展开的详情卡</summary>
    Card
}

/// <summary>
/// 集群中的一条岛。持有自己的弹簧集合与状态。
/// </summary>
public sealed class IslandItem
{
    public IslandItem(IslandEvent ev, Border visual)
    {
        _ev = ev;
        Visual = visual;
        var (sk, sc) = SpringPresets.Snappy;
        var (gk, gc) = SpringPresets.Gooey;
        var (pk, pc) = SpringPresets.Push;

        Width = new Spring(IslandMetrics.SphereSize, sk, sc);
        Height = new Spring(IslandMetrics.IslandHeight, sk, sc);
        X = new Spring(0, pk, pc);
        Y = new Spring(IslandMetrics.TopMargin, gk, gc);
        Opacity = new Spring(1, pk, pc);
        ScaleX = new Spring(1, gk, gc);
        ScaleY = new Spring(1, gk, gc);
        Neck = new Spring(0, gk, gc);
        Blur = new Spring(0, pk, pc);
        Glow = new Spring(0, pk, pc);
        ContentOpacity = new Spring(0, pk, pc);

        foreach (var s in Springs) SpringEngine.Instance.Register(s);
    }

    private IslandEvent _ev;
    public IslandEvent Event => _ev;
    /// <summary>到达该时刻后自动收起（媒体暂停时设置）</summary>
    public DateTime? HideAt { get; set; }
    public string Kind => _ev.Kind;
    /// <summary>当前渲染的内容形态："pill" | "card"</summary>
    public string ContentKey { get; set; } = "pill";

    /// <summary>悬停等待期的透明度系数（1.0 = 正常，&lt;1 = 半透明提示可穿透）</summary>
    public double HoverAlpha { get; set; } = 1.0;

    /// <summary>就地替换事件（媒体刷新 / 通知合并）</summary>
    public void ReplaceEvent(IslandEvent ev) => _ev = ev;
    public Border Visual { get; }
    public IslandRole Role { get; set; } = IslandRole.Sphere;
    public bool IsDying { get; set; }
    public bool IsEmerging { get; set; }
    public bool IsOffscreen { get; set; }
    public double MeasuredWidth { get; set; }
    /// <summary>
    /// 展开态（详情卡）的内容实测高度，由 UI 层在卡片构建后 Measure 得出。
    /// 供 ClusterStack 计算展开卡的目标高度，避免写死高度导致留白 / 裁切。
    /// </summary>
    public double MeasuredCardHeight { get; set; }

    /// <summary>
    /// 媒体内容的"结构签名"（标题|作者|播放态）。
    /// 仅当签名变化时才重建胶囊内容；纯进度推送（约 900ms 一次）不重建，
    /// 否则会造出空白 TextBlock 导致歌词显示约 900ms 后消失。
    /// </summary>
    public string MediaSig { get; set; } = "";
    public DateTime? TtlStart { get; set; }

    // 几何弹簧
    public Spring Width { get; }
    public Spring Height { get; }
    public Spring X { get; }
    public Spring Y { get; }
    public Spring Opacity { get; }
    public Spring ScaleX { get; }
    public Spring ScaleY { get; }
    public Spring Neck { get; }
    public Spring Blur { get; }
    public Spring Glow { get; }
    public Spring ContentOpacity { get; }

    public IEnumerable<Spring> Springs => new[]
    {
        Width, Height, X, Y, Opacity, ScaleX, ScaleY, Neck, Blur, Glow, ContentOpacity
    };

    public void SetPreset((double K, double C) p, params Spring[] springs)
    {
        foreach (var s in springs) s.SetParameters(p.K, p.C);
    }

    public void UnregisterAll()
    {
        foreach (var s in Springs) SpringEngine.Instance.Unregister(s);
    }
}

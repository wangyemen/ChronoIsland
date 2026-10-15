using System;
using System.Windows;
using System.Windows.Media;

namespace ChronoIsland.Animation;

/// <summary>
/// 水珠几何：主圆 + 顶部颈缩粘连线（docs §4）。
/// 每帧根据 Neck / ScaleY / Y 重算 Path。
/// </summary>
public static class DropletShape
{
    /// <summary>
    /// 生成从屏幕顶边(0)垂到岛顶边的粘连线路径。
    /// </summary>
    /// <param name="centerX">岛中心 X（屏幕坐标）</param>
    /// <param name="topY">形变后岛的顶边 Y</param>
    /// <param name="neckWidth">颈缩宽度（0 = 断开）</param>
    public static PathGeometry BuildNeck(double centerX, double topY, double neckWidth)
    {
        var g = new PathGeometry();
        if (neckWidth <= 0.2 || topY <= 0) return g;

        double half = neckWidth / 2.0;
        double cx = centerX;
        // 控制点上提，形成"液体拉丝"的内凹曲线
        double cy = topY * 0.55;
        double bottomHalf = neckWidth * 0.42;

        var fig = new PathFigure { StartPoint = new Point(cx - half, 0), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new QuadraticBezierSegment(new Point(cx - half, cy), new Point(cx - bottomHalf, topY), true));
        fig.Segments.Add(new LineSegment(new Point(cx + bottomHalf, topY), true));
        fig.Segments.Add(new QuadraticBezierSegment(new Point(cx + half, cy), new Point(cx + half, 0), true));
        g.Figures.Add(fig);
        return g;
    }

    /// <summary>
    /// 收起三段式的阶段判定（用于诊断/日志）。
    /// A Pinch 0-180ms · B Droplet 140-420ms · C Vanish 380-620ms
    /// </summary>
    public static char PhaseOf(double elapsedMs) => elapsedMs switch
    {
        < 140 => 'A',
        < 380 => 'B',
        _ => 'C'
    };
}

/// <summary>
/// Gooey 滤镜简化版：Blur + 提高对比（ColorMatrix），模拟液体融合。
/// docs §4.3。性能不足时降级为纯形变。
/// </summary>
public sealed class GooeyEffect : System.Windows.Media.Effects.ShaderEffect
{
    public GooeyEffect()
    {
        // 0.0.1 先用 Blur+Opacity 组合近似；后续替换为 HLSL
        PixelShader = null;
    }
}

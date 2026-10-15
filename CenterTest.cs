using System;

namespace ChronoIsland;

/// <summary>居中验证：不同缩放下的几何计算
/// 模拟 IslandWindow 的定位公式，确认中心恒等于屏幕中轴</summary>
public static class CenterTest
{
    public static void Run()
    {
        Console.WriteLine("=== 居中验证（不同缩放）===");
        Console.WriteLine("屏幕宽 2560，中轴 = 1280\n");
        Console.WriteLine("缩放 | 主岛宽 | 左边界 | 中心 | 偏差 | 左球中心 | 右球中心");

        double screenW = 2560, cx = screenW / 2;
        double pillW = 300;

        foreach (var scale in new[] { 0.7, 0.85, 1.0, 1.15, 1.3, 1.5 })
        {
            Core.IslandMetrics.Scale = scale;
            double w = pillW * scale;
            double left = cx + 0 - w / 2;              // 主岛 X 偏移 = 0
            double center = left + w / 2;
            double dev = center - cx;

            // 左右球体
            double sphOff = Core.IslandMetrics.SphereOffset(pillW, 0, -1);
            double lx = cx + sphOff * scale - (36 * scale) / 2;
            double lc = lx + 36 * scale / 2;
            double rx = cx - sphOff * scale - (36 * scale) / 2;

            Console.WriteLine($"{scale:0.00} | {w,6:0.0} | {left,6:0.0} | {center,6:0.0} | {dev,5:0.0} | {lc,8:0.0} | {rx + 36*scale/2,8:0.0}");
        }

        Console.WriteLine();
        Console.WriteLine("期望：偏差恒为 0，左右球体中心对称于 1280");
    }
}

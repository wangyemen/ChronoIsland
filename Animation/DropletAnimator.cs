using System;
using System.Threading.Tasks;
using ChronoIsland.Core;

namespace ChronoIsland.Animation;

/// <summary>
/// 水珠生成 / 收起动效编排（docs §4）。
/// 收起：A Pinch → B Droplet → C Vanish；生成 = 严格逆序。
/// </summary>
public static class DropletAnimator
{
    /// <summary>收起总时长（与三段式 setTimeout 对应）</summary>
    public const int CollapseMs = 680;

    /// <summary>水珠收起：胶囊 → 缩成点 → 水珠吸附上升 → 顶部消失</summary>
    public static async Task CollapseAsync(IslandItem it)
    {
        it.IsDying = true;
        it.ContentOpacity.To(0);

        // A · Pinch：宽度收敛到 36（正圆）+ 高光核心浮现
        it.SetPreset(SpringPresets.Snappy, it.Width, it.Height, it.Glow, it.Opacity);
        it.Width.To(IslandMetrics.SphereSize);
        it.Height.To(IslandMetrics.IslandHeight);
        it.Glow.To(1);

        await Task.Delay(140);
        if (!it.IsDying) return;

        // B · Droplet：压扁拉长 + 颈缩 + 吸附上提
        it.SetPreset(SpringPresets.Gooey, it.ScaleX, it.ScaleY, it.Y, it.Neck, it.Blur);
        it.ScaleX.To(1.08);
        it.ScaleY.To(0.82);
        it.Y.To(-6);
        it.Neck.To(18);
        it.Blur.To(2);
        it.Opacity.To(0.85);

        await Task.Delay(240);
        if (!it.IsDying) return;

        // C · Vanish：颈缩归零、被吸入、透明度归零
        it.SetPreset(SpringPresets.Dismiss, it.ScaleX, it.ScaleY, it.Neck, it.Opacity, it.Blur);
        it.Neck.To(0);
        it.ScaleX.To(0.6);
        it.ScaleY.To(0.35);
        it.Opacity.To(0);
        it.Blur.To(6);
        it.Glow.To(0);
    }

    /// <summary>水珠生成：从顶部滴落 → 拉伸成圆 → 摊开为胶囊（收起的逆序）</summary>
    public static async Task EmergeAsync(IslandItem it)
    {
        it.IsEmerging = true;
        var (gk, gc) = SpringPresets.Gooey;

        // C⁻¹ + B⁻¹：从顶部下方以水滴形态出现
        it.Width.Snap(IslandMetrics.SphereSize);
        it.Height.Snap(IslandMetrics.IslandHeight);
        it.Y.Snap(-6);
        it.Opacity.Snap(0);
        it.ScaleX.Snap(0.6);
        it.ScaleY.Snap(0.35);
        it.Neck.Snap(0);
        it.Blur.Snap(6);
        it.Glow.Snap(1);
        it.ContentOpacity.Snap(0);
        it.SetPreset(SpringPresets.Gooey, it.Y, it.Opacity, it.ScaleX, it.ScaleY, it.Neck, it.Blur, it.Glow);

        it.Opacity.To(0.9);
        it.ScaleX.To(1.06);
        it.ScaleY.To(0.7);
        it.Neck.To(12);
        it.Blur.To(1.5);

        await Task.Delay(180);

        // 落定
        it.Y.To(IslandMetrics.TopMargin);
        it.ScaleX.To(1);
        it.ScaleY.To(1);
        it.Neck.To(0);
        it.Blur.To(0);
        it.Opacity.To(1);

        await Task.Delay(200);

        // A⁻¹：摊开为胶囊
        it.SetPreset(SpringPresets.Snappy, it.Width, it.Height);
        it.Glow.To(0);
        it.ContentOpacity.To(1);
        it.IsEmerging = false;
    }
}

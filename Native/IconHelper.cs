using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Forms;

namespace ChronoIsland.Native;

/// <summary>
/// 图标加载。优先使用 assets\icon.ico（用户放置），缺失时回退到内置矢量胶囊图标。
/// </summary>
public static class IconHelper
{
    private static readonly string[] Candidates =
    {
        // 发布目录 / 开发目录
        Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico"),
        Path.Combine(AppContext.BaseDirectory, "icon.ico"),
        Path.Combine(Directory.GetCurrentDirectory(), "assets", "icon.ico"),
        Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico")
    };

    /// <summary>找到用户放置的 .ico，没有则返回 null</summary>
    public static string? FindIconFile()
    {
        foreach (var p in Candidates)
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>
    /// 托盘图标：优先 ico，否则内置矢量。
    /// 尺寸跟随系统设置（小图标 16 / 普通 32 / 高 DPI 更大）——
    /// 固定 32 在 125%/150% 缩放下会显得偏小或模糊。
    /// </summary>
    public static System.Drawing.Icon LoadTrayIcon()
    {
        var file = FindIconFile();
        int size = SystemInformation.SmallIconSize.Width;      // 系统实际需要的尺寸
        if (size <= 0) size = 16;
        if (file != null)
        {
            try { return new System.Drawing.Icon(file, new System.Drawing.Size(size, size)); }
            catch (Exception ex) { App.Log("Icon", ex); }
            try { return new System.Drawing.Icon(file); }       // 让系统自选
            catch (Exception ex) { App.Log("Icon", ex); }
        }
        return BitmapToIcon(RenderBuiltinIcon(Math.Max(size, 32)));
    }

    /// <summary>
    /// 窗口图标（ImageSource）。
    /// 注意：BitmapImage 直接读 .ico 只会取第一帧（通常 16×16），
    /// 必须显式指定 DecodePixelWidth 或用 IconBitmapDecoder 解出大尺寸。
    /// </summary>
    public static ImageSource? LoadWindowIcon(int size = 256)
    {
        var file = FindIconFile();
        if (file != null)
        {
            try
            {
                // 用 IconBitmapDecoder 选最大帧，避免拿到 16×16
                using var stream = File.OpenRead(file);
                var decoder = new IconBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame? best = null;
                foreach (var f in decoder.Frames)
                    if (best == null || f.PixelWidth > best.PixelWidth) best = f;
                if (best != null) return best;
            }
            catch (Exception ex) { App.Log("Icon", ex); }
            // 退路：显式解码尺寸
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(file, UriKind.Absolute);
                bi.DecodePixelWidth = size;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                return bi;
            }
            catch (Exception ex) { App.Log("Icon", ex); }
        }
        return RenderBuiltinIcon(64);
    }

    /// <summary>按需获取指定尺寸的窗口/任务栏图标</summary>
    public static ImageSource? LoadWindowIconSized(int size)
    {
        var file = FindIconFile();
        if (file == null) return RenderBuiltinIcon(size);
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(file, UriKind.Absolute);
            bi.DecodePixelWidth = size;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            return bi;
        }
        catch (Exception ex) { App.Log("Icon", ex); return RenderBuiltinIcon(size); }
    }

    /// <summary>BitmapSource → Icon（用于托盘）</summary>
    private static System.Drawing.Icon BitmapToIcon(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var bmp = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// 内置矢量图标：蓝紫渐变胶囊 + 左右两个小球（与集群设计呼应）。
    /// 与 IslandMetrics 同一母形态：高 36、圆角 = 半高、球体 36 正圆。
    /// </summary>
    private static BitmapSource RenderBuiltinIcon(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double s = size;
            double cx = s / 2, cy = s / 2;
            double pillW = s * 0.52, pillH = s * 0.26;
            double r = s * 0.07;                        // 球体半径

            var grad = new LinearGradientBrush(System.Windows.Media.Color.FromRgb(0x5E, 0x9B, 0xFF),
                                               System.Windows.Media.Color.FromRgb(0xB2, 0x7B, 0xFF), 0);

            // 左球
            dc.DrawEllipse(grad, null, new Point(cx - pillW / 2 - r * 1.9, cy), r, r);
            // 右球
            dc.DrawEllipse(grad, null, new Point(cx + pillW / 2 + r * 1.9, cy), r, r);
            // 中央胶囊
            dc.DrawRoundedRectangle(grad, null,
                new Rect(cx - pillW / 2, cy - pillH / 2, pillW, pillH), pillH / 2, pillH / 2);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }
}

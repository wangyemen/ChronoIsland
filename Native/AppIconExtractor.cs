using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChronoIsland.Native;

/// <summary>
/// 应用图标提取（借鉴 FluidBar 的 MediaSourceVisuals）。
/// 路径：AUMID → 进程名 → 窗口 WM_GETICON → 回退 exe 图标 → 缓存 PNG → 加载为 ImageSource。
/// LRU 缓存避免重复提取（docs §6 内存预算）。
/// </summary>
public static class AppIconExtractor
{
    private const uint WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL = 0;
    private const int GCL_HICON = -14;
    private const int GCL_HICONSM = -34;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetClassLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChronoIsland", "icons");

    private static readonly Dictionary<string, ImageSource?> MemCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> CacheOrder = new();
    private const int MaxCache = 128;

    /// <summary>AUMID → 可能的进程名列表（借鉴 FluidBar ProcessNamesForSource）</summary>
    public static IReadOnlyList<string> ProcessNamesFor(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return Array.Empty<string>();
        var s = sourceId.ToLowerInvariant();

        if (s.Contains("chrome")) return new[] { "chrome" };
        if (s.Contains("msedge") || s.Contains("edge")) return new[] { "msedge" };
        if (s.Contains("firefox")) return new[] { "firefox" };
        if (s.Contains("brave")) return new[] { "brave" };
        if (s.Contains("opera")) return new[] { "opera" };
        if (s.Contains("spotify")) return new[] { "Spotify" };
        if (s.Contains("cloudmusic") || s.Contains("netease")) return new[] { "cloudmusic" };
        if (s.Contains("qqmusic")) return new[] { "QQMusic" };
        if (s.Contains("kugou")) return new[] { "KuGou" };
        if (s.Contains("kwmusic") || s.Contains("kuwo")) return new[] { "kwmusic" };
        if (s.Contains("zunemusic") || s.Contains("groove")) return new[] { "Music" };
        if (s.Contains("vlc")) return new[] { "vlc" };
        if (s.Contains("potplayer")) return new[] { "PotPlayerMini64", "PotPlayerMini" };
        if (s.Contains("foobar")) return new[] { "foobar2000" };
        if (s.Contains("musicbee")) return new[] { "MusicBee" };
        if (s.Contains("aimp")) return new[] { "AIMP" };
        if (s.Contains("itunes")) return new[] { "iTunes" };
        if (s.Contains("wmplayer")) return new[] { "wmplayer" };

        // 通用：AUMID 形如 "Vendor.App!Task" → 取 vendor / 首段作为进程名猜测
        var bang = sourceId.IndexOf('!');
        var head = bang > 0 ? sourceId[..bang] : sourceId;
        var dot = head.LastIndexOf('.');
        var tail = dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head;
        return string.IsNullOrWhiteSpace(tail) ? Array.Empty<string>() : new[] { tail };
    }

    /// <summary>
    /// 获取应用图标（ImageSource）。找不到时返回 null，调用方用占位样式。
    /// </summary>
    public static ImageSource? GetIcon(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return null;
        var key = sourceId;

        lock (MemCache)
        {
            if (MemCache.TryGetValue(key, out var cached)) return cached;
        }

        ImageSource? result = null;
        try
        {
            var png = ExtractToPng(sourceId) ?? BuiltInFallback(sourceId);
            if (png != null && File.Exists(png))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(png, UriKind.Absolute);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 64;
                bi.EndInit();
                bi.Freeze();
                result = bi;
            }
        }
        catch (Exception ex) { App.Log("AppIcon", ex); }

        lock (MemCache)
        {
            MemCache[key] = result;
            CacheOrder.Enqueue(key);
            while (CacheOrder.Count > MaxCache) MemCache.Remove(CacheOrder.Dequeue());
        }
        return result;
    }

    /// <summary>提取并缓存为 PNG，返回路径；失败返回 null</summary>
    private static string? ExtractToPng(string sourceId)
    {
        var names = ProcessNamesFor(sourceId);
        if (names.Count == 0) return null;

        Directory.CreateDirectory(CacheDir);
        var outPath = Path.Combine(CacheDir, Sanitize(sourceId) + ".png");
        if (File.Exists(outPath)) return outPath;

        var procs = new List<Process>();
        foreach (var n in names)
        {
            try { procs.AddRange(Process.GetProcessesByName(n)); } catch { }
        }
        if (procs.Count == 0) return null;

        using var icon = TryWindowIcon(procs) ?? TryExeIcon(procs);
        if (icon == null) return null;

        using var bmp = icon.ToBitmap();
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        return outPath;
    }

    private static System.Drawing.Icon? TryWindowIcon(List<Process> procs)
    {
        var pids = new HashSet<uint>();
        foreach (var p in procs) { try { pids.Add((uint)p.Id); } catch { } }
        if (pids.Count == 0) return null;

        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            found = h; return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return null;

        var hi = SendMessage(found, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        if (hi == IntPtr.Zero) hi = SendMessage(found, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero);
        if (hi == IntPtr.Zero) hi = GetClassLong(found, GCL_HICON);
        if (hi == IntPtr.Zero) hi = GetClassLong(found, GCL_HICONSM);
        if (hi == IntPtr.Zero) return null;

        try
        {
            // 复制一份，避免句柄随窗口销毁失效
            using var src = System.Drawing.Icon.FromHandle(hi);
            return (System.Drawing.Icon)src.Clone();
        }
        catch { return null; }
    }

    private static System.Drawing.Icon? TryExeIcon(List<Process> procs)
    {
        foreach (var p in procs)
        {
            try
            {
                var exe = p.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    return System.Drawing.Icon.ExtractAssociatedIcon(exe);
            }
            catch { }
        }
        return null;
    }

    /// <summary>已知应用的兜底图标：用系统 Shell 按协议/目录找不到时，生成带首字母的渐变块</summary>
    private static string? BuiltInFallback(string sourceId)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var outPath = Path.Combine(CacheDir, Sanitize(sourceId) + ".fb.png");
            if (File.Exists(outPath)) return outPath;

            var (c1, c2) = ColorFor(sourceId);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var grad = new LinearGradientBrush(c1, c2, 45);
                dc.DrawRoundedRectangle(grad, null, new System.Windows.Rect(0, 0, 64, 64), 18, 18);
            }
            var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath)) enc.Save(fs);
            return outPath;
        }
        catch { return null; }
    }

    private static (Color, Color) ColorFor(string id)
    {
        var s = id.ToLowerInvariant();
        if (s.Contains("chrome")) return (Color.FromRgb(0x42, 0x85, 0xF4), Color.FromRgb(0x1A, 0x73, 0xE8));
        if (s.Contains("edge")) return (Color.FromRgb(0x0F, 0x9C, 0xE4), Color.FromRgb(0x0A, 0x6F, 0xB8));
        if (s.Contains("firefox")) return (Color.FromRgb(0xFF, 0x96, 0x1E), Color.FromRgb(0xE5, 0x4E, 0x00));
        if (s.Contains("spotify")) return (Color.FromRgb(0x1E, 0xD7, 0x60), Color.FromRgb(0x14, 0x9F, 0x46));
        if (s.Contains("cloudmusic") || s.Contains("netease")) return (Color.FromRgb(0xFF, 0x3B, 0x3B), Color.FromRgb(0xC2, 0x18, 0x29));
        if (s.Contains("qqmusic")) return (Color.FromRgb(0x2E, 0xD0, 0x77), Color.FromRgb(0x0F, 0x9E, 0x54));
        if (s.Contains("kugou")) return (Color.FromRgb(0x2E, 0x9C, 0xFF), Color.FromRgb(0x11, 0x6B, 0xC8));

        // 稳定散列 → 色相
        int h = 0; foreach (var ch in s) h = (h * 31 + ch) & 0xFFFFFF;
        double hue = h % 360;
        return (FromHsl(hue, 0.62, 0.60), FromHsl((hue + 25) % 360, 0.66, 0.46));
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private static string Sanitize(string s)
    {
        var arr = s.ToCharArray();
        for (int i = 0; i < arr.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), arr[i]) >= 0) arr[i] = '_';
        var name = new string(arr);
        return name.Length > 80 ? name[..80] : name;
    }
}

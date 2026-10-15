using System;
using System.IO;
using ChronoIsland.Native;

namespace ChronoIsland;

public static class IconDiag
{
    public static void Run()
    {
        Console.WriteLine("=== 应用图标提取诊断 ===");
        var targets = new[]
        {
            "Chrome", "Spotify.exe", "msedge", "cloudmusic",
            "MSEdge", "firefox", "QQMusic"
        };
        foreach (var t in targets)
        {
            var names = AppIconExtractor.ProcessNamesFor(t);
            Console.Write($"  {t,-16} → 进程名 [{string.Join(",", names)}]");
            var icon = AppIconExtractor.GetIcon(t);
            Console.WriteLine(icon == null ? "   图标: 无" : $"   图标: {icon.Width}x{icon.Height} ✓");
        }

        Console.WriteLine();
        Console.WriteLine("--- 缓存目录 ---");
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChronoIsland", "icons");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir))
                Console.WriteLine($"  {Path.GetFileName(f)}  {new FileInfo(f).Length}B");
        else Console.WriteLine("  (尚未创建)");
    }
}

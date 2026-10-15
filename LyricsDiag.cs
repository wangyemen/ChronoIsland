using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ChronoIsland;

/// <summary>
/// 歌词链路诊断：验证 Provider 能按轮转策略拿到真实歌词。
/// 用法：ChronoIsland.exe lyrics [标题] [艺人]
///       不带参数则跑内置样本（中文 / 英文各若干）。
/// </summary>
public static class LyricsDiag
{
    private static readonly (string Title, string Artist)[] Samples =
    {
        ("稻香", "周杰伦"),
        ("晴天", "周杰伦"),
        ("浮夸", "陈奕迅"),
        ("Shape of You", "Ed Sheeran"),
        ("Yellow", "Coldplay")
    };

    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 歌词链路诊断 ===");
        Console.WriteLine("策略: QQ音乐 → 网易云 → LRCLIB，每源配额 3 次，命中即缓存");
        Console.WriteLine();

        foreach (var (t, a) in Samples) RunOne(t, a);
    }

    public static void RunOne(string title, string artist)
    {
        var prov = new Sources.LyricsProvider();
        var sw = Stopwatch.StartNew();
        var call = prov.GetAsync(title, artist, "", 0);
        var done = Task.WhenAny(call, Task.Delay(45000)).GetAwaiter().GetResult();
        if (done != call)
        {
            Console.WriteLine($"[{title}] ⏱ 超时 45s");
            return;
        }
        var r = call.GetAwaiter().GetResult();
        Console.WriteLine($"[{title} / {artist}]");
        Console.WriteLine($"  成功={r.Success}  来源={r.Source}  行数={r.Lines.Count}  " +
                          $"同步={r.SyncType}  耗时={sw.ElapsedMilliseconds}ms");
        for (int i = 0; i < Math.Min(3, r.Lines.Count); i++)
            Console.WriteLine($"    [{r.Lines[i].StartMs}ms] {r.Lines[i].Text}");
        if (!r.Success) Console.WriteLine("    （无歌词：可能是纯音乐，或所有源均未命中）");
        Console.WriteLine();
    }
}
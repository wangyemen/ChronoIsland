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

    /// <summary>
    /// 轮转验证：**共用一个 Provider** 连续查多首歌，打印每次命中的源，
    /// 用来确认"QQ音乐连续用 3 次后自动换到下一个源"确实生效。
    /// 用法：ChronoIsland.exe lyrics rotate
    /// </summary>
    public static void RunRotation()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 轮转验证（共用一个 Provider，探测是否每 3 次换源）===");
        Console.WriteLine();

        var songs = new (string Title, string Artist)[]
        {
            ("稻香", "周杰伦"), ("晴天", "周杰伦"), ("浮夸", "陈奕迅"),
            ("七里香", "周杰伦"), ("富士山下", "陈奕迅"), ("青花瓷", "周杰伦"),
            ("演员", "薛之谦"), ("斑马斑马", "宋冬野"), ("成都", "赵雷"),
            ("Yellow", "Coldplay"), ("Shape of You", "Ed Sheeran"), ("Hello", "Adele")
        };

        // 关键：整轮只 new 一次，轮转状态才跨曲目累积
        var prov = new Sources.LyricsProvider();
        var usage = new System.Collections.Generic.Dictionary<string, int>();

        for (int i = 0; i < songs.Length; i++)
        {
            var (t, a) = songs[i];
            try
            {
                var r = prov.GetAsync(t, a, "", 0).GetAwaiter().GetResult();
                var src = r.Success ? r.Source.ToString() : "(miss)";
                if (r.Success)
                    usage[src] = usage.TryGetValue(src, out var n) ? n + 1 : 1;
                Console.WriteLine($"{i + 1,2}. {src,-10} {(r.Success ? r.Lines.Count + "行" : "-"),-6} {t} / {a}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{i + 1,2}. ERROR {ex.GetType().Name}: {ex.Message}  {t} / {a}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("各源命中次数：");
        foreach (var kv in usage) Console.WriteLine($"  {kv.Key}: {kv.Value}");
    }

    public static void RunOne(string title, string artist, int durationMs = 0)
    {
        var prov = new Sources.LyricsProvider();
        var sw = Stopwatch.StartNew();
        var call = prov.GetAsync(title, artist, "", durationMs);
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
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ChronoIsland.Sources;

namespace ChronoIsland;

/// <summary>
/// DSH 工作状态源自检：不启动 UI，直接读取投影缓存并持续观察状态变化。
/// 用法：
///   ChronoIsland.exe dsh            → 打印一次快照 + 探测到的路径
///   ChronoIsland.exe dsh watch [秒] → 持续轮询，打印每次状态变化
/// </summary>
public static class DshDiag
{
    public static void Run(int watchSeconds)
    {
        Console.WriteLine("=== DSH 工作状态源自检 ===");
        var home = DshSource.HomeDir();
        Console.WriteLine("DSH_HOME 探测结果 : " + (home ?? "(未找到)"));
        if (home != null)
        {
            var dir = Path.Combine(home, "storages", "session_projcache", "sessions");
            Console.WriteLine("投影缓存目录      : " + dir);
            Console.WriteLine("目录存在          : " + Directory.Exists(dir));
            if (Directory.Exists(dir))
                Console.WriteLine("缓存文件数        : " + Directory.GetFiles(dir, "*.json").Length);
        }
        Console.WriteLine();

        if (watchSeconds <= 0)
        {
            PrintAll();
            PrintOnce();
            return;
        }

        Console.WriteLine("--- 持续观察 " + watchSeconds + "s（每 500ms 采样，仅打印变化）---");
        string last = "";
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < watchSeconds)
        {
            var snap = DshSource.ReadActiveSession();
            var line = snap == null
                ? "(无缓存 / 未找到)"
                : "working=" + snap.Working + " turn=" + snap.Turn + " step=" + snap.Step +
                  " ctx=" + snap.ContextPercent + "% model=" + snap.Model + " title=" + snap.Title;
            if (line != last)
            {
                Console.WriteLine("[" + sw.Elapsed.TotalSeconds.ToString("F1") + "s] " + line);
                last = line;
            }
            Thread.Sleep(500);
        }
        Console.WriteLine("--- 观察结束 ---");
        PrintOnce();
    }

    /// <summary>
    /// 列出全部"正在工作"的会话 —— 多任务验证：每个会话各占一条灵动岛。
    /// </summary>
    private static void PrintAll()
    {
        Console.WriteLine("=== 多任务：全部工作中的会话（每条 = 一条独立岛）===");
        var list = DshSource.ReadWorkingSessions();
        Console.WriteLine("工作会话数: " + list.Count);
        Console.WriteLine();
        if (list.Count == 0)
        {
            Console.WriteLine("(当前没有任何会话在工作中)");
            Console.WriteLine();
            return;
        }
        Console.WriteLine("  #   岛 Id                  轮/步      上下文   标题");
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            Console.WriteLine("  " + (i + 1) + "   dsh:" + s.SessionId +
                "   第" + Math.Max(s.Turn, 1) + "轮/步" + s.Step +
                "   " + s.ContextPercent + "%      " + s.Title);
        }
        Console.WriteLine();
    }

    private static void PrintOnce()
    {
        var snap = DshSource.ReadActiveSession();
        if (snap == null)
        {
            Console.WriteLine("快照: null（DSH 未运行、路径不对，或没有任何会话缓存）");
            return;
        }
        Console.WriteLine("快照:");
        Console.WriteLine("  Working        : " + snap.Working);
        Console.WriteLine("  Turn / Step    : " + snap.Turn + " / " + snap.Step);
        Console.WriteLine("  Turns / Steps  : " + snap.Turns + " / " + snap.Steps);
        Console.WriteLine("  Title          : " + snap.Title);
        Console.WriteLine("  Model          : " + snap.Model);
        Console.WriteLine("  Context        : " + snap.SurfaceTokens + " / " + snap.ContextWindow
            + "  (" + snap.ContextPercent + "%)");
        Console.WriteLine("  StepElapsedMs  : " + snap.StepElapsedMs);
        Console.WriteLine("  LastOutputTok  : " + snap.LastOutputTokens);
        Console.WriteLine("  SessionId      : " + snap.SessionId);
        Console.WriteLine();
        Console.WriteLine("-> 灵动岛将显示：第一行「"
            + (string.IsNullOrWhiteSpace(snap.Title) ? "DSH 工作中" : snap.Title)
            + "」，第二行「第 " + Math.Max(snap.Turn, 1) + " 轮 · 步 " + snap.Step
            + " · 上下文 " + snap.ContextPercent + "% · " + ShortModel(snap.Model) + "」");
    }

    private static string ShortModel(string m)
    {
        if (string.IsNullOrEmpty(m)) return "";
        var i = m.LastIndexOf('/');
        return i >= 0 && i < m.Length - 1 ? m[(i + 1)..] : m;
    }
}

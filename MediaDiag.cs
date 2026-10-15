using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace ChronoIsland;

/// <summary>媒体诊断：dotnet run -- media</summary>
public static class MediaDiag
{
    public static async Task Run()
    {
        Console.WriteLine("=== GSMTC 会话诊断 ===");
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (mgr == null) { Console.WriteLine("manager = null (GSMTC 不可用)"); return; }
            Console.WriteLine("manager OK");

            var sessions = mgr.GetSessions();
            Console.WriteLine($"会话数: {sessions?.Count ?? 0}");
            if (sessions != null)
            {
                foreach (var s in sessions)
                {
                    var id = s.SourceAppUserModelId ?? "(null)";
                    var pb = s.GetPlaybackInfo();
                    var pr = await s.TryGetMediaPropertiesAsync();
                    var tl = s.GetTimelineProperties();
                    Console.WriteLine("----");
                    Console.WriteLine($"  AUMID : {id}");
                    Console.WriteLine($"  名称  : {Sources.MediaSourcePolicy.DisplayName(id)}  优先级 {Sources.MediaSourcePolicy.GetSourcePriority(id)}  浏览器 {Sources.MediaSourcePolicy.IsBrowserSource(id)}");
                    Console.WriteLine($"  状态  : {pb?.PlaybackStatus}");
                    Console.WriteLine($"  标题  : {pr?.Title}");
                    Console.WriteLine($"  艺术家: {pr?.Artist}");
                    Console.WriteLine($"  专辑  : {pr?.AlbumTitle}");
                    Console.WriteLine($"  进度  : {tl.Position} / {tl.EndTime}");
                }
            }
            var cur = mgr.GetCurrentSession();
            Console.WriteLine("----");
            Console.WriteLine($"当前会话: {cur?.SourceAppUserModelId ?? "(null)"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("EX: " + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
        }
    }
}

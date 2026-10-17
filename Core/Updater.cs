using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ChronoIsland.Core;

/// <summary>
/// 版本信息（version.txt 解析结果）。
///
/// 【文件格式】放在仓库根目录，由用户手动维护：
///   第 1 行：版本号（如 0.0.2，可带 v 前缀，解析时会去掉）
///   第 2 行：zip 下载链接（GitHub Releases 的 asset 直链）
///   其余行：可选更新说明，原样展示给用户
///
/// 【为什么用明文 txt 而不是 JSON】改一行就发布，不需要工具链；
/// 用 raw.githubusercontent.com 直接读，不消耗 GitHub API 配额。
/// </summary>
public sealed record UpdateInfo(string Version, string DownloadUrl, string Notes);

/// <summary>
/// 自动检查更新 + 自动下载替换。
///
/// 【发布形态】zip 绿色包：解压即用，更新时下载 zip → 解压到临时目录 → 覆盖程序目录。
/// 这样不需要安装器，也不需要处理注册表 / Program Files 提权。
/// </summary>
public static class Updater
{
    /// <summary>
    /// 版本信息地址。
    ///
    /// 【为什么要多个镜像】国内网络下 raw.githubusercontent.com 经常连得上但
    /// 请求超时（实测 TCP 443 通、HTTP 无响应）。jsDelivr 同时支持 gh 仓库文件
    /// 与 Releases 资产直链，作为官方 GitHub 链接的备选：
    ///   · 读 version.txt：gh/<owner>/<repo>@main/version.txt
    ///   · 下 release zip：gh-releases/<owner>/<repo>/releases/<tag>/<asset>
    /// 按顺序尝试几个等价地址，任意一个成功即可。
    /// </summary>
    public static readonly string[] VersionUrls =
    {
        "https://raw.githubusercontent.com/wangyemen/ChronoIsland/main/version.txt",
        "https://cdn.jsdelivr.net/gh/wangyemen/ChronoIsland@main/version.txt",
        "https://gcore.jsdelivr.net/gh/wangyemen/ChronoIsland@main/version.txt",
    };

    /// <summary>主地址（用于日志展示）</summary>
    public const string VersionUrl =
        "https://raw.githubusercontent.com/wangyemen/ChronoIsland/main/version.txt";

    /// <summary>仓库主页（检测失败时给用户一个手动入口）</summary>
    public const string RepoUrl = "https://github.com/wangyemen/ChronoIsland";

    /// <summary>
    /// 把 GitHub Releases 的资产直链翻译成 jsDelivr 等价地址。
    /// 形如 https://github.com/&lt;o&gt;/&lt;r&gt;/releases/download/&lt;tag&gt;/&lt;name&gt;
    /// →    https://cdn.jsdelivr.net/gh-releases/&lt;o&gt;/&lt;r&gt;/releases/&lt;tag&gt;/&lt;name&gt;
    /// 不是该形态时原样返回（说明版本文件写的不是 GitHub 资产链）。
    /// </summary>
    public static string ToJsDelivr(string url)
    {
        const string githubReleases = "https://github.com/";
        const string jsReleases = "https://cdn.jsdelivr.net/gh-releases/";
        if (!url.StartsWith(githubReleases + "wangyemen/ChronoIsland/releases/download/", StringComparison.OrdinalIgnoreCase))
            return url;
        return jsReleases + "wangyemen/ChronoIsland/" + url[githubReleases.Length..];
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // 版本文件只有几十字节，10s 足够；太短会误判慢网。
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub raw 会拒绝没有 User-Agent 的请求
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ChronoIsland-Updater/1.0");
        return c;
    }

    /// <summary>
    /// 拉取远端 version.txt。
    /// 失败（断网 / 404 / 格式不对）一律返回 null，由调用方决定是否提示。
    /// </summary>
    public static async Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct = default)
    {
        // 【逐个镜像尝试】任一成功即返回；全部失败才放弃。
        // 不要加 ?t= 时间戳：raw 对带查询串的路径行为不稳（实测会一直等到超时）。
        foreach (var url in VersionUrls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var raw = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                var info = Parse(raw);
                if (info != null)
                {
                    App.LogInfo("Update", $"版本文件读到了：{info.Version}（来源 {Host(url)}）");
                    return info;
                }
                App.LogInfo("Update", $"版本文件格式不正确：{Host(url)}");
            }
            catch (Exception ex)
            {
                App.LogInfo("Update", $"镜像不可用 {Host(url)}: {ex.Message}");
            }
        }
        App.LogInfo("Update", "所有镜像均不可用");
        return null;
    }

    private static string Host(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    /// <summary>解析 version.txt 内容（第 1 行版本、第 2 行下载链接、其余为说明）</summary>
    public static UpdateInfo? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var idx = 0;
        // 跳过开头空行
        while (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx])) idx++;
        if (idx >= lines.Length) return null;

        var ver = lines[idx++].Trim().TrimStart('v', 'V');
        while (idx < lines.Length && string.IsNullOrWhiteSpace(lines[idx])) idx++;
        if (idx >= lines.Length) return null;

        var url = lines[idx++].Trim();
        if (string.IsNullOrWhiteSpace(ver) || string.IsNullOrWhiteSpace(url)) return null;

        var notes = string.Join("\n", lines.Skip(idx)).Trim();
        return new UpdateInfo(ver, url, notes);
    }

    /// <summary>当前运行版本（取自程序集）</summary>
    public static string CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    /// <summary>
    /// 判断远端是否比本地新。按点分段做数值比较，
    /// 不用字符串比较（否则 "0.0.10" 会被判为小于 "0.0.9"）。
    /// </summary>
    public static bool IsNewer(string remote, string local)
    {
        static int[] Parts(string s) =>
            s.Trim().TrimStart('v', 'V')
             .Split(new[] { '.', '-', '+' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(x => int.TryParse(x, out var n) ? n : 0)
             .ToArray();

        var a = Parts(remote);
        var b = Parts(local);
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    /// <summary>
    /// 下载 zip 并原地覆盖程序目录，然后重启。
    ///
    /// 【为什么不能在当前进程里直接覆盖】程序自己在跑，exe / dll 被系统锁住。
    /// 做法：把解压好的新文件放到临时目录，再写一个 .bat，
    /// 等本进程退出后由它来 copy /x 覆盖 + 重新启动。
    /// </summary>
    /// <param name="progress">0~1 的进度回调（下载阶段）</param>
    public static async Task<bool> DownloadAndApplyAsync(
        UpdateInfo info, Action<double>? progress = null, CancellationToken ct = default)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var work = Path.Combine(Path.GetTempPath(), "ChronoIslandUpd_" + stamp);
        var zipPath = Path.Combine(work, "update.zip");
        var extractDir = Path.Combine(work, "new");

        try
        {
            Directory.CreateDirectory(work);

            // ---- 1) 下载（带进度）----
            // 官方 GitHub 资产链优先；失败（国内网络常连不上 github.com）
            // 自动回退 jsDelivr 的等价 gh-releases 直链（同一份文件）。
            var downloadUrls = new[] { info.DownloadUrl, ToJsDelivr(info.DownloadUrl) }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Exception? lastError = null;
            bool downloaded = false;
            foreach (var url in downloadUrls)
            {
                try
                {
                    App.LogInfo("Update", $"开始下载 {Host(url)} …");
                    using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                                .ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var total = resp.Content.Headers.ContentLength ?? -1;
                        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await using var dst = File.Create(zipPath);
                        var buf = new byte[81920];
                        long got = 0;
                        int n;
                        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                            got += n;
                            if (total > 0) progress?.Invoke(Math.Clamp((double)got / total, 0, 1));
                        }
                    }
                    App.LogInfo("Update", $"下载完成（{Host(url)}）");
                    downloaded = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    App.LogInfo("Update", $"下载源失败 {Host(url)}: {ex.Message}，尝试下一个");
                    try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                }
            }
            if (!downloaded)
            {
                App.LogInfo("Update", "所有下载源均失败: " + (lastError?.Message ?? "未知"));
                return false;
            }

            // ---- 2) 解压 ----
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // 有些 zip 会在根目录多一层文件夹（ChronoIsland/xxx.exe），自动下钻一层
            var root = extractDir;
            if (!File.Exists(Path.Combine(root, "ChronoIsland.exe")))
            {
                var sub = Directory.GetDirectories(root)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "ChronoIsland.exe")));
                if (sub != null) root = sub;
            }
            if (!File.Exists(Path.Combine(root, "ChronoIsland.exe")))
            {
                App.LogInfo("Update", "更新包内未找到 ChronoIsland.exe，放弃");
                return false;
            }

            // ---- 3) 写替换脚本，退出后执行 ----
            var exePath = Path.Combine(appDir, "ChronoIsland.exe");
            var bat = Path.Combine(work, "apply.bat");
            File.WriteAllText(bat, $"""
@echo off
chcp 65001 >nul
title ChronoIsland 更新中
echo 正在等待 ChronoIsland 退出...
:wait
tasklist /FI "IMAGENAME eq ChronoIsland.exe" 2>nul | find /I "ChronoIsland.exe" >nul
if not errorlevel 1 (
  timeout /t 1 /nobreak >nul
  goto wait
)
echo 正在安装新版本 {info.Version} ...
robocopy "{root}" "{appDir}" /E /IS /IT /R:3 /W:1 /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (
  echo 更新失败：无法写入程序目录。
  echo 请手动解压更新包覆盖，或关闭占用该目录的程序后重试。
  pause
  exit /b 1
)
start "" "{exePath}"
timeout /t 2 /nobreak >nul
cd /d "%TEMP%"
rmdir /S /Q "{work}"
exit
""");

            App.LogInfo("Update", $"更新包已就绪（{info.Version}），启动替换脚本");
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            App.LogInfo("Update", "下载/应用更新失败: " + ex.Message);
            try { Directory.Delete(work, true); } catch { /* 清理失败无所谓 */ }
            return false;
        }
    }
}

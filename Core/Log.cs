using System;
using System.IO;
using System.Text;

namespace ChronoIsland.Core;

/// <summary>日志级别。顺序即严重度，数值越大越严重。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>
/// 统一日志出口（docs §6 / §10）。
///
/// 【为什么要有这个类】此前 App.Log / App.LogInfo 各自无条件 File.AppendAllText，
/// 而 DSH 源 1s 轮询、媒体 ~900ms 推送都会写日志 —— app.log 会无限增长。
/// 设计文档 §6「内存」与 §10 都写了"滚动 1 MB × 2"，但一直没有实现。
///
/// 【职责】
///   · 唯一的日志写入点：所有模块（含后续插件）都走这里，不再各自拼路径；
///   · 大小轮转：app.log 超过 <see cref="MaxBytes"/> 时滚动，只保留 <see cref="MaxFiles"/> 个；
///   · 级别过滤：默认 Info，环境变量 CHRONO_LOG=Debug / Warn / Error 可提级（§10）。
///
/// 【线程安全】单个静态锁串行化"轮转 + 追加"，因此任何线程（含后台 Source）都可直接调用。
/// 【失败兜底】日志自身出错一律吞掉 —— 日志失败绝不能影响主流程。
/// </summary>
public static class Log
{
    /// <summary>单个日志文件的大小上限（1 MB，docs §6）。</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>日志文件个数上限（app.log + app.1.log，docs §6「1 MB × 2」）。</summary>
    public const int MaxFiles = 2;

    private static readonly object Gate = new();

    /// <summary>日志目录：%LOCALAPPDATA%\ChronoIsland\logs</summary>
    public static string Dir { get; }

    /// <summary>主日志文件路径</summary>
    public static string FilePath => Path.Combine(Dir, "app.log");

    /// <summary>当前最低写入级别（低于它的调用直接返回，零 IO）</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>已写入的字节数缓存，避免每次写都去问文件系统</summary>
    private static long _bytes = -1;

    static Log()
    {
        Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChronoIsland", "logs");
        ConfigureFromEnvironment();
    }

    /// <summary>
    /// 从环境变量读取级别覆盖：CHRONO_LOG=Debug | Info | Warn | Error（docs §10）。
    /// 解析失败保持 Info，绝不抛异常。
    /// </summary>
    public static void ConfigureFromEnvironment()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("CHRONO_LOG");
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse<LogLevel>(raw.Trim(), ignoreCase: true, out var lv))
                MinLevel = lv;
        }
        catch { /* 保持默认 */ }
    }

    public static void Debug(string scope, string message) => Write(LogLevel.Debug, scope, message);
    public static void Info(string scope, string message) => Write(LogLevel.Info, scope, message);
    public static void Warn(string scope, string message) => Write(LogLevel.Warn, scope, message);

    /// <summary>记录异常：第一行为 类型: 消息，随后是堆栈。</summary>
    public static void Error(string scope, Exception ex)
    {
        if (ex == null) return;
        Write(LogLevel.Error, scope,
            $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    /// <summary>记录错误文本（可带异常）。</summary>
    public static void Error(string scope, string message, Exception? ex = null)
        => Write(LogLevel.Error, scope, ex == null ? message : $"{message}\n{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>
    /// 唯一的写入路径：级别过滤 → 加锁 → 轮转 → 追加。
    /// 任何异常都被吞掉（日志失败不影响运行），这是刻意的。
    /// </summary>
    private static void Write(LogLevel level, string scope, string message)
    {
        if (level < MinLevel) return;
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{Tag(level)}] [{scope}] {message}\n";
            var bytes = Encoding.UTF8.GetByteCount(line);

            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                RotateIfNeeded(bytes);
                File.AppendAllText(FilePath, line, Encoding.UTF8);
                _bytes = Math.Max(0, CurrentLength()) + bytes;
            }
        }
        catch { /* 日志失败不影响运行 */ }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "INF"
    };

    /// <summary>当前 app.log 长度；首次调用时从磁盘读取，之后走缓存。</summary>
    private static long CurrentLength()
    {
        if (_bytes >= 0) return _bytes;
        try
        {
            var fi = new FileInfo(FilePath);
            _bytes = fi.Exists ? fi.Length : 0;
        }
        catch { _bytes = 0; }
        return _bytes;
    }

    /// <summary>
    /// 滚动：即将写入的这条会让文件超过 <see cref="MaxBytes"/> 时，
    /// 丢弃最旧的一份，app.log → app.1.log（更高序号同理）。
    /// 必须在 <see cref="Gate"/> 内调用。
    /// </summary>
    private static void RotateIfNeeded(long incoming)
    {
        if (CurrentLength() + incoming <= MaxBytes) return;

        try
        {
            // 最旧的一份直接删除：app.{MaxFiles-1}.log
            var oldest = Path.Combine(Dir, $"app.{MaxFiles - 1}.log");
            if (File.Exists(oldest)) File.Delete(oldest);

            // 依次后移：app.{n}.log → app.{n+1}.log
            for (int i = MaxFiles - 2; i >= 1; i--)
            {
                var src = Path.Combine(Dir, $"app.{i}.log");
                var dst = Path.Combine(Dir, $"app.{i + 1}.log");
                if (File.Exists(src)) File.Move(src, dst, overwrite: true);
            }

            // app.log → app.1.log
            if (MaxFiles >= 2 && File.Exists(FilePath))
                File.Move(FilePath, Path.Combine(Dir, "app.1.log"), overwrite: true);
        }
        catch { /* 轮转失败：至少保证还能继续写（下次再试） */ }

        _bytes = 0;
    }
}

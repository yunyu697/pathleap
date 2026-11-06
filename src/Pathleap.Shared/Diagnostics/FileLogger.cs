using System.Diagnostics;

namespace Pathleap.Shared.Diagnostics;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>
/// 每进程一个的同步文件日志。每日一个文件：{prefix}-yyyyMMdd.log。
/// M1 不做自动清理（删除策略必须走安全删除规范，留给后续里程碑）。
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly string _lpPrefix;
    private readonly object _lpGate = new();
    private StreamWriter? _lpWriter;
    private DateTime _lpFileDate;
    private LogLevel _lpMinimum = LogLevel.Info;
    private bool _lpDisposed;

    public FileLogger(string prefix, string logDirectory)
    {
        _lpPrefix = prefix;
        Directory = AppPaths.EnsureDirectory(logDirectory);
    }

    public string Directory { get; }

    public void SetMinimumLevel(LogLevel level) => _lpMinimum = level;

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Warn(string message) => Write(LogLevel.Warn, message, null);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < _lpMinimum)
        {
            return;
        }

        var lpNow = DateTime.Now;
        var lpLine = string.Format(
            "{0:yyyy-MM-ddTHH:mm:ss.fffzzz} [{1}] [T:{2}] {3}{4}",
            lpNow,
            level.ToString().ToUpperInvariant(),
            Environment.CurrentManagedThreadId,
            message,
            exception is null ? string.Empty : Environment.NewLine + exception);

        lock (_lpGate)
        {
            if (_lpDisposed)
            {
                return;
            }

            try
            {
                EnsureWriter(lpNow);
                _lpWriter!.WriteLine(lpLine);
                _lpWriter.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[{Branding.ProductId}] 日志写入失败: {ex.Message}");
            }
        }
    }

    private void EnsureWriter(DateTime now)
    {
        var lpDate = now.Date;
        if (_lpWriter is not null && lpDate == _lpFileDate)
        {
            return;
        }

        _lpWriter?.Dispose();
        _lpFileDate = lpDate;
        var lpPath = Path.Combine(Directory, $"{_lpPrefix}-{lpDate:yyyyMMdd}.log");
        _lpWriter = new StreamWriter(
            new FileStream(lpPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _lpWriter.AutoFlush = true;
    }

    public void Dispose()
    {
        lock (_lpGate)
        {
            if (_lpDisposed)
            {
                return;
            }

            _lpDisposed = true;
            _lpWriter?.Dispose();
            _lpWriter = null;
        }
    }
}

/// <summary>进程内静态日志入口。启动时调用 Initialize 一次。</summary>
public static class AppLog
{
    private static FileLogger? _lpLogger;

    public static bool IsInitialized => _lpLogger is not null;

    public static string LogDirectory => _lpLogger?.Directory ?? AppPaths.LogsRoot;

    public static void Initialize(string prefix, LogLevel minimum = LogLevel.Info)
    {
        _lpLogger ??= new FileLogger(prefix, AppPaths.LogsRoot);
        _lpLogger.SetMinimumLevel(minimum);
    }

    public static void SetMinimumLevel(LogLevel level) => _lpLogger?.SetMinimumLevel(level);

    public static void Debug(string message) => _lpLogger?.Debug(message);

    public static void Info(string message) => _lpLogger?.Info(message);

    public static void Warn(string message) => _lpLogger?.Warn(message);

    public static void Error(string message, Exception? exception = null) => _lpLogger?.Error(message, exception);

    public static void Shutdown()
    {
        _lpLogger?.Dispose();
        _lpLogger = null;
    }
}

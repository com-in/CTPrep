using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>日志接收器（服务层依赖此接口，UI 层订阅 AppLogger 的事件）。</summary>
public interface ILogSink
{
    LogLevel MinLevel { get; }

    void Log(LogLevel level, string message);

    /// <summary>记录一条将要执行的命令（演练模式下会打印但不会执行）。</summary>
    void Command(string display);
}

public static class LogSinkExtensions
{
    public static void Debug(this ILogSink sink, string message) => sink.Log(LogLevel.Debug, message);

    public static void Info(this ILogSink sink, string message) => sink.Log(LogLevel.Info, message);

    public static void Warn(this ILogSink sink, string message) => sink.Log(LogLevel.Warn, message);

    public static void Error(this ILogSink sink, string message) => sink.Log(LogLevel.Error, message);
}

/// <summary>一条日志。</summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string Display => $"[{Time:HH:mm:ss}] [{Level}] {Message}";
}

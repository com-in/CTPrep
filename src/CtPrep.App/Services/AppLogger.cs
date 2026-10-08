using System.Text;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>线程安全日志：同时写文件并抛出事件供 UI 订阅。</summary>
public sealed class AppLogger : ILogSink
{
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _logFilePath = string.Empty;

    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>UI 订阅此事件显示日志。</summary>
    public event Action<LogEntry>? EntryWritten;

    public string LogFilePath => _logFilePath;

    /// <summary>设置日志目录并创建日志文件。</summary>
    public void AttachFile(string directory)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _logFilePath = Path.Combine(directory, $"ctprep-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _writer = new StreamWriter(new FileStream(_logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read), Encodings.Console)
                {
                    AutoFlush = true,
                };
            }
            catch
            {
                _writer = null;
            }
        }
    }

    public void Log(LogLevel level, string message)
    {
        if (level < MinLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTime.Now, level, message);

        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(entry.Display);
            }
            catch
            {
                // 日志写失败不能影响主流程
            }
        }

        EntryWritten?.Invoke(entry);
    }

    public void Command(string display) => Log(LogLevel.Debug, $"$ {display}");

    public void Close()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // ignore
            }

            _writer = null;
        }
    }
}

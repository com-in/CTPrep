using System.IO.Compression;

namespace CtPrep.App.Services;

/// <summary>
/// 把日志、配置与运行环境信息打包成一个 zip，方便反馈问题。
/// 只做只读收集：不改动任何配置文件，也不读取注册表。
/// </summary>
public sealed class DiagnosticsService
{
    private readonly ILogSink _log;

    public DiagnosticsService(ILogSink log)
    {
        _log = log;
    }

    /// <summary>生成诊断包，返回 zip 的绝对路径。</summary>
    /// <param name="runtimeDir">运行时目录；logs 子目录会被整个收进去。</param>
    /// <param name="extraInfo">附加的现场信息（系统信息、磁盘列表等），写入 diagnostics.txt。</param>
    public string CreateBundle(string runtimeDir, string extraInfo)
    {
        Directory.CreateDirectory(runtimeDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(runtimeDir, $"CTPrep-diagnostics-{stamp}.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var logsDir = Path.Combine(runtimeDir, "logs");
            if (Directory.Exists(logsDir))
            {
                foreach (var file in Directory.GetFiles(logsDir, "*", SearchOption.AllDirectories))
                {
                    // 用相对于 runtime 的路径做条目名，解压后能看到 logs/... 的层次
                    var entry = Path.GetRelativePath(runtimeDir, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
                }
            }

            // 配置里可能有自定义地址，但不含凭据；仍然一并收集，便于复现
            foreach (var name in new[] { "config.ini", "custom.ini" })
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(path))
                {
                    zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
                }
            }

            var info = zip.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(info.Open()))
            {
                writer.WriteLine($"CTPrep {VersionText()}");
                writer.WriteLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"OS        : {Environment.OSVersion}");
                writer.WriteLine($"64-bit OS : {Environment.Is64BitOperatingSystem}");
                writer.WriteLine($"Runtime   : {runtimeDir}");
                writer.WriteLine();
                writer.WriteLine(extraInfo);
            }
        }

        _log.Info($"诊断包已生成：{zipPath}");
        return zipPath;
    }

    private static string VersionText()
    {
        var version = typeof(DiagnosticsService).Assembly.GetName().Version;
        return version is null ? "unknown" : version.ToString();
    }
}

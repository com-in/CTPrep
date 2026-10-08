using System.Diagnostics;
using System.Text;

namespace CtPrep.App.Services;

/// <summary>命令执行结果。</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, string CommandLine)
{
    public bool Success => ExitCode == 0;

    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}{Environment.NewLine}{StdErr}";

    /// <summary>失败时抛异常。</summary>
    public ProcessResult EnsureSuccess(string friendlyName)
    {
        if (!Success)
        {
            throw new InvalidOperationException(
                $"{friendlyName} 执行失败（退出码 {ExitCode}）。{Environment.NewLine}{Combined}".Trim());
        }

        return this;
    }
}

/// <summary>统一的进程执行器。所有对 dism / diskpart / bcdedit / powershell 的调用都经过这里。</summary>
public sealed class ProcessRunner
{
    private readonly ILogSink _log;

    public ProcessRunner(ILogSink log) => _log = log;

    /// <summary>直接运行可执行文件。</summary>
    public Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        return RunCoreAsync(fileName, arguments, workingDirectory, ct);
    }

    /// <summary>通过 cmd.exe 执行命令行（用于管道 / 重定向）。</summary>
    public Task<ProcessResult> RunCmdAsync(string commandLine, string? workingDirectory = null, CancellationToken ct = default)
    {
        return RunCoreAsync("cmd.exe", $"/d /c {commandLine}", workingDirectory, ct);
    }

    /// <summary>执行一段 PowerShell 脚本（写入临时 .ps1 后运行，避免转义地狱）。</summary>
    public async Task<ProcessResult> RunPowerShellAsync(string script, string? workingDirectory = null, CancellationToken ct = default)
    {
        var dir = workingDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(dir);
        var scriptPath = Path.Combine(dir, $"ctprep-{Guid.NewGuid():N}.ps1");

        // 用 UTF-8 with BOM 保存，保证 Windows PowerShell 5.1 正确识别中文
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), ct).ConfigureAwait(false);

        try
        {
            return await RunCoreAsync(
                "powershell.exe",
                $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                workingDirectory,
                ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    /// <summary>执行 diskpart 脚本文件。</summary>
    public Task<ProcessResult> RunDiskPartAsync(string scriptPath, CancellationToken ct = default)
    {
        return RunCoreAsync("diskpart.exe", $"/s \"{scriptPath}\"", Path.GetDirectoryName(scriptPath), ct);
    }

    private async Task<ProcessResult> RunCoreAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        CancellationToken ct)
    {
        var display = $"{fileName} {arguments}";
        _log.Command(display);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encodings.Console,
            StandardErrorEncoding = Encodings.Console,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stdout.AppendLine(e.Data);
            _log.Debug(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            stderr.AppendLine(e.Data);
            _log.Debug(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // WaitForExitAsync 后需要再等一下，确保异步输出读取完成
        process.WaitForExit();

        var result = new ProcessResult(process.ExitCode, stdout.ToString().Trim(), stderr.ToString().Trim(), display);
        if (!result.Success)
        {
            _log.Warn($"{fileName} 退出码 {result.ExitCode}");
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}

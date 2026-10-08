using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace CtPrep.App.Services;

/// <summary>下载进度。</summary>
public sealed record DownloadProgress(long Received, long? Total, double BytesPerSecond)
{
    public double? Percent => Total is > 0 ? (double)Received / Total.Value : null;

    public string Display
    {
        get
        {
            var got = FormatSize(Received);
            var speed = $"{FormatSize((long)BytesPerSecond)}/s";
            if (Total is > 0)
            {
                var total = FormatSize(Total.Value);
                var pct = Percent!.Value * 100;
                var eta = BytesPerSecond > 1
                    ? TimeSpan.FromSeconds((Total.Value - Received) / BytesPerSecond)
                    : TimeSpan.Zero;
                return $"{pct:0.0}%  {got}/{total}  {speed}  剩余 {eta:hh\\:mm\\:ss}";
            }

            return $"{got}  {speed}";
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}

/// <summary>文件下载：支持断点续传、进度回报、SHA256 校验；本地路径则直接复制。</summary>
public sealed class DownloadService
{
    /// <summary>取本地化文案（供错误消息使用）。</summary>
    private static string T(string key, params object?[] args) => LocalizationService.Current.T(key, args);

    private readonly ILogSink _log;
    private readonly HttpClient _http;

    public DownloadService(ILogSink log)
    {
        _log = log;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CTPrep/1.0");
    }

    /// <summary>
    /// 把 <paramref name="source"/>（URL 或本地路径）落到 <paramref name="targetDirectory"/>，
    /// 返回最终文件路径。已存在且大小一致时直接复用。
    /// </summary>
    public async Task<string> AcquireAsync(
        string source,
        string targetDirectory,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("下载地址为空。", nameof(source));
        }

        Directory.CreateDirectory(targetDirectory);

        // 本地文件 / UNC 路径：直接复制
        var localCandidate = ConfigService.ResolvePath(source);
        if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(localCandidate))
        {
            var destLocal = Path.Combine(targetDirectory, Path.GetFileName(localCandidate));
            if (!string.Equals(Path.GetFullPath(localCandidate), Path.GetFullPath(destLocal), StringComparison.OrdinalIgnoreCase))
            {
                // 目标目录里已有同样大小且校验通过的副本时直接复用，
                // 免得每次运行都把几百 MB ~ 几 GB 的现状文件重新复制一遍
                if (await IsUpToDateAsync(localCandidate, destLocal, expectedSha256, ct).ConfigureAwait(false))
                {
                    _log.Info($"复用已有本地副本，跳过复制：{destLocal}");
                }
                else
                {
                    _log.Info($"复制本地文件：{localCandidate} -> {destLocal}");
                    await CopyWithProgressAsync(localCandidate, destLocal, progress, ct).ConfigureAwait(false);
                }
            }

            await VerifyHashAsync(destLocal, expectedSha256, ct).ConfigureAwait(false);
            return destLocal;
        }

        if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException($"既不是有效的下载地址，本地也不存在该文件：{source}");
        }

        var fileName = GetFileNameFromUrl(source);
        var targetPath = Path.Combine(targetDirectory, fileName);
        var partialPath = targetPath + ".part";

        // 目标已存在且（未配 SHA256 或）校验通过时直接复用，避免每次运行都重新下载几 GB
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0 &&
            await VerifyHashAsync(targetPath, expectedSha256, ct, quiet: true).ConfigureAwait(false))
        {
            _log.Info($"已存在完整文件且校验通过，跳过下载：{targetPath}");
            return targetPath;
        }

        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        var request = new HttpRequestMessage(HttpMethod.Get, source);
        if (existing > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
            _log.Info($"检测到未完成下载，从 {DownloadProgress.FormatSize(existing)} 处续传。");
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // 服务器不支持 Range，从头开始
            _log.Warn("服务器不支持断点续传，重新下载。");
            existing = 0;
            TryDelete(partialPath);
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        var total = response.Content.Headers.ContentLength is { } len ? len + existing : (long?)null;
        _log.Info($"开始下载：{fileName}（{(total is > 0 ? DownloadProgress.FormatSize(total.Value) : "大小未知")}）");

        await using (var httpStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fileStream = new FileStream(partialPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await PumpAsync(httpStream, fileStream, existing, total, progress, ct).ConfigureAwait(false);
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(partialPath, targetPath);

        await VerifyHashAsync(targetPath, expectedSha256, ct).ConfigureAwait(false);
        _log.Info($"下载完成：{targetPath}");
        return targetPath;
    }

    /// <summary>
    /// 下载一份小体积文本资源（例如链接清单 JSON），返回 UTF-8 文本。
    /// 超过 <paramref name="maxBytes"/> 时立即停止，避免误把大文件当清单整份读进内存。
    /// </summary>
    public async Task<string> FetchTextAsync(string url, int maxBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new InvalidOperationException(T("Msg.FetchTextTooLarge", maxBytes / 1024));
        }

        var buffer = new byte[maxBytes + 1];
        var total = 0;
        await using (var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }
        }

        if (total > maxBytes)
        {
            throw new InvalidOperationException(T("Msg.FetchTextTooLarge", maxBytes / 1024));
        }

        var text = Encoding.UTF8.GetString(buffer, 0, total);
        // 有些编辑器会给 UTF-8 JSON 带 BOM，解析前去掉
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>校验 SHA256；<paramref name="quiet"/> 为真时不存在/为空则静默跳过。</summary>
    public async Task<bool> VerifyHashAsync(string filePath, string? expectedSha256, CancellationToken ct, bool quiet = false)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return true;
        }

        if (!File.Exists(filePath))
        {
            if (quiet)
            {
                return false;
            }

            throw new FileNotFoundException($"待校验文件不存在：{filePath}", filePath);
        }

        _log.Info($"校验 SHA256：{Path.GetFileName(filePath)}");
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        var expected = expectedSha256.Trim().Replace(" ", string.Empty).ToLowerInvariant();

        if (actual != expected)
        {
            throw new InvalidDataException($"SHA256 校验失败：{Path.GetFileName(filePath)}{Environment.NewLine}期望 {expected}{Environment.NewLine}实际 {actual}");
        }

        _log.Info("SHA256 校验通过。");
        return true;
    }

    /// <summary>目标目录里的副本是否与源文件同大小且校验通过（可安全复用，无需重新复制）。</summary>
    private static async Task<bool> IsUpToDateAsync(string sourcePath, string destPath, string? expectedSha256, CancellationToken ct)
    {
        if (!File.Exists(destPath) || new FileInfo(destPath).Length != new FileInfo(sourcePath).Length)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return true;
        }

        await using var stream = File.OpenRead(destPath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash);
        var expected = expectedSha256.Trim().Replace(" ", string.Empty);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task PumpAsync(
        Stream source,
        Stream destination,
        long alreadyReceived,
        long? total,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        var received = alreadyReceived;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var windowStart = sw.Elapsed;
        var windowBytes = 0L;
        var speed = 0d;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            windowBytes += read;

            var elapsed = sw.Elapsed - windowStart;
            if (elapsed.TotalMilliseconds >= 500)
            {
                speed = windowBytes / elapsed.TotalSeconds;
                windowBytes = 0;
                windowStart = sw.Elapsed;
            }

            progress?.Report(new DownloadProgress(received, total, speed));
        }

        progress?.Report(new DownloadProgress(received, total ?? received, speed));
    }

    private static async Task CopyWithProgressAsync(string from, string to, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var sourceInfo = new FileInfo(from);
        var total = sourceInfo.Length;
        long copied = 0;
        var buffer = new byte[1 << 20];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var src = File.OpenRead(from);
        await using var dst = File.Create(to);
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            var speed = sw.Elapsed.TotalSeconds > 0.1 ? copied / sw.Elapsed.TotalSeconds : 0;
            progress?.Report(new DownloadProgress(copied, total, speed));
        }
    }

    private static string GetFileNameFromUrl(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var name = Path.GetFileName(uri.LocalPath);
        name = Uri.UnescapeDataString(name);

        // 还原后的名字可能含 %2F、.. 等会被当作路径分隔的内容，一律过滤掉非法字符
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download.bin";
        }

        return name;
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

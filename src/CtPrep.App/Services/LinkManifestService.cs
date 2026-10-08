using System.Text.Json;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>
/// 「链接清单」解析：先请求一个 .json（通常部署在 Cloudflare Pages 上），
/// 再从中取镜像 / PE 的真实下载链接。换托管、换链接时只需更新清单，不用重新发布程序。
/// 同一次运行内按 URL 缓存，避免镜像与 PE 指向同一份清单时重复请求。
/// </summary>
public sealed class LinkManifestService
{
    /// <summary>清单大小上限：正常清单只有几 KB，超过说明取回的不是清单。</summary>
    private const int MaxManifestBytes = 512 * 1024;

    private static string T(string key, params object?[] args) => LocalizationService.Current.T(key, args);

    private readonly DownloadService _download;
    private readonly ILogSink _log;
    private readonly Dictionary<string, LinkManifest> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LinkManifestService(DownloadService download, ILogSink log)
    {
        _download = download;
        _log = log;
    }

    /// <summary>
    /// 判断地址是否指向「链接清单」：http(s) 且路径以 .json 结尾（忽略查询串与大小写）。
    /// 镜像本身只会是 .iso / .wim / .esd，用后缀区分不会误判。
    /// </summary>
    public static bool IsManifestUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
               uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取回并解析清单（同一次运行内按 URL 缓存）。</summary>
    public async Task<LinkManifest> LoadAsync(string url, CancellationToken ct)
    {
        if (_cache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        _log.Info(T("Msg.ManifestFetching", url));
        var json = await _download.FetchTextAsync(url, MaxManifestBytes, ct).ConfigureAwait(false);
        var manifest = Parse(json, url);
        _cache[url] = manifest;
        return manifest;
    }

    /// <summary>按本机版本解析镜像链接。</summary>
    public async Task<ManifestEntry> ResolveImageAsync(string manifestUrl, SystemInfo system, CancellationToken ct)
    {
        var manifest = await LoadAsync(manifestUrl, ct).ConfigureAwait(false);
        var entry = MatchImage(manifest, system)
            ?? throw new InvalidOperationException(T("Msg.ManifestNoImage", system.VersionKey));
        LogResolved("Msg.ManifestImageResolved", entry);
        return entry;
    }

    /// <summary>解析 PE 链接（不含版本匹配）。</summary>
    public async Task<ManifestEntry> ResolvePeAsync(string manifestUrl, CancellationToken ct)
    {
        var manifest = await LoadAsync(manifestUrl, ct).ConfigureAwait(false);
        var entry = manifest.Pe ?? throw new InvalidOperationException(T("Msg.ManifestNoPe"));
        LogResolved("Msg.ManifestPeResolved", entry);
        return entry;
    }

    /// <summary>
    /// 列出清单里可选的镜像版本（排除 "Default" 回退项），供新手模式「安装其他系统」展示。
    /// 同一次运行内按 URL 缓存，只发一次请求。
    /// </summary>
    public async Task<IReadOnlyList<string>> ListImageKeysAsync(string manifestUrl, CancellationToken ct)
    {
        var manifest = await LoadAsync(manifestUrl, ct).ConfigureAwait(false);
        var keys = manifest.Images.Keys
            .Where(k => !k.Equals("Default", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k)
            .ToList();
        return keys;
    }

    /// <summary>按给定版本键直接取镜像条目（不按本机版本匹配），用于新手模式选装其它系统。</summary>
    public async Task<ManifestEntry> ResolveImageByKeyAsync(string manifestUrl, string key, CancellationToken ct)
    {
        var manifest = await LoadAsync(manifestUrl, ct).ConfigureAwait(false);
        if (!manifest.Images.TryGetValue(key, out var entry) || string.IsNullOrWhiteSpace(entry.Url))
        {
            throw new InvalidOperationException(T("Msg.ManifestNoImage", key));
        }

        LogResolved("Msg.ManifestImageResolved", entry);
        return entry;
    }

    private void LogResolved(string key, ManifestEntry entry)
    {
        _log.Info(T(key, entry.Url));
        if (string.IsNullOrWhiteSpace(entry.Sha256))
        {
            _log.Warn(T("Msg.ManifestNoSha"));
        }
    }

    /// <summary>
    /// 按「精确 VersionKey → 家族前缀 → Default」挑选镜像条目，与 config.ini 的匹配规则一致。
    /// 找不到时返回 null。
    /// </summary>
    public static ManifestEntry? MatchImage(LinkManifest manifest, SystemInfo system)
    {
        if (manifest.Images.TryGetValue(system.VersionKey, out var exact) && !string.IsNullOrWhiteSpace(exact.Url))
        {
            return exact;
        }

        // 退化匹配：忽略版本号，只用家族名（例如 "Windows 11"）
        var familyHit = manifest.Images.FirstOrDefault(pair =>
            !pair.Key.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pair.Value.Url) &&
            pair.Key.StartsWith(system.FamilyName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(familyHit.Key))
        {
            return familyHit.Value;
        }

        if (manifest.Images.TryGetValue("Default", out var fallback) && !string.IsNullOrWhiteSpace(fallback.Url))
        {
            return fallback;
        }

        return null;
    }

    /// <summary>解析清单文本；格式不合法时抛出带原因的异常。</summary>
    public static LinkManifest Parse(string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(T("Msg.ManifestInvalid", source, ex.Message));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(T("Msg.ManifestInvalid", source, "根节点不是对象"));
            }

            var manifest = new LinkManifest();

            if (root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number)
            {
                manifest.Schema = schema.GetInt32();
            }

            if (root.TryGetProperty("updated", out var updated) && updated.ValueKind == JsonValueKind.String)
            {
                manifest.Updated = updated.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in images.EnumerateObject())
                {
                    var entry = ReadEntry(property.Value);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Url))
                    {
                        manifest.Images[property.Name.Trim()] = entry;
                    }
                }
            }

            if (root.TryGetProperty("pe", out var pe) && pe.ValueKind == JsonValueKind.Object)
            {
                var entry = ReadEntry(pe);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Url))
                {
                    manifest.Pe = entry;
                }
            }

            if (manifest.Images.Count == 0 && manifest.Pe is null)
            {
                throw new InvalidOperationException(T("Msg.ManifestInvalid", source, "没有找到任何可用的 images / pe 条目"));
            }

            return manifest;
        }
    }

    private static ManifestEntry? ReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ManifestEntry
        {
            Url = element.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()?.Trim() ?? string.Empty
                : string.Empty,
            Sha256 = element.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String
                ? sha.GetString()?.Trim() ?? string.Empty
                : string.Empty,
        };
    }
}

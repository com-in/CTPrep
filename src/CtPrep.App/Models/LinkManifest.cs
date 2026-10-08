namespace CtPrep.App.Models;

/// <summary>链接清单里的一条链接（镜像或 PE）。</summary>
public sealed class ManifestEntry
{
    public string Url { get; set; } = string.Empty;

    /// <summary>SHA256 校验值；留空表示不校验。</summary>
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>
/// 远程「链接清单」：例如部署在 Cloudflare Pages 上的 images.json。
/// 程序在下载前先取回它，再按本机版本挑出真正的镜像/PE 链接。
/// 换托管、换链接时只需更新清单，不用重新发布程序。
/// </summary>
public sealed class LinkManifest
{
    /// <summary>清单格式版本（当前为 1）。</summary>
    public int Schema { get; set; } = 1;

    /// <summary>清单更新时间（仅作展示/排查，可留空）。</summary>
    public string Updated { get; set; } = string.Empty;

    /// <summary>
    /// 镜像条目：键为版本匹配键（如 "Windows 11 24H2"），"Default" 为未命中时的回退项。
    /// 匹配规则与 config.ini 的 [Image] 段一致：精确命中 → 家族前缀 → Default。
    /// </summary>
    public Dictionary<string, ManifestEntry> Images { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PE 链接（可选）。</summary>
    public ManifestEntry? Pe { get; set; }
}

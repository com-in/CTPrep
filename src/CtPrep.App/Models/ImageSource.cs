namespace CtPrep.App.Models;

/// <summary>一个可下载的系统镜像条目。</summary>
public sealed class ImageSource
{
    /// <summary>配置键，例如 "Windows 11 24H2"。</summary>
    public string Key { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>是否为 Default 回退项。</summary>
    public bool IsDefault { get; set; }

    public override string ToString() => string.IsNullOrEmpty(Url) ? $"{Key}（未配置）" : Key;
}

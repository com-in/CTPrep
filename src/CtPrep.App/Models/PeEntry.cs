namespace CtPrep.App.Models;

/// <summary>一个可下载的 PE 条目。</summary>
public sealed class PeEntry
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;

    public override string ToString() => string.IsNullOrEmpty(Url) ? $"{Name}（未配置）" : Name;
}

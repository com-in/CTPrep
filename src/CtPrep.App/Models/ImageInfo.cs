namespace CtPrep.App.Models;

/// <summary>WIM/ESD 中的一个映像索引。</summary>
public sealed class ImageInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    public string SizeText => SizeBytes <= 0
        ? string.Empty
        : $"{SizeBytes / 1024d / 1024d / 1024d:0.00} GB";

    /// <summary>
    /// 映像名推断出的 Windows 主版本：7 / 8 / 10 / 11；认不出来为 0。
    /// 应答文件要据此裁剪版本专属节点（例如 HideOnlineAccountScreens 只有 Win10 及以上才有）。
    /// </summary>
    public int MajorVersion
    {
        get
        {
            var name = Name ?? string.Empty;
            if (name.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) return 11;
            if (name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)) return 10;
            if (name.Contains("Windows 8.1", StringComparison.OrdinalIgnoreCase)) return 8;
            if (name.Contains("Windows 8", StringComparison.OrdinalIgnoreCase)) return 8;
            if (name.Contains("Windows 7", StringComparison.OrdinalIgnoreCase)) return 7;
            return 0;
        }
    }

    public string Display => string.IsNullOrEmpty(SizeText)
        ? $"[{Index}] {Name}"
        : $"[{Index}] {Name}  ({SizeText})";

    public override string ToString() => Display;
}

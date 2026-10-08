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
    /// 这只是兜底：编排层优先用 dism 报告的真实版本号（对改版/精简映像更可靠）。
    /// 应答文件要据此裁剪版本专属节点（写错会让安装程序拒绝整份文件）。
    /// </summary>
    public int MajorVersion
    {
        get
        {
            // 去掉空格并统一大小写，"Windows7"/"Windows 7"/"Win7" 这类写法都能命中
            var name = (Name ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
            if (name.Contains("WINDOWS11") || name.Contains("WIN11")) return 11;
            if (name.Contains("WINDOWS10") || name.Contains("WIN10")) return 10;
            if (name.Contains("WINDOWS8.1") || name.Contains("WIN8.1")) return 8;
            if (name.Contains("WINDOWS8") || name.Contains("WIN8")) return 8;
            if (name.Contains("WINDOWS7") || name.Contains("WIN7")) return 7;
            return 0;
        }
    }

    public string Display => string.IsNullOrEmpty(SizeText)
        ? $"[{Index}] {Name}"
        : $"[{Index}] {Name}  ({SizeText})";

    public override string ToString() => Display;
}

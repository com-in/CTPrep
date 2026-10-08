namespace CtPrep.App.Models;

/// <summary>一个分区（来自 Storage 模块的探测结果）。</summary>
public sealed class PartitionInfo
{
    public int DiskNumber { get; set; }
    public int PartitionNumber { get; set; }

    /// <summary>PE 中为它固定分配的盘符（例如目标分区 W、EFI 分区 S）。</summary>
    public string AssignLetter { get; set; } = string.Empty;

    public string DriveLetter { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Type { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public bool IsBoot { get; set; }
    public bool IsEsp { get; set; }

    public string SizeText => $"{SizeBytes / 1024d / 1024d / 1024d:0.0} GB";

    public override string ToString()
    {
        var letter = string.IsNullOrEmpty(DriveLetter) ? "无盘符" : $"{DriveLetter}:";
        return $"磁盘 {DiskNumber} 分区 {PartitionNumber}  {letter}  {SizeText}  {Type}";
    }
}

namespace CtPrep.App.Models;

/// <summary>磁盘信息。</summary>
public sealed class DiskInfo
{
    public int DiskNumber { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string PartitionStyle { get; set; } = string.Empty;
    public bool IsSystem { get; set; }

    public string SizeText => $"{SizeBytes / 1024d / 1024d / 1024d:0.0} GB";

    public List<PartitionInfo> Partitions { get; } = new();

    public override string ToString() =>
        $"磁盘 {DiskNumber}  {FriendlyName}  {SizeText}  {PartitionStyle}{(IsSystem ? "  [系统盘]" : string.Empty)}";
}

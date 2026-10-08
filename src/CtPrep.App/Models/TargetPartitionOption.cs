namespace CtPrep.App.Models;

/// <summary>
/// 高级设置「安装位置」下拉框的一项。
/// <see cref="PartitionNumber"/> 为 0 表示自动（当前系统分区）。
/// </summary>
public sealed class TargetPartitionOption
{
    public TargetPartitionOption(int diskNumber, int partitionNumber, string display)
    {
        DiskNumber = diskNumber;
        PartitionNumber = partitionNumber;
        Display = display;
    }

    public int DiskNumber { get; }

    /// <summary>分区号；0 表示「自动」。</summary>
    public int PartitionNumber { get; }

    public string Display { get; }

    /// <summary>是否为「自动」项。</summary>
    public bool IsAuto => PartitionNumber == 0;

    public override string ToString() => Display;
}

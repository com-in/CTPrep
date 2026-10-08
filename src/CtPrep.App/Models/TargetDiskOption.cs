namespace CtPrep.App.Models;

/// <summary>高级设置「目标磁盘」下拉框的一项；<see cref="DiskNumber"/> 为 -1 表示自动（当前系统盘）。</summary>
public sealed class TargetDiskOption
{
    public TargetDiskOption(int diskNumber, string display)
    {
        DiskNumber = diskNumber;
        Display = display;
    }

    public int DiskNumber { get; }

    public string Display { get; }

    public override string ToString() => Display;
}

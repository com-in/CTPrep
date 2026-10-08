namespace CtPrep.App.Models;

/// <summary>固件类型。</summary>
public enum FirmwareType
{
    Unknown = 0,
    Bios = 1,
    Uefi = 2,
}

/// <summary>安装方式。</summary>
public enum InstallMode
{
    /// <summary>全新安装：格式化目标分区。</summary>
    Clean = 0,

    /// <summary>保留文件：清空 Windows / ProgramData 等系统目录，保留用户数据。</summary>
    KeepFiles = 1,
}

/// <summary>分区处理方案。</summary>
public enum PartitionScheme
{
    /// <summary>只处理目标分区，保留同一磁盘上的其它分区（默认）。</summary>
    KeepExisting = 0,

    /// <summary>整盘重建为 GPT（UEFI）。</summary>
    WipeDiskGpt = 1,

    /// <summary>整盘重建为 MBR（BIOS）。</summary>
    WipeDiskMbr = 2,
}

/// <summary>运行模式。</summary>
public enum RunMode
{
    Novice = 0,
    Expert = 1,
}

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>部署管线阶段，供 UI 显示进度。</summary>
public enum DeployStage
{
    Idle = 0,
    DetectSystem,
    ResolveSource,
    Download,
    ExtractImage,
    ChooseImageIndex,
    PrepareStaging,
    BuildPePayload,
    RegisterBootEntry,
    RunDeployment,
    Done,
    Failed,
}

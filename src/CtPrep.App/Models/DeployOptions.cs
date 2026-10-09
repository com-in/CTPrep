namespace CtPrep.App.Models;

/// <summary>一次重装任务的完整参数（新手模式由程序自动填，高级设置由用户填）。</summary>
public sealed class DeployOptions
{
    // ---------- 来源 ----------
    /// <summary>PE 下载地址或本地路径。</summary>
    public string PeSource { get; set; } = string.Empty;

    /// <summary>PE 的 SHA256（可空）。</summary>
    public string PeSha256 { get; set; } = string.Empty;

    /// <summary>系统镜像下载地址或本地路径。</summary>
    public string ImageSource { get; set; } = string.Empty;

    /// <summary>镜像的 SHA256（可空）。</summary>
    public string ImageSha256 { get; set; } = string.Empty;

    /// <summary>
    /// 选装其它系统时，用来在确认框里显示的友好版本名（例如 "Windows 10"）。
    /// 留空时确认框直接显示镜像地址；设置后显示这个名字而不是一长串下载 URL。
    /// </summary>
    public string? UserImageLabel { get; set; }

    /// <summary>
    /// 是否导出当前系统已装的第三方驱动，随新系统一起安装。
    /// 重装后网卡驱动丢失是最常见的「装完不能用」，所以默认开。
    /// </summary>
    /// <summary>
    /// 载荷写进暂存分区后，是否删掉先前下载/复制进运行时目录的那几 GB。
    /// 只删位于下载目录内的文件；用户自己的本地镜像不在其中，不会被删。
    /// </summary>
    public bool CleanupDownloads { get; set; } = true;

    public bool ExportCurrentDrivers { get; set; } = true;

    /// <summary>要安装的映像索引（1 起）；0 表示未指定，由程序按规则自动挑选。</summary>
    public int ImageIndex { get; set; }

    /// <summary>
    /// 系统镜像是否为「用户自己挑选的本地文件」。
    /// true 时不自动猜版本：单版本直接用，多版本弹窗让用户选；
    /// false（联网下载的官方镜像）才按本机版本自动匹配、缺失时回退专业版。
    /// </summary>
    public bool UserSuppliedImage { get; set; }

    // ---------- 目标 ----------
    /// <summary>
    /// 目标磁盘号：-1 表示自动（系统盘），≥0 表示用户在高级设置里明确指定的磁盘。
    /// 指定非系统盘时 FillTargetAsync 会把分区方案强制为整盘重建。
    /// </summary>
    public int TargetDiskNumber { get; set; } = -1;

    public int TargetPartitionNumber { get; set; }

    /// <summary>用户指定的目标磁盘描述（下拉框显示文本），仅用于确认窗口与日志。</summary>
    public string TargetDiskLabel { get; set; } = string.Empty;

    /// <summary>EFI 系统分区号（UEFI 才有；0 表示无）。</summary>
    public int EspPartitionNumber { get; set; }

    /// <summary>
    /// 当前系统所在磁盘号（-1 表示未知）。与 <see cref="TargetDiskNumber"/> 不同即为「跨盘安装」：
    /// 目标磁盘整盘重建，且 PE 里需要额外清理系统盘旧引导库中的本次 PE 引导项。
    /// </summary>
    public int SourceDiskNumber { get; set; } = -1;

    /// <summary>系统盘上的 EFI 系统分区号（UEFI 才有；0 表示无）。跨盘时用于清理旧引导库。</summary>
    public int SourceEspPartitionNumber { get; set; }

    /// <summary>系统盘上的 BIOS 系统保留分区号（0 表示引导库直接位于系统分区上）。跨盘时用于清理旧引导库。</summary>
    public int SourceSystemReservedPartitionNumber { get; set; }

    /// <summary>目标磁盘当前的分区表类型（GPT / MBR / RAW），用于判断是否需要转换。</summary>
    public string TargetDiskStyle { get; set; } = string.Empty;

    /// <summary>目标磁盘上所有分区的分区号。整盘重建时用来逐个删除并保住暂存分区。</summary>
    public List<int> TargetDiskPartitionNumbers { get; } = new();

    /// <summary>BIOS 下的系统保留分区号（0 表示没有，直接用目标分区）。</summary>
    public int SystemReservedPartitionNumber { get; set; }

    public FirmwareType Firmware { get; set; } = FirmwareType.Uefi;

    public InstallMode InstallMode { get; set; } = InstallMode.Clean;

    public PartitionScheme PartitionScheme { get; set; } = PartitionScheme.KeepExisting;

    /// <summary>
    /// 是否格式化引导分区（UEFI 的 EFI 系统分区 / BIOS 的系统保留分区）。
    /// 格式化会清除旧系统的引导项；关闭则保留原内容（安装中途失败时旧系统仍可引导）。
    /// 整个磁盘重建时无意义：新分区必然是新格式化的。
    /// </summary>
    public bool FormatBootPartition { get; set; } = true;

    /// <summary>
    /// 目标映像的 Windows 主版本（7 / 8 / 10 / 11；未知为 0）。
    /// 编排层优先从 dism 读取的真实版本号解析，失败时回退到映像名推断；
    /// 应答文件据此裁剪版本专属节点（写错会让安装程序拒绝整份文件）。
    /// </summary>
    public int WindowsMajorVersion { get; set; }

    /// <summary>
    /// 目标映像的处理器架构（dism 报告的 "x64" / "x86" / "arm64"；空 = 未识别，按 amd64 处理）。
    /// 应答文件的 processorArchitecture 必须与映像一致：x86 映像配 amd64 组件会让安装失败。
    /// </summary>
    public string ImageArchitecture { get; set; } = string.Empty;

    // ---------- 无人值守 ----------
    public bool Unattended { get; set; } = true;
    public string UserName { get; set; } = "CTUser";
    public string Password { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "China Standard Time";

    /// <summary>跳过 Win11 OOBE 强制联网。</summary>
    public bool BypassNetworkRequirement { get; set; } = true;

    /// <summary>
    /// 禁用设备加密：写入 PreventDeviceEncryption 策略，让新装的 Windows 11 不再自动加密整盘。
    /// </summary>
    public bool DisableDeviceEncryption { get; set; } = true;

    /// <summary>首次进入系统前禁用 Windows Defender（策略注册表 + 停用相关服务）。</summary>
    public bool DisableDefender { get; set; }

    // ---------- 驱动 ----------
    /// <summary>驱动包来源（URL 或本地路径），会在首次启动时安装。</summary>
    public List<string> DriverSources { get; } = new();

    // ---------- 暂存 / 行为 ----------
    public int StagingSizeMB { get; set; } = 12288;
    public string StagingLabel { get; set; } = "CTPREP";
    public string TargetLabel { get; set; } = "Windows";

    /// <summary>演练模式：只打印命令，不做真实磁盘改动。</summary>
    public bool DryRun { get; set; }

    /// <summary>准备完成后自动重启进入 PE。</summary>
    public bool RebootAfterPrepare { get; set; } = true;

    /// <summary>部署完成后关机而不是重启。</summary>
    public bool ShutdownAfterDeploy { get; set; }

    // ---------- 运行期生成 ----------
    /// <summary>注册到 BCD 的 PE 引导项 GUID，部署时用于删除。</summary>
    public string PeBootEntryGuid { get; set; } = string.Empty;
    public string PeDeviceOptionsGuid { get; set; } = string.Empty;

    /// <summary>暂存分区在 Windows 下的盘符（准备阶段用）。</summary>
    public string StagingDriveLetter { get; set; } = string.Empty;

    /// <summary>暂存分区是否为本次新建（新建则在部署完成后删除并合并空间）。</summary>
    public bool CleanupStagingPartition { get; set; }

    /// <summary>暂存分区所在磁盘号（-1 表示未知）。</summary>
    public int StagingDiskNumber { get; set; } = -1;

    /// <summary>暂存分区的分区号（-1 表示未知）。</summary>
    public int StagingPartitionNumber { get; set; } = -1;

    /// <summary>
    /// 新建暂存分区时被压缩的那个分区所在磁盘（-1 表示未知）。
    /// 暂存分区与目标分区不在同一磁盘时，部署完成后把空间还给这个分区，而不是目标卷。
    /// </summary>
    public int StagingHostDiskNumber { get; set; } = -1;

    /// <summary>新建暂存分区时被压缩的那个分区号（-1 表示未知）。</summary>
    public int StagingHostPartitionNumber { get; set; } = -1;
}

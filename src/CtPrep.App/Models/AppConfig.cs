namespace CtPrep.App.Models;

/// <summary>config.ini 解析结果。</summary>
public sealed class AppConfig
{
    /// <summary>运行时目录（绝对路径）。</summary>
    public string RuntimeDir { get; set; } = string.Empty;

    /// <summary>演练模式：只打印命令，不做真实磁盘改动。</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// 载荷写进暂存分区后，是否删掉先前下载/复制进运行时目录的那几 GB。
    /// 关掉可以保留下载缓存，下次重装不用重新下载。
    /// </summary>
    public bool CleanupDownloads { get; set; } = true;

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>
    /// 界面语言代码（zh-CN / en-US / auto）。
    /// 为空或 auto 时按系统 UI 语言判定；界面上改过语言后写入 runtime/ui-language.txt 覆盖本值。
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>PE 条目集合，键为配置名。</summary>
    public Dictionary<string, PeEntry> PeEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认 PE 的名称。</summary>
    public string DefaultPe { get; set; } = string.Empty;

    /// <summary>系统镜像条目集合，键为版本名。</summary>
    public Dictionary<string, ImageSource> ImageEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认系统镜像 URL（回退项）。</summary>
    public ImageSource? DefaultImage { get; set; }

    /// <summary>驱动包列表（原样保留配置值，可能是 URL 也可能是本地路径）。</summary>
    public List<string> DriverSources { get; } = new();

    /// <summary>
    /// 是否导出当前系统已装的第三方驱动，随新系统一起安装。
    /// 重装后网卡驱动丢失是最常见的「装完不能用」，所以默认开。
    /// </summary>
    public bool ExportCurrentDrivers { get; set; } = true;

    public int StagingSizeMB { get; set; } = 12288;
    public string StagingLabel { get; set; } = "CTPREP";
    public string TargetLabel { get; set; } = "Windows";
    public InstallMode DefaultInstallMode { get; set; } = InstallMode.Clean;
    public bool DefaultUnattended { get; set; } = true;

    /// <summary>
    /// 默认是否禁用设备加密（让新系统不自动整盘加密）。Windows 11 会在新机器上自动开启。
    /// </summary>
    public bool DisableDeviceEncryption { get; set; } = true;
    public string DefaultTimeZone { get; set; } = "China Standard Time";

    /// <summary>按系统信息匹配镜像；未命中返回 Default。</summary>
    public ImageSource? ResolveImage(SystemInfo info)
    {
        if (ImageEntries.TryGetValue(info.VersionKey, out var exact) && !string.IsNullOrWhiteSpace(exact.Url))
        {
            return exact;
        }

        // 退化匹配：忽略版本号，只用家族名（例如 "Windows 11"）
        var familyHit = ImageEntries.Values.FirstOrDefault(v =>
            !v.IsDefault &&
            !string.IsNullOrWhiteSpace(v.Url) &&
            v.Key.StartsWith(info.FamilyName, StringComparison.OrdinalIgnoreCase));
        if (familyHit is not null)
        {
            return familyHit;
        }

        return DefaultImage;
    }

    /// <summary>取默认 PE；找不到则取第一个已配置 URL 的条目。</summary>
    public PeEntry? ResolvePe()
    {
        if (!string.IsNullOrEmpty(DefaultPe) &&
            PeEntries.TryGetValue(DefaultPe, out var pe) &&
            !string.IsNullOrWhiteSpace(pe.Url))
        {
            return pe;
        }

        return PeEntries.Values.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Url));
    }
}

using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>把「下载 → 提取 → 准备暂存分区 → 注入 PE → 注册引导」整条管线串起来。</summary>
public sealed class DeployOrchestrator
{
    /// <summary>取本地化文案（部署阶段提示与用户可见的异常信息）。</summary>
    private static string T(string key, params object?[] args) => LocalizationService.Current.T(key, args);

    /// <summary>准备完成后自动重启进入 PE 的倒计时秒数；界面上的倒计时显示必须与它一致。</summary>
    public const int RebootDelaySeconds = 10;

    private readonly AppLogger _log;
    private readonly ProcessRunner _runner;
    private readonly SystemInfoService _systemInfoService;
    private readonly StorageService _storage;
    private readonly DownloadService _download;
    private readonly ImageService _images;
    private readonly DismService _dism;
    private readonly BootService _boot;
    private readonly PePayloadService _payload;
    private readonly Func<IReadOnlyList<ImageInfo>, int, int?>? _editionSelector;

    /// <param name="editionSelector">
    /// 多版本映像的选择回调：传入映像列表与建议预选的索引，返回用户选中的索引；取消返回 null。
    /// 由界面层注入（服务层不直接弹窗）。
    /// </param>
    public DeployOrchestrator(
        AppLogger log,
        ProcessRunner runner,
        SystemInfoService systemInfoService,
        StorageService storage,
        DownloadService download,
        ImageService images,
        DismService dism,
        BootService boot,
        PePayloadService payload,
        Func<IReadOnlyList<ImageInfo>, int, int?>? editionSelector = null)
    {
        _log = log;
        _runner = runner;
        _systemInfoService = systemInfoService;
        _storage = storage;
        _download = download;
        _images = images;
        _dism = dism;
        _boot = boot;
        _payload = payload;
        _editionSelector = editionSelector;
    }

    /// <summary>执行完整准备流程。返回 PE 引导项（若已注册）。</summary>
    public async Task<PeBootEntry?> RunAsync(
        DeployOptions options,
        SystemInfo system,
        AppConfig config,
        IProgress<DeployProgress> progress,
        CancellationToken ct = default)
    {
        var runtime = config.RuntimeDir;
        var downloadRoot = Path.Combine(runtime, "downloads");
        var workRoot = Path.Combine(runtime, "work");
        Directory.CreateDirectory(downloadRoot);
        Directory.CreateDirectory(workRoot);

        // 部署前校验：UEFI 机器若没有 EFI 系统分区（MBR/CSM 旧式引导），PE 里 bcdboot /s S: 必然失败
        if (options.Firmware == FirmwareType.Uefi &&
            options.PartitionScheme != PartitionScheme.WipeDiskGpt &&
            options.EspPartitionNumber <= 0)
        {
            throw new InvalidOperationException(T("Msg.NoEsp"));
        }

        // ---------- 1. 下载 ----------
        Report(progress, DeployStage.Download, 0, T("Progress.DownloadPe"));
        var downloadProgress = new Progress<DownloadProgress>(p =>
            Report(progress, DeployStage.Download, (int)((p.Percent ?? 0) * 50), T("Progress.DownloadPeItem", p.Display)));

        var peFile = await _download
            .AcquireAsync(options.PeSource, Path.Combine(downloadRoot, "pe"), options.PeSha256, downloadProgress, ct)
            .ConfigureAwait(false);

        Report(progress, DeployStage.Download, 50, T("Progress.DownloadImage"));
        var imageDownloadProgress = new Progress<DownloadProgress>(p =>
            Report(progress, DeployStage.Download, 50 + (int)((p.Percent ?? 0) * 50), T("Progress.DownloadImageItem", p.Display)));

        var imageFile = await _download
            .AcquireAsync(options.ImageSource, Path.Combine(downloadRoot, "image"), options.ImageSha256, imageDownloadProgress, ct)
            .ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        // ---------- 2. 提取映像 ----------
        Report(progress, DeployStage.ExtractImage, 60, T("Progress.ParsePe"));
        var bootWim = await _images.ExtractPeWimAsync(peFile, Path.Combine(workRoot, "pe"), ct).ConfigureAwait(false);

        Report(progress, DeployStage.ExtractImage, 70, T("Progress.ParseImage"));
        var installImage = await _images.ExtractInstallImageAsync(imageFile, Path.Combine(workRoot, "image"), ct).ConfigureAwait(false);

        // 下载的 ISO 已无用途，及时删除释放空间
        if (!string.Equals(imageFile, installImage, StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(imageFile).Equals(".iso", StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(imageFile);
        }

        // ---------- 3. 选定映像索引 ----------
        Report(progress, DeployStage.ChooseImageIndex, 75, T("Progress.ReadEditions"));
        var imageInfos = await _dism.GetWimInfoAsync(installImage, ct).ConfigureAwait(false);
        options.ImageIndex = ResolveImageIndex(options, imageInfos, system);

        var selected = imageInfos.FirstOrDefault(i => i.Index == options.ImageIndex);

        // 应答文件必须按目标映像的「真实」版本与架构渲染：
        //  - 版本决定 OOBE 里哪些节点能写（写进目标系统不认识的节点会让安装程序拒绝整份文件）；
        //  - processorArchitecture 必须与映像一致（x86 映像配 amd64 组件同样会让安装失败）。
        // 版本号优先读 dism 详情（对改版/精简映像也可靠），失败时回退到映像名推断。
        var imageDetail = await _dism.GetWimInfoDetailAsync(installImage, options.ImageIndex, ct).ConfigureAwait(false);
        options.WindowsMajorVersion = imageDetail.MajorVersion != 0
            ? imageDetail.MajorVersion
            : selected?.MajorVersion ?? 0;
        options.ImageArchitecture = imageDetail.Architecture;

        _log.Info($"将安装：{selected?.Name ?? $"索引 {options.ImageIndex}"}（Windows 主版本 {(options.WindowsMajorVersion == 0 ? "未知" : options.WindowsMajorVersion.ToString())}，架构 {(string.IsNullOrEmpty(options.ImageArchitecture) ? "未知，按 amd64 处理" : options.ImageArchitecture)}）");

        // ---------- 4. 准备驱动包 ----------
        var driverDirs = new List<string>();
        if (options.DriverSources.Count > 0)
        {
            Report(progress, DeployStage.PrepareStaging, 78, T("Progress.PrepareDrivers"));
            driverDirs = await _images
                .StageDriversAsync(
                    options.DriverSources,
                    runtime,
                    Path.Combine(workRoot, "drivers"),
                    new Progress<DownloadProgress>(),
                    _download,
                    ct)
                .ConfigureAwait(false);
        }

        // ---------- 5. 准备暂存分区 ----------
        // 配置的暂存大小只是下限：实际空间必须同时容纳 PE、系统映像和已准备的驱动包。
        // StorageService 会额外预留 2 GB，以避免复制和生成脚本时因空间临界而失败。
        var payloadBytes = checked(
            new FileInfo(installImage).Length +
            new FileInfo(bootWim).Length +
            GetDirectorySize(driverDirs));
        var configuredStagingBytes = Math.Max(0L, (long)options.StagingSizeMB * 1024 * 1024);
        var stagingPayloadBytes = Math.Max(configuredStagingBytes, payloadBytes);
        var requiredBytes = checked(stagingPayloadBytes + (2L * 1024 * 1024 * 1024));

        if (options.DryRun)
        {
            _log.Warn(T("Msg.DryRunNotice"));
            LogDryRunPlan(options, requiredBytes);
            Report(progress, DeployStage.Done, 100, T("Progress.DryRunDone"), false);
            return null;
        }

        Report(progress, DeployStage.PrepareStaging, 82, T("Progress.PrepareStaging"));
        var stagingDir = await _storage
            .PrepareStagingVolumeAsync(stagingPayloadBytes, options.StagingLabel, options.TargetDiskNumber, ct)
            .ConfigureAwait(false);
        options.StagingDriveLetter = stagingDir.Length >= 2 ? stagingDir[..2].TrimEnd('\\') : "Z:";
        options.CleanupStagingPartition = _storage.StagingWasCreated;
        options.StagingDiskNumber = _storage.StagingDiskNumber;
        options.StagingPartitionNumber = _storage.StagingPartitionNumber;
        options.StagingHostDiskNumber = _storage.StagingHostDiskNumber;
        options.StagingHostPartitionNumber = _storage.StagingHostPartitionNumber;

        // 整盘重建时，若暂存分区恰好落在目标磁盘上（单盘机器压缩系统卷的结果），
        // 分区表转换会连它一起清掉，install.wim 也就没了。这种情况提前报错并给出改法。
        if (options.PartitionScheme != PartitionScheme.KeepExisting &&
            options.StagingDiskNumber == options.TargetDiskNumber)
        {
            var wantGpt = options.PartitionScheme == PartitionScheme.WipeDiskGpt;
            var style = options.TargetDiskStyle;
            var needConvert = wantGpt
                ? !style.Equals("GPT", StringComparison.OrdinalIgnoreCase)
                : !style.Equals("MBR", StringComparison.OrdinalIgnoreCase);
            if (needConvert)
            {
                throw new InvalidOperationException(
                    T("Msg.StagingOnTargetDisk", options.TargetDiskNumber, style, wantGpt ? "GPT" : "MBR"));
            }
        }

        // 暂存分区返回的路径已包含卷标目录（例如 E:\CTPREP），直接作为载荷根目录
        Directory.CreateDirectory(stagingDir);
        var stagingRoot = stagingDir;

        // ---------- 6. 构建 PE 载荷 ----------
        var stagingImagePath = Path.Combine(stagingRoot, Path.GetFileName(installImage));

        Report(progress, DeployStage.BuildPePayload, 88, T("Progress.CopyPayload"));
        await _payload
            .StagePayloadFilesAsync(options, stagingRoot, bootWim, installImage, driverDirs, ct)
            .ConfigureAwait(false);

        Report(progress, DeployStage.BuildPePayload, 91, T("Progress.InjectAutorun"));
        await _payload
            .InjectAutoRunAsync(Path.Combine(stagingRoot, "boot.wim"), ct)
            .ConfigureAwait(false);

        // ---------- 7. 注册引导 ----------
        Report(progress, DeployStage.RegisterBootEntry, 95, T("Progress.RegisterBoot"));
        // 暂存目录（含卷标子目录）必须原样传给引导项，ramdisk 才能在分区根目录之外找到 boot.wim
        var entry = await _boot
            .CreateRamdiskEntryAsync(stagingRoot, options.TargetLabel, options.Firmware, ct)
            .ConfigureAwait(false);
        options.PeBootEntryGuid = entry.Guid;
        // 只有本次创建的私有设备选项对象才需要后续清理；回退到 {ramdiskoptions} 时它是
        // WinRE 等引导项共用的公共配置，删掉会破坏系统恢复环境的 ramdisk 设置。
        options.PeDeviceOptionsGuid = entry.DeviceOptionsIsPrivate ? entry.DeviceOptionsId : string.Empty;
        var bootEntryGuid = entry.Guid;

        try
        {
            // 引导项 GUID 需要写进 PE 侧脚本，因此脚本在引导项创建之后再渲染
            Report(progress, DeployStage.RegisterBootEntry, 97, T("Progress.WriteScripts"));
            await _payload
                .WriteScriptsAsync(options, stagingRoot, stagingImagePath, ct)
                .ConfigureAwait(false);

            await _boot.ScheduleOnceAsync(entry.Guid, ct).ConfigureAwait(false);

            _log.Info(T("Progress.PrepareDone"));

            // 映像已经落到暂存分区，删除工作目录里的中间文件释放 C 盘空间
            TryDeleteUnder(workRoot, installImage, bootWim);

            if (options.RebootAfterPrepare)
            {
                Report(progress, DeployStage.RunDeployment, 99, T("Progress.RebootSoon"));
                await _runner
                    .RunCmdAsync($"shutdown /r /t {RebootDelaySeconds} /c \"CTPrep 即将重启并开始部署\" /d p:4:1", null, ct)
                    .ConfigureAwait(false);
            }

            Report(progress, DeployStage.Done, 100, T("Progress.PrepareDone"));
            return entry;
        }
        catch
        {
            // 引导项已注册但后续步骤失败：回滚删除本次引导项，避免 BCD 里残留失效项
            _log.Warn($"引导项注册后的后续步骤失败，回滚删除引导项 {bootEntryGuid}。");
            await _boot.DeleteEntryAsync(bootEntryGuid, CancellationToken.None).ConfigureAwait(false);
            if (entry.DeviceOptionsIsPrivate)
            {
                await _boot.DeleteEntryAsync(entry.DeviceOptionsId, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// 决定要安装哪个映像索引：
    /// <list type="bullet">
    /// <item>用户自己选的本地映像：单版本直接采用；多版本弹窗让用户选，不做自动猜测。</item>
    /// <item>联网获取的官方镜像：匹配与本机相同的版本；镜像里没有该版本时回退专业版。</item>
    /// </list>
    /// </summary>
    private int ResolveImageIndex(DeployOptions options, IReadOnlyList<ImageInfo> infos, SystemInfo system)
    {
        if (infos.Count == 0)
        {
            throw new InvalidOperationException(T("Msg.NoImageIndex"));
        }

        // ---------- 1. 用户自选的本地映像 ----------
        if (options.UserSuppliedImage)
        {
            if (infos.Count == 1)
            {
                var only = infos[0];
                _log.Info($"本地映像只包含 1 个版本，直接采用：{only.Display}");
                return only.Index;
            }

            _log.Info($"本地映像包含 {infos.Count} 个版本，等待用户选择...");
            var picked = _editionSelector?.Invoke(infos, options.ImageIndex);
            if (picked is null)
            {
                throw new OperationCanceledException(T("Msg.NoEditionSelected"));
            }

            var chosen = infos.FirstOrDefault(i => i.Index == picked.Value);
            if (chosen is null)
            {
                throw new InvalidOperationException(T("Msg.EditionIndexMissing", picked.Value));
            }

            _log.Info($"已选择版本：{chosen.Display}");
            return chosen.Index;
        }

        // ---------- 2. 联网获取的镜像 ----------
        // 界面里明确指定的索引优先（例如用户点过「分析镜像」后在下拉框里选了版本）
        if (options.ImageIndex > 0 && infos.Any(i => i.Index == options.ImageIndex))
        {
            _log.Info($"沿用界面指定的映像索引 {options.ImageIndex}。");
            return options.ImageIndex;
        }

        var wanted = ImageEditionResolver.FromSystem(system);
        var matched = ImageEditionResolver.FindByFamily(infos, wanted);
        if (matched is not null)
        {
            _log.Info($"按本机版本匹配：{system.EditionId}（{system.EditionName}）→ {matched.Display}");
            return matched.Index;
        }

        // 本机版本在目标镜像里不存在（Win7 旗舰版升 Win10、家庭版升只有专业版的镜像等）→ 回退专业版
        var pro = ImageEditionResolver.FindByFamily(infos, EditionFamily.Pro);
        if (pro is not null)
        {
            _log.Warn($"镜像里没有与本机 {system.EditionId} 对应的版本，回退安装{ImageEditionResolver.Describe(EditionFamily.Pro)}：{pro.Display}");
            return pro.Index;
        }

        _log.Warn($"镜像里既没有本机版本（{system.EditionId}）也没有专业版，使用第一个索引：{infos[0].Display}");
        return infos[0].Index;
    }

    /// <summary>
    /// 把探测到的目标磁盘/分区信息回填到选项。
    /// <list type="bullet">
    /// <item>未指定目标磁盘（-1）或指定的是系统盘：沿用「只处理系统盘上的目标分区」的既有逻辑。</item>
    /// <item>指定了非系统盘：强制整盘重建该磁盘（新 ESP / 系统保留分区随新布局创建），
    /// 系统盘只记录位置信息，供 PE 侧清理旧引导库里的 PE 引导项。</item>
    /// </list>
    /// </summary>
    public async Task FillTargetAsync(DeployOptions options, CancellationToken ct = default)
    {
        var systemDrive = _storage.GetSystemDriveLetter();
        var disks = await _storage.GetDisksAsync(ct).ConfigureAwait(false);
        options.Firmware = _systemInfoService.GetFirmware();

        var source = disks.FirstOrDefault(d =>
            d.Partitions.Any(p => p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)));
        if (source is null)
        {
            throw new InvalidOperationException($"未能在磁盘列表中找到系统分区 {systemDrive}:");
        }

        options.SourceDiskNumber = source.DiskNumber;
        if (options.Firmware == FirmwareType.Uefi)
        {
            options.SourceEspPartitionNumber = source.Partitions.FirstOrDefault(p => p.IsEsp)?.PartitionNumber ?? 0;
        }
        else
        {
            options.SourceSystemReservedPartitionNumber = source.Partitions.FirstOrDefault(p =>
                p.Type.Equals("Reserved", StringComparison.OrdinalIgnoreCase) ||
                (p.IsSystem && !p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)))?.PartitionNumber ?? 0;
        }

        // ---------- 用户在高级设置里明确选了安装分区 ----------
        if (options.TargetDiskNumber >= 0 && options.TargetPartitionNumber > 0)
        {
            var target = disks.FirstOrDefault(d => d.DiskNumber == options.TargetDiskNumber)
                ?? throw new InvalidOperationException(T("Msg.TargetDiskGone", options.TargetDiskNumber));
            var partition = target.Partitions.FirstOrDefault(p => p.PartitionNumber == options.TargetPartitionNumber)
                ?? throw new InvalidOperationException(
                    T("Msg.TargetPartitionGone", options.TargetDiskNumber, options.TargetPartitionNumber));

            options.TargetDiskStyle = target.PartitionStyle;
            options.TargetDiskPartitionNumbers.Clear();
            options.TargetDiskPartitionNumbers.AddRange(target.Partitions.Select(p => p.PartitionNumber));

            if (options.PartitionScheme != PartitionScheme.KeepExisting)
            {
                // 整盘重建：目标磁盘被清空重建，分区号由 diskpart 重新分配
                var scheme = options.Firmware == FirmwareType.Uefi
                    ? PartitionScheme.WipeDiskGpt
                    : PartitionScheme.WipeDiskMbr;

                // 2TB 以上的磁盘放不下 MBR 分区表，BIOS 机器只能换盘或改用 UEFI
                CheckMbrCapacity(target, scheme);

                options.PartitionScheme = scheme;
                options.TargetPartitionNumber = 0;
                options.EspPartitionNumber = 0;
                options.SystemReservedPartitionNumber = 0;

                _log.Info($"目标（整盘重建）：{target}");
                return;
            }

            // 保留磁盘上的其它分区，只把选定分区作为安装目标。
            // 选了引导分区本身（ESP / 系统保留）没有意义，这里直接挡掉。
            if (partition.IsEsp ||
                partition.Type.Equals("Reserved", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    T("Msg.TargetPartitionIsBoot", options.TargetDiskNumber, options.TargetPartitionNumber));
            }

            if (options.Firmware == FirmwareType.Uefi)
            {
                options.EspPartitionNumber = target.Partitions.FirstOrDefault(p => p.IsEsp)?.PartitionNumber ?? 0;
                if (options.EspPartitionNumber == 0)
                {
                    // 没有 ESP 就没法把引导写进去，bcdboot 必然失败
                    throw new InvalidOperationException(T("Msg.NoEspOnTargetDisk", target.DiskNumber));
                }
            }
            else
            {
                options.SystemReservedPartitionNumber = target.Partitions.FirstOrDefault(p =>
                    p.Type.Equals("Reserved", StringComparison.OrdinalIgnoreCase) ||
                    (p.IsSystem && p.PartitionNumber != options.TargetPartitionNumber))?.PartitionNumber ?? 0;
            }

            _log.Info($"目标：磁盘 {target.DiskNumber} 分区 {options.TargetPartitionNumber}（{partition.SizeText}）");
            _log.Info($"引导分区号 {(options.Firmware == FirmwareType.Uefi ? options.EspPartitionNumber : options.SystemReservedPartitionNumber)}，固件 {options.Firmware}");
            return;
        }

        // ---------- 目标就是系统盘：沿用原有逻辑 ----------
        foreach (var disk in disks)
        {
            if (disk.DiskNumber != source.DiskNumber)
            {
                continue;
            }

            var targetPartition = disk.Partitions.FirstOrDefault(p =>
                p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase));
            if (targetPartition is null)
            {
                continue;
            }

            // MBR 分区表最大只能承载 2TB，超出时 create partition primary 必然失败
            CheckMbrCapacity(disk, options.PartitionScheme);

            options.TargetDiskNumber = disk.DiskNumber;
            options.TargetPartitionNumber = targetPartition.PartitionNumber;
            options.TargetDiskStyle = disk.PartitionStyle;
            options.TargetDiskPartitionNumbers.Clear();
            options.TargetDiskPartitionNumbers.AddRange(disk.Partitions.Select(p => p.PartitionNumber));

            if (options.Firmware == FirmwareType.Uefi)
            {
                var esp = disk.Partitions.FirstOrDefault(p => p.IsEsp);
                options.EspPartitionNumber = esp?.PartitionNumber ?? 0;
            }
            else
            {
                var reserved = disk.Partitions.FirstOrDefault(p =>
                    p.Type.Equals("Reserved", StringComparison.OrdinalIgnoreCase) ||
                    (p.IsSystem && !p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)));
                options.SystemReservedPartitionNumber = reserved?.PartitionNumber ?? 0;
            }

            _log.Info($"目标：{disk}");
            _log.Info($"目标分区号 {options.TargetPartitionNumber}，ESP/系统分区号 {(options.Firmware == FirmwareType.Uefi ? options.EspPartitionNumber : options.SystemReservedPartitionNumber)}，固件 {options.Firmware}");
            return;
        }

        throw new InvalidOperationException($"未能在磁盘列表中找到系统分区 {systemDrive}:");
    }

    /// <summary>MBR 方案下校验磁盘容量（超过 2TB 时 create partition primary 必然失败）。</summary>
    private void CheckMbrCapacity(DiskInfo disk, PartitionScheme scheme)
    {
        if (scheme == PartitionScheme.WipeDiskMbr &&
            disk.SizeBytes > 2L * 1024L * 1024L * 1024L * 1024L)
        {
            throw new InvalidOperationException(
                T("Msg.DiskTooLargeForMbr", disk.FriendlyName,
                    (disk.SizeBytes / 1024d / 1024 / 1024 / 1024).ToString("0.##")));
        }
    }

    private void LogDryRunPlan(DeployOptions options, long requiredBytes)
    {
        var crossDisk = options.SourceDiskNumber >= 0 && options.SourceDiskNumber != options.TargetDiskNumber;

        _log.Info("---------------- 演练计划 ----------------");
        _log.Info($"PE 来源        : {options.PeSource}");
        _log.Info($"镜像来源       : {options.ImageSource}");
        _log.Info($"映像索引       : {options.ImageIndex}");
        _log.Info($"目标磁盘/分区  : 磁盘 {options.TargetDiskNumber} / 分区 {options.TargetPartitionNumber}");
        if (crossDisk)
        {
            _log.Info($"跨盘安装       : 目标磁盘整盘重建，系统盘（磁盘 {options.SourceDiskNumber}）保持不动");
        }

        _log.Info($"分区方案       : {options.PartitionScheme}");
        _log.Info($"格式化引导分区 : {(options.FormatBootPartition ? "是" : "否")}");
        _log.Info($"禁用 Defender  : {(options.DisableDefender ? "是" : "否")}");
        _log.Info($"固件           : {options.Firmware}");
        _log.Info($"安装方式       : {options.InstallMode}");
        _log.Info($"驱动包         : {(options.DriverSources.Count == 0 ? "无" : string.Join(", ", options.DriverSources))}");
        _log.Info($"暂存分区需求   : {requiredBytes / 1024 / 1024} MB（配置 {options.StagingSizeMB} MB）");
        _log.Info($"无人值守       : {(options.Unattended ? "开启" : "关闭")}");
        _log.Info("将要执行的关键命令：");
        _log.Info($"  diskpart /s <暂存>\\{options.StagingLabel}\\diskpart-target.txt");
        _log.Info($"  dism /Apply-Image /ImageFile:<暂存>\\install.wim /Index:{options.ImageIndex} /ApplyDir:W:\\");
        _log.Info("  bcdboot W:\\Windows /s S: /f UEFI /l zh-cn");
        _log.Info($"  bcdedit /set {{bootmgr}} default <PE 引导项>");
        _log.Info("  shutdown /r /t 10");
        _log.Info("------------------------------------------");
    }

    private static long GetDirectorySize(IEnumerable<string> directories)
    {
        var size = 0L;
        foreach (var directory in directories.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                size = checked(size + new FileInfo(file).Length);
            }
        }

        return size;
    }

    private static void Report(IProgress<DeployProgress> progress, DeployStage stage, int percent, string message, bool isError = false)
        => progress.Report(new DeployProgress(stage, percent, message, isError));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>仅删除位于指定根目录下的文件，避免误删用户下载缓存。</summary>
    private static void TryDeleteUnder(string root, params string[] paths)
    {
        // 带结尾分隔符做前缀匹配，防止 work2 这类目录被误判为 work 的子路径
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(full);
            }
        }
    }
}

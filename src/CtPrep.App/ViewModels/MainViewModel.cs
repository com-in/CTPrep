using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CtPrep.App.Models;
using CtPrep.App.Services;
using CtPrep.App.Views;

namespace CtPrep.App.ViewModels;

/// <summary>主界面逻辑：同时承载「新手模式」与「高级设置」。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly AppConfig _custom;
    private readonly AppLogger _log;
    private readonly ProcessRunner _runner;
    private readonly SystemInfoService _systemInfoService;
    private readonly StorageService _storage;
    private readonly DownloadService _download;
    private readonly LinkManifestService _manifests;
    private readonly DismService _dism;
    private readonly ImageService _images;
    private readonly DeployOrchestrator _orchestrator;

    private CancellationTokenSource? _cts;
    private readonly StringBuilder _logBuffer = new();
    private readonly System.Windows.Threading.DispatcherTimer _rebootTimer;
    private readonly System.Windows.Threading.DispatcherTimer _logTimer;
    private bool _logDirty;

    public MainViewModel(AppConfig config, AppConfig customConfig, AppLogger log)
    {
        _config = config;
        _custom = customConfig;
        _log = log;
        _runner = new ProcessRunner(log);
        _systemInfoService = new SystemInfoService(log);
        _storage = new StorageService(_runner, log);
        _download = new DownloadService(log);
        _manifests = new LinkManifestService(_download, log);
        _dism = new DismService(_runner, log);
        _images = new ImageService(_runner, log, _dism);
        var boot = new BootService(_runner, log);
        var payload = new PePayloadService(_runner, _dism, log);

        _orchestrator = new DeployOrchestrator(
            log, _runner, _systemInfoService, _storage, _download, _images, _dism, boot, payload,
            PickImageEdition);

        OneClickCommand = new RelayCommand(OneClickAsync, () => !IsBusy);
        ExpertDeployCommand = new RelayCommand(ExpertDeployAsync, () => !IsBusy);
        RefreshCommand = new RelayCommand(RefreshAsync, () => !IsBusy);
        // 「分析镜像」既能分析地址框里的镜像，也能分析本地文件框里选中的文件，
        // 所以两个来源任意一个非空就应该可用（只看地址框会让"浏览本地文件"这条路走不通）。
        AnalyzeImageCommand = new RelayCommand(AnalyzeImageAsync, () => !IsBusy && HasImageSource);
        BrowseImageCommand = new RelayCommand(BrowseImageAsync, () => !IsBusy);
        BrowseNoviceImageCommand = new RelayCommand(BrowseNoviceImageAsync, () => !IsBusy);
        ClearNoviceImageCommand = new RelayCommand(ClearNoviceImageAsync, () => !IsBusy);
        PickOtherImageCommand = new RelayCommand(PickOtherImageAsync, () => !IsBusy && !IsLoadingVersions);
        ResetVersionCommand = new RelayCommand(ResetVersionAsync, () => !IsBusy);
        CancelRebootCommand = new RelayCommand(CancelRebootAsync);

        _rebootTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _rebootTimer.Tick += OnRebootTimerTick;
        OpenRuntimeCommand = new RelayCommand(OpenRuntimeAsync);
        CancelCommand = new RelayCommand(CancelAsync, () => IsBusy);

        // 日志合并刷新：部署期间日志行产生很快，逐行重建整段文本会让 UI 线程反复
        // 重排一个很大的 TextBox（性能瓶颈）。这里改成最多约 8 次/秒统一刷新一次。
        // 必须先建好定时器再订阅日志事件，否则首条日志可能走到 FlushLog 时定时器还是 null。
        _logTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _logTimer.Tick += (_, _) => FlushLog();

        _log.EntryWritten += OnLogEntry;

        // 高级设置的初值来自 custom.ini；缺失时回退到 config.ini
        ExpertImageSource = FirstNonEmpty(_custom.DefaultImage?.Url, _config.DefaultImage?.Url);
        ExpertTimeZone = _custom.DefaultTimeZone;
        EnsureTimeZoneOption(ExpertTimeZone);
        ExpertStagingSizeMB = _custom.StagingSizeMB;
        ExpertDryRun = _custom.DryRun;
        ExpertDrivers = string.Join(Environment.NewLine, _custom.DriverSources);
    }

    /// <summary>取本地化文案（视图模型里的弹窗、状态与日志消息）。</summary>
    private static string T(string key, params object?[] args) => LocalizationService.Current.T(key, args);

    /// <summary>可选界面语言（下拉框数据源；显示名用各语言自己的写法）。</summary>
    public IReadOnlyList<LanguageOption> Languages => LocalizationService.Current.Languages;

    /// <summary>当前界面语言代码；界面上改动后立即生效并持久化。</summary>
    public string LanguageCode
    {
        get => LocalizationService.Current.Language;
        set
        {
            if (string.IsNullOrEmpty(value) ||
                string.Equals(LocalizationService.Current.Language, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            LocalizationService.Current.SetLanguage(value);
            LocalizationService.Save(_config.RuntimeDir, value);

            // 安装位置下拉项的显示文本是本地化拼出来的，跟随语言重建
            RebuildTargetPartitionOptions();

            // null 会让 WPF 重新读取所有绑定，语言切换后界面一次性刷新
            OnPropertyChanged(null);
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// <summary>判断镜像来源是不是本机上已存在的文件（用户自选的本地映像）。</summary>
    private static bool IsLocalFile(string source) =>
        !string.IsNullOrWhiteSpace(source) &&
        !source.Contains("://", StringComparison.Ordinal) &&
        Path.IsPathRooted(source) &&
        File.Exists(source);

    private static bool Matches(string? configured, string? actual) =>
        !string.IsNullOrWhiteSpace(configured) &&
        string.Equals(configured.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- 系统信息

    private string _systemInfoText = T("Info.NotDetected");
    public string SystemInfoText
    {
        get => _systemInfoText;
        private set => SetProperty(ref _systemInfoText, value);
    }

    private string _targetText = "-";
    public string TargetText
    {
        get => _targetText;
        private set => SetProperty(ref _targetText, value);
    }

    // ---------------------------------------------------------------- 状态

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                RefreshCommands();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    private string _statusText = T("Status.Ready");
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    // ---------------------------------------------------------------- 重启倒计时

    private int _rebootRemaining;
    public int RebootRemaining
    {
        get => _rebootRemaining;
        private set
        {
            if (SetProperty(ref _rebootRemaining, value))
            {
                OnPropertyChanged(nameof(RebootCountdownText));
                OnPropertyChanged(nameof(RebootCountdownVisible));
            }
        }
    }

    public bool RebootCountdownVisible => _rebootRemaining > 0;

    public string RebootCountdownText => T("Status.RebootCountdown", _rebootRemaining);

    public ICommand CancelRebootCommand { get; }

    /// <summary>与 DeployOrchestrator 里实际下发的 shutdown /t 保持一致。</summary>
    private void StartRebootCountdown()
    {
        _rebootTimer.Stop();
        RebootRemaining = DeployOrchestrator.RebootDelaySeconds;
        _rebootTimer.Start();
    }

    private void OnRebootTimerTick(object? sender, EventArgs e)
    {
        if (RebootRemaining <= 1)
        {
            _rebootTimer.Stop();
            RebootRemaining = 0;
            return;
        }

        RebootRemaining -= 1;
    }

    private async Task CancelRebootAsync()
    {
        _rebootTimer.Stop();
        RebootRemaining = 0;

        var result = await _runner
            .RunCmdAsync("shutdown /a", null, CancellationToken.None)
            .ConfigureAwait(true);

        if (result.Success)
        {
            _log.Info(T("Msg.RebootCancelled"));
            StatusText = T("Status.RebootCancelled");
        }
        else
        {
            _log.Warn(T("Msg.RebootCancelFailed", result.Combined));
            StatusText = T("Status.RebootCancelFailed");
        }
    }

    // ---------------------------------------------------------------- 日志

    private string _logText = string.Empty;
    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public string RuntimeDir => _config.RuntimeDir;

    // ---------------------------------------------------------------- 新手模式

    /// <summary>新手模式可选指定的本地镜像；为空时按 config.ini 的下载地址走。</summary>
    private string _noviceImageFile = string.Empty;
    public string NoviceImageFile
    {
        get => _noviceImageFile;
        private set
        {
            if (SetProperty(ref _noviceImageFile, value))
            {
                OnPropertyChanged(nameof(NoviceHint));
                OnPropertyChanged(nameof(HasNoviceImageFile));
            }
        }
    }

    public bool HasNoviceImageFile => !string.IsNullOrWhiteSpace(_noviceImageFile);

    public string NoviceHint =>
        HasNoviceImageFile
            ? T("Novice.HintLocal") + "\n" + NoviceImageFile
            : !string.IsNullOrEmpty(_noviceSelectedVersion)
                ? T("Novice.HintVersion", _noviceSelectedVersion)
                : T("Novice.HintAuto");

    /// <summary>「安装其他系统」从清单拉出的可选版本名列表。</summary>
    private readonly ObservableCollection<string> _noviceVersions = new();

    public ObservableCollection<string> NoviceVersions => _noviceVersions;

    /// <summary>
    /// 新手模式选装的其他系统版本（清单里的版本键）。为空表示仍按本机当前版本自动匹配。
    /// 设值时清掉本地镜像选择：二者只能选其一。
    /// </summary>
    private string? _noviceSelectedVersion;

    public string? NoviceSelectedVersion
    {
        get => _noviceSelectedVersion;
        set
        {
            if (SetProperty(ref _noviceSelectedVersion, value))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    NoviceImageFile = string.Empty;
                }

                OnPropertyChanged(nameof(NoviceHint));
            }
        }
    }

    private bool _noviceVersionsVisible;
    public bool NoviceVersionsVisible
    {
        get => _noviceVersionsVisible;
        set => SetProperty(ref _noviceVersionsVisible, value);
    }

    private bool _isLoadingVersions;
    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        set
        {
            if (SetProperty(ref _isLoadingVersions, value))
            {
                (PickOtherImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand OneClickCommand { get; }
    public ICommand BrowseNoviceImageCommand { get; }
    public ICommand ClearNoviceImageCommand { get; }
    public ICommand PickOtherImageCommand { get; }
    public ICommand ResetVersionCommand { get; }

    private Task BrowseNoviceImageAsync()
    {
        var file = PickImageFile();
        if (string.IsNullOrEmpty(file))
        {
            return Task.CompletedTask;
        }

        // 选了本地镜像就取消「安装其他系统」的选择：两种来源只能选其一
        if (!string.IsNullOrEmpty(_noviceSelectedVersion))
        {
            _noviceSelectedVersion = null;
            OnPropertyChanged(nameof(NoviceSelectedVersion));
            OnPropertyChanged(nameof(NoviceHint));
        }

        NoviceImageFile = file;
        _log.Info(T("Msg.LocalImagePicked", file));
        return Task.CompletedTask;
    }

    private Task ClearNoviceImageAsync()
    {
        if (!HasNoviceImageFile)
        {
            return Task.CompletedTask;
        }

        NoviceImageFile = string.Empty;
        _log.Info(T("Msg.LocalImageCleared"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 「安装其他系统」：从配置的镜像清单里拉出版本列表展示。配置的系统镜像必须是链接清单（.json）；
    /// 直接给 ISO 地址的配置列不出版本，会提示用户。
    /// </summary>
    private async Task PickOtherImageAsync()
    {
        var url = _config.DefaultImage?.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = T("Msg.NoImage", "?");
            return;
        }

        if (!LinkManifestService.IsManifestUrl(url))
        {
            StatusText = T("Msg.ImageSourceNotManifest");
            return;
        }

        IsLoadingVersions = true;
        StatusText = T("Novice.LoadingVersions");
        try
        {
            var keys = await _manifests.ListImageKeysAsync(url, CancellationToken.None).ConfigureAwait(true);
            _noviceVersions.Clear();
            foreach (var key in keys)
            {
                _noviceVersions.Add(key);
            }

            NoviceVersionsVisible = _noviceVersions.Count > 0;
            StatusText = _noviceVersions.Count > 0
                ? T("Novice.PickVersion")
                : T("Msg.ManifestNoImage", url);
        }
        catch (Exception ex)
        {
            NoviceVersionsVisible = false;
            _log.Warn(T("Msg.ManifestFetchFailed", ex.Message));
            StatusText = T("Msg.ManifestFetchFailed", ex.Message);
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    private Task ResetVersionAsync()
    {
        NoviceSelectedVersion = null;
        return Task.CompletedTask;
    }

    private async Task OneClickAsync()
    {
        await RunGuardedAsync(async ct =>
        {
            var system = await DetectAsync(ct).ConfigureAwait(true);

            var pe = _config.ResolvePe();
            if (pe is null)
            {
                throw new InvalidOperationException(T("Msg.NoPe"));
            }

            // PE 与镜像地址都可以是「链接清单」(.json)：先取回清单，再按本机版本挑真正的链接
            var peUrl = pe.Url;
            var peSha = pe.Sha256;
            if (LinkManifestService.IsManifestUrl(peUrl))
            {
                var resolvedPe = await _manifests.ResolvePeAsync(peUrl, ct).ConfigureAwait(true);
                peUrl = resolvedPe.Url;
                peSha = resolvedPe.Sha256;
            }

            var image = _config.ResolveImage(system);
            var localImage = NoviceImageFile.Trim();
            // 手工选了本地镜像就跳过下载，此时不要求 config.ini 里一定配了下载地址
            if (localImage.Length == 0 && (image is null || string.IsNullOrWhiteSpace(image.Url)))
            {
                throw new InvalidOperationException(T("Msg.NoImage", system.VersionKey));
            }

            var imageUrl = localImage.Length == 0 ? image?.Url ?? string.Empty : localImage;
            // 本地文件没有配置里的校验值，留空即跳过 SHA256 校验
            var imageSha = localImage.Length == 0 ? image?.Sha256 ?? string.Empty : string.Empty;
            if (localImage.Length == 0 && LinkManifestService.IsManifestUrl(imageUrl))
            {
                ManifestEntry resolvedImage;
                if (!string.IsNullOrEmpty(_noviceSelectedVersion))
                {
                    // 新手模式选装其它系统：直接用选中的版本键，不再按本机版本匹配
                    resolvedImage = await _manifests
                        .ResolveImageByKeyAsync(imageUrl, _noviceSelectedVersion, ct).ConfigureAwait(true);
                }
                else
                {
                    resolvedImage = await _manifests.ResolveImageAsync(imageUrl, system, ct).ConfigureAwait(true);
                }

                imageUrl = resolvedImage.Url;
                imageSha = resolvedImage.Sha256;
            }

            var options = new DeployOptions
            {
                PeSource = peUrl,
                PeSha256 = peSha,
                ImageSource = imageUrl,
                ImageSha256 = imageSha,
                // 0 = 让程序按规则挑：本地多版本映像会弹窗，联网镜像按本机版本匹配
                ImageIndex = 0,
                UserSuppliedImage = localImage.Length > 0,
                InstallMode = _config.DefaultInstallMode,
                PartitionScheme = PartitionScheme.KeepExisting,
                // 新手模式固定装到系统盘；换磁盘属于高级设置
                TargetDiskNumber = -1,
                Unattended = _config.DefaultUnattended,
                // 沿用原系统用户名；检测不到时才退回默认名
                UserName = string.IsNullOrWhiteSpace(system.UserName) ? DefaultUserName : system.UserName,
                BypassNetworkRequirement = true,
                TimeZone = _config.DefaultTimeZone,
                StagingSizeMB = _config.StagingSizeMB,
                StagingLabel = _config.StagingLabel,
                TargetLabel = _config.TargetLabel,
                DryRun = _config.DryRun,
                RebootAfterPrepare = true,
                // 新手模式选装其它系统：把选中的版本名记下来，确认框里显示它而不是地址
                UserImageLabel = string.IsNullOrEmpty(_noviceSelectedVersion) ? null : _noviceSelectedVersion,
            };
            options.DriverSources.AddRange(_config.DriverSources);

            await _orchestrator.FillTargetAsync(options, ct).ConfigureAwait(true);
            UpdateTargetText(options);

            // 三步确认，每步都必须读完（确定按钮强制等待 3 秒）
            if (!ConfirmDeployment(options))
            {
                _log.Info(T("Msg.NoviceCancelled"));
                StatusText = T("Status.Cancelled");
                return;
            }

            await _orchestrator.RunAsync(options, system, _config, CreateProgress(), ct).ConfigureAwait(true);

            // 新手模式总是安排重启；演练模式不下发 shutdown，自然也没有倒计时
            if (!_config.DryRun)
            {
                StartRebootCountdown();
            }
        }).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- 高级设置

    /// <summary>镜像来源是否可用：地址框或本地文件框任一个有内容。</summary>
    private bool HasImageSource =>
        !string.IsNullOrWhiteSpace(_expertImageSource) ||
        !string.IsNullOrWhiteSpace(_expertCustomImageFile);

    private string _expertImageSource = string.Empty;
    public string ExpertImageSource
    {
        get => _expertImageSource;
        set
        {
            if (SetProperty(ref _expertImageSource, value))
            {
                (AnalyzeImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _expertCustomImageFile = string.Empty;
    public string ExpertCustomImageFile
    {
        get => _expertCustomImageFile;
        set
        {
            if (SetProperty(ref _expertCustomImageFile, value))
            {
                (AnalyzeImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<ImageInfo> ImageOptions { get; } = new();

    private ImageInfo? _selectedImage;
    public ImageInfo? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (SetProperty(ref _selectedImage, value) && value is not null)
            {
                ExpertImageIndex = value.Index;
            }
        }
    }

    // 0 = 未指定：联网镜像按本机版本自动匹配，本地多版本镜像会弹窗让用户选
    private int _expertImageIndex;
    public int ExpertImageIndex
    {
        get => _expertImageIndex;
        set => SetProperty(ref _expertImageIndex, value);
    }

    private bool _expertCleanInstall = true;
    public bool ExpertCleanInstall
    {
        get => _expertCleanInstall;
        set => SetProperty(ref _expertCleanInstall, value);
    }

    private bool _expertWipeDisk;
    public bool ExpertWipeDisk
    {
        get => _expertWipeDisk;
        set
        {
            if (!SetProperty(ref _expertWipeDisk, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ExpertCleanInstallEnabled));
            OnPropertyChanged(nameof(ExpertFormatBootEnabled));

            // 整盘重建会把目标磁盘全部分区删掉重新分区，目标分区必然重新格式化，
            // 「保留文件」在这种组合下不可能成立，因此强制回到全新安装。
            if (value)
            {
                ExpertCleanInstall = true;
            }
        }
    }

    /// <summary>整盘重建时「全新安装」由上面的选项决定，不允许再改（避免出现矛盾的组合）。</summary>
    public bool ExpertCleanInstallEnabled => !_expertWipeDisk;

    // ---------------------------------------------------------------- 安装位置（分区）

    /// <summary>最近一次检测到的磁盘列表（用于构建下拉框与判断系统盘）。</summary>
    private IReadOnlyList<DiskInfo> _disks = Array.Empty<DiskInfo>();

    /// <summary>系统盘所在磁盘号（-1 表示尚未检测到）。</summary>
    private int _sourceDiskNumber = -1;

    /// <summary>当前 Windows 所在分区号（0 表示尚未检测到）。</summary>
    private int _sourcePartitionNumber;

    /// <summary>因选择了非系统盘上的分区而强制勾选「整盘重建」时，记住用户原来的选择，切回自动时恢复。</summary>
    private bool _wipeForcedByCrossDisk;
    private bool _wipeRememberedByCrossDisk;
    private bool _cleanRememberedByCrossDisk;

    /// <summary>重建下拉框期间抑制选中项的副作用处理。</summary>
    private bool _rebuildingPartitionOptions;

    public ObservableCollection<TargetPartitionOption> TargetPartitionOptions { get; } = new();

    private TargetPartitionOption? _selectedTargetPartition;
    public TargetPartitionOption? SelectedTargetPartition
    {
        get => _selectedTargetPartition;
        set
        {
            if (!SetProperty(ref _selectedTargetPartition, value))
            {
                return;
            }

            if (!_rebuildingPartitionOptions)
            {
                ApplyTargetPartitionSelection();
            }
        }
    }

    /// <summary>是否选择了「非系统盘上的分区」作为安装目标（该磁盘会被整盘重建）。</summary>
    public bool IsCrossDiskTarget =>
        _selectedTargetPartition is not null &&
        !_selectedTargetPartition.IsAuto &&
        _sourceDiskNumber >= 0 &&
        _selectedTargetPartition.DiskNumber != _sourceDiskNumber;

    /// <summary>选择非系统盘上的分区时的红色提示文案；其余情况为空。</summary>
    public string CrossDiskHint =>
        IsCrossDiskTarget && _selectedTargetPartition is not null
            // 提示说的是「整块磁盘会被重建」，所以这里报磁盘号，不是分区名
            ? T("Install.CrossDiskHint", _selectedTargetPartition.DiskNumber)
            : string.Empty;

    /// <summary>整盘重建会把全部分区（含引导分区）重建，不能再改这个开关。</summary>
    public bool ExpertWipeDiskEnabled => !IsCrossDiskTarget;

    private bool _expertFormatBootPartition = true;
    public bool ExpertFormatBootPartition
    {
        get => _expertFormatBootPartition;
        set => SetProperty(ref _expertFormatBootPartition, value);
    }

    /// <summary>整盘重建时引导分区必然是新格式化的，这个开关没有意义。</summary>
    public bool ExpertFormatBootEnabled => !_expertWipeDisk;

    private bool _expertDisableDefender;
    public bool ExpertDisableDefender
    {
        get => _expertDisableDefender;
        set => SetProperty(ref _expertDisableDefender, value);
    }

    /// <summary>用检测到的磁盘列表重建下拉框；语言切换、重新检测后都会调用。</summary>
    private void RebuildTargetPartitionOptions()
    {
        _rebuildingPartitionOptions = true;
        try
        {
            var previousDisk = _selectedTargetPartition?.DiskNumber ?? -1;
            var previousPart = _selectedTargetPartition?.PartitionNumber ?? 0;
            TargetPartitionOptions.Clear();

            // 第一项：自动（当前 Windows 所在分区）
            TargetPartitionOptions.Add(new TargetPartitionOption(-1, 0,
                _sourcePartitionNumber > 0
                    ? T("Install.PartitionAutoNamed", DescribeSystemPartition())
                    : T("Install.PartitionAuto")));

            foreach (var disk in _disks.OrderBy(d => d.DiskNumber))
            {
                foreach (var part in disk.Partitions.OrderBy(p => p.PartitionNumber))
                {
                    TargetPartitionOptions.Add(new TargetPartitionOption(
                        disk.DiskNumber, part.PartitionNumber, DescribePartition(disk, part)));
                }
            }

            var selected = TargetPartitionOptions.FirstOrDefault(o =>
                               o.DiskNumber == previousDisk && o.PartitionNumber == previousPart)
                           ?? TargetPartitionOptions[0];
            if (previousPart > 0 && selected.PartitionNumber != previousPart)
            {
                _log.Warn(T("Msg.PartitionSelectionReset", previousPart));
            }

            if (!ReferenceEquals(_selectedTargetPartition, selected))
            {
                _selectedTargetPartition = selected;
                OnPropertyChanged(nameof(SelectedTargetPartition));
            }
        }
        finally
        {
            _rebuildingPartitionOptions = false;
        }

        ApplyTargetPartitionSelection();
    }

    /// <summary>当前 Windows 所在分区的描述文本（供「自动」项显示）。</summary>
    private string DescribeSystemPartition()
    {
        var disk = _disks.FirstOrDefault(d => d.DiskNumber == _sourceDiskNumber);
        var part = disk?.Partitions.FirstOrDefault(p => p.PartitionNumber == _sourcePartitionNumber);
        return part is null ? T("Install.PartitionAuto") : DescribePartition(disk!, part);
    }

    /// <summary>安装位置下拉框里某一项的显示文本（按当前语言拼）。</summary>
    private string DescribePartition(DiskInfo disk, PartitionInfo part)
    {
        var letter = string.IsNullOrEmpty(part.DriveLetter)
            ? T("Install.NoLetter")
            : part.DriveLetter + ":";
        var text = T("Install.PartitionItem", disk.DiskNumber, part.PartitionNumber, letter, part.SizeText);

        // 引导分区与系统保留分区不能当安装目标，标出来免得选错
        if (part.IsEsp)
        {
            return text + " · " + T("Install.PartitionEsp");
        }

        if (part.Type.Equals("Reserved", StringComparison.OrdinalIgnoreCase))
        {
            return text + " · " + T("Install.PartitionReserved");
        }

        if (disk.DiskNumber == _sourceDiskNumber && part.PartitionNumber == _sourcePartitionNumber)
        {
            return text + " · " + T("Install.PartitionCurrent");
        }

        return text;
    }

    /// <summary>
    /// 选择非系统盘上的分区时的联动：该磁盘上还没有可引导的 Windows 环境，
    /// 必须整盘重建才能装出能启动的系统；切回自动时恢复原选择。
    /// </summary>
    private void ApplyTargetPartitionSelection()
    {
        if (IsCrossDiskTarget)
        {
            if (!_wipeForcedByCrossDisk)
            {
                _wipeForcedByCrossDisk = true;
                _wipeRememberedByCrossDisk = _expertWipeDisk;
                _cleanRememberedByCrossDisk = _expertCleanInstall;
                ExpertWipeDisk = true;
            }
        }
        else if (_wipeForcedByCrossDisk)
        {
            _wipeForcedByCrossDisk = false;
            ExpertWipeDisk = _wipeRememberedByCrossDisk;
            // 「整盘重建」被强制打开时，它的 setter 也把「全新安装」一并打开了。
            // 切回系统盘必须把两者一起还原，否则用户原本的「保留文件」选择会被静默吞掉。
            ExpertCleanInstall = _cleanRememberedByCrossDisk;
        }

        OnPropertyChanged(nameof(IsCrossDiskTarget));
        OnPropertyChanged(nameof(CrossDiskHint));
        OnPropertyChanged(nameof(ExpertWipeDiskEnabled));
    }

    // ---------------------------------------------------------------- 时区

    private readonly List<TimeZoneOption> _timeZones = BuildTimeZones();

    /// <summary>时区下拉框数据源：Windows 时区标识 + 系统本地化的显示名。</summary>
    public IReadOnlyList<TimeZoneOption> TimeZoneOptions => _timeZones;

    private static List<TimeZoneOption> BuildTimeZones()
    {
        var list = new List<TimeZoneOption>();
        try
        {
            foreach (var tz in TimeZoneInfo.GetSystemTimeZones()
                         .OrderBy(t => t.BaseUtcOffset)
                         .ThenBy(t => t.DisplayName, StringComparer.CurrentCulture))
            {
                var display = string.IsNullOrWhiteSpace(tz.DisplayName) ? tz.Id : tz.DisplayName;
                list.Add(new TimeZoneOption(tz.Id, display));
            }
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // 极端环境下拿不到时区列表：下面补一个默认可选项，界面不至于空白
        }

        if (list.Count == 0)
        {
            list.Add(new TimeZoneOption("China Standard Time", "China Standard Time"));
        }

        return list;
    }

    /// <summary>
    /// 配置里给的时区标识若不在系统时区列表里（例如精简系统），
    /// 补一个同名选项，保证下拉框能选中它而不是显示空白。
    /// </summary>
    private void EnsureTimeZoneOption(string id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            _timeZones.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _timeZones.Add(new TimeZoneOption(id, id));
        OnPropertyChanged(nameof(TimeZoneOptions));
    }

    private bool _expertUnattended = true;
    public bool ExpertUnattended
    {
        get => _expertUnattended;
        set => SetProperty(ref _expertUnattended, value);
    }

    private bool _expertBypassNro = true;
    public bool ExpertBypassNro
    {
        get => _expertBypassNro;
        set => SetProperty(ref _expertBypassNro, value);
    }

    /// <summary>检测不到原系统用户名时使用的默认账户名。</summary>
    public const string DefaultUserName = "CTUser";

    private string _expertUserName = DefaultUserName;
    public string ExpertUserName
    {
        get => _expertUserName;
        set => SetProperty(ref _expertUserName, value);
    }

    private string _expertPassword = string.Empty;
    public string ExpertPassword
    {
        get => _expertPassword;
        set => SetProperty(ref _expertPassword, value);
    }

    private string _expertComputerName = string.Empty;
    public string ExpertComputerName
    {
        get => _expertComputerName;
        set => SetProperty(ref _expertComputerName, value);
    }

    private string _expertTimeZone = "China Standard Time";
    public string ExpertTimeZone
    {
        get => _expertTimeZone;
        set => SetProperty(ref _expertTimeZone, value);
    }

    private string _expertDrivers = string.Empty;
    public string ExpertDrivers
    {
        get => _expertDrivers;
        set => SetProperty(ref _expertDrivers, value);
    }

    private int _expertStagingSizeMB = 12288;
    public int ExpertStagingSizeMB
    {
        get => _expertStagingSizeMB;
        set => SetProperty(ref _expertStagingSizeMB, value);
    }

    private bool _expertDryRun;
    public bool ExpertDryRun
    {
        get => _expertDryRun;
        set => SetProperty(ref _expertDryRun, value);
    }

    private bool _expertReboot = true;
    public bool ExpertReboot
    {
        get => _expertReboot;
        set => SetProperty(ref _expertReboot, value);
    }

    private bool _expertShutdown;
    public bool ExpertShutdown
    {
        get => _expertShutdown;
        set => SetProperty(ref _expertShutdown, value);
    }

    public ICommand ExpertDeployCommand { get; }
    public ICommand AnalyzeImageCommand { get; }
    public ICommand BrowseImageCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenRuntimeCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// 多版本本地映像的选择窗口（服务层回调到这里）。
    /// 返回用户选中的索引；取消返回 null。
    /// 注意：编排链路用 ConfigureAwait(false)，回调可能不在 UI 线程上，
    /// 必须切回主线程再创建窗口，否则抛「调用线程必须为 STA」。
    /// </summary>
    private int? PickImageEdition(IReadOnlyList<ImageInfo> editions, int preselectIndex) =>
        OnUi(() =>
        {
            var window = new Views.EditionPickerWindow(editions, preselectIndex);
            var owner = Application.Current?.MainWindow;
            if (owner is not null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            return window.ShowDialog() == true ? window.SelectedEdition?.Index : null;
        });

    private Task BrowseImageAsync()
    {
        var file = PickImageFile();
        if (string.IsNullOrEmpty(file))
        {
            return Task.CompletedTask;
        }

        ExpertCustomImageFile = file;
        _log.Info(T("Msg.ImagePicked", file));
        return Task.CompletedTask;
    }

    /// <summary>弹出系统镜像选择框；用户取消时返回 null。</summary>
    private static string? PickImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("Dialog.PickImageTitle"),
            Filter = "Windows image (*.iso;*.wim;*.esd)|*.iso;*.wim;*.esd|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private async Task AnalyzeImageAsync()
    {
        await RunGuardedAsync(async ct =>
        {
            var source = string.IsNullOrWhiteSpace(ExpertCustomImageFile)
                ? ExpertImageSource.Trim()
                : ExpertCustomImageFile.Trim();

            // 地址框里贴的是「链接清单」时，先按本机版本解析出真正的镜像地址再分析
            string? expectedSha256 = null;
            if (LinkManifestService.IsManifestUrl(source))
            {
                var system = await Task.Run(() => _systemInfoService.Detect(), ct).ConfigureAwait(true);
                var resolved = await _manifests.ResolveImageAsync(source, system, ct).ConfigureAwait(true);
                source = resolved.Url;
                expectedSha256 = resolved.Sha256;
            }

            // 本地文件是复制进来的，文案要说「复制」，别把读本地盘说成下载
            var copyLocal = DownloadService.IsLocalSource(source);
            StatusText = T(copyLocal ? "Status.AnalyzingLocal" : "Status.Analyzing");
            var file = await _download
                .AcquireAsync(source, Path.Combine(_config.RuntimeDir, "downloads", "image"), expectedSha256,
                    new Progress<DownloadProgress>(p =>
                        StatusText = T(copyLocal ? "Status.CopyImage" : "Status.DownloadImage", p.Display)), ct)
                .ConfigureAwait(true);

            var work = Path.Combine(_config.RuntimeDir, "work", "image");
            var install = await _images.ExtractInstallImageAsync(file, work, ct).ConfigureAwait(true);
            var infos = await _dism.GetWimInfoAsync(install, ct).ConfigureAwait(true);

            ImageOptions.Clear();
            foreach (var info in infos)
            {
                ImageOptions.Add(info);
            }

            SelectedImage = ImageOptions.FirstOrDefault();
            StatusText = T("Status.Analyzed", ImageOptions.Count);
        }).ConfigureAwait(true);
    }

    private async Task ExpertDeployAsync()
    {
        await RunGuardedAsync(async ct =>
        {
            var system = await DetectAsync(ct).ConfigureAwait(true);

            // PE 来源固定取配置（custom.ini 优先，缺失回退 config.ini），界面不提供自定义
            var customPe = _custom.ResolvePe();
            var configPe = _config.ResolvePe();
            var peUrl = FirstNonEmpty(customPe?.Url, configPe?.Url);
            // SHA256 必须与最终采用的 URL 同源，否则 custom.ini 缺失时会拿空值静默跳过校验
            var peSha = string.Equals(peUrl, configPe?.Url, StringComparison.OrdinalIgnoreCase)
                ? configPe?.Sha256
                : customPe?.Sha256;
            if (LinkManifestService.IsManifestUrl(peUrl))
            {
                var resolvedPe = await _manifests.ResolvePeAsync(peUrl, ct).ConfigureAwait(true);
                peUrl = resolvedPe.Url;
                peSha = resolvedPe.Sha256;
            }

            var customFile = ExpertCustomImageFile.Trim();
            var imageUrl = customFile.Length == 0 ? ExpertImageSource.Trim() : customFile;
            var customImage = _custom.DefaultImage;
            // 配置里的校验值只对「配置里那条地址」有效；清单解析出的地址用清单里的 sha256
            var imageSha = Matches(customImage?.Url, imageUrl) ? customImage!.Sha256 : string.Empty;
            if (LinkManifestService.IsManifestUrl(imageUrl))
            {
                var resolvedImage = await _manifests.ResolveImageAsync(imageUrl, system, ct).ConfigureAwait(true);
                imageUrl = resolvedImage.Url;
                imageSha = resolvedImage.Sha256;
            }

            // 手填的账户名 / 计算机名先按 Windows 命名规则规整，否则带非法字符的名字会
            // 让应答文件里的账户创建失败（严重时整个 oobeSystem 组件被跳过，一个账户都建不出来）。
            // 规范化后的值会进入下面的确认窗口，用户能看到真正会写入的名字。
            var userName = NormalizeUserName(ExpertUserName);
            var computerName = NormalizeComputerName(ExpertComputerName);

            // 安装位置：分区号 0 = 自动（当前系统分区）；
            // 选了另一块磁盘上的分区时 FillTargetAsync 会要求整盘重建该磁盘。
            var selectedPartition = SelectedTargetPartition;
            var targetDisk = selectedPartition is { IsAuto: false } ? selectedPartition.DiskNumber : -1;
            var targetPartition = selectedPartition is { IsAuto: false } ? selectedPartition.PartitionNumber : 0;
            if (IsCrossDiskTarget && selectedPartition is not null)
            {
                _log.Info(T("Msg.CrossDiskChosen", selectedPartition.Display));
            }

            var options = new DeployOptions
            {
                PeSource = peUrl,
                // 界面不可再改 PE 地址，PE 来源就是配置里的那份，SHA256 直接套用
                PeSha256 = peSha ?? string.Empty,
                ImageSource = imageUrl,
                ImageSha256 = imageSha,
                ImageIndex = ExpertImageIndex,
                // 手填的本地路径同样算「用户自选」，不自动猜版本
                UserSuppliedImage = IsLocalFile(imageUrl),
                TargetDiskNumber = targetDisk,
                TargetPartitionNumber = targetPartition,
                TargetDiskLabel = IsCrossDiskTarget && selectedPartition is not null ? selectedPartition.Display : string.Empty,
                InstallMode = ExpertCleanInstall ? InstallMode.Clean : InstallMode.KeepFiles,
                PartitionScheme = ExpertWipeDisk
                    ? (_systemInfoService.GetFirmware() == FirmwareType.Uefi ? PartitionScheme.WipeDiskGpt : PartitionScheme.WipeDiskMbr)
                    : PartitionScheme.KeepExisting,
                FormatBootPartition = ExpertFormatBootPartition,
                Unattended = ExpertUnattended,
                BypassNetworkRequirement = ExpertBypassNro,
                DisableDefender = ExpertDisableDefender,
                UserName = userName,
                Password = ExpertPassword,
                ComputerName = computerName,
                TimeZone = string.IsNullOrWhiteSpace(ExpertTimeZone) ? _custom.DefaultTimeZone : ExpertTimeZone.Trim(),
                StagingSizeMB = ExpertStagingSizeMB <= 0 ? 12288 : ExpertStagingSizeMB,
                StagingLabel = _custom.StagingLabel,
                TargetLabel = _custom.TargetLabel,
                DryRun = ExpertDryRun,
                RebootAfterPrepare = ExpertReboot,
                ShutdownAfterDeploy = ExpertShutdown,
            };

            foreach (var line in ExpertDrivers.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                options.DriverSources.Add(line.Trim());
            }

            await _orchestrator.FillTargetAsync(options, ct).ConfigureAwait(true);
            UpdateTargetText(options);

            // 三步确认，每步都必须读完（确定按钮强制等待 3 秒）
            if (!ConfirmDeployment(options))
            {
                _log.Info(T("Status.Cancelled"));
                StatusText = T("Status.Cancelled");
                return;
            }

            await _orchestrator.RunAsync(options, system, _custom, CreateProgress(), ct).ConfigureAwait(true);

            if (ExpertReboot && !ExpertDryRun)
            {
                StartRebootCountdown();
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 规整手填的账户名。结果不可用时回退到默认名，因此确认窗口里永远不会是空值。
    /// </summary>
    private string NormalizeUserName(string typed)
    {
        var trimmed = typed.Trim();
        var result = SystemInfoService.SanitizeAccountName(trimmed);
        if (result.Length == 0)
        {
            result = DefaultUserName;
        }

        if (!string.Equals(result, trimmed, StringComparison.Ordinal))
        {
            _log.Warn(T("Msg.NameNormalized", T("Unattend.User"), trimmed, result));
        }

        return result;
    }

    /// <summary>规整手填的计算机名。结果为空时返回空串，由应答文件生成器随机取名。</summary>
    private string NormalizeComputerName(string typed)
    {
        var trimmed = typed.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var result = SystemInfoService.SanitizeComputerName(trimmed);
        if (result.Length == 0)
        {
            _log.Warn(T("Msg.ComputerNameRandom", trimmed));
            return string.Empty;
        }

        if (!string.Equals(result, trimmed, StringComparison.Ordinal))
        {
            _log.Warn(T("Msg.NameNormalized", T("Unattend.Computer"), trimmed, result));
        }

        return result;
    }

    // ---------------------------------------------------------------- 安装确认

    /// <summary>
    /// 安装前三步确认：数据备份 → 目标与方式 → 开始执行。
    /// 每一步都由 <see cref="ConfirmWindow"/> 强制等待 3 秒后才能点确定，
    /// 危险行（清空 C 盘、整盘重建、改写磁盘）一律红色显示。
    /// </summary>
    private bool ConfirmDeployment(DeployOptions options)
    {
        // ---- 第 1 步：数据备份 ----
        var backupOk = Confirm(
            T("Confirm1.Title"),
            new ConfirmLine(T("Confirm1.Line1"), true),
            new ConfirmLine(T("Confirm1.Line2")));
        if (!backupOk)
        {
            return false;
        }

        // ---- 第 2 步：安装目标与方式 ----
        var targetLines = new List<ConfirmLine>
        {
            new(T("Confirm2.Target", TargetText)),
            new(options.InstallMode == InstallMode.Clean ? T("Confirm2.ModeClean") : T("Confirm2.ModeKeepFiles")),
        };

        if (options.PartitionScheme != PartitionScheme.KeepExisting)
        {
            var crossDisk = options.SourceDiskNumber >= 0 && options.SourceDiskNumber != options.TargetDiskNumber;
            targetLines.Add(crossDisk && options.TargetDiskLabel.Length > 0
                ? new ConfirmLine(T("Confirm2.WipeOtherDisk", options.TargetDiskLabel), true)
                : new ConfirmLine(T("Confirm2.WipeDisk"), true));
        }
        else
        {
            // 引导分区的处理方式：格式化重建还是保留旧文件（保留旧文件时安装失败旧系统仍可引导）
            targetLines.Add(new ConfirmLine(
                options.FormatBootPartition ? T("Confirm2.BootFormat") : T("Confirm2.BootKeep")));
        }

        var imageSource = options.ImageSource.Trim();
        var imageDisplay = !string.IsNullOrWhiteSpace(options.UserImageLabel)
            ? options.UserImageLabel
            : imageSource;
        targetLines.Add(new ConfirmLine(
            options.UserSuppliedImage
                ? T("Confirm2.ImageLocal", imageDisplay)
                : T("Confirm2.ImageOnline", imageDisplay)));

        // 账户名是这里唯一"程序替你决定"的东西（沿用原系统用户名）。写出来，
        // 免得装完进系统才发现名字被换成了默认名。
        targetLines.Add(new ConfirmLine(T(
            "Confirm2.Account",
            string.IsNullOrWhiteSpace(options.UserName) ? DefaultUserName : options.UserName)));

        if (!Confirm(T("Confirm2.Title"), targetLines))
        {
            return false;
        }

        // ---- 第 3 步：开始执行 ----
        var startLines = new List<ConfirmLine>
        {
            new(options.DryRun ? T("Confirm3.DryRun") : T("Confirm3.Real"), !options.DryRun),
        };

        if (options.RebootAfterPrepare && !options.DryRun)
        {
            startLines.Add(new ConfirmLine(T("Confirm3.Reboot")));
        }

        return Confirm(T("Confirm3.Title"), startLines);
    }

    /// <summary>弹出一个带强制阅读时间的确认窗口；点确定返回 true。</summary>
    private static bool Confirm(string title, params ConfirmLine[] lines) =>
        Confirm(title, (IEnumerable<ConfirmLine>)lines);

    private static bool Confirm(string title, IEnumerable<ConfirmLine> lines) =>
        OnUi(() =>
        {
            var window = new Views.ConfirmWindow(title, lines);
            var owner = Application.Current?.MainWindow;
            if (owner is not null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            return window.ShowDialog() == true && window.Confirmed;
        });

    /// <summary>
    /// 在 UI 线程上执行并取回结果。
    /// 部署编排链路上大量使用 ConfigureAwait(false)，回调可能落在池线程（MTA）上；
    /// 直接在那里创建 WPF 窗口会抛「调用线程必须为 STA」。这里统一切回主线程。
    /// </summary>
    private static T OnUi<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.Invoke(action);
    }

    // ---------------------------------------------------------------- 公共流程

    private async Task<SystemInfo> DetectAsync(CancellationToken ct)
    {
        StatusText = T("Status.Detecting");
        var info = await Task.Run(() => _systemInfoService.Detect(), ct).ConfigureAwait(true);

        // 高级设置里的账户名同样默认沿用原系统用户名；用户填过别的就尊重用户
        if (info.UserName.Length > 0 &&
            string.Equals(_expertUserName, DefaultUserName, StringComparison.Ordinal))
        {
            ExpertUserName = info.UserName;
        }

        var disks = await _storage.GetDisksAsync(ct).ConfigureAwait(true);
        var firmware = _systemInfoService.GetFirmware();

        // 记住磁盘列表与系统分区位置：安装位置下拉框据此构建
        _disks = disks;
        var systemDrive = _storage.GetSystemDriveLetter();
        var sourceDisk = disks.FirstOrDefault(d => d.Partitions.Any(p =>
            p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)));
        _sourceDiskNumber = sourceDisk?.DiskNumber ?? -1;
        _sourcePartitionNumber = sourceDisk?.Partitions.FirstOrDefault(p =>
            p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))?.PartitionNumber ?? 0;
        RebuildTargetPartitionOptions();

        var firmwareText = firmware == FirmwareType.Uefi
            ? T("Info.FirmwareUefi")
            : firmware == FirmwareType.Bios
                ? T("Info.FirmwareBios")
                : T("Info.FirmwareUnknown");

        SystemInfoText =
            $"{info}{Environment.NewLine}" +
            T("Info.DetectVersionKey", info.VersionKey) + Environment.NewLine +
            T("Info.DetectFirmware", firmwareText) + Environment.NewLine +
            T("Info.DetectDisks", disks.Count);

        foreach (var disk in disks)
        {
            _log.Info(disk.ToString());
        }

        return info;
    }

    private async Task RefreshAsync()
    {
        await RunGuardedAsync(async ct =>
        {
            await DetectAsync(ct).ConfigureAwait(true);
            StatusText = T("Status.Detected");
        }).ConfigureAwait(true);
    }

    private Task OpenRuntimeAsync()
    {
        try
        {
            Directory.CreateDirectory(_config.RuntimeDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _config.RuntimeDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(T("Msg.OpenDirFailed", ex.Message), T("Dialog.ErrorTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    private Task CancelAsync()
    {
        _cts?.Cancel();
        StatusText = T("Status.Cancelling");
        return Task.CompletedTask;
    }

    private async Task RunGuardedAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProgressValue = 0;
        _cts = new CancellationTokenSource();

        try
        {
            await action(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _log.Warn(T("Status.Cancelled"));
            StatusText = T("Status.Cancelled");
        }
        catch (Exception ex)
        {
            _log.Error(T("Status.Failed") + ex.Message);
            StatusText = T("Status.Failed");
            MessageBox.Show(ex.Message, T("Dialog.FailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            // 部署结束后补一次刷新，确保最后几行日志已经出现在界面上
            FlushLog();
            _cts.Dispose();
            _cts = null;
        }
    }

    private IProgress<DeployProgress> CreateProgress() => new Progress<DeployProgress>(p =>
    {
        ProgressValue = p.Percent;
        StatusText = p.Message;
        if (p.IsError)
        {
            _log.Error(p.Message);
        }
        else
        {
            _log.Info(p.Message);
        }
    });

    private void UpdateTargetText(DeployOptions options)
    {
        var firmware = options.Firmware == FirmwareType.Uefi ? T("Info.FirmwareUefi") : "BIOS";
        var mode = options.InstallMode == InstallMode.Clean ? T("Target.ModeClean") : T("Target.ModeKeepFiles");
        var runMode = options.DryRun ? T("Target.DryRun") : T("Target.Real");
        TargetText = options.PartitionScheme == PartitionScheme.KeepExisting
            ? T("Target.Summary", options.TargetDiskNumber, options.TargetPartitionNumber, firmware, $"{mode} / {runMode}")
            : T("Target.SummaryWipe", options.TargetDiskNumber, firmware, $"{mode} / {runMode}");
    }

    private void OnLogEntry(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => AppendLog(entry));
            return;
        }

        AppendLog(entry);
    }

    private void AppendLog(LogEntry entry)
    {
        _logBuffer.AppendLine(entry.Display);

        // 只保留最近一段日志：TextBox 的排版开销与字符数近似线性，
        // 无限增长会让长时间部署越来越卡。
        if (_logBuffer.Length > 120_000)
        {
            _logBuffer.Remove(0, _logBuffer.Length - 80_000);
        }

        _logDirty = true;

        // 部署中（IsBusy）合并刷新；空闲时立即刷新，保证交互反馈即时。
        if (IsBusy)
        {
            if (!_logTimer.IsEnabled)
            {
                _logTimer.Start();
            }

            return;
        }

        FlushLog();
    }

    /// <summary>把日志缓冲区一次性同步到界面。</summary>
    private void FlushLog()
    {
        _logTimer?.Stop();
        if (!_logDirty)
        {
            return;
        }

        _logDirty = false;
        LogText = _logBuffer.ToString();
    }

    private void RefreshCommands()
    {
        (OneClickCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExpertDeployCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AnalyzeImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        // 这三个也是 !IsBusy：漏掉它们会让部署期间「浏览 / 清除」按钮仍可点击。
        (BrowseImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseNoviceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearNoviceImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        // 这两个按钮的 can-execute 也含 !IsBusy（PickOtherImage 还含 !IsLoadingVersions），
        // 漏掉会让部署/拉取期间仍可点击。
        (PickOtherImageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResetVersionCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

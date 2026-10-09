using System.Reflection;
using System.Text;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>PE 侧载荷：渲染部署脚本、准备暂存分区内容、把自动启动脚本注入 boot.wim。</summary>
public sealed class PePayloadService
{
    /// <summary>PE 中固定分配给目标系统分区的盘符。</summary>
    public const char TargetLetter = 'W';

    /// <summary>PE 中固定分配给 EFI 分区的盘符。</summary>
    public const char EspLetter = 'S';

    /// <summary>PE 中固定分配给 BIOS 系统保留分区的盘符。</summary>
    public const char BiosSystemLetter = 'B';

    private readonly ProcessRunner _runner;
    private readonly DismService _dism;
    private readonly ILogSink _log;

    public PePayloadService(ProcessRunner runner, DismService dism, ILogSink log)
    {
        _runner = runner;
        _dism = dism;
        _log = log;
    }

    /// <summary>把 PE 映像、系统映像、驱动包与标记文件放入暂存分区（不含脚本渲染）。</summary>
    public async Task StagePayloadFilesAsync(
        DeployOptions options,
        string stagingRoot,
        string bootWimPath,
        string installImagePath,
        IReadOnlyList<string> driverDirectories,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(stagingRoot);

        // 1. PE 映像
        var bootWimDest = Path.Combine(stagingRoot, "boot.wim");
        _log.Info($"复制 PE 映像到暂存分区：{bootWimDest}");
        CopyFileIfDifferent(bootWimPath, bootWimDest);

        // 2. 系统映像（文件名必须能写成 ASCII：PE 控制台用代码页 437，中文名写出会变 '?'）
        var installDest = Path.Combine(stagingRoot, GetDeployImageFileName(installImagePath));
        if (!string.Equals(Path.GetFileName(installImagePath), Path.GetFileName(installDest), StringComparison.Ordinal))
        {
            _log.Warn($"系统映像文件名包含非 ASCII 字符（{Path.GetFileName(installImagePath)}），已统一命名为 {Path.GetFileName(installDest)} 以兼容 PE 控制台。");
        }

        _log.Info($"复制系统映像到暂存分区：{installDest}");
        CopyFileIfDifferent(installImagePath, installDest);

        // 3. boot.sdi（ramdisk 引导必需）
        CopyFileIfDifferent(ResolveBootSdi(bootWimPath, installImagePath), Path.Combine(stagingRoot, "boot.sdi"));

        // 4. 驱动包
        var driverDir = Path.Combine(stagingRoot, "drivers");
        if (driverDirectories.Count > 0)
        {
            Directory.CreateDirectory(driverDir);
            var i = 0;
            foreach (var dir in driverDirectories.Where(Directory.Exists))
            {
                i++;
                CopyDirectory(dir, Path.Combine(driverDir, $"pack{i}"));
            }

            _log.Info($"已放入 {i} 个驱动包。");
        }

        // 5. 标记文件：PE 侧据此定位暂存分区
        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "ctprep.marker"),
            $"CTPrep {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}",
            Encoding.ASCII,
            ct).ConfigureAwait(false);

        _log.Info($"暂存分区文件准备完成：{stagingRoot}");
    }

    /// <summary>渲染并写出 PE 侧要执行的脚本（需要在引导项 GUID 生成之后调用）。</summary>
    public async Task WriteScriptsAsync(
        DeployOptions options,
        string stagingRoot,
        string installImagePath,
        CancellationToken ct = default)
    {
        var imageFileName = GetDeployImageFileName(installImagePath);

        // PE 侧脚本一律写成 ASCII：PE 控制台使用代码页 437，非 ASCII 字节会被
        // cmd 当作运算符解析（GBK 尾字节 0x7C 即管道符），从而静默破坏命令行。
        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "deploy.cmd"),
            RenderDeployCmd(options, imageFileName),
            Encoding.ASCII,
            ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "diskpart-target.txt"),
            RenderDiskPartTarget(options),
            Encoding.ASCII,
            ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "unattend.xml"),
            RenderUnattend(options),
            new UTF8Encoding(false),
            ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "SetupComplete.cmd"),
            RenderSetupComplete(options),
            Encoding.ASCII,
            ct).ConfigureAwait(false);

        // 语言选择：PE 控制台是代码页 437，脚本里不能出现非 ASCII，
        // 因此只写一个 ASCII 语言代码，由原生界面自己查表翻译。
        await File.WriteAllTextAsync(
            Path.Combine(stagingRoot, "lang.txt"),
            LocalizationService.Current.Language,
            Encoding.ASCII,
            ct).ConfigureAwait(false);

        using var ui = Assembly.GetExecutingAssembly().GetManifestResourceStream("CtPrep.App.Assets.pe.CTPrep.PeUi.exe")
            ?? throw new InvalidOperationException("缺少原生 PE 安装界面，请重新构建程序。");
        await using var uiFile = File.Create(Path.Combine(stagingRoot, "CTPrep.PeUi.exe"));
        await ui.CopyToAsync(uiFile, ct).ConfigureAwait(false);

        // 界面用的中文字体已经链接在 CTPrep.PeUi.exe 内部（AddFontMemResourceEx
        // 从内存注册），不需要再往暂存分区放字体文件：之前那份文件因为
        // startnet.cmd 的拷贝清单里没有它，从没到达过 PE 界面所在的 RAM 盘，
        // 导致汉字全部显示为方块。
        _log.Info("部署脚本已写入暂存分区。");
    }

    /// <summary>
    /// 把自动运行脚本注入 boot.wim，使第三方 PE（雷电PE 等）启动后也无需人工干预。
    /// CTPrep 自建的 PE 已内置同一份脚本，注入只是覆盖为相同内容。
    /// </summary>
    public async Task InjectAutoRunAsync(string bootWimPath, CancellationToken ct = default)
    {
        var mountDir = Path.Combine(Path.GetTempPath(), "ctprep-wimmount");
        _log.Info("正在把自动部署脚本注入 PE 映像（可能需要 1-2 分钟）...");

        try
        {
            // 源 PE 文件若带只读属性，File.Copy 会一起继承过来，DISM 挂载就会报 0xc1510111（无权修改此映像）
            ClearReadOnly(bootWimPath);

            // 上一次崩溃可能留下已注册的挂载点，先清理再挂载，否则会报「已挂载到其他目录」
            await _dism.CleanupMountAsync(mountDir, ct).ConfigureAwait(false);
            await _dism.MountWimAsync(bootWimPath, 1, mountDir, ct).ConfigureAwait(false);

            foreach (var tool in new[] { "dism.exe", "bcdboot.exe", "diskpart.exe", "wpeutil.exe", "winpeshl.exe" })
            {
                if (!File.Exists(Path.Combine(mountDir, "Windows", "System32", tool)))
                    throw new InvalidOperationException($"PE 映像缺少必要组件 {tool}，请换用完整的 x64 WinPE。已停止准备，不会安排进入此 PE。");
            }

            var winpeshl = Path.Combine(mountDir, "Windows", "System32", "winpeshl.exe");
            var hasWinpeshl = File.Exists(winpeshl);
            if (!hasWinpeshl)
            {
                _log.Warn("该 PE 映像里没有 winpeshl.exe，无法把启动入口交回它；" +
                          "重启后请手动执行 X:\\Windows\\System32\\startnet.cmd 开始部署。");
            }

            var target = Path.Combine(mountDir, "Windows", "System32", "startnet.cmd");
            var content = ReadEmbeddedText("Assets/pe/startnet.cmd");
            await File.WriteAllTextAsync(target, content, Encoding.ASCII, ct).ConfigureAwait(false);
            _log.Info($"已写入 {target}");

            DisableWinPeshlIni(mountDir);
            await RestoreWinPeshlShellAsync(mountDir, hasWinpeshl, ct).ConfigureAwait(false);

            await _dism.UnmountWimAsync(mountDir, commit: true, ct).ConfigureAwait(false);
            _log.Info("PE 映像注入完成。");
        }
        catch
        {
            try
            {
                await _dism.UnmountWimAsync(mountDir, commit: false, ct).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    /// <summary>
    /// WinPE 只有在「System32 下不存在 winpeshl.ini」时才会自动执行 startnet.cmd。
    /// 第三方 PE（雷电PE）通常自带 winpeshl.ini 去拉起自己的外壳，所以这里把它改名备份，
    /// 让我们的 startnet.cmd 重新成为启动入口。
    /// </summary>
    private void DisableWinPeshlIni(string mountDir)
    {
        var iniPath = Path.Combine(mountDir, "Windows", "System32", "winpeshl.ini");
        if (!File.Exists(iniPath))
        {
            _log.Info("PE 内没有 winpeshl.ini，startnet.cmd 将按 WinPE 默认流程自动执行。");
            return;
        }

        var backup = iniPath + ".ctprep.bak";
        try
        {
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(iniPath, backup);
            _log.Info($"该 PE 自带 winpeshl.ini，已改名备份为 {Path.GetFileName(backup)}，改由 startnet.cmd 接管启动。");
        }
        catch (Exception ex)
        {
            _log.Warn($"禁用 winpeshl.ini 失败（将依赖注册表项兜底）：{ex.Message}");
        }
    }

    /// <summary>
    /// 一部分第三方 PE 会直接改写 SYSTEM 注册表的 Setup\CmdLine，用 PECMD 之类的外壳顶替
    /// winpeshl.exe —— 这种情况下 winpeshl.ini 与 startnet.cmd 会被完全跳过。
    /// 这里在离线注册表里把它改回 winpeshl.exe，保证 startnet.cmd 一定被执行。
    /// </summary>
    private async Task RestoreWinPeshlShellAsync(string mountDir, bool hasWinpeshl, CancellationToken ct)
    {
        var hive = Path.Combine(mountDir, "Windows", "System32", "config", "SYSTEM");
        if (!File.Exists(hive))
        {
            return;
        }

        const string key = @"HKLM\CTPrepOfflinePe";
        var load = await _runner.RunAsync("reg.exe", $"load \"{key}\" \"{hive}\"", null, ct).ConfigureAwait(false);
        if (!load.Success)
        {
            _log.Warn($"离线注册表加载失败，跳过外壳检查：{load.Combined.Trim()}");
            return;
        }

        try
        {
            var query = await _runner
                .RunAsync("reg.exe", $"query \"{key}\\Setup\" /v CmdLine", null, ct)
                .ConfigureAwait(false);

            var cmdLine = ParseRegStringValue(query.StdOut);
            if (cmdLine is null)
            {
                _log.Info("该 PE 注册表未自定义 Setup\\CmdLine，按 WinPE 默认流程启动。");
                return;
            }

            if (cmdLine.Contains("winpeshl", StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"该 PE 的 Setup\\CmdLine 已指向 winpeshl.exe：{cmdLine}");
                return;
            }

            // 映像里没有 winpeshl.exe 时不能改：改了会连 PE 原来的外壳也一起失去，直接黑屏
            if (!hasWinpeshl)
            {
                _log.Warn($"该 PE 的 Setup\\CmdLine = {cmdLine}，但映像里没有 winpeshl.exe，保持原样不修改。");
                return;
            }

            _log.Warn($"检测到第三方 PE 外壳：Setup\\CmdLine = {cmdLine}，已改回 winpeshl.exe。");
            var add = await _runner
                .RunAsync("reg.exe", $"add \"{key}\\Setup\" /v CmdLine /t REG_SZ /d winpeshl.exe /f", null, ct)
                .ConfigureAwait(false);
            if (!add.Success)
            {
                _log.Warn($"改写 Setup\\CmdLine 失败：{add.Combined.Trim()}");
            }
        }
        finally
        {
            var unload = await _runner.RunAsync("reg.exe", $"unload \"{key}\"", null, ct).ConfigureAwait(false);
            if (!unload.Success)
            {
                _log.Warn($"离线注册表卸载失败（可忽略）：{unload.Combined.Trim()}");
            }
        }
    }

    /// <summary>从 reg.exe 的输出里取出 REG_SZ / REG_EXPAND_SZ 的值，没有则返回 null。</summary>
    private static string? ParseRegStringValue(string stdout)
    {
        foreach (var line in stdout.Split('\n'))
        {
            var text = line.Trim();
            var marker = text.IndexOf("REG_SZ", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                marker = text.IndexOf("REG_EXPAND_SZ", StringComparison.OrdinalIgnoreCase);
            }

            if (marker < 0)
            {
                continue;
            }

            var typeEnd = text.IndexOf(' ', marker);
            return typeEnd < 0 ? string.Empty : text[(typeEnd + 1)..].Trim();
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 脚本渲染
    // ------------------------------------------------------------------

    private string RenderDeployCmd(DeployOptions o, string imageFileName)
    {
        var bcdStore = GetBcdStorePath(o);
        var bcdCleanup = bcdStore is null
            ? "rem nothing to clean up: the boot store is rebuilt with the target partition"
            : string.Join(Environment.NewLine, new[]
            {
                $"bcdedit /store \"{bcdStore}\" /delete {o.PeBootEntryGuid} /f >>\"%LOG%\" 2>&1",
                // 私有设备选项对象是本次注册时新建的，一并清掉，避免 BCD 里累积无用对象
                string.IsNullOrEmpty(o.PeDeviceOptionsGuid)
                    ? "rem the ramdisk device options object is shared ({ramdiskoptions}) and is kept"
                    : $"bcdedit /store \"{bcdStore}\" /delete {o.PeDeviceOptionsGuid} /f >>\"%LOG%\" 2>&1",
            });

        // 整盘重建会新建 BIOS 系统分区，不能依据重建前的分区号决定 /s。
        var bootLetter = o.Firmware == FirmwareType.Uefi ? EspLetter
            : o.PartitionScheme == PartitionScheme.WipeDiskMbr || o.SystemReservedPartitionNumber > 0
                ? BiosSystemLetter : TargetLetter;
        var bcdboot = $"\"%SystemRoot%\\System32\\bcdboot.exe\" {TargetLetter}:\\Windows /s {bootLetter}: /f {(o.Firmware == FirmwareType.Uefi ? "UEFI" : "BIOS")} /v";

        var mode = o.InstallMode == InstallMode.Clean ? "clean install" : "keep files";

        var keepFilesBlock = o.InstallMode == InstallMode.KeepFiles
            ? string.Join(Environment.NewLine, new[]
            {
                "call :log \"      removing the old system directories, keeping user files\"",
                $"if exist \"%TARGET%\\Windows\" rd /s /q \"%TARGET%\\Windows\" >>\"%LOG%\" 2>&1",
                $"if exist \"%TARGET%\\Program Files\" rd /s /q \"%TARGET%\\Program Files\" >>\"%LOG%\" 2>&1",
                $"if exist \"%TARGET%\\Program Files (x86)\" rd /s /q \"%TARGET%\\Program Files (x86)\" >>\"%LOG%\" 2>&1",
                $"if exist \"%TARGET%\\ProgramData\" rd /s /q \"%TARGET%\\ProgramData\" >>\"%LOG%\" 2>&1",
                "del /f /q /a \"%TARGET%\\hiberfil.sys\" >nul 2>&1",
                "del /f /q /a \"%TARGET%\\pagefile.sys\" >nul 2>&1",
                "del /f /q /a \"%TARGET%\\swapfile.sys\" >nul 2>&1",
            })
            : "rem clean install: the target partition was formatted, nothing to clean up";

        var cleanupCall = o.CleanupStagingPartition
            ? "call :cleanup_staging"
            : "rem the staging partition is a reused volume and is kept";

        var template = ReadEmbeddedText("Assets/pe/deploy.cmd.tpl");
        return template
            .Replace("{{IMAGE_FILE}}", imageFileName)
            .Replace("{{IMAGE_INDEX}}", o.ImageIndex.ToString())
            .Replace("{{TARGET_LETTER}}", TargetLetter.ToString())
            .Replace("{{BOOT_LETTER}}", bootLetter.ToString())
            .Replace("{{INSTALL_MODE}}", mode)
            .Replace("{{KEEP_FILES_BLOCK}}", keepFilesBlock)
            .Replace("{{DEFENDER_REMOVE}}", RenderDefenderRemoval(o))
            .Replace("{{BOOT_CLEANUP_BLOCK}}", RenderBootCleanupBlock(o))
            .Replace("{{BCDBOOT}}", bcdboot)
            .Replace("{{BCD_CLEANUP}}", bcdCleanup)
            .Replace("{{OLD_BOOT_CLEANUP}}", RenderOldBootCleanup(o))
            .Replace("{{CLEANUP_STAGING_CALL}}", cleanupCall)
            .Replace("{{CLEANUP_EXTEND_LINES}}", RenderCleanupExtendLines(o))
            .Replace("{{SHUTDOWN_OR_REBOOT}}", o.ShutdownAfterDeploy ? "\"%SystemRoot%\\System32\\wpeutil.exe\" shutdown" : "\"%SystemRoot%\\System32\\wpeutil.exe\" reboot");
    }

    /// <summary>
    /// 从刚铺好、还没启动过的映像里离线卸载 Windows Defender 功能。
    /// 只有在这个时机做才可靠：系统启动后篡改防护（tamper protection）会把
    /// 「仅靠策略禁用」的 Defender 自动改回来，而功能被卸载后它根本不存在。
    /// 卸载失败不阻断部署——SetupComplete 里的策略禁用仍然生效，只是可能被系统改回去。
    /// </summary>
    private static string RenderDefenderRemoval(DeployOptions o)
    {
        if (!o.DisableDefender)
        {
            return string.Empty;
        }

        return "rem ---- Remove the Windows Defender feature (offline, before the first boot) ----" + "\n" +
               "\"%DISM%\" /English /Image:%TARGET%\\ /Disable-Feature /FeatureName:Windows-Defender >>\"%LOG%\" 2>&1" + "\n" +
               "if errorlevel 1 (" + "\n" +
               "    call :log \"!!!!! WARNING: the Windows Defender feature could not be removed; the policy-based disable still applies\"" + "\n" +
               ") else (" + "\n" +
               "    call :log \"      Windows Defender feature removed from the applied image\"" + "\n" +
               ")";
    }

    /// <summary>
    /// BIOS 且没有独立系统保留分区时，引导文件（bootmgr / BCD）就在目标分区根目录里。
    /// 「保留文件」模式不会格式化目标分区，所以勾选了「格式化引导分区」时必须手动清掉旧引导文件，
    /// 否则 bcdboot 会往旧引导库里追加条目，首次启动出现多余的旧启动项。
    /// </summary>
    private static string RenderBootCleanupBlock(DeployOptions o)
    {
        if (o.Firmware == FirmwareType.Bios &&
            o.PartitionScheme == PartitionScheme.KeepExisting &&
            o.SystemReservedPartitionNumber == 0 &&
            o.FormatBootPartition &&
            o.InstallMode == InstallMode.KeepFiles)
        {
            return string.Join(Environment.NewLine, new[]
            {
                "call :log \"      clearing the old boot files on the target volume\"",
                "del /f /q /a \"%TARGET%\\bootmgr\" >nul 2>&1",
                "del /f /q /a \"%TARGET%\\BOOTNXT\" >nul 2>&1",
                "if exist \"%TARGET%\\Boot\" rd /s /q \"%TARGET%\\Boot\" >>\"%LOG%\" 2>&1",
            });
        }

        return "rem no extra boot file cleanup is needed for this configuration";
    }

    /// <summary>
    /// 跨盘安装时，PE 引导项写在「当前系统盘」的引导库里；目标磁盘被整盘重建后，
    /// 那份旧引导库还在。这里给它临时分配一个盘符并删除本次 PE 引导项，
    /// 避免以后从旧磁盘启动时菜单里残留一个进不去的 CTPrep 项。
    /// </summary>
    private static string RenderOldBootCleanup(DeployOptions o)
    {
        if (o.SourceDiskNumber < 0 || o.SourceDiskNumber == o.TargetDiskNumber)
        {
            return "rem the previous system disk is the target disk; there is no separate old boot store";
        }

        var bootPartition = o.Firmware == FirmwareType.Uefi
            ? o.SourceEspPartitionNumber
            : o.SourceSystemReservedPartitionNumber;
        if (bootPartition <= 0)
        {
            return string.Join(Environment.NewLine, new[]
            {
                "rem the old boot store location on the previous system disk is unknown;",
                "rem the stale one-time PE entry could not be removed (harmless: it never boots again)",
            });
        }

        var bcdPath = o.Firmware == FirmwareType.Uefi ? "\\EFI\\Microsoft\\Boot\\BCD" : "\\Boot\\BCD";
        var devOpts = string.IsNullOrEmpty(o.PeDeviceOptionsGuid)
            ? "rem the ramdisk device options object is shared ({ramdiskoptions}) and is kept"
            : $"bcdedit /store \"%OLDBOOT_LETTER%:{bcdPath}\" /delete {o.PeDeviceOptionsGuid} /f >>\"%LOG%\" 2>&1";
        // GUID 为空时不能渲染出半截 bcdedit 命令：那会污染日志并让人误判为清理失败
        var delEntry = string.IsNullOrEmpty(o.PeBootEntryGuid)
            ? "rem the PE boot entry identifier is unknown; nothing to delete"
            : $"if exist \"%OLDBOOT_LETTER%:{bcdPath}\" bcdedit /store \"%OLDBOOT_LETTER%:{bcdPath}\" /delete {o.PeBootEntryGuid} /f >>\"%LOG%\" 2>&1";

        return string.Join(Environment.NewLine, new[]
        {
            "rem remove the one-time PE entry from the boot store of the previous system disk",
            "set \"OLDBOOT_LETTER=\"",
            "for %%L in (R Q V U Y Z) do if not defined OLDBOOT_LETTER if not exist \"%%L:\\\" set \"OLDBOOT_LETTER=%%L\"",
            "if not defined OLDBOOT_LETTER (",
            "    call :log \"      no free drive letter to reach the old boot store; cleanup skipped\"",
            ") else (",
            $"> \"%WORK%\\diskpart-oldboot.txt\" echo select disk {o.SourceDiskNumber}",
            $">>\"%WORK%\\diskpart-oldboot.txt\" echo select partition {bootPartition}",
            ">>\"%WORK%\\diskpart-oldboot.txt\" echo assign letter=%OLDBOOT_LETTER%",
            ">>\"%WORK%\\diskpart-oldboot.txt\" echo exit",
            "diskpart /s \"%WORK%\\diskpart-oldboot.txt\" >>\"%LOG%\" 2>&1",
            delEntry,
            devOpts,
            ")",
        });
    }

    /// <summary>
    /// 暂存分区删除后如何回收空间：
    /// 与目标分区同盘时（例如单盘机器压缩系统卷）扩展目标卷；
    /// 跨盘时扩展被压缩的宿主分区（目标卷在另一块磁盘上，扩展它没有意义）。
    /// </summary>
    private static string RenderCleanupExtendLines(DeployOptions o)
    {
        if (o.StagingDiskNumber < 0 || o.StagingDiskNumber == o.TargetDiskNumber)
        {
            return string.Join(Environment.NewLine, new[]
            {
                $">>\"%WORK%\\diskpart-cleanup.txt\" echo select volume={TargetLetter}",
                ">>\"%WORK%\\diskpart-cleanup.txt\" echo extend",
            });
        }

        if (o.StagingHostDiskNumber >= 0 && o.StagingHostPartitionNumber > 0)
        {
            return string.Join(Environment.NewLine, new[]
            {
                $">>\"%WORK%\\diskpart-cleanup.txt\" echo select disk {o.StagingHostDiskNumber}",
                $">>\"%WORK%\\diskpart-cleanup.txt\" echo select partition {o.StagingHostPartitionNumber}",
                ">>\"%WORK%\\diskpart-cleanup.txt\" echo extend",
            });
        }

        return "rem the staging volume is on another disk and its original partition is unknown; the freed space stays unallocated";
    }

    /// <summary>PE 侧脚本中引用的系统映像文件名；非 ASCII 名统一改成 install.wim，避免写出成 '?'。</summary>
    private static string GetDeployImageFileName(string installImagePath)
    {
        var name = Path.GetFileName(installImagePath);
        return name.All(c => c < 128) ? name : "install.wim";
    }

    private static string RenderDiskPartTarget(DeployOptions o)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"rem CTPrep target partition script - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"select disk {o.TargetDiskNumber}");

        if (o.PartitionScheme == PartitionScheme.WipeDiskGpt || o.PartitionScheme == PartitionScheme.WipeDiskMbr)
        {
            var wantGpt = o.PartitionScheme == PartitionScheme.WipeDiskGpt;

            // 暂存分区就在目标磁盘上时不能 clean：install.wim / deploy.cmd 还在上面，
            // 清掉之后后面的 dism /Apply-Image 必然失败。改为逐个删除其它分区。
            var others = o.TargetDiskPartitionNumbers
                .Where(p => p != o.StagingPartitionNumber)
                // 从高分区号往低分区号删，避免删除较低分区后 Windows 重新编号，
                // 使后续命令选中错误分区或误删暂存分区。
                .OrderByDescending(p => p)
                .ToList();
            var preserveStaging = o.StagingDiskNumber >= 0 &&
                                   o.StagingDiskNumber == o.TargetDiskNumber &&
                                   o.StagingPartitionNumber > 0;

            if (preserveStaging)
            {
                sb.AppendLine("rem keeping the staging partition that holds the deployment payload");
                foreach (var pn in others)
                {
                    sb.AppendLine($"select partition {pn}");
                    sb.AppendLine("delete partition override");
                }

                sb.AppendLine($"select disk {o.TargetDiskNumber}");
            }
            else
            {
                sb.AppendLine("clean");
                // 分区表类型已经对了就不转换：对 GPT 盘执行 convert gpt 会报「磁盘已是 GPT」
                if (string.IsNullOrEmpty(o.TargetDiskStyle) ||
                    !o.TargetDiskStyle.Equals(wantGpt ? "GPT" : "MBR", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine(wantGpt ? "convert gpt" : "convert mbr");
                }
                else
                {
                    sb.AppendLine($"rem the disk is already {o.TargetDiskStyle}, no conversion needed");
                }
            }

            if (wantGpt)
            {
                sb.AppendLine("create partition efi size=300");
                sb.AppendLine("format quick fs=fat32 label=\"System\"");
                sb.AppendLine($"assign letter={EspLetter}");
                sb.AppendLine("create partition msr size=16");
                sb.AppendLine("create partition primary");
                sb.AppendLine($"format quick fs=ntfs label=\"{o.TargetLabel}\"");
                sb.AppendLine($"assign letter={TargetLetter}");
            }
            else
            {
                sb.AppendLine("create partition primary size=500");
                sb.AppendLine("format quick fs=ntfs label=\"System\"");
                sb.AppendLine("active");
                sb.AppendLine($"assign letter={BiosSystemLetter}");
                sb.AppendLine("create partition primary");
                sb.AppendLine($"format quick fs=ntfs label=\"{o.TargetLabel}\"");
                sb.AppendLine($"assign letter={TargetLetter}");
            }
        }
        else
        {
            // 保留同一磁盘上的其它分区，只处理目标分区
            sb.AppendLine($"select partition {o.TargetPartitionNumber}");
            sb.AppendLine("remove noerr");
            if (o.InstallMode == InstallMode.Clean)
            {
                sb.AppendLine($"format quick fs=ntfs label=\"{o.TargetLabel}\"");
            }

            sb.AppendLine($"assign letter={TargetLetter}");

            if (o.Firmware == FirmwareType.Uefi && o.EspPartitionNumber > 0)
            {
                sb.AppendLine($"select disk {o.TargetDiskNumber}");
                sb.AppendLine($"select partition {o.EspPartitionNumber}");
                sb.AppendLine("remove noerr");
                AppendBootPartitionFormat(sb, o, "fat32");
                sb.AppendLine($"assign letter={EspLetter}");
            }
            else if (o.Firmware == FirmwareType.Bios && o.SystemReservedPartitionNumber > 0)
            {
                sb.AppendLine($"select disk {o.TargetDiskNumber}");
                sb.AppendLine($"select partition {o.SystemReservedPartitionNumber}");
                sb.AppendLine("remove noerr");
                AppendBootPartitionFormat(sb, o, "ntfs");
                sb.AppendLine($"assign letter={BiosSystemLetter}");
            }
        }

        sb.AppendLine("exit");
        return sb.ToString();
    }

    /// <summary>
    /// 按「格式化引导分区」选项决定是否重置引导分区。
    /// 格式化能清掉旧系统的引导项（得到干净的启动环境）；不格式化则保留原内容，
    /// 安装中途失败时旧系统仍然可引导。
    /// </summary>
    private static void AppendBootPartitionFormat(StringBuilder sb, DeployOptions o, string fileSystem)
    {
        if (o.FormatBootPartition)
        {
            sb.AppendLine($"format quick fs={fileSystem} label=\"System\"");
        }
        else
        {
            sb.AppendLine("rem the boot partition is kept as is (format boot partition is disabled)");
        }
    }

    private static string RenderUnattend(DeployOptions o)
    {
        if (!o.Unattended)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
                   "<!-- Unattended mode is disabled; this file is an empty placeholder. -->" + Environment.NewLine +
                   "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"></unattend>";
        }

        var computerName = string.IsNullOrWhiteSpace(o.ComputerName)
            ? $"CT-{Random.Shared.Next(100000, 999999)}"
            : o.ComputerName;

        // 架构必须与目标映像一致：x86 映像配 amd64 组件会让安装程序拒绝整份文件
        var arch = NormalizeArchitecture(o.ImageArchitecture);
        var majorText = o.WindowsMajorVersion == 0 ? "unknown" : o.WindowsMajorVersion.ToString();

        var userName = string.IsNullOrWhiteSpace(o.UserName) ? "CTUser" : o.UserName;

        var template = ReadEmbeddedText("Assets/pe/unattend.xml.tpl");
        var passwordBlock = string.IsNullOrEmpty(o.Password)
            ? "            <!-- no password was set: the account signs in without one -->"
            : $"            <Password>{Environment.NewLine}" +
              $"              <Value>{Escape(o.Password)}</Value>{Environment.NewLine}" +
              $"              <PlainText>true</PlainText>{Environment.NewLine}" +
              "            </Password>";

        // 自动登录：第一次进桌面不再要求输入密码或选账户。
        // 密码为空时省略 <Password>，否则空值节点会让部分版本拒绝登录。
        var autoLogonPassword = string.IsNullOrEmpty(o.Password)
            ? string.Empty
            : $"          <Password>{Environment.NewLine}" +
              $"            <Value>{Escape(o.Password)}</Value>{Environment.NewLine}" +
              $"            <PlainText>true</PlainText>{Environment.NewLine}" +
              "          </Password>";

        var autoLogonBlock =
            "      <AutoLogon>" + Environment.NewLine +
            "        <Enabled>true</Enabled>" + Environment.NewLine +
            "        <LogonCount>1</LogonCount>" + Environment.NewLine +
            $"        <Username>{Escape(userName)}</Username>" + Environment.NewLine +
            autoLogonPassword +
            "      </AutoLogon>";

        return template
            .Replace("{{COMPUTER_NAME}}", Escape(computerName))
            .Replace("{{USER_NAME}}", Escape(userName))
            .Replace("{{PASSWORD_BLOCK}}", passwordBlock)
            .Replace("{{AUTOLOGON_BLOCK}}", autoLogonBlock)
            .Replace("{{OOBE_BLOCK}}", RenderOobeBlock(o.WindowsMajorVersion))
            .Replace("{{TIME_ZONE}}", Escape(o.TimeZone))
            .Replace("{{BYPASS_NRO}}", o.BypassNetworkRequirement ? "1" : "0")
            .Replace("{{DEVICE_ENCRYPTION_BLOCK}}", RenderDeviceEncryptionBlock(o))
            .Replace("{{MAJOR}}", majorText)
            .Replace("{{ARCH}}", arch);
    }

    /// <summary>
    /// 禁用设备加密的 specialize 命令。Windows 11 在满足条件的新机器上会自动开启「设备加密」
    /// （BitLocker 的自动变体，无需用户操作就加密整盘），带来恢复密钥与数据风险。
    /// 写入 PreventDeviceEncryption 策略后新系统不会再自动加密。
    /// specialize 阶段早于设备加密的评估，放这里最可靠；OOBE 之后再写往往为时已晚。
    /// </summary>
    private static string RenderDeviceEncryptionBlock(DeployOptions o)
    {
        if (!o.DisableDeviceEncryption)
        {
            return string.Empty;
        }

        return "        <RunSynchronousCommand wcm:action=\"add\">" + Environment.NewLine +
               "          <Order>2</Order>" + Environment.NewLine +
               "          <Description>Prevent device encryption</Description>" + Environment.NewLine +
               "          <Path>reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker\" /v PreventDeviceEncryption /t REG_DWORD /d 1 /f</Path>" + Environment.NewLine +
               "        </RunSynchronousCommand>";
    }

    /// <summary>
    /// 按目标 Windows 版本生成 OOBE 节点。
    /// 只放各版本都认识的节点，版本专属节点按主版本追加 —— 应答文件里出现目标系统
    /// 不认识的节点会导致 Windows 直接拒绝整个文件。
    /// </summary>
    private static string RenderOobeBlock(int majorVersion)
    {
        var sb = new StringBuilder();

        // 通用节点：Vista 起所有版本都有；Windows 7 的官方自动化文档
        // （Automate Windows Welcome，dd744547）逐页列出了其中每一个。
        sb.AppendLine("        <HideEULAPage>true</HideEULAPage>");

        // HideOEMRegistrationScreen / HideOnlineAccountScreens 是 Windows 8 新增的设置
        // （见官方文「Changed Answer File Settings from Windows 7」）。Windows 7 的组件
        // 里没有它们，写进去会被安装程序拒收 —— 实测 Win7 SP1 的 setuperr.log：
        //   "Setting is not defined in this component"（指向 OOBE/HideOEMRegistrationScreen）。
        // 版本未知（0）时同样不写：漏写只是少隐藏一页，写错是整份文件被拒。
        if (majorVersion >= 8)
        {
            sb.AppendLine("        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>");
            sb.AppendLine("        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>");
        }

        sb.AppendLine("        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>");
        sb.AppendLine("        <NetworkLocation>Work</NetworkLocation>");
        sb.AppendLine("        <ProtectYourPC>3</ProtectYourPC>");

        // 不再写 SkipMachineOOBE / SkipUserOOBE：它们是已弃用的设置，且 Windows 7 的
        // 官方自动化文档采用的是「逐页配置即跳过」机制 —— 上面的设置已经覆盖
        // Windows Welcome 的全部页面（语言/许可/账户/计算机名/保护/时间/位置/无线），
        // 不需要再冒险使用文档之外的节点。

        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>把 dism 报告的架构名规整成应答文件里的 processorArchitecture 取值；未知按 amd64。</summary>
    private static string NormalizeArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return "amd64";
        }

        return architecture.Trim().ToLowerInvariant() switch
        {
            "x86" or "i386" or "i686" => "x86",
            "arm64" or "aarch64" => "arm64",
            _ => "amd64", // x64 / amd64（以及任何未知写法）都按 64 位处理
        };
    }

    private static string RenderSetupComplete(DeployOptions o) =>
        ReadEmbeddedText("Assets/pe/SetupComplete.cmd.tpl")
            .Replace("{{DEFENDER_BLOCK}}", RenderDefenderBlock(o));

    /// <summary>
    /// 「禁用 Windows Defender」的可选段落。
    /// 首次进入系统前写策略注册表并停掉相关服务；较新的 Windows 可能因篡改防护自动恢复，
    /// 因此所有命令都只记日志、不因失败中断收尾流程。
    /// </summary>
    private static string RenderDefenderBlock(DeployOptions o)
    {
        if (!o.DisableDefender)
        {
            return "echo [4/5] Windows Defender is left enabled >>\"%LOG%\"";
        }

        return string.Join(Environment.NewLine, new[]
        {
            "echo [4/5] Disabling Windows Defender >>\"%LOG%\"",
            "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\" /v DisableAntiSpyware /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\" /v DisableAntiVirus /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection\" /v DisableRealtimeMonitoring /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection\" /v DisableBehaviorMonitoring /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection\" /v DisableOnAccessProtection /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection\" /v DisableScanOnRealtimeEnable /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection\" /v DisableIOAVProtection /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows Defender\" /v DisableAntiSpyware /t REG_DWORD /d 1 /f >>\"%LOG%\" 2>&1",
            "rem stop the services as well; newer builds may restore them because of tamper protection",
            "sc config WinDefend start= disabled >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\WinDefend\" /v Start /t REG_DWORD /d 4 /f >>\"%LOG%\" 2>&1",
            "sc config WdNisSvc start= disabled >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\WdNisSvc\" /v Start /t REG_DWORD /d 4 /f >>\"%LOG%\" 2>&1",
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\WdNisDrv\" /v Start /t REG_DWORD /d 4 /f >>\"%LOG%\" 2>&1",
        });
    }



    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>PE 部署时使用的 BCD 库路径（PE 内的路径）。返回 null 表示无需清理。</summary>
    private static string? GetBcdStorePath(DeployOptions o)
    {
        if (string.IsNullOrEmpty(o.PeBootEntryGuid))
        {
            return null;
        }

        // 跨盘安装：PE 引导项在「旧系统盘」的引导库里（由 OLD_BOOT_CLEANUP 处理），
        // 目标磁盘的引导库是全新写入的，没有需要清理的对象。
        if (o.SourceDiskNumber >= 0 && o.SourceDiskNumber != o.TargetDiskNumber)
        {
            return null;
        }

        if (o.Firmware == FirmwareType.Uefi)
        {
            return $"{EspLetter}:\\EFI\\Microsoft\\Boot\\BCD";
        }

        // 整盘重建：旧引导库随整盘清空一起消失，新库由 bcdboot 重新写入，无需清理。
        if (o.PartitionScheme != PartitionScheme.KeepExisting)
        {
            return null;
        }

        // BIOS：引导库要么在独立系统保留分区上，要么就在系统/目标分区根目录里。
        return o.SystemReservedPartitionNumber > 0
            ? $"{BiosSystemLetter}:\\Boot\\BCD"
            : $"{TargetLetter}:\\Boot\\BCD";
    }

    private static string ResolveBootSdi(string bootWimPath, string installImagePath)
    {
        // 优先使用与 PE 配套的 boot.sdi
        var candidates = new List<string>();

        var isoDir = Path.GetDirectoryName(bootWimPath);
        if (isoDir is not null)
        {
            candidates.Add(Path.Combine(isoDir, "boot.sdi"));

            // 雷电PE 等第三方 PE 的 sdi 不叫 boot.sdi（雷电PE 叫 Dream-M.sdi），所以再兜一层
            if (Directory.Exists(isoDir))
            {
                candidates.AddRange(Directory
                    .GetFiles(isoDir, "*.sdi")
                    .Where(p => !Path.GetFileName(p).Equals("boot.sdi", StringComparison.OrdinalIgnoreCase)));
            }
        }

        var runtimeDir = Path.GetDirectoryName(installImagePath);
        if (runtimeDir is not null)
        {
            candidates.Add(Path.Combine(runtimeDir, "boot.sdi"));
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        candidates.Add(Path.Combine(systemRoot, "Boot", "DVD", "PCAT", "boot.sdi"));
        candidates.Add(Path.Combine(systemRoot, "Boot", "DVD", "EFI", "boot.sdi"));

        var hit = candidates.FirstOrDefault(File.Exists);
        if (hit is null)
        {
            throw new FileNotFoundException(
                "找不到 boot.sdi（ramdisk 引导必需）。请把 Windows ISO 中的 \\boot\\boot.sdi 放到 runtime 目录，或确认系统盘存在 Windows\\Boot\\DVD\\PCAT\\boot.sdi。");
        }

        return hit;
    }

    private static void CopyFileIfDifferent(string from, string to)
    {
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    /// <summary>去掉文件的只读属性：DISM 挂载只读的 WIM 会报 0xc1510111（无权装载或修改此映像）。</summary>
    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(from, to));
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to), overwrite: true);
        }
    }

    /// <summary>读取嵌入资源文本。</summary>
    private static string ReadEmbeddedText(string relativePath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(relativePath.Replace('/', '.'), StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"缺少嵌入资源：{relativePath}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

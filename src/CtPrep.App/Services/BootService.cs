using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>创建的 PE 引导项。</summary>
/// <param name="Guid">osloader 引导项标识；PE 侧据此删除本次注册的引导项。</param>
/// <param name="DeviceOptionsId">
/// ramdisk 设备选项对象的标识：正常情况下是本次创建的私有 GUID；
/// 只有在私有对象建不出来时才回退成众所周知的 <c>{ramdiskoptions}</c>。
/// </param>
/// <param name="DeviceOptionsIsPrivate">
/// 设备选项对象是否由本程序创建。为 false 时说明用的是共享的 <c>{ramdiskoptions}</c>，
/// 部署结束后不能删除它（它是 WinRE 等其它引导项也可能在用的公共配置）。
/// </param>
public sealed record PeBootEntry(string Guid, string DeviceOptionsId, bool DeviceOptionsIsPrivate);

/// <summary>引导项管理：创建/删除 PE 的 ramdisk 引导项。</summary>
/// <remarks>
/// <para>宿主侧一律操作「实时 BCD 库」（不带 /store）：</para>
/// UEFI 机器上实时库就是 EFI 分区里的 BCD，BIOS 机器上就是活动系统分区里的 BCD，两者都能被固件读取。
/// PE 侧删除引导项时才需要带 /store，路径由 <see cref="PePayloadService"/> 渲染进 deploy.cmd。
/// <para>
/// 设备选项对象一律用「临时创建的私有 GUID」，而不是众所周知的 <c>{ramdiskoptions}</c>：
/// </para>
/// <list type="number">
/// <item><c>{ramdiskoptions}</c> 是别名，很多精简/全新安装的系统里根本没有这个对象，
/// 直接 <c>bcdedit /set {ramdiskoptions} ...</c> 会报「尝试引用指定项时出错。系统找不到指定的文件。」；</item>
/// <item>它同时被 WinRE 等引导项共享，覆盖它会连带破坏系统恢复环境的 ramdisk 配置。</item>
/// </list>
/// </remarks>
public sealed class BootService
{
    /// <summary>众所周知的 RAM 磁盘选项别名；仅作私有对象不可用时的兜底。</summary>
    private const string RamdiskOptionsAlias = "{ramdiskoptions}";

    /// <summary>失败时统一附带的可执行排查建议，避免用户只看到一句 bcdedit 原始报错。</summary>
    private const string FailureHint =
        "可排查的方向：" + "\n" +
        "  1) 确认以管理员身份运行本程序（bcdedit 写引导库必须提权）；" +
        "  2) 若引导库被 BitLocker / 安全启动保护，请先暂停保护；" +
        "  3) 管理员命令行执行 bcdedit /enum all，确认引导库本身可读且未损坏。" + "\n" +
        "完整命令与输出已写入 runtime\\logs 下的日志文件。";

    private readonly ProcessRunner _runner;
    private readonly ILogSink _log;

    public BootService(ProcessRunner runner, ILogSink log)
    {
        _runner = runner;
        _log = log;
    }

    /// <summary>创建 PE 的 ramdisk 引导项；载荷写完后再安排下一次启动。</summary>
    /// <param name="stagingRoot">
    /// 暂存目录，例如 <c>E:\CTPREP</c>（暂存分区根目录下的卷标目录）或 <c>E:\</c>。
    /// boot.wim / boot.sdi 的实际位置就是该目录，因此 ramdisk 引用必须带上这一段子路径；
    /// 只写分区根目录会让固件找不到 boot.wim，直接导致「重启后进不去 PE」。
    /// </param>
    public async Task<PeBootEntry> CreateRamdiskEntryAsync(
        string stagingRoot,
        string displayName,
        FirmwareType firmware,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot) || stagingRoot.Length < 2 || stagingRoot[1] != ':')
        {
            throw new InvalidOperationException($"暂存目录路径无效，无法创建 PE 引导项：{stagingRoot}");
        }

        var drive = char.ToUpperInvariant(stagingRoot[0]);
        var sub = stagingRoot.Length > 2 ? stagingRoot[2..].Trim('\\', '/') : string.Empty;
        var dirPart = sub.Length == 0 ? "\\" : $"\\{sub}\\";

        if (stagingRoot.Contains('"') || !Path.IsPathFullyQualified(stagingRoot))
            throw new InvalidOperationException("暂存目录必须是本地磁盘上的绝对路径。");
        foreach (var file in new[] { "boot.wim", "boot.sdi" })
        {
            var path = Path.Combine(stagingRoot, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidOperationException($"PE 引导文件不存在或为空：{path}，已停止注册引导。");
        }
        var device = new StringBuilder(4096);
        if (QueryDosDevice($"{drive}:", device, device.Capacity) == 0)
            throw new InvalidOperationException($"无法解析暂存盘 {drive}: 的设备映射（Win32 错误 {Marshal.GetLastWin32Error()}），该分区可能已离线。请重新准备暂存分区后再试。");
        var nativeDevice = device.ToString();
        if (!Regex.IsMatch(nativeDevice, @"^\\Device\\HarddiskVolume\d+$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException($"暂存盘 {drive}: 不是本地固定磁盘分区（实际映射：{nativeDevice}），固件无法从它引导。请把程序放到本地磁盘上运行，不要使用共享目录、网络盘、SUBST 盘符或映射盘。");
        _log.Info($"暂存卷映射：{drive}: -> {nativeDevice}；引导目录：{stagingRoot}");

        // 先确认实时引导库可写：如果连库都打不开，后面每一条 bcdedit 都只会返回
        // 「尝试引用指定项时出错」这类看不出原因的报错。
        await EnsureStoreReadableAsync(ct).ConfigureAwait(false);

        _log.Info("创建 PE 引导项（ramdisk）...");

        // 显示名会拼进 bcdedit /create /d 与 description，必须先过滤引号与控制字符，防止破坏参数
        displayName = SanitizeDisplayName(displayName);

        var entryGuid = "{" + System.Guid.NewGuid().ToString() + "}";
        var optionsId = string.Empty;
        var optionsIsPrivate = false;
        var entryCreated = false;
        try
        {
            // 1) ramdisk 设备选项对象（私有 GUID，不碰 {ramdiskoptions}）
            (optionsId, optionsIsPrivate) = await EnsureDeviceOptionsObjectAsync(ct).ConfigureAwait(false);

            // 2) 设备选项内容：SDI 所在分区 + SDI 路径
            // 设备一律用「盘符」形式：bcdedit 文档里 device 只接受 BOOT / PARTITION=<盘符> /
            // HD_PARTITION=<盘符> / FILE= / RAMDISK= / VHD= / LOCATE，\Device\HarddiskVolumeN 不是合法取值。
            var partition = $"{drive}:";
            var setDevice = await TryBcdAsync($"/set {optionsId} ramdisksdidevice partition={partition}", ct).ConfigureAwait(false);
            if (!setDevice.Success)
            {
                // hd_partition 与 partition 同义，但会显式关闭自动 VHD 探测，虚拟机里更可靠
                _log.Warn($"ramdisksdidevice partition={partition} 失败，改用 hd_partition={partition} 重试。{Describe(setDevice)}");
                await BcdAsync($"/set {optionsId} ramdisksdidevice hd_partition={partition}", "设置 ramdisk 引导分区", ct).ConfigureAwait(false);
            }
            await BcdAsync($"/set {optionsId} ramdisksdipath \"{dirPart}boot.sdi\"", "设置 ramdisk 引导 SDI 路径", ct).ConfigureAwait(false);

            // 3) osloader 引导项
            await BcdAsync($"/create {entryGuid} /d \"{displayName} PE\" /application osloader", "创建 PE 引导项", ct).ConfigureAwait(false);
            entryCreated = true;
            _log.Info($"PE 引导项：{entryGuid}；设备选项：{optionsId}");

            // UEFI 走 winload.efi，Legacy BIOS 走 winload.exe
            var loaderPath = firmware == FirmwareType.Uefi
                ? "path \\windows\\system32\\boot\\winload.efi"
                : "path \\windows\\system32\\boot\\winload.exe";

            // 以下属性是从 RAM 磁盘启动 WinPE 所必需的；任一失败都不继续重启。
            var requiredSetters = new[]
            {
                $"device \"ramdisk=[{partition}]{dirPart}boot.wim,{optionsId}\"",
                $"osdevice \"ramdisk=[{partition}]{dirPart}boot.wim,{optionsId}\"",
                loaderPath,
                "systemroot \\windows",
                "winpe yes",
            };

            foreach (var setter in requiredSetters)
            {
                await BcdAsync($"/set {entryGuid} {setter}", "写入 PE 引导项必需属性", ct).ConfigureAwait(false);
            }

            // 4) 回读校验：确认引导项真的落地（此前出现过「命令返回成功但对象并不存在」的情况，
            //    不校验就会带着一个进不去的引导项重启）。
            await VerifyEntryAsync(entryGuid, ct).ConfigureAwait(false);

            // 这些只影响诊断、显示或旧硬件兼容性。不同 Windows/虚拟机的 BCD 架构
            // 可能不支持其中一部分，因此失败时记录警告而不阻断 PE 启动。
            var optionalSetters = new[]
            {
                "detecthal yes",
                "locale zh-CN",
                "inherit {bootloadersettings}",
            };

            foreach (var setter in optionalSetters)
            {
                var args = $"/set {entryGuid} {setter}";
                var result = await TryBcdAsync(args, ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    _log.Warn($"可选 BCD 属性未设置，已跳过：bcdedit {args}{Environment.NewLine}{result.Combined}");
                }
            }

            _log.Info("PE 引导项已创建，等待部署载荷写入完成。");
            return new PeBootEntry(entryGuid, optionsId, optionsIsPrivate);
        }
        catch
        {
            if (entryCreated) await DeleteEntryAsync(entryGuid, CancellationToken.None).ConfigureAwait(false);
            if (optionsIsPrivate && optionsId.Length > 0) await DeleteEntryAsync(optionsId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ScheduleOnceAsync(string guid, CancellationToken ct = default)
    {
        await BcdAsync($"/bootsequence {guid}", "安排下次启动进入 PE", ct).ConfigureAwait(false);
        _log.Info("部署载荷已就绪，下次启动进入 PE。");
    }

    /// <summary>删除引导项（回滚用；正常流程由 PE 侧删除）。</summary>
    public async Task DeleteEntryAsync(string guid, CancellationToken ct = default)
    {
        var result = await TryBcdAsync($"/delete {guid} /f", ct).ConfigureAwait(false);
        if (result.Success)
        {
            _log.Info($"已删除引导项 {guid}");
        }
        else
        {
            _log.Warn($"删除引导项 {guid} 失败：{result.Combined}");
        }
    }

    // ------------------------------------------------------------------
    // 内部步骤
    // ------------------------------------------------------------------

    /// <summary>确认 bcdedit 能打开实时引导库；打不开时直接给出可读原因，而不是让后续命令逐个报错。</summary>
    private async Task EnsureStoreReadableAsync(CancellationToken ct)
    {
        var probe = await TryBcdAsync("/enum {bootmgr}", ct).ConfigureAwait(false);
        if (probe.Success)
        {
            return;
        }

        _log.Error($"bcdedit 无法打开实时引导库：{Describe(probe)}");
        throw new InvalidOperationException(
            "无法打开实时 BCD 引导库，已停止注册 PE 引导项。" + Environment.NewLine + FailureHint);
    }

    /// <summary>
    /// 准备 ramdisk 设备选项对象。
    /// 优先创建私有对象（随机 GUID + /device）：不依赖引导库里是否已存在 {ramdiskoptions} 别名，
    /// 也不会覆盖 WinRE 正在使用的公共配置；只有私有对象拿不到时才回退到众所周知别名。
    /// </summary>
    private async Task<(string Id, bool IsPrivate)> EnsureDeviceOptionsObjectAsync(CancellationToken ct)
    {
        var privateId = "{" + System.Guid.NewGuid().ToString() + "}";
        var create = await TryBcdAsync($"/create {privateId} /d \"CTPrep Ramdisk\" /device", ct).ConfigureAwait(false);
        if (create.Success && await ObjectExistsAsync(privateId, ct).ConfigureAwait(false))
        {
            _log.Info($"ramdisk 设备选项对象（私有）：{privateId}");
            return (privateId, true);
        }

        // 创建命令返回成功但对象并不存在，或创建本身失败：都不使用它，改走兜底路径。
        _log.Warn($"私有 ramdisk 设备选项对象不可用，回退到 {RamdiskOptionsAlias}。{Describe(create)}");

        if (!await ObjectExistsAsync(RamdiskOptionsAlias, ct).ConfigureAwait(false))
        {
            var fallbackArgs = $"/create {RamdiskOptionsAlias} /d \"CTPrep Ramdisk\"";
            var fallback = await TryBcdAsync(fallbackArgs, ct).ConfigureAwait(false);
            if (!fallback.Success || !await ObjectExistsAsync(RamdiskOptionsAlias, ct).ConfigureAwait(false))
            {
                _log.Error($"bcdedit {fallbackArgs} 失败：{Describe(fallback)}");
                throw new InvalidOperationException(
                    $"无法在 BCD 中创建 ramdisk 设备选项对象（私有 GUID 与 {RamdiskOptionsAlias} 均不可用）。" + Environment.NewLine +
                    $"{Describe(fallback)}" + Environment.NewLine + FailureHint);
            }
        }

        _log.Warn($"{RamdiskOptionsAlias} 是 WinRE 等引导项共用的配置，本次覆盖了它的 ramdisksdidevice / ramdisksdipath。");
        return (RamdiskOptionsAlias, false);
    }

    /// <summary>回读引导项，确认 ramdisk 设备元素确实写入；失败即中止，避免重启到一个进不去的引导项。</summary>
    private async Task VerifyEntryAsync(string entryGuid, CancellationToken ct)
    {
        var result = await TryBcdAsync($"/enum {entryGuid}", ct).ConfigureAwait(false);
        var text = result.StdOut + Environment.NewLine + result.StdErr;
        if (result.Success && text.Contains("ramdisk", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("winpe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _log.Error($"PE 引导项回读校验未通过：{Describe(result)}");
        throw new InvalidOperationException(
            $"PE 引导项 {entryGuid} 写入后回读校验未通过（退出码 {result.ExitCode}）。" + Environment.NewLine +
            "引导项没有真正带上 ramdisk 设备信息，重启后会进不去 PE，因此已停止本次准备。" + Environment.NewLine +
            result.Combined + Environment.NewLine + FailureHint);
    }

    /// <summary>判断标识符在实时引导库中是否真的存在（只看 bcdedit 的退出码并不可靠）。</summary>
    private async Task<bool> ObjectExistsAsync(string id, CancellationToken ct)
    {
        var result = await TryBcdAsync($"/enum {id}", ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return false;
        }

        var text = result.StdOut + Environment.NewLine + result.StdErr;
        // 已知别名 bcdedit 会直接回显别名；万一回显成底层 GUID，则用元素名判定
        return text.Contains(id, StringComparison.OrdinalIgnoreCase)
            || text.Contains("ramdisksdidevice", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProcessResult> BcdAsync(string args, string what, CancellationToken ct)
    {
        var result = await TryBcdAsync(args, ct).ConfigureAwait(false);
        if (result.Success)
        {
            return result;
        }

        _log.Error($"bcdedit {args} 执行失败（退出码 {result.ExitCode}）：{result.Combined}");
        throw new InvalidOperationException(
            $"bcdedit {args} 执行失败（退出码 {result.ExitCode}）。" + Environment.NewLine +
            result.Combined + Environment.NewLine +
            $"当前步骤：{what}。" + Environment.NewLine + FailureHint);
    }

    private static string Describe(ProcessResult result)
        => $"{result.CommandLine}（退出码 {result.ExitCode}）{Environment.NewLine}{result.Combined}".Trim();

    /// <summary>过滤掉会破坏 bcdedit 参数的字符；结果为空时回退到默认名。</summary>
    private static string SanitizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "CTPrep";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '"' or '\\' or '\'' or ':' or '|' or '<' or '>' or '?' or '*' or '\r' or '\n' or '\t')
            {
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            sb.Append(c);
        }

        var name = sb.ToString().Trim();
        return name.Length == 0 ? "CTPrep" : name;
    }

    private Task<ProcessResult> TryBcdAsync(string args, CancellationToken ct)
        => _runner.RunAsync(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"), args, null, ct);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int capacity);
}

namespace CtPrep.App.Services;

/// <summary>
/// 判断这台机器上有多少「新系统才有的能力」。
///
/// 按能力探测而不是按版本号判断：Windows 7 上有没有某个 cmdlet 并不仅仅取决于版本
/// （还取决于装了哪个 PowerShell 版本、装没装 WMF），而探测本身是最直接的答案。
/// 探测结果会缓存，一次运行只问一遍。
///
/// 目前关心两项：
///   - Storage 模块（Get-Disk / Get-Partition / Resize-Partition ...）：Windows 8 / Server 2012 起才有
///   - Mount-DiskImage：Windows 8 起才有，Windows 7 无法挂载 ISO
/// </summary>
public sealed class PlatformCapabilities
{
    private readonly ProcessRunner _runner;
    private readonly ILogSink _log;

    private bool? _storageModule;
    private bool? _isoMount;

    public PlatformCapabilities(ProcessRunner runner, ILogSink log)
    {
        _runner = runner;
        _log = log;
    }

    /// <summary>PowerShell Storage 模块是否可用（不可用时磁盘探测要改走 WMI）。</summary>
    public async Task<bool> HasStorageModuleAsync(CancellationToken ct = default)
    {
        if (_storageModule is null)
        {
            _storageModule = await HasCommandAsync("Get-Disk", ct).ConfigureAwait(false);
            _log.Info(_storageModule.Value
                ? "磁盘探测：PowerShell Storage 模块可用"
                : "磁盘探测：这台机器没有 Storage 模块（Windows 7 及更早），改走 WMI");
        }

        return _storageModule.Value;
    }

    /// <summary>能否挂载 ISO（不能时要靠内置的 ISO 解析器直接读文件）。</summary>
    public async Task<bool> CanMountIsoAsync(CancellationToken ct = default)
    {
        if (_isoMount is null)
        {
            _isoMount = await HasCommandAsync("Mount-DiskImage", ct).ConfigureAwait(false);
            _log.Info(_isoMount.Value
                ? "ISO 处理：可以直接挂载"
                : "ISO 处理：这台机器不能挂载 ISO（Windows 7 及更早），改用内置解析器直接读取");
        }

        return _isoMount.Value;
    }

    private async Task<bool> HasCommandAsync(string cmdlet, CancellationToken ct)
    {
        // 只用 Get-Command，Windows 7 自带的 PowerShell 2.0 也有它
        var script = $"$c = Get-Command {cmdlet} -ErrorAction SilentlyContinue\n" +
                     "if ($c) { Write-Output 'YES' } else { Write-Output 'NO' }";
        try
        {
            var result = await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
            return (result.StdOut ?? string.Empty).Contains("YES", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            // PowerShell 本身起不来时也按「没有」处理，后面的回退路径同样能跑
            _log.Warn($"探测 {cmdlet} 是否可用时出错，按不可用处理：{ex.Message}");
            return false;
        }
    }
}

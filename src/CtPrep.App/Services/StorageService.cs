using System.Text.Json;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>分区与磁盘操作。探测走 PowerShell Storage 模块，改动走 diskpart。</summary>
public sealed class StorageService
{
    private readonly ProcessRunner _runner;
    private readonly ILogSink _log;

    public StorageService(ProcessRunner runner, ILogSink log)
    {
        _runner = runner;
        _log = log;
    }

    private const string LetterHelper = @"
function Get-Letter($p) {
  $l = $p.DriveLetter
  if ($null -eq $l) { return '' }
  $s = [string]$l
  if ($s.Length -eq 0) { return '' }
  if ([int][char]$s[0] -eq 0) { return '' }
  return $s
}
";

    /// <summary>枚举所有磁盘及其分区。</summary>
    public async Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken ct = default)
    {
        var script = LetterHelper + @"
$ErrorActionPreference = 'Stop'
$out = @()
foreach ($d in Get-Disk | Sort-Object Number) {
  $parts = @()
  foreach ($p in (Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Sort-Object PartitionNumber)) {
    $parts += [pscustomobject]@{
      DiskNumber      = [int]$p.DiskNumber
      PartitionNumber = [int]$p.PartitionNumber
      DriveLetter     = Get-Letter $p
      Size            = [long]$p.Size
      Type            = [string]$p.Type
      IsSystem        = [bool]$p.IsSystem
      IsBoot          = [bool]$p.IsBoot
      IsEsp           = ([string]$p.Type -eq 'System')
    }
  }
  $out += [pscustomobject]@{
    Number       = [int]$d.Number
    FriendlyName = [string]$d.FriendlyName
    Size         = [long]$d.Size
    Style        = [string]$d.PartitionStyle
    IsSystem     = [bool]$d.IsSystem
    Partitions   = $parts
  }
}
ConvertTo-Json -InputObject @($out) -Depth 6 -Compress
";

        var result = await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
        result.EnsureSuccess("枚举磁盘分区");

        var disks = new List<DiskInfo>();
        using var doc = JsonDocument.Parse(result.StdOut);
        foreach (var diskEl in AsArray(doc.RootElement))
        {
            var disk = new DiskInfo
            {
                DiskNumber = GetInt(diskEl, "Number"),
                FriendlyName = GetString(diskEl, "FriendlyName"),
                SizeBytes = GetLong(diskEl, "Size"),
                PartitionStyle = GetString(diskEl, "Style"),
                IsSystem = GetBool(diskEl, "IsSystem"),
            };

            if (diskEl.TryGetProperty("Partitions", out var partsEl))
            {
                foreach (var partEl in AsArray(partsEl))
                {
                    disk.Partitions.Add(new PartitionInfo
                    {
                        DiskNumber = GetInt(partEl, "DiskNumber"),
                        PartitionNumber = GetInt(partEl, "PartitionNumber"),
                        DriveLetter = GetString(partEl, "DriveLetter"),
                        SizeBytes = GetLong(partEl, "Size"),
                        Type = GetString(partEl, "Type"),
                        IsSystem = GetBool(partEl, "IsSystem"),
                        IsBoot = GetBool(partEl, "IsBoot"),
                        IsEsp = GetBool(partEl, "IsEsp"),
                    });
                }
            }

            disks.Add(disk);
        }

        _log.Debug($"枚举到 {disks.Count} 块磁盘");
        return disks;
    }

    /// <summary>
    /// 准备暂存分区并返回其盘符。
    /// 优先复用已有空闲卷；否则压缩系统卷后新建一个临时分区。
    /// </summary>
    /// <param name="targetDiskNumber">目标系统所在磁盘号；复用候选会排除该磁盘上的卷，避免暂存区落在将要被格式化的磁盘上。</param>
    public async Task<string> PrepareStagingVolumeAsync(
        long requiredBytes,
        string label,
        int targetDiskNumber,
        CancellationToken ct = default)
    {
        var need = requiredBytes + (2L * 1024 * 1024 * 1024); // 预留 2GB 余量
        var needMb = need / (1024 * 1024);

        var script = LetterHelper + $@"
$ErrorActionPreference = 'Stop'
$need = [long]{need}
$label = '{label.Replace("'", "''")}'
$excludeDisk = [int]{targetDiskNumber}

# ---- 方案 A：复用现成的、空间足够的非系统 NTFS 卷 ----
$cand = Get-Volume |
    Where-Object {{ $_.DriveType -eq 'Fixed' -and $_.FileSystem -eq 'NTFS' -and $_.DriveLetter -and $_.SizeRemaining -gt $need }} |
    Where-Object {{
        $p = Get-Partition -DriveLetter $_.DriveLetter -ErrorAction SilentlyContinue
        $null -eq $p -or ($p.DiskNumber -ne $excludeDisk -and $p.IsSystem -ne $true)
    }} |
    Sort-Object SizeRemaining -Descending |
    Select-Object -First 1

if ($cand) {{
    $dir = Join-Path ($cand.DriveLetter + ':\') $label
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $rp = Get-Partition -DriveLetter $cand.DriveLetter -ErrorAction SilentlyContinue
    $rpDisk = if ($rp) {{ [int]$rp.DiskNumber }} else {{ -1 }}
    $rpNum  = if ($rp) {{ [int]$rp.PartitionNumber }} else {{ -1 }}
    Write-Output ('REUSE|' + $cand.DriveLetter + '|' + $dir + '|' + $rpDisk + '|' + $rpNum)
    exit 0
}}

# ---- 方案 B：压缩系统卷，新建临时分区 ----
$sysDrive = $env:SystemDrive.TrimEnd(':')
$sysPart  = Get-Partition -DriveLetter $sysDrive -ErrorAction Stop
$sysDisk  = Get-Disk -Number $sysPart.DiskNumber

if ($sysDisk.PartitionStyle -eq 'RAW') {{ throw '系统磁盘分区表为 RAW，无法压缩。' }}

$support = Get-PartitionSupportedSize -DiskNumber $sysPart.DiskNumber -PartitionNumber $sysPart.PartitionNumber
# Resize-Partition 只接受按对齐粒度（通常 1 MiB）取整后的大小：把「当前大小 - 需要空间」
# 这种任意字节数直接传过去会被拒，报 Size Not Supported (StorageWMI 4097)。
# 基准必须用分区大小而不是卷大小——两者相差一个文件系统开销。
$align    = 1048576
$partSize = [long]$sysPart.Size
$newSize  = [long]([math]::Floor(($partSize - $need) / $align) * $align)
if ($newSize -lt $support.SizeMin) {{
    throw ('可用空间不足：需要 ' + [math]::Round($need/1GB,2) + ' GB，最多只能压缩出 ' + [math]::Round(($partSize - $support.SizeMin)/1GB,2) + ' GB。请先清理磁盘或改用已有数据分区。')
}}
if ($newSize -le 0 -or $newSize -ge $partSize) {{
    throw ('计算出的压缩目标无效：' + $newSize + ' 字节（当前分区 ' + $partSize + ' 字节）。')
}}

try {{
    Resize-Partition -DiskNumber $sysPart.DiskNumber -PartitionNumber $sysPart.PartitionNumber -Size $newSize
}} catch {{
    throw ('压缩系统分区失败：' + $_.Exception.Message + '。常见原因是分区里有不可移动的文件（页面文件、休眠文件、系统还原点），可先关闭休眠、清理磁盘后再试。')
}}

$newPart = New-Partition -DiskNumber $sysPart.DiskNumber -UseMaximumSize
# PE 固定使用 S:（EFI）、W:（目标 Windows）、B:（BIOS 系统保留分区），
# 暂存分区不能占用这些盘符，否则后续 assign/bcdboot 会发生冲突。
$free = @('T','U','V','Y','Z') | Where-Object {{ -not (Test-Path ($_ + ':\')) }} | Select-Object -First 1
if (-not $free) {{ throw '没有可用盘符可以分配给临时分区。' }}

Add-PartitionAccessPath -DiskNumber $newPart.DiskNumber -PartitionNumber $newPart.PartitionNumber -AccessPath ($free + ':\')
Format-Volume -DriveLetter $free -FileSystem NTFS -NewFileSystemLabel $label -Confirm:$false -Force | Out-Null

# 末尾两个字段是被压缩的宿主分区：跨盘安装时把空间还给它（目标卷在另一块磁盘上）
Write-Output ('CREATED|' + $free + '|' + ($free + ':\' + $label) + '|' + [int]$newPart.DiskNumber + '|' + [int]$newPart.PartitionNumber + '|' + [int]$sysPart.DiskNumber + '|' + [int]$sysPart.PartitionNumber)
";

        _log.Info($"准备暂存分区，需要约 {needMb} MB 空间...");
        var result = await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
        result.EnsureSuccess("准备暂存分区");

        var line = result.StdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.StartsWith("REUSE|", StringComparison.Ordinal) ||
                                    value.StartsWith("CREATED|", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(line))
        {
            var output = result.StdOut.Trim();
            var error = result.StdErr.Trim();
            var details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                "暂存分区脚本没有返回可用的结果。" +
                (string.IsNullOrWhiteSpace(details) ? string.Empty : $"{Environment.NewLine}PowerShell 输出：{details}"));
        }

        var parts = line.Split('|');
        if (parts.Length < 3)
        {
            throw new InvalidOperationException($"暂存分区准备失败，输出异常：{line}");
        }

        if (parts[0] == "REUSE")
        {
            _log.Info($"复用现有分区 {parts[1]}: 作为暂存区");
        }
        else
        {
            _log.Info($"已新建临时分区 {parts[1]}: 作为暂存区");
        }

        StagingWasCreated = parts[0] == "CREATED";
        StagingDiskNumber = parts.Length > 3 && int.TryParse(parts[3], out var diskNo) ? diskNo : -1;
        StagingPartitionNumber = parts.Length > 4 && int.TryParse(parts[4], out var partNo) ? partNo : -1;
        // 宿主分区（被压缩的那个）只有新建路径才有；复用路径维持 -1。
        StagingHostDiskNumber = parts.Length > 5 && int.TryParse(parts[5], out var hostDisk) ? hostDisk : -1;
        StagingHostPartitionNumber = parts.Length > 6 && int.TryParse(parts[6], out var hostPart) ? hostPart : -1;
        return parts[2];
    }

    /// <summary>暂存分区是否为本次新建（新建的部署后需要删除并合并空间）。</summary>
    public bool StagingWasCreated { get; private set; }

    /// <summary>暂存分区所在磁盘号；-1 表示未取到。</summary>
    public int StagingDiskNumber { get; private set; } = -1;

    /// <summary>暂存分区的分区号；-1 表示未取到。整盘重建时需要据此保住暂存分区不被删除。</summary>
    public int StagingPartitionNumber { get; private set; } = -1;

    /// <summary>新建暂存分区时被压缩的宿主分区所在磁盘号；-1 表示未知（复用路径没有宿主分区）。</summary>
    public int StagingHostDiskNumber { get; private set; } = -1;

    /// <summary>新建暂存分区时被压缩的宿主分区号；-1 表示未知。</summary>
    public int StagingHostPartitionNumber { get; private set; } = -1;

    /// <summary>取系统盘符（不含冒号）。</summary>
    public string GetSystemDriveLetter()
    {
        var env = Environment.GetEnvironmentVariable("SystemDrive");
        return string.IsNullOrWhiteSpace(env) ? "C" : env.TrimEnd(':');
    }

    // ---------- BitLocker ----------

    /// <summary>
    /// 查询某个卷的 BitLocker 保护状态。
    /// 家庭版 / 没装 BitLocker 组件的系统上 Get-BitLockerVolume 不存在，
    /// 此时返回 Available=false（按未加密处理），绝不因为探测不到就阻断部署。
    /// </summary>
    public async Task<BitLockerStatus> GetBitLockerStatusAsync(string? letter, CancellationToken ct = default)
    {
        var drive = (letter ?? string.Empty).Trim().TrimEnd(':');
        if (drive.Length != 1)
        {
            return BitLockerStatus.Unknown;
        }

        var script = $@"
$ErrorActionPreference = 'Stop'
$mp = '{drive}:'
try {{
    $v = Get-BitLockerVolume -MountPoint $mp -ErrorAction Stop
}} catch {{
    Write-Output 'UNAVAILABLE'
    exit 0
}}
if ($null -eq $v) {{ Write-Output 'UNAVAILABLE'; exit 0 }}
$prot = [string]$v.ProtectionStatus
$pct = 0
try {{ $pct = [int]$v.EncryptionPercentage }} catch {{ $pct = 0 }}
Write-Output ('STATUS|' + $prot + '|' + $pct)
";
        // 探测失败（组件缺失、权限、WMI 异常）都不能中断流程，只看输出
        var result = await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
        var line = (result.StdOut ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value == "UNAVAILABLE" ||
                                    value.StartsWith("STATUS|", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(line) || line == "UNAVAILABLE")
        {
            return BitLockerStatus.Unknown;
        }

        var parts = line.Split('|');
        var on = parts.Length > 1 && parts[1].Equals("On", StringComparison.OrdinalIgnoreCase);
        var pct = parts.Length > 2 && int.TryParse(parts[2], out var parsed) ? parsed : 0;
        return new BitLockerStatus(true, on, pct);
    }

    /// <summary>
    /// 暂停 BitLocker 保护。RebootCount 0 表示一直保持暂停，直到手动恢复——
    /// 重装要重启多次，用默认值 1 会在第一次重启后自动恢复保护，等于没暂停。
    /// </summary>
    public async Task<bool> SuspendBitLockerAsync(string? letter, CancellationToken ct = default)
    {
        var drive = (letter ?? string.Empty).Trim().TrimEnd(':');
        if (drive.Length != 1)
        {
            return false;
        }

        var script = $@"
$ErrorActionPreference = 'Stop'
Suspend-BitLocker -MountPoint '{drive}:' -RebootCount 0
";
        var result = await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return true;
        }

        _log.Warn($"暂停 BitLocker 失败（{drive}:）：{result.Combined}");
        return false;
    }

    /// <summary>恢复 BitLocker 保护（装完系统后手动调用）。</summary>
    public async Task<bool> ResumeBitLockerAsync(string? letter, CancellationToken ct = default)
    {
        var drive = (letter ?? string.Empty).Trim().TrimEnd(':');
        if (drive.Length != 1)
        {
            return false;
        }

        var script = $@"
$ErrorActionPreference = 'Stop'
Resume-BitLocker -MountPoint '{drive}:'
";
        var result = await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return true;
        }

        _log.Warn($"恢复 BitLocker 失败（{drive}:）：{result.Combined}");
        return false;
    }

    // ---------- JSON 辅助 ----------

    private static IEnumerable<JsonElement> AsArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                yield return item;
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
        }
    }

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt32() : 0;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number ? v.GetInt64() : 0;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True;
}

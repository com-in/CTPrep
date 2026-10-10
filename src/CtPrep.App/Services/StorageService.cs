using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>分区与磁盘操作。探测走 PowerShell Storage 模块，改动走 diskpart。</summary>
/// <remarks>
/// Windows 7 既没有 Storage 模块也没有 Mount-DiskImage，所以两条路径都留了回退：
/// 探测改走 WMI，改动改走 diskpart。能力判断交给 <see cref="PlatformCapabilities"/>，
/// 不传则自行创建一个（调用方共享一份可以少探测几次）。
/// </remarks>
public sealed class StorageService
{
    private readonly ProcessRunner _runner;
    private readonly ILogSink _log;
    private readonly PlatformCapabilities _capabilities;

    public StorageService(ProcessRunner runner, ILogSink log, PlatformCapabilities? capabilities = null)
    {
        _runner = runner;
        _log = log;
        _capabilities = capabilities ?? new PlatformCapabilities(runner, log);
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
        // Windows 7 没有 Storage 模块，Get-Disk / Get-Partition 直接不存在
        if (!await _capabilities.HasStorageModuleAsync(ct).ConfigureAwait(false))
        {
            return GetDisksViaWmi();
        }

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

    // ---------------------------------------------------------------- WMI

    // MSR（Microsoft 保留分区）的判定区间：Windows 上固定 16MB，少数机器 128MB。
    // 取一个明显大于对齐间隙（1MB）的窗口，避免把普通空隙误当成隐藏分区。
    private const long MinHiddenPartitionBytes = 8L * 1024 * 1024;
    private const long MaxHiddenPartitionBytes = 256L * 1024 * 1024;

    /// <summary>Windows 7 回退：用 WMI 读磁盘与分区（Storage 模块在那上面不存在）。</summary>
    private IReadOnlyList<DiskInfo> GetDisksViaWmi()
    {
        var partitions = new List<WmiPartition>();
        using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskPartition"))
        {
            foreach (ManagementBaseObject item in searcher.Get())
            {
                partitions.Add(new WmiPartition
                {
                    DiskIndex = WmiInt(item, "DiskIndex"),
                    Index = WmiInt(item, "Index"),
                    Start = WmiLong(item, "StartingOffset"),
                    Size = WmiLong(item, "Size"),
                    Type = item["Type"]?.ToString() ?? string.Empty,
                    BootPartition = WmiBool(item, "BootPartition"),
                });
            }
        }

        // 盘符：逻辑盘 -> 分区，靠 Win32_LogicalDiskToPartition 关联
        var letters = new Dictionary<(int Disk, int Index), string>();
        using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
        {
            foreach (ManagementBaseObject item in searcher.Get())
            {
                var reference = ParsePartitionReference(item["Antecedent"]?.ToString());
                var letter = ParseDriveLetter(item["Dependent"]?.ToString());
                if (reference is not null && letter.Length == 1)
                {
                    letters[reference.Value] = letter;
                }
            }
        }

        var systemDrive = GetSystemDriveLetter();
        var disks = new List<DiskInfo>();
        using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
        {
            foreach (ManagementBaseObject item in searcher.Get())
            {
                disks.Add(new DiskInfo
                {
                    DiskNumber = WmiInt(item, "Index"),
                    FriendlyName = (item["Model"] ?? item["Caption"])?.ToString() ?? string.Empty,
                    SizeBytes = WmiLong(item, "Size"),
                });
            }
        }

        foreach (var disk in disks)
        {
            var owned = partitions
                .Where(p => p.DiskIndex == disk.DiskNumber)
                .OrderBy(p => p.Start)
                .ToList();

            disk.PartitionStyle = owned.Any(p => p.Type.StartsWith("GPT", StringComparison.OrdinalIgnoreCase))
                ? "GPT"
                : "MBR";

            var number = 0;
            for (var i = 0; i < owned.Count; i++)
            {
                var part = owned[i];

                // GPT 磁盘上 WMI 不报告 MSR（微软保留分区），而 diskpart 的分区号是把它
                // 算在内的——直接按 Index+1 编号会整体错位（实测：C 盘 WMI 里是 index=1，
                // diskpart 里却是分区 3）。MSR 是夹在 ESP 与第一个数据分区之间的一段
                // 16MB（少数 128MB）空隙，用这段缺口把它补回去。
                if (i > 0 && disk.PartitionStyle == "GPT")
                {
                    var previous = owned[i - 1];
                    var gap = part.Start - (previous.Start + previous.Size);
                    if (gap >= MinHiddenPartitionBytes && gap <= MaxHiddenPartitionBytes)
                    {
                        number++;
                        disk.Partitions.Add(new PartitionInfo
                        {
                            DiskNumber = disk.DiskNumber,
                            PartitionNumber = number,
                            SizeBytes = gap,
                            Type = "Reserved",
                        });
                    }
                }

                number++;
                letters.TryGetValue((part.DiskIndex, part.Index), out var letter);
                disk.Partitions.Add(new PartitionInfo
                {
                    DiskNumber = disk.DiskNumber,
                    PartitionNumber = number,
                    DriveLetter = letter ?? string.Empty,
                    SizeBytes = part.Size,
                    Type = NormalizeWmiPartitionType(part.Type),
                    // WMI 的 BootPartition 指的是放引导文件的那个分区（UEFI 下就是 ESP），
                    // 对应 Storage 的 IsSystem
                    IsSystem = part.BootPartition,
                    IsBoot = !string.IsNullOrEmpty(letter) &&
                             letter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                    IsEsp = WmiPartitionTypeName(part.Type)
                        .Equals("System", StringComparison.OrdinalIgnoreCase),
                });
            }

            disk.IsSystem = disk.Partitions.Any(p => p.IsBoot);
        }

        disks.Sort((a, b) => a.DiskNumber.CompareTo(b.DiskNumber));
        _log.Debug($"WMI 枚举到 {disks.Count} 块磁盘");
        return disks;
    }

    private sealed class WmiPartition
    {
        public int DiskIndex;
        public int Index;
        public long Start;
        public long Size;
        public string Type = string.Empty;
        public bool BootPartition;
    }

    /// <summary>从 "Disk #0, Partition #1" 这样的引用里取出磁盘号与分区序号。</summary>
    private static (int Disk, int Index)? ParsePartitionReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"Disk\s*#\s*(\d+)\s*,\s*Partition\s*#\s*(\d+)",
            RegexOptions.IgnoreCase);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
               int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static string ParseDriveLetter(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, "\"([A-Za-z]):\"");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
    }

    /// <summary>去掉 "GPT:" / "MBR:" 前缀后的类型名，例如 "GPT: System" -> "System"。</summary>
    private static string WmiPartitionTypeName(string type)
    {
        var colon = type.IndexOf(':');
        return colon >= 0 ? type[(colon + 1)..].Trim() : type.Trim();
    }

    private static string NormalizeWmiPartitionType(string type)
    {
        var name = WmiPartitionTypeName(type);
        return name.Equals("Basic Data", StringComparison.OrdinalIgnoreCase) ? "Basic" : name;
    }

    private static int WmiInt(ManagementBaseObject item, string name) =>
        item[name] is null ? 0 : Convert.ToInt32(item[name], CultureInfo.InvariantCulture);

    private static long WmiLong(ManagementBaseObject item, string name) =>
        item[name] is null ? 0 : Convert.ToInt64(item[name], CultureInfo.InvariantCulture);

    private static bool WmiBool(ManagementBaseObject item, string name) =>
        item[name] is not null && Convert.ToBoolean(item[name], CultureInfo.InvariantCulture);

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

        // Windows 7 上 Resize-Partition / New-Partition / Format-Volume 全都不存在，
        // 只能退回 diskpart（顺带用 WMI 找可复用的卷，因为 Get-Volume 也没有）
        if (!await _capabilities.HasStorageModuleAsync(ct).ConfigureAwait(false))
        {
            return await PrepareStagingVolumeLegacyAsync(need, label, targetDiskNumber, ct)
                .ConfigureAwait(false);
        }

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

    /// <summary>
    /// Windows 7 回退：准备暂存分区。
    /// 方案 A 用 WMI 找可复用的卷（Get-Volume 在 Win7 上不存在），
    /// 方案 B 用 diskpart 压缩系统分区再建一个（Resize-Partition / New-Partition 都没有）。
    /// </summary>
    private async Task<string> PrepareStagingVolumeLegacyAsync(
        long need,
        string label,
        int targetDiskNumber,
        CancellationToken ct)
    {
        _log.Info($"准备暂存分区（diskpart 回退），需要约 {need / (1024 * 1024)} MB 空间...");

        var volumes = ReadLogicalVolumes();
        var disks = GetDisksViaWmi();

        // ---- 方案 A：复用一个空间足够、不在目标盘上的 NTFS 卷 ----
        PartitionInfo? best = null;
        var bestFree = 0L;
        foreach (var disk in disks)
        {
            if (disk.DiskNumber == targetDiskNumber)
            {
                continue;
            }

            foreach (var part in disk.Partitions)
            {
                if (string.IsNullOrEmpty(part.DriveLetter) || part.IsSystem)
                {
                    continue;
                }

                if (!volumes.TryGetValue(part.DriveLetter, out var volume) ||
                    !volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (volume.FreeSpace > need && volume.FreeSpace > bestFree)
                {
                    best = part;
                    bestFree = volume.FreeSpace;
                }
            }
        }

        if (best is not null)
        {
            var reuseDir = Path.Combine(best.DriveLetter + ":\\", label);
            Directory.CreateDirectory(reuseDir);
            StagingWasCreated = false;
            StagingDiskNumber = best.DiskNumber;
            StagingPartitionNumber = best.PartitionNumber;
            StagingHostDiskNumber = -1;
            StagingHostPartitionNumber = -1;
            _log.Info($"复用现有分区 {best.DriveLetter}: 作为暂存区");
            return reuseDir;
        }

        // ---- 方案 B：压缩系统分区，新建临时分区 ----
        var systemDrive = GetSystemDriveLetter();
        var hostDisk = disks.FirstOrDefault(d => d.Partitions.Any(p =>
            p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)));
        var hostPart = hostDisk?.Partitions.FirstOrDefault(p =>
            p.DriveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase));

        if (hostDisk is null || hostPart is null)
        {
            throw new InvalidOperationException(
                $"没有找到系统分区（{systemDrive}:），无法压缩出暂存空间。");
        }

        if (hostDisk.PartitionStyle.Equals("RAW", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("系统磁盘分区表为 RAW，无法压缩。");
        }

        var free = PickFreeStagingLetter(volumes);
        if (free is null)
        {
            throw new InvalidOperationException("没有可用盘符可以分配给临时分区。");
        }

        var shrinkMb = (long)Math.Ceiling(need / (1024.0 * 1024.0));
        var created = await RunCreateStagingPartitionAsync(
            hostDisk, hostPart, shrinkMb, label, free.Value, ct).ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException(
                "压缩系统分区并新建暂存分区失败。常见原因是分区里有不可移动的文件" +
                "（页面文件、休眠文件、系统还原点），可先关闭休眠、清理磁盘后再试。");
        }

        // 新建完再查一次，拿到它真正的分区号（diskpart 不告诉我们）
        var staged = GetDisksViaWmi()
            .SelectMany(d => d.Partitions.Select(p => (Disk: d, Part: p)))
            .FirstOrDefault(x => x.Part.DriveLetter.Equals(free.Value.ToString(),
                StringComparison.OrdinalIgnoreCase));

        StagingWasCreated = true;
        StagingDiskNumber = staged.Part?.DiskNumber ?? hostDisk.DiskNumber;
        StagingPartitionNumber = staged.Part?.PartitionNumber ?? -1;
        StagingHostDiskNumber = hostDisk.DiskNumber;
        StagingHostPartitionNumber = hostPart.PartitionNumber;
        _log.Info($"已新建临时分区 {free}: 作为暂存区（宿主分区：磁盘 {hostDisk.DiskNumber} 分区 {hostPart.PartitionNumber}）");

        var dir = Path.Combine(free.Value + ":\\", label);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 用 diskpart 压缩系统分区并新建、格式化、分配盘符。
    /// MBR 磁盘上主分区数已满时退回「创建逻辑分区」再试一次。
    /// </summary>
    private async Task<bool> RunCreateStagingPartitionAsync(
        DiskInfo hostDisk,
        PartitionInfo hostPart,
        long shrinkMb,
        string label,
        char letter,
        CancellationToken ct)
    {
        var attempts = hostDisk.PartitionStyle.Equals("MBR", StringComparison.OrdinalIgnoreCase)
            ? new[] { "primary", "logical" }
            : new[] { "primary" };

        foreach (var kind in attempts)
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"ctprep-staging-{kind}.txt");
            var lines = new[]
            {
                $"select disk {hostDisk.DiskNumber}",
                $"select partition {hostPart.PartitionNumber}",
                $"shrink desired={shrinkMb}",
                $"create partition {kind}",
                $"format fs=ntfs quick label={label}",
                $"assign letter={letter}",
                "exit",
            };
            await File.WriteAllLinesAsync(scriptPath, lines, ct).ConfigureAwait(false);

            var result = await _runner.RunDiskPartAsync(scriptPath, ct).ConfigureAwait(false);
            _log.Debug($"diskpart（{kind}）退出码 {result.ExitCode}");

            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
                // 临时脚本删不掉不影响结果
            }

            if (result.ExitCode == 0)
            {
                return true;
            }

            _log.Warn($"diskpart 用 {kind} 方式创建暂存分区失败：{result.Combined}");
        }

        return false;
    }

    /// <summary>读所有固定盘的盘符、文件系统与剩余空间。</summary>
    private static Dictionary<string, (string FileSystem, long FreeSpace)> ReadLogicalVolumes()
    {
        var map = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, FileSystem, FreeSpace, DriveType FROM Win32_LogicalDisk");
        foreach (ManagementBaseObject item in searcher.Get())
        {
            var id = item["DeviceID"]?.ToString() ?? string.Empty;
            if (id.Length < 2 || id[1] != ':')
            {
                continue;
            }

            if (WmiInt(item, "DriveType") != 3)
            {
                continue; // 3 = 本地固定磁盘
            }

            map[id[0].ToString()] = (
                item["FileSystem"]?.ToString() ?? string.Empty,
                WmiLong(item, "FreeSpace"));
        }

        return map;
    }

    /// <summary>
    /// 挑一个没被占用的盘符。跳过 PE 里固定占用的 S:（EFI）、W:（目标 Windows）、
    /// B:（BIOS 系统保留分区），否则后续 assign / bcdboot 会冲突。
    /// </summary>
    private static char? PickFreeStagingLetter(
        IReadOnlyDictionary<string, (string FileSystem, long FreeSpace)> volumes)
    {
        foreach (var candidate in new[] { 'T', 'U', 'V', 'Y', 'Z' })
        {
            var letter = candidate.ToString();
            if (!volumes.ContainsKey(letter) && !Directory.Exists(letter + ":\\"))
            {
                return candidate;
            }
        }

        return null;
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

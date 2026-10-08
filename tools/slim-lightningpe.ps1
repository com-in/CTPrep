#Requires -Version 5.1
<#
.SYNOPSIS
  把雷电PE（Lightning PE）的 64 位映像深度精简并注入 CTPrep 自动部署脚本，
  产出一份可直接放进程序 runtime 目录的 boot.wim / boot.sdi。

.DESCRIPTION
  流程：
    1. 挂载雷电PE 整包里的 ISO，取出 Dream-M_64.wim 与 Dream-M.sdi 到工作目录（NTFS）；
    2. 挂载 WIM，盘点内部结构（Inventory 模式，输出 inventory.txt）；
    3. Build 模式：按 -Remove 列表删减、注入 startnet.cmd、禁用 winpeshl.ini、
       把离线注册表 Setup\CmdLine 改回 winpeshl.exe；
    4. 提交并以 Max(LZX) 压缩导出到 -OutPeDir\boot.wim，同时复制 boot.sdi。

.PARAMETER Mode
  Inventory = 只挂载并盘点，不做任何改动（保留挂载，供 Build 复用）；
  Build     = 执行删减、注入、导出。

.PARAMETER Remove
  Build 模式下要删除的挂载点内相对路径（相对 WIM 根，例如 'Program Files\SomeApp'）。

.EXAMPLE
  # 1) 盘点
  .\slim-lightningpe.ps1 -Mode Inventory
  # 2) 精简并导出
  .\slim-lightningpe.ps1 -Mode Build -Remove 'Program Files\SomeApp'
#>
[CmdletBinding()]
param(
    [ValidateSet('Inventory', 'Build', 'Verify')]
    [string]$Mode = 'Inventory',

    [string[]]$Remove = @(),

    # Verify 模式要校验的 PE 映像
    [string]$VerifyWim = '',

    [string]$WorkRoot = 'C:\CTPrep-pe',
    [string]$IsoPath  = 'C:\CTPrep-pe\Sources\V1.8-B3.2_NVME.ISO',
    [string]$OutPeDir = 'E:\Projects\CTPrep\publish\runtime\pe',

    # CTPrep 的 PE 侧入口脚本（单一事实来源，避免与程序内置版本不一致）
    [string]$StartNetSource = 'E:\Projects\CTPrep\src\CtPrep.App\Assets\pe\startnet.cmd',

    [switch]$SkipIsoExtract
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'DISM 挂载/导出需要管理员权限，请以管理员身份运行本脚本。'
    }
}

function Write-Step2([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok2([string]$text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Note2([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

Assert-Admin
Import-Module Dism -ErrorAction Stop | Out-Null

New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
Start-Transcript -Path (Join-Path $WorkRoot 'run.log') -Force | Out-Null
trap {
    $msg = $_ | Out-String
    Write-Host "FATAL: $msg" -ForegroundColor Red
    $msg | Set-Content -LiteralPath (Join-Path $WorkRoot 'error.txt') -Encoding UTF8
    exit 1
}

$mount    = Join-Path $WorkRoot 'mount'
$workDir  = Join-Path $WorkRoot 'work'
$srcWim   = Join-Path $workDir 'Dream-M_64.wim'
$srcSdi   = Join-Path $workDir 'Dream-M.sdi'
$invPath  = Join-Path $WorkRoot 'inventory.txt'

New-Item -ItemType Directory -Path $WorkRoot, $workDir, $OutPeDir -Force | Out-Null

# ---------------------------------------------------------------------------
# 0. 前置检查
# ---------------------------------------------------------------------------
Write-Step2 '前置检查'
if (-not (Test-Path -LiteralPath $StartNetSource)) {
    throw "找不到 startnet.cmd：$StartNetSource"
}
Write-Ok2 "注入源：$StartNetSource"

# 上一次运行若异常退出，离线注册表可能仍挂在 HKLM 下，
# 会锁住挂载点内的 SYSTEM 文件，导致后续卸载报「拒绝访问」。
Write-Step2 '清理上次残留的离线注册表'
$prevEap0 = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& reg.exe unload 'HKLM\CTPrepOfflinePe' 2>&1 | Out-Null
$ErrorActionPreference = $prevEap0
Write-Ok2 '已确认无残留'

# ---------------------------------------------------------------------------
# Verify 模式：只读挂载已有映像，检查精简与注入是否真的落地
# ---------------------------------------------------------------------------
if ($Mode -eq 'Verify') {
    if (-not (Test-Path -LiteralPath $VerifyWim)) { throw "找不到要校验的 WIM：$VerifyWim" }

    Write-Step2 "只读挂载 $VerifyWim"
    try { Dismount-WindowsImage -Path $mount -Discard | Out-Null } catch { }
    & dism.exe /English /Cleanup-Mountpoints 2>&1 | Out-Null
    if (Test-Path -LiteralPath $mount) { Remove-Item -LiteralPath $mount -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $mount -Force | Out-Null
    Mount-WindowsImage -ImagePath $VerifyWim -Index 1 -Path $mount -ReadOnly | Out-Null
    Write-Ok2 '已只读挂载'

    $script:ok = $true
    function Test-Absent([string]$rel, [string]$desc) {
        if (Test-Path -LiteralPath (Join-Path $mount $rel)) {
            Write-Note2 "未通过：$desc 仍存在（$rel）"; $script:ok = $false
        } else { Write-Ok2 "$desc 已移除：$rel" }
    }
    function Test-Present([string]$rel, [string]$desc) {
        if (Test-Path -LiteralPath (Join-Path $mount $rel)) { Write-Ok2 "$desc 存在：$rel" }
        else { Write-Note2 "未通过：$desc 缺失（$rel）"; $script:ok = $false }
    }

    Write-Step2 '精简结果'
    Test-Absent  'Windows\syswow64'            '32 位子系统'
    Test-Absent  'Program Files\Others'        '第三方工具目录'
    Test-Absent  'Windows\Dream-M'             'PE 自带浏览器/播放器'
    Test-Absent  'Windows\InputMethod'         '中文输入法'
    Test-Absent  'Windows\System32\Macromed'   'Flash'
    Test-Absent  'Windows\System32\winpeshl.ini' 'winpeshl.ini（应已改名）'
    Test-Present 'Windows\System32\winpeshl.ini.ctprep.bak' 'winpeshl.ini 备份'

    Write-Step2 '部署组件完整性'
    Test-Present 'Windows\System32\startnet.cmd'    '自动部署入口'
    Test-Present 'Windows\System32\wpeinit.exe'     'wpeinit'
    Test-Present 'Windows\System32\dism.exe'        'dism'
    Test-Present 'Windows\System32\diskpart.exe'    'diskpart'
    Test-Present 'Windows\System32\bcdboot.exe'     'bcdboot'
    Test-Present 'Windows\System32\config\SYSTEM'   'SYSTEM 注册表'
    Test-Present 'Windows\System32\DriverStore'     '驱动库'

    Write-Step2 'startnet.cmd 内容'
    $sn = Join-Path $mount 'Windows\System32\startnet.cmd'
    if (Test-Path -LiteralPath $sn) {
        $txt = Get-Content -LiteralPath $sn -Raw
        if ($txt -match 'CTPrep deployment environment' -and $txt -match 'deploy\.cmd') {
            Write-Ok2 '含部署横幅与 deploy.cmd 调用'
        } else {
            Write-Note2 '未通过：内容与预期不符'; $script:ok = $false
        }
        $bad = @($txt.ToCharArray() | Where-Object { [int][char]$_ -gt 127 }).Count
        if ($bad -gt 0) { Write-Note2 "未通过：含 $bad 个非 ASCII 字符"; $script:ok = $false }
        else { Write-Ok2 '纯 ASCII，符合 PE 代码页 437 要求' }
    }

    Write-Step2 '外壳归一化'
    $hive = Join-Path $mount 'Windows\System32\config\SYSTEM'
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & reg.exe unload 'HKLM\CTPrepOfflinePe' 2>&1 | Out-Null
    $load = & reg.exe load 'HKLM\CTPrepOfflinePe' $hive 2>&1
    if ($LASTEXITCODE -eq 0) {
        $q = & reg.exe query 'HKLM\CTPrepOfflinePe\Setup' /v CmdLine 2>&1
        $cl = $null
        foreach ($line in ($q -split "`r?`n")) {
            if ($line -match 'REG_(EXPAND_)?SZ\s+(.*)$') { $cl = $Matches[2].Trim(); break }
        }
        if ($cl -match 'winpeshl') { Write-Ok2 "Setup\CmdLine = $cl" }
        else { Write-Note2 "未通过：Setup\CmdLine = $cl"; $script:ok = $false }
        & reg.exe unload 'HKLM\CTPrepOfflinePe' 2>&1 | Out-Null
    } else {
        Write-Note2 "未通过：离线注册表加载失败 $load"; $script:ok = $false
    }
    $ErrorActionPreference = $prevEap

    try { Dismount-WindowsImage -Path $mount -Discard | Out-Null } catch { }
    & dism.exe /English /Cleanup-Mountpoints 2>&1 | Out-Null

    Write-Host ''
    if ($script:ok) { Write-Host 'VERIFY RESULT: ALL CHECKS PASSED' -ForegroundColor Green }
    else            { Write-Host 'VERIFY RESULT: SOME CHECKS FAILED' -ForegroundColor Red }
    exit 0
}

# ---------------------------------------------------------------------------
# 1. 挂载 ISO 并取出 WIM / SDI
# ---------------------------------------------------------------------------
if (-not $SkipIsoExtract) {
    Write-Step2 '挂载雷电PE ISO'
    if (-not (Test-Path -LiteralPath $IsoPath)) {
        throw "找不到 ISO：$IsoPath"
    }

    $img = Get-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue
    if ($null -eq $img -or -not $img.Attached) {
        $img = Mount-DiskImage -ImagePath $IsoPath -PassThru
    }

    $vol = $img | Get-Volume
    if (-not $vol.DriveLetter) { Start-Sleep -Seconds 1; $vol = Get-Volume -Partition ($img | Get-Partition) }
    $L = [string]$vol.DriveLetter
    if (-not $L) { throw 'ISO 已挂载但未取得盘符。' }
    Write-Ok2 "ISO 盘符：${L}:"

    $isoWim = Join-Path "${L}:\" 'LiPE\Dream-M_64.wim'
    $isoSdi = Join-Path "${L}:\" 'LiPE\Data\Dream-M.sdi'
    foreach ($f in @($isoWim, $isoSdi)) {
        if (-not (Test-Path -LiteralPath $f)) { throw "ISO 内缺少文件：$f" }
    }

    Copy-Item -LiteralPath $isoWim -Destination $srcWim -Force
    Copy-Item -LiteralPath $isoSdi -Destination $srcSdi -Force
    Write-Ok2 ('已取出 Dream-M_64.wim ({0:N1} MB) 与 Dream-M.sdi' -f ((Get-Item -LiteralPath $srcWim).Length / 1MB))

    Dismount-DiskImage -ImagePath $IsoPath | Out-Null
    Write-Ok2 'ISO 已卸载'
}

# ---------------------------------------------------------------------------
# 2. 挂载 WIM
# ---------------------------------------------------------------------------
# 必须每次在本进程内重新挂载：DISM 只允许创建挂载点的那个会话提交改动，
# 复用上一次进程留下的挂载点在 Dismount -Save 时会报「拒绝访问」。
if (Test-Path -LiteralPath $srcWim) {
    # 从 ISO 复制出来的文件带只读属性，DISM 会以 0xc1510111 拒绝可写挂载
    Set-ItemProperty -LiteralPath $srcWim -Name IsReadOnly -Value $false
}

Write-Step2 '清理残留挂载点'
# Dismount 对未挂载路径抛 COM 终止错误，-ErrorAction 压不住，必须 try/catch
try { Dismount-WindowsImage -Path $mount -Discard | Out-Null } catch { }
# 用 dism.exe 而非 Cleanup-Mountpoints cmdlet：后者在部分系统上不存在
& dism.exe /English /Cleanup-Mountpoints 2>&1 | Out-Null
if (Test-Path -LiteralPath $mount) {
    Remove-Item -LiteralPath $mount -Recurse -Force -ErrorAction SilentlyContinue
}
# DISM 要求挂载目录必须已存在且为空，不会自动创建
New-Item -ItemType Directory -Path $mount -Force | Out-Null

Write-Step2 '挂载 Dream-M_64.wim（索引 1，可写）'
Mount-WindowsImage -ImagePath $srcWim -Index 1 -Path $mount | Out-Null
Write-Ok2 "已挂载：$mount"

# ---------------------------------------------------------------------------
# 3. 盘点
# ---------------------------------------------------------------------------
Write-Step2 '盘点内部结构'
$report = New-Object System.Collections.Generic.List[string]
$report.Add("WIM inventory - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
$report.Add("source: $srcWim")

$wi = Get-WindowsImage -ImagePath $srcWim
$report.Add('')
$report.Add('### images in wim')
foreach ($i in $wi) {
    $report.Add(('  index {0}: "{1}"  {2:N1} MB' -f $i.ImageIndex, $i.ImageName, ($i.ImageSize / 1MB)))
}

Write-Host '    扫描文件（可能需要 1-2 分钟）...'
$fl = @(Get-ChildItem -LiteralPath $mount -Recurse -File -Force -ErrorAction SilentlyContinue)
$rootLen = $mount.TrimEnd('\').Length + 1

$report.Add('')
$report.Add('### top-level directories (recursive size)')
$fl | Group-Object { $_.FullName.Substring($rootLen).Split('\')[0] } |
    ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            MB   = [math]::Round((($_.Group | Measure-Object Length -Sum).Sum) / 1MB, 1)
            Files = $_.Count
        }
    } | Sort-Object MB -Descending | ForEach-Object {
        $report.Add(('  {0,-32} {1,9:N1} MB  ({2} files)' -f $_.Name, $_.MB, $_.Files))
    }

$report.Add('')
$report.Add('### second-level directories under the big three (recursive size)')
foreach ($top in @('Windows', 'Program Files', 'Program Files (x86)', 'Users')) {
    $tp = Join-Path $mount $top
    if (-not (Test-Path -LiteralPath $tp)) { continue }
    $report.Add("  [$top]")
    Get-ChildItem -LiteralPath $tp -Directory -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $s = (Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
              Measure-Object Length -Sum).Sum
        [pscustomobject]@{ Name = $_.Name; MB = [math]::Round($s / 1MB, 1) }
    } | Sort-Object MB -Descending | Where-Object { $_.MB -ge 2 } | ForEach-Object {
        $report.Add(('    {0,-32} {1,9:N1} MB' -f $_.Name, $_.MB))
    }
}

$report.Add('')
$report.Add('### 30 largest files')
$fl | Sort-Object Length -Descending | Select-Object -First 30 | ForEach-Object {
    $rel = $_.FullName.Substring($rootLen)
    $report.Add(('  {0,9:N1} MB  {1}' -f ($_.Length / 1MB), $rel))
}

$report.Add('')
$report.Add('### boot-related files')
foreach ($rel in @(
        'Windows\System32\startnet.cmd',
        'Windows\System32\winpeshl.ini',
        'Windows\System32\winpeshl.exe',
        'Windows\System32\pecmd.exe',
        'Windows\System32\PECMD.exe',
        'Windows\System32\wpeinit.exe',
        'Windows\System32\config\SYSTEM',
        'Windows\Boot\DVD\PCAT\boot.sdi')) {
    $p = Join-Path $mount $rel
    $report.Add(('  {0,-45} {1}' -f $rel, $(if (Test-Path -LiteralPath $p) { 'YES' } else { '-' })))
}

$sub = Get-ChildItem -LiteralPath $mount -Directory -Force -ErrorAction SilentlyContinue |
       Select-Object -ExpandProperty Name
$report.Add('')
$report.Add("### mount root entries: $($sub -join ', ')")
$report.Add("### total: $([math]::Round((($fl | Measure-Object Length -Sum).Sum)/1MB,1)) MB, $($fl.Count) files")

$report | Set-Content -LiteralPath $invPath -Encoding UTF8
Write-Ok2 "盘点结果：$invPath"

if ($Mode -eq 'Inventory') {
    Write-Host ''
    # 必须在此进程内卸载：跨会话残留的挂载点无法被下次运行提交
    try { Dismount-WindowsImage -Path $mount -Discard | Out-Null } catch { }
    Write-Note2 'Inventory 模式结束：未做任何改动，挂载已卸载。'
    $report | ForEach-Object { Write-Host $_ }
    exit 0
}

# ---------------------------------------------------------------------------
# 4. 删减
# ---------------------------------------------------------------------------
if ($Remove.Count -gt 0) {
    Write-Step2 '执行删减'
    foreach ($rel in $Remove) {
        $target = Join-Path $mount $rel
        if (Test-Path -LiteralPath $target) {
            $before = (Get-ChildItem -LiteralPath $target -Recurse -File -Force -ErrorAction SilentlyContinue |
                       Measure-Object Length -Sum).Sum
            Remove-Item -LiteralPath $target -Recurse -Force
            Write-Ok2 ('删除 {0}（{1:N1} MB）' -f $rel, ($before / 1MB))
        } else {
            Write-Note2 "跳过（不存在）：$rel"
        }
    }
}

# ---------------------------------------------------------------------------
# 5. 注入 CTPrep 自动部署
# ---------------------------------------------------------------------------
Write-Step2 '注入 CTPrep 自动部署脚本'
Copy-Item -LiteralPath $StartNetSource -Destination (Join-Path $mount 'Windows\System32\startnet.cmd') -Force
Write-Ok2 '已写入 Windows\System32\startnet.cmd'

$ini = Join-Path $mount 'Windows\System32\winpeshl.ini'
if (Test-Path -LiteralPath $ini) {
    Move-Item -LiteralPath $ini -Destination "$ini.ctprep.bak" -Force
    Write-Ok2 '已把 winpeshl.ini 改名备份为 winpeshl.ini.ctprep.bak'
} else {
    Write-Ok2 '没有 winpeshl.ini，startnet.cmd 将按默认流程执行'
}

$hive = Join-Path $mount 'Windows\System32\config\SYSTEM'
if (Test-Path -LiteralPath $hive) {
    $key = 'HKLM\CTPrepOfflinePe'
    # reg.exe 的 stderr 在 ErrorActionPreference=Stop 下会变成终止错误，这里必须放宽
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    # 键可能本来就没加载，卸载失败属正常，忽略即可
    & reg.exe unload $key 2>&1 | Out-Null

    $load = & reg.exe load $key "$hive" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Note2 "离线注册表加载失败，跳过外壳检查：$load"
    } else {
        try {
            $q = & reg.exe query "$key\Setup" /v CmdLine 2>&1
            $cmdLine = $null
            foreach ($line in ($q -split "`r?`n")) {
                if ($line -match 'REG_(EXPAND_)?SZ\s+(.*)$') { $cmdLine = $Matches[2].Trim(); break }
            }

            if ($null -eq $cmdLine) {
                Write-Ok2 '注册表未自定义 Setup\CmdLine'
            } elseif ($cmdLine -match 'winpeshl') {
                Write-Ok2 "Setup\CmdLine 已指向 winpeshl.exe：$cmdLine"
            } else {
                Write-Note2 "检测到第三方外壳 Setup\CmdLine = $cmdLine，改回 winpeshl.exe"
                & reg.exe add "$key\Setup" /v CmdLine /t REG_SZ /d winpeshl.exe /f 2>&1 | Out-Null
            }
        } finally {
            & reg.exe unload $key 2>&1 | Out-Null
        }
    }

    $ErrorActionPreference = $prevEap
}

# ---------------------------------------------------------------------------
# 6. 提交并导出
# ---------------------------------------------------------------------------
Write-Step2 '提交改动'
Dismount-WindowsImage -Path $mount -Save | Out-Null
Write-Ok2 '已提交'

$outWim = Join-Path $OutPeDir 'boot.wim'
Write-Step2 "以 Max(LZX) 压缩导出到 $outWim"
if (Test-Path -LiteralPath $outWim) { Remove-Item -LiteralPath $outWim -Force }
Export-WindowsImage -SourceImagePath $srcWim -SourceIndex 1 -DestinationImagePath $outWim -CompressionType Max | Out-Null

Copy-Item -LiteralPath $srcSdi -Destination (Join-Path $OutPeDir 'boot.sdi') -Force

& dism.exe /English /Cleanup-Mountpoints 2>&1 | Out-Null
Stop-Transcript | Out-Null

$wimMB = [math]::Round((Get-Item -LiteralPath $outWim).Length / 1MB, 1)
Write-Host ''
Write-Host '=============================================' -ForegroundColor Green
Write-Host (' boot.wim : {0}  ({1:N1} MB)' -f $outWim, $wimMB) -ForegroundColor Green
Write-Host (' boot.sdi : {0}' -f (Join-Path $OutPeDir 'boot.sdi')) -ForegroundColor Green
Write-Host '=============================================' -ForegroundColor Green
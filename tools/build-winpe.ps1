#Requires -Version 5.1
<#
.SYNOPSIS
  构建 CTPrep 专用 WinPE 映像（精简、纯 ASCII、内置自动部署脚本与三款工具），
  并可把一份系统安装镜像裁剪成「只保留一个版本」的精简映像。

.DESCRIPTION
  用 Windows ADK 的 WinPE 附加包生成 runtime\pe\boot.wim 与 boot.sdi：
    - 只安装 CTPrep 真正需要的可选组件，其余一律不加
    - 用 LZX 最大压缩重新导出，把体积压到最小
    - 把 CTPrep 的自动部署脚本烘焙进 Windows\System32\startnet.cmd
    - 把 DiskGenius / WinNTSetup / Dism++ 放进 PE 的 X:\Tools

  指定 -ImageDir 时，还会扫描该目录下的系统安装镜像（.iso / .wim / .esd），
  读出里面全部版本（Home / Pro / Education / Enterprise …），问你要保留哪一个，
  再用 DISM 只导出那一个版本（其余全部丢弃），结果写到 <映像目录>\slim\install.wim。

  必须以管理员身份运行：DISM 挂载 WIM、Mount-DiskImage 挂载 ISO 都需要管理员权限。

.PARAMETER ExtrasDir
  三款第三方工具的存放目录，默认 tools\pe-extras。
  期望结构：<ExtrasDir>\DiskGenius\...、<ExtrasDir>\WinNTSetup\...、<ExtrasDir>\Dism++\...

.PARAMETER AdkRoot
  Windows ADK 根目录。不填则自动搜索常见安装位置。

.PARAMETER WithPowerShell
  额外加入 WinPE-NetFx + WinPE-PowerShell + WinPE-DismCmdlets（约增加 150MB）。

.PARAMETER DriversDir
  可选：把该目录下的 .inf 驱动一并注入 PE。

.PARAMETER Iso
  额外用 MakeWinPEMedia 生成启动 ISO。CTPrep 采用 ramdisk 引导，
  只需要 boot.wim，ISO 仅供 U 盘手动启动时备用。

.PARAMETER KeepWork
  保留临时构建目录 build\winpe（默认构建完成后删除）。

.PARAMETER AllowOverSize
  最终体积超过 MaxSizeMB 时仍然继续（默认直接报错）。

.PARAMETER ImageDir
  可选：系统安装镜像所在目录（.iso / .wim / .esd）。指定后会读出镜像里的全部版本，
  询问要保留哪一个，其余版本全部丢弃，结果写到 <该目录>\slim\install.wim。
  也可以直接传单个镜像文件的路径。

.PARAMETER ImageIndex
  要保留的映像索引（即 dism 显示的 Index）。不填则交互式询问。

.PARAMETER ImageOutDir
  精简映像的输出目录，默认 <ImageDir>\slim。

.PARAMETER ImageCompress
  精简映像的压缩方式，默认 max。
    max      = LZX，兼容性最好：任何年份的 dism 都能 /Apply-Image 展开，且能挂载。
    recovery = LZMS，体积最小（微软自家的 install.esd 就是这种），比 max 再小约 15%~20%；
               但 2017 年前后的老 PE（例如雷电PE 的 15063 内核）无法展开 LZMS 映像，
               只能给较新的 PE 用；LZMS 映像也无法再挂载。
    fast     = XPRESS，最差。

.PARAMETER DeepSlim
  挂载精简后的映像做一次离线深度清理（微软官方的 /StartComponentCleanup /ResetBase），
  删掉被取代的组件版本 —— 这是 WinSxS 里最大的一块。耗时较长（可能十几分钟），
  且清理后系统无法再卸载已安装的更新。

.PARAMETER DropWinRE
  在深度清理时一并丢弃映像自带的 WinRE.wim（约 500~700 MB）。代价是装好的系统
  没有恢复环境，「重置此电脑」不可用。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-winpe.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-winpe.ps1 -WithPowerShell -Iso

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build-winpe.ps1 -ImageDir D:\iso
#>
[CmdletBinding()]
param(
    [string]   $OutputDir,
    [string]   $WorkDir,
    [string]   $ExtrasDir,
    [string]   $AdkRoot,
    [string[]] $Components,
    [string]   $DriversDir,
    [switch]   $WithPowerShell,
    [switch]   $Iso,
    [switch]   $KeepWork,
    [switch]   $AllowOverSize,
    [long]     $MaxSizeMB = 1024,
    [string]   $ImageDir,
    [int]      $ImageIndex = 0,
    [string]   $ImageOutDir,
    [ValidateSet('fast', 'max', 'recovery')]
    [string]   $ImageCompress = 'max',
    [switch]   $DeepSlim,
    [switch]   $DropWinRE
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'runtime\pe' }
if (-not $WorkDir)   { $WorkDir   = Join-Path $RepoRoot 'build\winpe' }
if (-not $ExtrasDir) { $ExtrasDir = Join-Path $PSScriptRoot 'pe-extras' }

function Write-Step { param([string] $Text) Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Text) Write-Host "    $Text" -ForegroundColor Green }
function Write-Note { param([string] $Text) Write-Host "    $Text" -ForegroundColor Yellow }

function Invoke-Native {
    param([string] $Exe, [string[]] $Arguments, [string] $What)
    Write-Host "    > $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray

    # ADK 的 copype.cmd 在结尾执行 `exit /b`。直接从 PowerShell 调用时，
    # 某些 ADK 版本会连同当前 PowerShell 调用链一起提前结束，后续挂载和注入步骤
    # 完全不会执行。让批处理文件在独立 cmd.exe 子进程中运行可避免此问题。
    if ([System.IO.Path]::GetExtension($Exe).Equals('.cmd', [System.StringComparison]::OrdinalIgnoreCase)) {
        # 必须使用 CALL；否则某些 ADK 的批处理会把 /c 的调用链直接结束，
        # PowerShell 看不到后续的挂载、注入和导出步骤。
        $quotedExe = '"' + $Exe.Replace('"', '\"') + '"'
        $quotedArgs = @($Arguments | ForEach-Object {
                $text = [string]$_
                if ($text -match '[\s"]') { '"' + $text.Replace('"', '\"') + '"' } else { $text }
            }) -join ' '
        & $env:ComSpec /d /c "call $quotedExe $quotedArgs"
    }
    else {
        & $Exe @Arguments
    }

    if ($LASTEXITCODE -ne 0) {
        throw "$What 失败（退出码 $LASTEXITCODE）。"
    }
}

function Find-Copype {
    param([string] $Root)

    $bases = New-Object System.Collections.Generic.List[string]
    if ($Root) { $bases.Add($Root) }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    foreach ($pf in $programFilesX86, $env:ProgramFiles) {
        if ($pf) {
            $bases.Add((Join-Path $pf 'Windows Kits\10\Assessment and Deployment Kit'))
            $bases.Add((Join-Path $pf 'Windows Kits\11\Assessment and Deployment Kit'))
        }
    }

    # 附加组件的目录布局随 ADK 版本变化，两种都要试：
    #   ADK 10.1.28000.1 起：<ADK>\Windows Preinstallation Environment\copype.cmd
    #   更早的版本：        <ADK>\Windows Preinstallation Environment\<arch>\copype.cmd
    # 最后一项兼容 -AdkRoot 直接指向 Windows Preinstallation Environment 目录的情况。
    $relatives = @(
        'Windows Preinstallation Environment\copype.cmd',
        'Windows Preinstallation Environment\amd64\copype.cmd',
        'Windows Preinstallation Environment\x64\copype.cmd',
        'copype.cmd'
    )

    foreach ($base in $bases) {
        foreach ($relative in $relatives) {
            $candidate = Join-Path $base $relative
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }

    return $null
}

function Find-ToolFolder {
    param([string] $Root, [string] $Name, [string[]] $ExeNames)

    if (-not (Test-Path -LiteralPath $Root)) { return $null }

    $key = ($Name -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()

    # 首选：与工具同名的子目录（推荐布局）
    foreach ($dir in Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue) {
        if ((($dir.Name -replace '[^a-zA-Z0-9]', '').ToLowerInvariant()) -eq $key) {
            return $dir.FullName
        }
    }

    # 兜底：递归找可执行文件，取它所在目录
    $best = $null
    foreach ($exe in $ExeNames) {
        $hit = Get-ChildItem -LiteralPath $Root -Recurse -Filter $exe -File -ErrorAction SilentlyContinue |
               Sort-Object { $_.FullName.Length } |
               Select-Object -First 1
        if ($hit -and (-not $best -or $hit.FullName.Length -lt $best.Length)) {
            $best = $hit.DirectoryName
        }
    }

    return $best
}

function Get-PathFileSystem {
    param([string] $Path)

    # 只认盘符路径（C:\...）。UNC 与设备路径查不到，返回 $null 表示「不判断」。
    try {
        $root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($Path))
    }
    catch {
        return $null
    }

    if ($root -notmatch '^[A-Za-z]:\\$') { return $null }

    try {
        $volume = Get-CimInstance -ClassName Win32_LogicalDisk `
                                  -Filter "DeviceID='$($root.Substring(0, 2))'" `
                                  -ErrorAction Stop
    }
    catch {
        return $null
    }

    if ($volume) { return [string] $volume.FileSystem }
    return $null
}

function Read-Choice {
    param([int] $Maximum, [string] $Prompt)

    if (-not [Environment]::UserInteractive) {
        throw "需要交互式输入（$Prompt）。请在 PowerShell 窗口里运行本脚本，或用参数显式指定。"
    }

    while ($true) {
        $answer = Read-Host $Prompt
        $number = 0
        if ($answer -and [int]::TryParse($answer.Trim(), [ref] $number) -and $number -ge 1 -and $number -le $Maximum) {
            return $number
        }

        Write-Note "请输入 1 到 $Maximum 之间的数字。"
    }
}

function Mount-IsoImage {
    param([string] $Path)

    $image = Mount-DiskImage -ImagePath $Path -PassThru -ErrorAction Stop
    $volume = $image | Get-Volume -ErrorAction SilentlyContinue
    if (-not $volume -or -not $volume.DriveLetter) {
        Start-Sleep -Seconds 1
        $volume = Get-Volume -Partition ($image | Get-Partition) -ErrorAction SilentlyContinue
    }

    if (-not $volume -or -not $volume.DriveLetter) {
        throw "ISO 已挂载但没有分配到盘符：$Path"
    }

    return [string] $volume.DriveLetter
}

function Get-WimEditions {
    param([string] $Dism, [string] $WimFile)

    # 首选 DISM 模块的 Get-WindowsImage：拿到的是对象，不受系统语言影响
    try {
        $images = @(Get-WindowsImage -ImagePath $WimFile -ErrorAction Stop |
                    Select-Object @{ Name = 'Index'; Expression = { $_.ImageIndex } },
                                  @{ Name = 'Name';  Expression = { $_.ImageName } })
        if ($images.Count -gt 0) { return $images }
    }
    catch {
        Write-Note "Get-WindowsImage 读取失败，改用 dism 输出解析：$($_.Exception.Message)"
    }

    # 兜底：解析 dism /Get-WimInfo 的文本（标签随系统语言变化，中英两种写法都认）
    $text = & $Dism '/Get-WimInfo' "/WimFile:$WimFile" | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "读取映像信息失败（退出码 $LASTEXITCODE）：$WimFile"
    }

    $editions = New-Object System.Collections.Generic.List[object]
    $current = $null

    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^\s*(?:Index|索引)\s*[:：]\s*(\d+)\s*$') {
            $current = [pscustomobject]@{ Index = [int] $Matches[1]; Name = '' }
            $editions.Add($current)
            continue
        }

        if ($null -eq $current) { continue }

        if ($line -match '^\s*(?:Name|名称)\s*[:：]\s*(.+?)\s*$') {
            $current.Name = $Matches[1]
        }
    }

    return $editions
}

# ===========================================================================
#  0. 前置检查
# ===========================================================================
Write-Step '检查运行环境'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请以管理员身份运行本脚本：DISM 挂载/卸载 WIM 需要管理员权限。'
}
Write-Ok '管理员权限确认'

# DISM 的 WIM 挂载点是靠 NTFS 重解析点实现的：/Mount-Image 会把挂载目录本身变成
# 一个 reparse point，里面的文件按需从 WIM 读出。exFAT / FAT32 没有重解析点，
# 于是挂载会「成功」（DISM 退出码 0，所以 copype 不会报 Failed to mount），
# 但目录始终是空的 —— 随后拷 bootmgfw.efi 失败、卸载时报 still mounted。
# 因此工作目录必须落在 NTFS 卷上，不是就自动换到系统盘。
$workFs = Get-PathFileSystem -Path $WorkDir
if ($workFs -and $workFs -ne 'NTFS') {
    $originalWorkDir = $WorkDir
    $WorkDir = Join-Path $env:SystemDrive 'CTPrep-build\winpe'
    Write-Note "工作目录所在卷是 $workFs，DISM 挂载 WIM 需要 NTFS。"
    Write-Note "  原路径：$originalWorkDir"
    Write-Note "  已改用：$WorkDir"
}

$copype = Find-Copype -Root $AdkRoot
if (-not $copype) {
    Write-Host @'
未找到 Windows ADK 的 copype.cmd。

请先安装 Windows ADK 与「Windows PE 附加组件」，两者版本必须一致。
官方下载（成对，ADK 10.1.28000.1）：
  ADK           https://go.microsoft.com/fwlink/?linkid=2337875
  PE 附加组件    https://go.microsoft.com/fwlink/?linkid=2337681
分别运行 adksetup.exe 与 adkwinpesetup.exe，勾选 Deployment Tools 和
Windows Preinstallation Environment 即可，其余组件不必装。

不要用 winget 装 PE 附加组件：winget 源的 Microsoft.WindowsADK.WinPEAddon
清单停留在 10.1.26100.2454，而微软已下架该版本的载荷，安装会以
0x80091007（payload 哈希校验失败）结束。

装好后重新运行本脚本，或用 -AdkRoot 指定 ADK 根目录。
'@ -ForegroundColor Yellow

    throw '未找到 copype.cmd。'
}
Write-Ok "ADK：$copype"

# copype.cmd 在旧布局里位于 <arch>\ 下，新布局里直接位于 WinPE 根目录。
# 统一推导出 WinPE 根目录，两种布局都能得到正确的 WinPE_OCs 位置。
$peRoot = Split-Path -Parent $copype
if ((Split-Path -Leaf $peRoot) -in @('amd64', 'x64', 'x86', 'arm64')) {
    $peRoot = Split-Path -Parent $peRoot
}
$adkRootReal = Split-Path -Parent $peRoot

$ocDir = Join-Path $peRoot 'amd64\WinPE_OCs'
if (-not (Test-Path -LiteralPath $ocDir)) { throw "找不到 WinPE 可选组件目录：$ocDir" }

$makeWinPEMedia = Join-Path $peRoot 'MakeWinPEMedia.cmd'
if (-not (Test-Path -LiteralPath $makeWinPEMedia)) {
    $makeWinPEMedia = Join-Path $peRoot 'amd64\MakeWinPEMedia.cmd'
}

# ADK 的工具目录带架构名。32 位宿主下 PROCESSOR_ARCHITECTURE 是 x86，
# 真实架构在 PROCESSOR_ARCHITEW6432 里（与 DandISetEnv.bat 的处理一致）。
$hostArch = $env:PROCESSOR_ARCHITECTURE
if ($env:PROCESSOR_ARCHITEW6432) { $hostArch = $env:PROCESSOR_ARCHITEW6432 }
$deployTools = Join-Path $adkRootReal "Deployment Tools\$($hostArch.ToLowerInvariant())"

# copype.cmd 靠这些环境变量定位 WinPE 源码与工具，直接调用时它们为空，
# 会报 "The following processor architecture was not found"。
# 这里按 DandISetEnv.bat 的规则补齐（copype.cmd 只用到这三个）。
$env:WinPERoot   = $peRoot
$env:DISMRoot    = Join-Path $deployTools 'DISM'
$env:OSCDImgRoot = Join-Path $deployTools 'Oscdimg'

$dism = Join-Path $deployTools 'DISM\dism.exe'
if (-not (Test-Path -LiteralPath $dism)) {
    $dism = 'dism.exe'
    Write-Note '未找到 ADK 自带的 DISM，改用系统 dism.exe'
}

if (-not $Components -or $Components.Count -eq 0) {
    # 基础 WinPE 已自带 cmd、wpeinit、dism、diskpart 和 bcdboot，正好满足
    # CTPrep 的部署脚本需求。可选组件必须和基础 WIM 使用完全相同的 ADK 版本；
    # 不同版本混用会在 /Add-Package 时返回错误 87，因此默认不注入组件。
    $Components = @()
    if ($WithPowerShell) {
        $Components = @('WinPE-NetFx', 'WinPE-PowerShell', 'WinPE-DismCmdlets')
    }
}
Write-Ok "可选组件：$(if ($Components.Count -eq 0) { '无（使用基础 WinPE 组件）' } else { $Components -join ', ' })"

$startnetSource = Join-Path $RepoRoot 'src\CtPrep.App\Assets\pe\startnet.cmd'
if (-not (Test-Path -LiteralPath $startnetSource)) {
    throw "找不到 PE 自动运行脚本：$startnetSource"
}

# ===========================================================================
#  1. 生成 PE 骨架
# ===========================================================================
Write-Step '生成 PE 骨架（copype）'

# copype.cmd 要求目标目录不存在（它自己会创建），所以这里只清残留、不预先建目录。
if (Test-Path -LiteralPath $WorkDir) {
    Write-Note "清理上次残留的构建目录：$WorkDir"
    Remove-Item -LiteralPath $WorkDir -Recurse -Force
}

Invoke-Native -Exe $copype -Arguments @('amd64', $WorkDir) -What 'copype'

$bootWim = Join-Path $WorkDir 'media\sources\boot.wim'
$bootSdi = Join-Path $WorkDir 'media\Boot\boot.sdi'
$mount   = Join-Path $WorkDir 'mount'

if (-not (Test-Path -LiteralPath $bootWim)) { throw "copype 未生成 boot.wim：$bootWim" }
if (-not (Test-Path -LiteralPath $bootSdi)) { throw "copype 未生成 boot.sdi：$bootSdi" }
Write-Ok ("骨架 boot.wim：{0:N1} MB" -f ((Get-Item -LiteralPath $bootWim).Length / 1MB))

# ===========================================================================
#  2. 挂载 → 加组件 → 精简 → 烘焙脚本 → 放入工具
# ===========================================================================
$mounted = $false
try {
    Write-Step '挂载 boot.wim'
    Invoke-Native -Exe $dism -Arguments @('/Mount-Image', "/ImageFile:$bootWim", '/Index:1', "/MountDir:$mount") -What '挂载 boot.wim'
    $mounted = $true

    Write-Step '添加 WinPE 可选组件'
    foreach ($component in $Components) {
        $cab = Join-Path $ocDir "$component.cab"
        if (-not (Test-Path -LiteralPath $cab)) {
            Write-Note "跳过不存在的组件：$component"
            continue
        }
        Invoke-Native -Exe $dism -Arguments @("/Image:$mount", '/Add-Package', "/PackagePath:$cab") -What "添加 $component"
        Write-Ok $component
    }

    if ($DriversDir) {
        Write-Step '注入驱动'
        if (-not (Test-Path -LiteralPath $DriversDir)) { throw "驱动目录不存在：$DriversDir" }
        Invoke-Native -Exe $dism -Arguments @("/Image:$mount", '/Add-Driver', "/Driver:$DriversDir", '/Recurse') -What '注入驱动'
    }

    Write-Step '精简映像'
    foreach ($relative in 'Windows\System32\Recovery', 'Windows\System32\config\RegBack') {
        $target = Join-Path $mount $relative
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
            Write-Ok "已删除 $relative"
        }
    }

    Write-Step '烘焙 CTPrep 自动部署脚本'
    $bytes = [System.IO.File]::ReadAllBytes($startnetSource)
    $nonAscii = $bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1
    if ($null -ne $nonAscii) {
        $hex = '{0:X2}' -f $nonAscii
        throw "startnet.cmd 含有非 ASCII 字节（0x$hex）。PE 控制台是代码页 437，该字节会被 cmd 当作运算符解析。请改为纯 ASCII。"
    }
    Copy-Item -LiteralPath $startnetSource -Destination (Join-Path $mount 'Windows\System32\startnet.cmd') -Force
    Write-Ok 'Windows\System32\startnet.cmd'

    Write-Step '放入第三方工具（X:\Tools）'
    $toolSpecs = @(
        [pscustomobject]@{ Name = 'DiskGenius'; Exes = @('DiskGenius.exe', 'DiskGenius_x64.exe', 'DiskGenius64.exe') },
        [pscustomobject]@{ Name = 'WinNTSetup'; Exes = @('WinNTSetup_x64.exe', 'WinNTSetup.exe', 'WinNTSetup_x86.exe') },
        [pscustomobject]@{ Name = 'Dism++';     Exes = @('Dism++x64.exe', 'Dism++x86.exe', 'Dism++.exe') }
    )

    $toolsDest = Join-Path $mount 'Tools'
    $missingTools = New-Object System.Collections.Generic.List[string]

    foreach ($spec in $toolSpecs) {
        $source = Find-ToolFolder -Root $ExtrasDir -Name $spec.Name -ExeNames $spec.Exes
        if (-not $source) {
            $missingTools.Add($spec.Name)
            Write-Note "未找到 $($spec.Name)，PE 内该项不可用"
            continue
        }

        $dest = Join-Path $toolsDest $spec.Name
        New-Item -ItemType Directory -Path $dest -Force | Out-Null

        if ((Resolve-Path -LiteralPath $source).Path -eq (Resolve-Path -LiteralPath $ExtrasDir).Path) {
            # 工具直接堆在 ExtrasDir 根目录：只能复制可执行文件本身
            foreach ($exe in $spec.Exes) {
                $file = Join-Path $source $exe
                if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $dest -Force }
            }
        }
        else {
            Copy-Item -Path (Join-Path $source '*') -Destination $dest -Recurse -Force
        }

        Write-Ok "$($spec.Name) <- $source"
    }

    Write-Step '体积构成（最大的几个目录）'
    Get-ChildItem -LiteralPath (Join-Path $mount 'Windows') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $size = (Get-ChildItem -LiteralPath $_.FullName -Recurse -File -ErrorAction SilentlyContinue |
                     Measure-Object -Property Length -Sum).Sum
            [pscustomobject]@{ Folder = $_.Name; MB = [math]::Round($size / 1MB, 1) }
        } |
        Sort-Object MB -Descending |
        Select-Object -First 5 |
        ForEach-Object { Write-Host ("    {0,-20} {1,8:N1} MB" -f $_.Folder, $_.MB) }

    Write-Step '提交并卸载 boot.wim'
    Invoke-Native -Exe $dism -Arguments @('/Unmount-Image', "/MountDir:$mount", '/Commit') -What '提交卸载'
    $mounted = $false
}
finally {
    if ($mounted) {
        Write-Note '构建中断，正在放弃挂载点...'
        & $dism '/Unmount-Image' "/MountDir:$mount" '/Discard' | Out-Null
    }
}

# ===========================================================================
#  3. 最大压缩重新导出
# ===========================================================================
Write-Step '以 LZX 最大压缩重新导出（这一步需要几分钟）'
$tempWim = Join-Path $WorkDir 'boot.max.wim'
Invoke-Native -Exe $dism -Arguments @(
    '/Export-Image',
    "/SourceImageFile:$bootWim",
    '/SourceIndex:1',
    "/DestinationImageFile:$tempWim",
    '/Compress:max',
    '/Bootable'
) -What '导出 boot.wim'
Move-Item -LiteralPath $tempWim -Destination $bootWim -Force

# ===========================================================================
#  4. 输出产物
# ===========================================================================
Write-Step '写出产物'
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$outputWim = Join-Path $OutputDir 'boot.wim'
$outputSdi = Join-Path $OutputDir 'boot.sdi'
Copy-Item -LiteralPath $bootWim -Destination $outputWim -Force
Copy-Item -LiteralPath $bootSdi -Destination $outputSdi -Force
Write-Ok $outputWim
Write-Ok $outputSdi

if ($Iso) {
    Write-Step '生成启动 ISO'
    if (-not (Test-Path -LiteralPath $makeWinPEMedia)) { throw "找不到 MakeWinPEMedia.cmd：$makeWinPEMedia" }
    $isoPath = Join-Path $OutputDir 'CTPrep-WinPE.iso'
    Invoke-Native -Exe $makeWinPEMedia -Arguments @('/ISO', $WorkDir, $isoPath) -What '生成 ISO'
    Write-Ok $isoPath
}

# ===========================================================================
#  5. 体积报告
# ===========================================================================
Write-Step '体积报告'

$wimMB = (Get-Item -LiteralPath $outputWim).Length / 1MB
$totalMB = (Get-ChildItem -LiteralPath $OutputDir -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum / 1MB

Write-Host ("    boot.wim : {0,8:N1} MB" -f $wimMB)
Write-Host ("    目录合计 : {0,8:N1} MB  (上限 {1:N0} MB)" -f $totalMB, $MaxSizeMB)

if ($totalMB -gt $MaxSizeMB) {
    if ($AllowOverSize) {
        Write-Note "已超出 $MaxSizeMB MB，因指定了 -AllowOverSize 继续执行。"
    }
    else {
        throw "产物合计 $([math]::Round($totalMB,1)) MB，超过上限 $MaxSizeMB MB。请去掉 -WithPowerShell、精简第三方工具，或用 -MaxSizeMB 调整上限。"
    }
}

if ($missingTools.Count -gt 0) {
    Write-Note "以下工具未打进 PE：$($missingTools -join ', ')"
    Write-Note "把它们的文件夹放进 $ExtrasDir 后重新运行本脚本即可。"
}

if (-not $KeepWork) {
    Write-Step '清理临时构建目录'
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Ok "已删除 $WorkDir"
}

# ===========================================================================
#  6. 精简系统安装镜像（只在指定了 -ImageDir 时执行）
# ===========================================================================
if ($ImageDir) {
    Write-Step '精简系统安装镜像'

    if (-not (Test-Path -LiteralPath $ImageDir)) {
        throw "找不到映像目录：$ImageDir"
    }

    $imageInput = Get-Item -LiteralPath $ImageDir
    $imageSearchRoot = $null
    $candidates = @()

    if ($imageInput.PSIsContainer) {
        $imageSearchRoot = $imageInput.FullName
        if (-not $ImageOutDir) { $ImageOutDir = Join-Path $imageSearchRoot 'slim' }

        $candidates = @(Get-ChildItem -LiteralPath $imageSearchRoot -File -ErrorAction SilentlyContinue |
                        Where-Object { $_.Extension -in '.iso', '.wim', '.esd' })
    }
    else {
        if (-not $ImageOutDir) { $ImageOutDir = Join-Path $imageInput.DirectoryName 'slim' }
        $candidates = @($imageInput)
    }

    # 输出目录本身就是产物所在地，不要把它当成输入再扫一遍
    # 前缀匹配必须带结尾分隔符，否则 slim2 之类目录会被误排除
    $outFullPath = [System.IO.Path]::GetFullPath($ImageOutDir).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $outPrefix = $outFullPath + [System.IO.Path]::DirectorySeparatorChar
    $candidates = @($candidates | Where-Object {
        -not $_.FullName.StartsWith($outPrefix, [System.StringComparison]::OrdinalIgnoreCase)
    })

    if ($imageSearchRoot -and $candidates.Count -eq 0) {
        $candidates = @(Get-ChildItem -LiteralPath $imageSearchRoot -Recurse -File -ErrorAction SilentlyContinue |
                        Where-Object {
                            ($_.Extension -in '.iso', '.wim', '.esd') -and
                            (-not $_.FullName.StartsWith($outPrefix, [System.StringComparison]::OrdinalIgnoreCase))
                        })
    }

    if ($candidates.Count -eq 0) {
        throw "在 $ImageDir 下没有找到 .iso / .wim / .esd 系统安装镜像。"
    }

    if ($candidates.Count -eq 1) {
        $sourceImage = $candidates[0]
        Write-Ok "使用的映像：$($sourceImage.FullName)"
    }
    else {
        Write-Host '    发现多个镜像：'
        for ($i = 0; $i -lt $candidates.Count; $i++) {
            Write-Host ("      [{0}] {1}" -f ($i + 1), $candidates[$i].FullName)
        }
        $pick = Read-Choice -Maximum $candidates.Count -Prompt '  请输入要使用的镜像编号'
        $sourceImage = $candidates[$pick - 1]
    }

    $wimPath = $sourceImage.FullName
    $mountedIso = $null
    try {
        if ($sourceImage.Extension -eq '.iso') {
            Write-Host '    正在挂载 ISO ...'
            $letter = Mount-IsoImage -Path $wimPath
            $mountedIso = $wimPath

            $innerWim = @('install.wim', 'install.esd') |
                ForEach-Object { Join-Path "${letter}:\sources" $_ } |
                Where-Object { Test-Path -LiteralPath $_ } |
                Select-Object -First 1

            if (-not $innerWim) {
                throw "ISO 的 sources 目录下没有 install.wim / install.esd：$wimPath"
            }

            $wimPath = $innerWim
            Write-Ok "ISO 内的系统映像：$wimPath"
        }

        Write-Host '    正在读取版本列表 ...'
        $editions = Get-WimEditions -Dism $dism -WimFile $wimPath
        if ($editions.Count -eq 0) {
            throw "未能从映像中读出任何版本：$wimPath（可手动运行 `"$dism`" /Get-WimInfo /WimFile:`"$wimPath`" 确认）"
        }

        Write-Host ''
        Write-Host '    映像内包含以下版本：'
        foreach ($edition in $editions) {
            Write-Host ("      [{0}] {1}" -f $edition.Index, $edition.Name)
        }
        Write-Host ''

        $keepIndex = $ImageIndex
        if ($keepIndex -le 0) {
            if ($editions.Count -eq 1) {
                $keepIndex = $editions[0].Index
                Write-Note "该映像只有一个版本，直接保留：$($editions[0].Name)"
            }
            else {
                $maxIndex = ($editions | Measure-Object -Property Index -Maximum).Maximum
                $keepIndex = Read-Choice -Maximum $maxIndex -Prompt '  请输入要保留的版本编号（其余版本会被移除）'
            }
        }

        $chosen = $editions | Where-Object { $_.Index -eq $keepIndex } | Select-Object -First 1
        if (-not $chosen) {
            throw "映像里没有 Index = $keepIndex 的版本（也可能它不是列表中的编号）。请重新运行并从上面的列表里选择。"
        }

        $chosenName = $chosen.Name
        if ($editions.Count -gt 1) {
            Write-Note "将只保留「$chosenName」，丢弃其余 $($editions.Count - 1) 个版本。"
        }

        New-Item -ItemType Directory -Path $ImageOutDir -Force | Out-Null
        $slimWim = Join-Path $ImageOutDir 'install.wim'
        if (Test-Path -LiteralPath $slimWim) { Remove-Item -LiteralPath $slimWim -Force }

        # -DeepSlim 要挂载映像，而挂载只支持 LZX，所以先出一个 LZX 中间件，
        # 清理完再按目标格式压一遍；不清理时直接一次导出到最终文件。
        $workingWim = Join-Path $ImageOutDir $(if ($DeepSlim) { 'install.work.wim' } else { 'install.wim' })
        if (Test-Path -LiteralPath $workingWim) { Remove-Item -LiteralPath $workingWim -Force }

        Write-Step "导出「$chosenName」（这一步需要几分钟）"
        Invoke-Native -Exe $dism -Arguments @(
            '/Export-Image',
            "/SourceImageFile:$wimPath",
            "/SourceIndex:$keepIndex",
            "/DestinationImageFile:$workingWim",
            "/Compress:$(if ($DeepSlim) { 'max' } else { $ImageCompress })"
        ) -What '导出系统映像'

        $sizeBefore = (Get-Item -LiteralPath $workingWim).Length / 1MB

        if ($DeepSlim) {
            Write-Step '深度精简（挂载映像做离线清理）'

            # 挂载点必须是 NTFS（WIM 挂载靠重解析点实现），用的是构建工作目录所在的卷。
            $mountBase = $WorkDir
            if (-not (Test-Path -LiteralPath $mountBase)) {
                $mountBase = Join-Path $env:SystemDrive 'CTPrep-build'
            }
            $imageMount = Join-Path $mountBase 'image'
            if (Test-Path -LiteralPath $imageMount) { Remove-Item -LiteralPath $imageMount -Recurse -Force }
            New-Item -ItemType Directory -Path $imageMount -Force | Out-Null

            Invoke-Native -Exe $dism -Arguments @(
                '/Mount-Image', "/ImageFile:$workingWim", '/Index:1', "/MountDir:$imageMount"
            ) -What '挂载系统映像'

            $imageMounted = $true
            try {
                if ($DropWinRE) {
                    $winre = Join-Path $imageMount 'Windows\System32\Recovery\WinRE.wim'
                    if (Test-Path -LiteralPath $winre) {
                        Remove-Item -LiteralPath $winre -Force
                        Write-Ok '已丢弃 WinRE.wim'
                    }
                }

                foreach ($relative in 'Windows\Panther', 'Windows\Temp') {
                    $target = Join-Path $imageMount $relative
                    if (Test-Path -LiteralPath $target) {
                        Get-ChildItem -LiteralPath $target -Force -ErrorAction SilentlyContinue |
                            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                    }
                }
                Write-Ok '已清理安装日志与临时目录'

                Write-Note '正在清理组件存储（ResetBase），这一步可能要十几分钟...'
                Invoke-Native -Exe $dism -Arguments @(
                    "/Image:$imageMount", '/Cleanup-Image', '/StartComponentCleanup', '/ResetBase'
                ) -What '离线清理组件存储'

                Invoke-Native -Exe $dism -Arguments @(
                    '/Unmount-Image', "/MountDir:$imageMount", '/Commit'
                ) -What '提交系统映像'
                $imageMounted = $false
            }
            finally {
                if ($imageMounted) {
                    & $dism '/Unmount-Image' "/MountDir:$imageMount" '/Discard' | Out-Null
                }
            }

            Write-Ok ("离线清理：{0:N0} MB -> {1:N0} MB" -f $sizeBefore, ((Get-Item -LiteralPath $workingWim).Length / 1MB))
        }

        # 清理后的中间件是 LZX；目标格式不同就再压一遍（LZMS 无法挂载，只能放最后）。
        if ($DeepSlim) {
            if ($ImageCompress -eq 'max') {
                Move-Item -LiteralPath $workingWim -Destination $slimWim -Force
            }
            else {
                Write-Step "按 $ImageCompress 重新压缩"
                Invoke-Native -Exe $dism -Arguments @(
                    '/Export-Image',
                    "/SourceImageFile:$workingWim",
                    '/SourceIndex:1',
                    "/DestinationImageFile:$slimWim",
                    "/Compress:$ImageCompress"
                ) -What '最终导出'
                Remove-Item -LiteralPath $workingWim -Force -ErrorAction SilentlyContinue
            }
        }

        $result = Get-WimEditions -Dism $dism -WimFile $slimWim
        if ($result.Count -ne 1) {
            Write-Note "注意：导出后映像内仍有 $($result.Count) 个版本，请检查上面的 DISM 输出。"
        }

        Write-Host ''
        Write-Host ("    保留版本 : {0}" -f $chosenName)
        Write-Host ("    原有版本 : {0} 个，已丢弃 {1} 个" -f $editions.Count, ($editions.Count - 1))
        Write-Host ("    映像体积 : {0:N0} MB（压缩方式 {1}）" -f ((Get-Item -LiteralPath $slimWim).Length / 1MB), $ImageCompress)
        Write-Ok $slimWim

        if (-not $DeepSlim) {
            Write-Note '各版本之间共享绝大部分文件（WinSxS、驱动、语言资源），所以「只保留一个版本」本身省不了多少。'
            Write-Note '想再压一截：加 -DeepSlim（离线组件清理），以及 -DropWinRE（丢弃 WinRE.wim，约 500 MB）。'
        }
        Write-Host ''
        Write-Host '    在 config.ini / custom.ini 的 [Image] 段里指向它即可（绝对路径可以直接用）：'
        Write-Host '        [Image]'
        Write-Host '        Default=SlimImage'
        Write-Host ("        SlimImage={0}" -f $slimWim)
    }
    finally {
        if ($mountedIso) {
            Dismount-DiskImage -ImagePath $mountedIso -ErrorAction SilentlyContinue | Out-Null
            Write-Note "已卸载 ISO：$mountedIso"
        }
    }
}

# ===========================================================================
#  7. 下一步
# ===========================================================================
Write-Host ''
Write-Host '构建完成。' -ForegroundColor Green
Write-Host '把 config.ini / custom.ini 里的 PE 地址指向本产物即可（程序会自动在其同目录找 boot.sdi）：'
Write-Host '    [PE]'
Write-Host '    Default=LocalPe'
Write-Host '    LocalPe=.\runtime\pe\boot.wim'
Write-Host ''
Write-Host '安装镜像的 \boot\boot.sdi 也已内置回退路径，通常无需额外准备。'

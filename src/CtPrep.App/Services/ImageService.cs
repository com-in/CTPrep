using System.IO.Compression;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>镜像/PE 文件处理：ISO 挂载、ZIP 解压、定位 install.wim / boot.wim。</summary>
public sealed class ImageService
{
    private readonly ProcessRunner _runner;
    private readonly ILogSink _log;
    private readonly DismService _dism;

    public ImageService(ProcessRunner runner, ILogSink log, DismService dism)
    {
        _runner = runner;
        _log = log;
        _dism = dism;
    }

    /// <summary>从 ISO / WIM / ESD 中取出可部署的系统映像（install.wim 或 install.esd），返回其路径。</summary>
    public async Task<string> ExtractInstallImageAsync(string sourcePath, string workDir, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is ".wim" or ".esd")
        {
            return sourcePath;
        }

        if (ext != ".iso")
        {
            throw new NotSupportedException($"不支持的系统镜像格式：{ext}（仅支持 .iso / .wim / .esd）");
        }

        Directory.CreateDirectory(workDir);
        var letter = await MountIsoAsync(sourcePath, ct).ConfigureAwait(false);
        try
        {
            var sources = Path.Combine(letter + ":", "sources");
            var candidates = new[] { "install.wim", "install.esd" }
                .Select(n => Path.Combine(sources, n))
                .Where(File.Exists)
                .ToList();

            if (candidates.Count == 0)
            {
                throw new FileNotFoundException($"在 ISO 的 sources 目录下没有找到 install.wim / install.esd：{sourcePath}");
            }

            var src = candidates[0];
            var dest = Path.Combine(workDir, Path.GetFileName(src));
            _log.Info($"提取系统映像：{src} -> {dest}（约 {DownloadProgress.FormatSize(new FileInfo(src).Length)}）");
            await CopyFileAsync(src, dest, ct).ConfigureAwait(false);

            // 先确认这份映像真的能读，再让调用方去删原 ISO。
            // 顺序反过来的话，ISO 删了才发现 WIM 是坏的，就两头都没了。
            await VerifyImageAsync(dest, ct).ConfigureAwait(false);

            // 校验通过，上一轮留下的另一种格式（install.wim / install.esd）就没用了。
            // 一次部署只会用其中一个，留着就是几个 GB 白占地方。
            TryDeleteSiblings(workDir, dest);
            return dest;
        }
        finally
        {
            await DismountIsoAsync(sourcePath, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 校验提取出来的 install.wim / install.esd 是否可用：dism 能读出映像列表才算数。
    /// 读不出来就把这份半成品删掉，免得它和原 ISO 一起占着几个 GB。
    /// </summary>
    private async Task VerifyImageAsync(string imagePath, CancellationToken ct)
    {
        IReadOnlyList<ImageInfo> infos;
        try
        {
            infos = await _dism.GetWimInfoAsync(imagePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDelete(imagePath);
            throw new InvalidOperationException(
                $"提取出的映像无法读取，已删除该文件：{imagePath}（{ex.Message}）", ex);
        }

        if (infos.Count == 0)
        {
            TryDelete(imagePath);
            throw new InvalidOperationException($"提取出的映像里没有任何可用版本：{imagePath}");
        }

        _log.Info($"映像校验通过：{infos.Count} 个版本 —— {string.Join(" / ", infos.Select(i => i.Name))}");
    }

    /// <summary>
    /// 清掉工作目录里上一轮留下的另一种格式的映像（install.wim / install.esd）。
    /// 只认这两个文件名，目录里的其它东西一概不碰。
    /// </summary>
    private void TryDeleteSiblings(string workDir, string keepPath)
    {
        foreach (var name in new[] { "install.wim", "install.esd" })
        {
            var path = Path.Combine(workDir, name);
            if (!path.Equals(keepPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _log.Info($"已删除：{path}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"删除失败（不影响继续）：{path} -> {ex.Message}");
        }
    }

    /// <summary>从 PE 来源（.wim / .iso / .zip / .7z）中取出 64 位 PE 映像，返回其路径。</summary>
    public async Task<string> ExtractPeWimAsync(string sourcePath, string workDir, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext == ".wim")
        {
            return sourcePath;
        }

        Directory.CreateDirectory(workDir);

        if (ext is ".zip" or ".7z")
        {
            var extractDir = Path.Combine(workDir, "pe-extract");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            _log.Info($"解压 PE 压缩包：{sourcePath}");
            await ExtractArchiveAsync(sourcePath, extractDir, ext, ct).ConfigureAwait(false);

            // 雷电PE 这类整盘启动包，压缩包里放的是 ISO；ISO 里才带得全 boot.sdi 等结构
            var innerIso = Directory.GetFiles(extractDir, "*.iso", SearchOption.AllDirectories).FirstOrDefault();
            if (innerIso is not null)
            {
                _log.Info($"压缩包内找到 PE 光盘镜像：{innerIso}");
                var innerWim = await ExtractPeWimAsync(innerIso, workDir, ct).ConfigureAwait(false);
                TryDeleteDirectory(extractDir);
                return innerWim;
            }

            var picked = SelectPeWim(Directory.GetFiles(extractDir, "*.wim", SearchOption.AllDirectories))
                ?? throw new FileNotFoundException($"压缩包内没有找到可用的 64 位 PE 映像（*.wim）：{sourcePath}");

            await CopySiblingSdiAsync(picked, workDir, ct).ConfigureAwait(false);
            _log.Info($"使用压缩包内的 PE 映像：{picked}");
            return picked;
        }

        if (ext != ".iso")
        {
            throw new NotSupportedException($"不支持的 PE 格式：{ext}（仅支持 .wim / .iso / .zip / .7z）");
        }

        var letter = await MountIsoAsync(sourcePath, ct).ConfigureAwait(false);
        try
        {
            var root = letter + ":";
            var candidate = SelectPeWim(Directory.GetFiles(root, "*.wim", SearchOption.AllDirectories))
                ?? throw new FileNotFoundException($"ISO 中没有找到可用的 64 位 PE 映像（*.wim）：{sourcePath}");

            var dest = Path.Combine(workDir, "boot.wim");
            _log.Info($"提取 PE 映像：{candidate} -> {dest}");
            await CopyFileAsync(candidate, dest, ct).ConfigureAwait(false);

            // 一并取出 ramdisk 引导必需的 boot.sdi
            var sdi = SelectPeSdi(root, candidate);
            if (sdi is not null)
            {
                var sdiDest = Path.Combine(workDir, "boot.sdi");
                await CopyFileAsync(sdi, sdiDest, ct).ConfigureAwait(false);
                _log.Info($"已提取 boot.sdi：{sdi} -> {sdiDest}");
            }
            else
            {
                _log.Warn("ISO 内没有找到任何 *.sdi，稍后将回退到 Windows 自带的通用 boot.sdi。");
            }

            return dest;
        }
        finally
        {
            await DismountIsoAsync(sourcePath, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 在候选里挑出最合适的 PE 映像：优先 boot.wim，其次排除 32 位版本后优先名字带 64 的，
    /// 最后才退回路径最短的（避免深层的 install.wim 之类被误取）。
    /// </summary>
    private static string? SelectPeWim(IEnumerable<string> wimPaths)
    {
        var all = wimPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (all.Count == 0)
        {
            return null;
        }

        var bootWim = all.FirstOrDefault(p => Path.GetFileName(p).Equals("boot.wim", StringComparison.OrdinalIgnoreCase));
        if (bootWim is not null)
        {
            return bootWim;
        }

        var x64 = all.Where(p => !LooksLike32BitPe(p)).ToList();
        var pool = x64.Count > 0 ? x64 : all;

        return pool.FirstOrDefault(p => Path.GetFileName(p).Contains("64", StringComparison.Ordinal))
            ?? pool.OrderBy(p => p.Length).First();
    }

    /// <summary>
    /// 判断某个 wim 是否看起来是 32 位 PE。雷电PE 会同时给出 Dream-M_32.wim 与
    /// Dream-M_64.wim，而老的「取第一个 *.wim」兜底有取错的风险。
    /// </summary>
    private static bool LooksLike32BitPe(string wimPath)
    {
        var name = Path.GetFileNameWithoutExtension(wimPath);
        var dir = Path.GetDirectoryName(wimPath) ?? string.Empty;

        if (name.Contains("x86", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("32bit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.EndsWith("_32", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-32", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".32", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("32", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 挑出 ramdisk 引导用的 sdi：优先 ISO 标准位置，其次与 PE 映像同目录的 sdi，最后全盘任意一个。
    /// 雷电PE 的 sdi 不叫 boot.sdi（叫 Dream-M.sdi），所以不能只按文件名找。
    /// </summary>
    private static string? SelectPeSdi(string isoRoot, string peWimPath)
    {
        var standard = new[]
        {
            Path.Combine(isoRoot, "boot", "boot.sdi"),
            Path.Combine(isoRoot, "boot.sdi"),
        }.FirstOrDefault(File.Exists);

        if (standard is not null)
        {
            return standard;
        }

        var wimDir = Path.GetDirectoryName(peWimPath);
        if (wimDir is not null)
        {
            var sibling = Directory.GetFiles(wimDir, "*.sdi").FirstOrDefault();
            if (sibling is not null)
            {
                return sibling;
            }
        }

        return Directory.GetFiles(isoRoot, "*.sdi", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>解压 PE 压缩包：.zip 用内置解压，.7z 交给 Windows 自带的 tar.exe（libarchive）。</summary>
    private async Task ExtractArchiveAsync(string archivePath, string targetDir, string ext, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        if (ext == ".zip")
        {
            ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            return;
        }

        var result = await _runner
            .RunAsync("tar.exe", $"-xf \"{archivePath}\" -C \"{targetDir}\"", null, ct)
            .ConfigureAwait(false);
        result.EnsureSuccess("解压 PE 压缩包（tar.exe）");
    }

    /// <summary>把与 PE 映像同目录的 sdi（若有）复制成 boot.sdi，供 ramdisk 引导使用。</summary>
    private async Task CopySiblingSdiAsync(string wimPath, string workDir, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(wimPath);
        if (dir is null)
        {
            return;
        }

        var sdi = Directory.GetFiles(dir, "*.sdi").FirstOrDefault();
        if (sdi is null)
        {
            return;
        }

        var dest = Path.Combine(workDir, "boot.sdi");
        await CopyFileAsync(sdi, dest, ct).ConfigureAwait(false);
        _log.Info($"已提取 boot.sdi：{sdi} -> {dest}");
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>挂载 ISO 并返回盘符。</summary>
    public async Task<char> MountIsoAsync(string isoPath, CancellationToken ct = default)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
$path = '{isoPath.Replace("'", "''")}'
# 上次运行可能残留挂载，先卸载同名镜像，否则 Mount-DiskImage 会直接报已挂载
$prev = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
if ($null -ne $prev) {{
    Dismount-DiskImage -ImagePath $path -ErrorAction SilentlyContinue | Out-Null
}}
$img = Mount-DiskImage -ImagePath $path -PassThru
$vol = $img | Get-Volume
if (-not $vol.DriveLetter) {{
    Start-Sleep -Seconds 1
    $vol = Get-Volume -Partition ($img | Get-Partition)
}}
Write-Output ([string]$vol.DriveLetter)
";
        var result = await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
        result.EnsureSuccess("挂载 ISO");

        var text = result.StdOut.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException($"ISO 挂载成功但未获得盘符：{isoPath}");
        }

        _log.Info($"ISO 已挂载：{isoPath} -> {text[0]}:");
        return text[0];
    }

    /// <summary>卸载 ISO。</summary>
    public async Task DismountIsoAsync(string isoPath, CancellationToken ct = default)
    {
        try
        {
            var script = $@"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}'";
            await _runner.RunPowerShellAsync(script, null, ct).ConfigureAwait(false);
            _log.Info($"ISO 已卸载：{isoPath}");
        }
        catch (Exception ex)
        {
            _log.Warn($"卸载 ISO 失败（可忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 导出当前系统已安装的第三方驱动（pnputil /export-driver）。
    /// Windows 7 的 pnputil 没有 /export-driver，会直接失败；这里靠退出码判断后跳过，
    /// 不去做版本探测——版本探测在这类场景里比看退出码更容易误判。
    /// </summary>
    /// <returns>导出目录；失败或没有导出到任何 .inf 时返回 null，调用方跳过即可。</returns>
    public async Task<string?> ExportCurrentDriversAsync(string destDir, CancellationToken ct = default)
    {
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        if (!File.Exists(pnputil))
        {
            _log.Warn("未找到 pnputil.exe，跳过当前系统驱动导出。");
            return null;
        }

        Directory.CreateDirectory(destDir);
        var result = await _runner
            .RunAsync(pnputil, $"/export-driver * \"{destDir}\"", null, ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            _log.Warn($"导出当前系统驱动失败（pnputil 退出码 {result.ExitCode}），已跳过。{result.Combined}");
            return null;
        }

        var infCount = Directory.GetFiles(destDir, "*.inf", SearchOption.AllDirectories).Length;
        if (infCount == 0)
        {
            _log.Warn($"驱动导出命令成功，但目录里没有 .inf，已跳过：{destDir}");
            return null;
        }

        _log.Info($"已导出当前系统驱动 {infCount} 个包：{destDir}");
        return destDir;
    }

    /// <summary>读取 config.ini 里 [Drivers] 指定的驱动包并解压到目标目录，返回收集到的 inf 文件所在目录集合。</summary>
    public async Task<List<string>> StageDriversAsync(
        IEnumerable<string> sources,
        string runtimeDir,
        string targetDir,
        IProgress<DownloadProgress>? progress,
        DownloadService downloader,
        CancellationToken ct = default)
    {
        var result = new List<string>();
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }

        Directory.CreateDirectory(targetDir);
        var index = 0;
        var failed = 0;

        foreach (var source in sources.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            index++;
            var sub = Path.Combine(targetDir, $"pack{index}");
            Directory.CreateDirectory(sub);

            var ext = Path.GetExtension(source).ToLowerInvariant();
            try
            {
                if (ext is ".exe")
                {
                    // .exe 驱动安装包：直接放进目标目录，由 SetupComplete.cmd 静默执行
                    var file = await downloader.AcquireAsync(source, sub, null, progress, ct).ConfigureAwait(false);
                    result.Add(Path.GetDirectoryName(file)!);
                    continue;
                }

                if (ext is ".zip")
                {
                    var file = await downloader.AcquireAsync(source, Path.Combine(runtimeDir, "drivers-cache"), null, progress, ct).ConfigureAwait(false);
                    ZipFile.ExtractToDirectory(file, sub, overwriteFiles: true);
                    result.Add(sub);
                    continue;
                }

                if (Directory.Exists(ConfigService.ResolvePath(source)))
                {
                    var srcDir = ConfigService.ResolvePath(source);
                    CopyDirectory(srcDir, sub);
                    result.Add(sub);
                    continue;
                }

                // 其它情况：当作文件下载后原样放入
                var downloaded = await downloader.AcquireAsync(source, sub, null, progress, ct).ConfigureAwait(false);
                result.Add(Path.GetDirectoryName(downloaded)!);
            }
            catch (Exception ex)
            {
                failed++;
                _log.Warn($"驱动包处理失败，已跳过：{source} -> {ex.Message}");
            }
        }

        if (failed > 0)
        {
            _log.Warn($"共 {failed} 个驱动包处理失败并已跳过。");
        }

        return result;
    }

    private static async Task CopyFileAsync(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        await using var src = File.OpenRead(from);
        await using var dst = File.Create(to);
        await src.CopyToAsync(dst, 1 << 20, ct).ConfigureAwait(false);
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
}

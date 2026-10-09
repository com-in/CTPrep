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

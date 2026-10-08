using System.Text.RegularExpressions;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>单个映像索引的详情（dism 报告）；Unknown 表示读取失败。</summary>
public sealed record WimImageDetail(string Architecture, string Version, int MajorVersion)
{
    public static readonly WimImageDetail Unknown = new(string.Empty, string.Empty, 0);
}

/// <summary>DISM 封装：读取映像信息、挂载/卸载 WIM（用于注入 PE 部署载荷）。</summary>
public sealed class DismService
{
    private readonly ProcessRunner _runner;
    private readonly ILogSink _log;

    public DismService(ProcessRunner runner, ILogSink log)
    {
        _runner = runner;
        _log = log;
    }

    /// <summary>读取 WIM/ESD 内的映像列表。</summary>
    public async Task<IReadOnlyList<ImageInfo>> GetWimInfoAsync(string wimPath, CancellationToken ct = default)
    {
        // /English 保证输出为英文，便于稳定解析
        var result = await _runner
            .RunAsync("dism.exe", $"/English /Get-WimInfo /WimFile:\"{wimPath}\"", null, ct)
            .ConfigureAwait(false);
        result.EnsureSuccess("读取映像信息");

        var list = new List<ImageInfo>();
        ImageInfo? current = null;

        foreach (var raw in result.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Regex.Match(line, @"^(Index|Name|Description|Size)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                continue;
            }

            var key = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Value.Trim();

            switch (key)
            {
                case "index":
                    if (!int.TryParse(value, out var index) || index <= 0)
                    {
                        throw new InvalidOperationException($"解析映像索引失败：{value}");
                    }

                    current = new ImageInfo { Index = index };
                    list.Add(current);
                    break;
                case "name" when current is not null:
                    current.Name = value;
                    break;
                case "description" when current is not null && string.IsNullOrEmpty(current.Description):
                    current.Description = value;
                    break;
                case "size" when current is not null:
                    var digits = new string(value.Where(char.IsDigit).ToArray());
                    current.SizeBytes = long.TryParse(digits, out var size) ? size : 0;
                    break;
            }
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException($"未能从 {Path.GetFileName(wimPath)} 解析出任何映像索引。");
        }

        _log.Info($"镜像 {Path.GetFileName(wimPath)} 共 {list.Count} 个索引。");
        return list;
    }

    /// <summary>
    /// 读取单个映像索引的详情：真实版本号（如 6.1.7601 / 10.0.22621）与处理器架构。
    /// 版本号比映像名可靠（对改版/精简映像也准确）；任何失败都返回 Unknown，不抛异常。
    /// </summary>
    public async Task<WimImageDetail> GetWimInfoDetailAsync(string wimPath, int index, CancellationToken ct = default)
    {
        try
        {
            var result = await _runner
                .RunAsync("dism.exe", $"/English /Get-WimInfo /WimFile:\"{wimPath}\" /Index:{index}", null, ct)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                _log.Warn($"读取映像详情失败（退出码 {result.ExitCode}），按默认值处理。");
                return WimImageDetail.Unknown;
            }

            var arch = string.Empty;
            var version = string.Empty;
            var inDetails = false;
            foreach (var raw in result.StdOut.Split('\n'))
            {
                var line = raw.TrimEnd('\r');

                // 先跳过工具横幅：横幅里自带一行 "Version: <dism 自身版本>"，不能当成映像版本
                if (!inDetails)
                {
                    inDetails = line.Contains("Details for", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                var m = Regex.Match(line, @"^\s*(Architecture|Version)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    continue;
                }

                var key = m.Groups[1].Value.ToLowerInvariant();
                var value = m.Groups[2].Value.Trim();
                if (key == "architecture" && arch.Length == 0)
                {
                    arch = value;
                }
                else if (key == "version" && version.Length == 0)
                {
                    version = value;
                }
            }

            var major = MapMajorVersion(version);
            _log.Info($"映像 {index} 详情：架构 {(arch.Length == 0 ? "未知" : arch)}，版本 {(version.Length == 0 ? "未知" : version)}。");
            return new WimImageDetail(arch, version, major);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取映像架构/版本失败：{ex.Message}");
            return WimImageDetail.Unknown;
        }
    }

    /// <summary>把 dism 报告的映像版本号（6.1.7601 / 10.0.22621 …）映射成产品主版本：7 / 8 / 10 / 11；认不出来为 0。</summary>
    public static int MapMajorVersion(string version)
    {
        var parts = (version ?? string.Empty).Split('.');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return 0;
        }

        if (major == 6)
        {
            // 6.1 = Windows 7，6.2 = Windows 8，6.3 = Windows 8.1（对应答文件来说 8 与 8.1 同级）
            return minor switch { 1 => 7, 2 => 8, 3 => 8, _ => 0 };
        }

        if (major != 10)
        {
            return 0;
        }

        // 10.0.22000 起是 Windows 11
        var build = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 0;
        return build >= 22000 ? 11 : 10;
    }

    /// <summary>挂载 WIM 到目录（可写）。</summary>
    public async Task MountWimAsync(string wimPath, int index, string mountDir, CancellationToken ct = default)
    {
        if (Directory.Exists(mountDir))
        {
            Directory.Delete(mountDir, recursive: true);
        }

        Directory.CreateDirectory(mountDir);

        var result = await _runner
            .RunAsync("dism.exe", $"/English /Mount-Image /ImageFile:\"{wimPath}\" /Index:{index} /MountDir:\"{mountDir}\"", null, ct)
            .ConfigureAwait(false);
        result.EnsureSuccess("挂载 PE 映像");
    }

    /// <summary>卸载 WIM。</summary>
    public async Task UnmountWimAsync(string mountDir, bool commit, CancellationToken ct = default)
    {
        var args = $"/English /Unmount-Image /MountDir:\"{mountDir}\"{(commit ? " /Commit" : " /Discard")}";
        var result = await _runner.RunAsync("dism.exe", args, null, ct).ConfigureAwait(false);
        result.EnsureSuccess(commit ? "提交 PE 映像" : "丢弃 PE 映像改动");

        // 卸载后残余目录清理，避免影响下一次挂载
        try
        {
            if (Directory.Exists(mountDir) && !Directory.EnumerateFileSystemEntries(mountDir).Any())
            {
                Directory.Delete(mountDir);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>清理可能残留的挂载点。</summary>
    public async Task CleanupMountAsync(string mountDir, CancellationToken ct = default)
    {
        try
        {
            await _runner.RunAsync("dism.exe", "/English /Cleanup-Mountpoints", null, ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }
}

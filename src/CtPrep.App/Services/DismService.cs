using System.Text.RegularExpressions;
using CtPrep.App.Models;

namespace CtPrep.App.Services;

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

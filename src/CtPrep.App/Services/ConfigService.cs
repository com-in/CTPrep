using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>加载并规范化 config.ini（默认配置）与 custom.ini（高级设置配置）。</summary>
public sealed class ConfigService
{
    private readonly ILogSink _log;

    public ConfigService(ILogSink log)
    {
        _log = log;
        ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.ini");
        CustomConfigPath = Path.Combine(AppContext.BaseDirectory, "custom.ini");
    }

    /// <summary>默认配置文件绝对路径（新手模式使用，位于主程序同目录）。</summary>
    public string ConfigPath { get; }

    /// <summary>高级设置配置文件绝对路径（位于主程序同目录）。</summary>
    public string CustomConfigPath { get; }

    /// <summary>默认配置（config.ini）。</summary>
    public AppConfig Config { get; private set; } = new();

    /// <summary>高级设置配置（custom.ini）。</summary>
    public AppConfig CustomConfig { get; private set; } = new();

    /// <summary>加载默认配置 config.ini。</summary>
    public AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException(
                $"未找到配置文件：{ConfigPath}{Environment.NewLine}" +
                "请把 config.ini 放在 CTPrep.exe 同目录后再启动。");
        }

        Config = Parse(IniFile.Load(ConfigPath), ConfigPath);
        _log.Info($"默认配置加载成功：{ConfigPath}");
        _log.Info($"运行时目录：{Config.RuntimeDir}");
        return Config;
    }

    /// <summary>
    /// 加载高级设置配置 custom.ini；文件不存在时先用 config.ini 复制一份再加载。
    /// 这样高级设置与默认配置各自独立，互不影响。
    /// </summary>
    public AppConfig LoadCustom()
    {
        if (!File.Exists(CustomConfigPath))
        {
            File.Copy(ConfigPath, CustomConfigPath);
            _log.Info($"未找到 custom.ini，已根据 config.ini 生成：{CustomConfigPath}");
        }

        CustomConfig = Parse(IniFile.Load(CustomConfigPath), CustomConfigPath);
        _log.Info($"高级设置配置加载成功：{CustomConfigPath}");
        return CustomConfig;
    }

    private AppConfig Parse(IniFile ini, string path)
    {
        var config = new AppConfig
        {
            RuntimeDir = ResolvePath(ini.GetString("General", "RuntimeDir", @".\runtime")),
            DryRun = ini.GetBool("General", "DryRun", false),
            CleanupDownloads = ini.GetBool("General", "CleanupDownloads", true),
            LogLevel = ParseLogLevel(ini.GetString("General", "LogLevel", "Info")),
            Language = ini.GetString("General", "Language", LocalizationService.AutoValue),

            DefaultPe = ini.GetString("PE", "Default"),
            StagingSizeMB = ini.GetInt("Deploy", "StagingSizeMB", 12288),
            StagingLabel = ini.GetString("Deploy", "StagingLabel", "CTPREP"),
            TargetLabel = ini.GetString("Deploy", "TargetLabel", "Windows"),
            DefaultUnattended = ini.GetBool("Deploy", "DefaultUnattended", true),
            DefaultTimeZone = ini.GetString("Deploy", "DefaultTimeZone", "China Standard Time"),
            DefaultInstallMode = ini.GetString("Deploy", "DefaultInstallMode", "Clean")
                .Equals("KeepFiles", StringComparison.OrdinalIgnoreCase)
                ? InstallMode.KeepFiles
                : InstallMode.Clean,

            ExportCurrentDrivers = ini.GetBool("Drivers", "ExportCurrent", true),
        };

        // [PE]
        var peSection = ini.Section("PE");
        var peSha = ini.GetString("PE", "Sha256");
        foreach (var (key, value) in peSection)
        {
            if (key.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Sha256", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            config.PeEntries[key] = new PeEntry { Name = key, Url = value, Sha256 = peSha };
        }

        // [Image]
        var imageSection = ini.Section("Image");
        var imageSha = ini.GetString("Image", "Sha256");
        foreach (var (key, value) in imageSection)
        {
            if (key.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                config.DefaultImage = new ImageSource { Key = "Default", Url = value, Sha256 = imageSha, IsDefault = true };
                continue;
            }

            if (key.Equals("Sha256", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            config.ImageEntries[key] = new ImageSource { Key = key, Url = value, Sha256 = imageSha };
        }

        // [Drivers]
        // 这一段里除 ExportCurrent 之外的键都是驱动包（值即路径 / URL）。
        // ExportCurrent 是开关，必须排除，否则它的 "1" 会被当成一个驱动包路径。
        foreach (var (key, value) in ini.Section("Drivers"))
        {
            if (key.Equals("ExportCurrent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                config.DriverSources.Add(value);
            }
        }

        Directory.CreateDirectory(config.RuntimeDir);
        return config;
    }

    /// <summary>把配置里的相对路径解析为绝对路径（相对主程序目录）。</summary>
    public static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AppContext.BaseDirectory;
        }

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded));
    }

    private static LogLevel ParseLogLevel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}

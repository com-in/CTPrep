namespace CtPrep.App.Models;

/// <summary>本机（或目标系统）信息，用于匹配同版本镜像。</summary>
public sealed class SystemInfo
{
    /// <summary>注册表 ProductName 原始值，例如 "Windows 10 Pro"。</summary>
    public string RawProductName { get; set; } = string.Empty;

    /// <summary>规范化后的系统家族名，例如 "Windows 11"。</summary>
    public string FamilyName { get; set; } = string.Empty;

    /// <summary>版本号，例如 "24H2"。</summary>
    public string DisplayVersion { get; set; } = string.Empty;

    /// <summary>版本 ID，例如 "Professional"。</summary>
    public string EditionId { get; set; } = string.Empty;

    /// <summary>版本中文名，例如 "专业版"。</summary>
    public string EditionName { get; set; } = string.Empty;

    /// <summary>主版本号，例如 26100。</summary>
    public int BuildNumber { get; set; }

    /// <summary>修订号（UBR）。</summary>
    public int Ubr { get; set; }

    /// <summary>架构，例如 "x64"。</summary>
    public string Architecture { get; set; } = "x64";

    /// <summary>系统语言，例如 "zh-CN"。</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>
    /// 原系统里真实使用的用户名（从用户配置文件里挑最近使用的那个）。
    /// 重装后默认沿用这个名字；取不到时为空字符串。
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>用户可见的完整描述。</summary>
    public string FullName =>
        string.IsNullOrEmpty(EditionName)
            ? $"{FamilyName} {DisplayVersion}"
            : $"{FamilyName} {DisplayVersion} {EditionName}";

    /// <summary>构建号字符串，例如 "26100.2894"。</summary>
    public string BuildText => Ubr > 0 ? $"{BuildNumber}.{Ubr}" : BuildNumber.ToString();

    /// <summary>用于匹配 config.ini [Image] 配置项的键，例如 "Windows 11 24H2"。</summary>
    public string VersionKey => $"{FamilyName} {DisplayVersion}".Trim();

    public override string ToString() => $"{FullName} (Build {BuildText}, {Architecture})";
}

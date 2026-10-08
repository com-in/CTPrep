using CtPrep.App.Models;

namespace CtPrep.App.Services;

/// <summary>映像版本族；官方镜像里 DISM 报出的英文名称（以及中文名称）都按这个归类。</summary>
public enum EditionFamily
{
    Unknown,
    Home,
    Pro,
    ProWorkstation,
    ProEducation,
    Education,
    Enterprise,
}

/// <summary>
/// 映像版本（edition）识别与匹配。
/// 只做「归类」和「按族查找」，是否弹窗、是否回退由 <see cref="DeployOrchestrator"/> 决定。
/// </summary>
public static class ImageEditionResolver
{
    /// <summary>把 DISM 报出的映像名称归类到一个版本族；识别不了返回 Unknown。</summary>
    /// <remarks>英文走 DISM /English 输出（Windows 11 Pro），中文镜像也能命中（Windows 11 专业版）。</remarks>
    public static EditionFamily Classify(string? imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
        {
            return EditionFamily.Unknown;
        }

        // 顺序必须从「最具体」到「最宽泛」：Pro for Workstations / Pro Education
        // 都含 "Pro"，先判具体的才不会被归成普通专业版。
        if (ContainsAny(imageName, "pro for workstations", "workstation", "专业工作站版"))
        {
            return EditionFamily.ProWorkstation;
        }

        if (ContainsAny(imageName, "pro education", "专业教育版"))
        {
            return EditionFamily.ProEducation;
        }

        if (ContainsAny(imageName, "enterprise", "企业版"))
        {
            return EditionFamily.Enterprise;
        }

        if (ContainsAny(imageName, "education", "教育版"))
        {
            return EditionFamily.Education;
        }

        if (ContainsAny(imageName, "pro", "专业版"))
        {
            return EditionFamily.Pro;
        }

        if (ContainsAny(imageName, "home", "家庭"))
        {
            return EditionFamily.Home;
        }

        return EditionFamily.Unknown;
    }

    /// <summary>
    /// 本机版本 → 期望安装的目标版本族。
    /// 老系统里存在、新系统已取消的版本（如旗舰版 Ultimate）直接映射到专业版。
    /// </summary>
    public static EditionFamily FromSystem(SystemInfo system)
    {
        var id = system.EditionId?.Trim() ?? string.Empty;
        return id switch
        {
            "Core" or "CoreN" or "CoreSingleLanguage" or "CoreCountrySpecific"
                or "Starter" or "HomeBasic" or "HomePremium" => EditionFamily.Home,
            "ProfessionalWorkstation" => EditionFamily.ProWorkstation,
            "ProfessionalEducation" => EditionFamily.ProEducation,
            "Education" or "EducationN" => EditionFamily.Education,
            "Enterprise" or "EnterpriseN" or "EnterpriseS" => EditionFamily.Enterprise,
            // 旗舰版 / 商用版在 Win10 及以后已取消，按规则回退到专业版
            "Professional" or "ProfessionalN" or "Business" or "Ultimate" => EditionFamily.Pro,
            // 未知（含服务器版等）同样回退专业版
            _ => EditionFamily.Pro,
        };
    }

    /// <summary>在映像列表里找出属于指定版本族的第一项；没有返回 null。</summary>
    public static ImageInfo? FindByFamily(IReadOnlyList<ImageInfo> images, EditionFamily family)
    {
        if (family == EditionFamily.Unknown)
        {
            return null;
        }

        return images.FirstOrDefault(image => Classify(image.Name) == family);
    }

    /// <summary>版本族的可读名字，用于日志与界面提示。</summary>
    public static string Describe(EditionFamily family) => family switch
    {
        EditionFamily.Home => "家庭版",
        EditionFamily.Pro => "专业版",
        EditionFamily.ProWorkstation => "专业工作站版",
        EditionFamily.ProEducation => "专业教育版",
        EditionFamily.Education => "教育版",
        EditionFamily.Enterprise => "企业版",
        _ => "未知版本",
    };

    private static bool ContainsAny(string text, params string[] tokens) =>
        tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
}

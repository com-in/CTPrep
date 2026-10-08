using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CtPrep.App.Models;
using Microsoft.Win32;

namespace CtPrep.App.Services;

/// <summary>探测本机系统信息，用于匹配“同版本”镜像。</summary>
public sealed class SystemInfoService
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    private readonly ILogSink _log;

    public SystemInfoService(ILogSink log) => _log = log;

    public SystemInfo Detect()
    {
        var info = new SystemInfo();

        using (var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey))
        {
            if (key is not null)
            {
                info.RawProductName = key.GetValue("ProductName") as string ?? string.Empty;
                info.DisplayVersion = (key.GetValue("DisplayVersion") as string)
                                      ?? (key.GetValue("ReleaseId") as string)
                                      ?? string.Empty;
                info.EditionId = key.GetValue("EditionID") as string ?? string.Empty;

                var buildFromName = ParseInt(key.GetValue("CurrentBuildNumber"));
                info.BuildNumber = buildFromName > 0 ? buildFromName : ParseInt(key.GetValue("CurrentBuild"));
                info.Ubr = ParseInt(key.GetValue("UBR"));
            }
        }

        info.FamilyName = ResolveFamily(info.RawProductName, info.BuildNumber);
        info.EditionName = ResolveEdition(info.EditionId, info.RawProductName);
        info.Architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "unknown",
        };
        info.Locale = CultureInfo.InstalledUICulture.Name;
        info.UserName = DetectUserName();

        _log.Info($"本机系统：{info}");
        _log.Info($"镜像匹配键：{info.VersionKey}");
        _log.Info($"固件类型：{NativeMethods.GetFirmware()}");

        return info;
    }

    public FirmwareType GetFirmware() => NativeMethods.GetFirmware();

    // ---------------------------------------------------------------- 用户名

    /// <summary>
    /// 不能拿来当重装后用户名的账户：系统账户，以及新系统里已经存在的内置账户。
    /// 内置账户（Administrator / Guest）必须排除 —— 它们在新系统里本来就存在，
    /// 应答文件再用 wcm:action="add" 添加同名账户会被 Windows 拒绝，反而连账户都建不出来。
    /// </summary>
    private static readonly HashSet<string> ReservedProfileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Default", "Default User", "Default.migrated", "defaultuser0", "defaultuser1",
        "Public", "All Users", "Administrator", "Guest", "WDAGUtilityAccount",
        "SystemProfile", "LocalService", "NetworkService", "DefaultAppPool",
    };

    /// <summary>
    /// 找出原系统里真正在用的用户名：取用户配置文件中最近使用（NTUSER.DAT 最后写入时间最新）
    /// 且不是系统/内置账户的那一个。取不到时返回空字符串。
    /// </summary>
    private string DetectUserName()
    {
        try
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var root = string.IsNullOrEmpty(windowsDir) ? @"C:\" : Path.GetPathRoot(windowsDir) ?? @"C:\";

            // Vista 之后在 \Users，XP 在 \Documents and Settings（仅兜底）
            var usersRoot = Path.Combine(root, "Users");
            if (!Directory.Exists(usersRoot))
            {
                usersRoot = Path.Combine(root, "Documents and Settings");
            }

            // 收集所有"最近用过"的候选配置文件，按 NTUSER.DAT 最后写入时间倒序
            var candidates = new List<(string Account, string Folder, DateTime Stamp)>();

            using (var profiles = Registry.LocalMachine.OpenSubKey(
                       @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
            {
                if (profiles is not null)
                {
                    foreach (var sid in profiles.GetSubKeyNames())
                    {
                        using var profile = profiles.OpenSubKey(sid);
                        var imagePath = profile?.GetValue("ProfileImagePath") as string;
                        if (string.IsNullOrWhiteSpace(imagePath))
                        {
                            continue;
                        }

                        // 只认本地系统盘上的配置文件，排除服务账户与漫游/网络路径
                        if (!imagePath!.StartsWith(usersRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var hive = Path.Combine(imagePath, "NTUSER.DAT");
                        if (!File.Exists(hive))
                        {
                            continue;
                        }

                        var folder = Path.GetFileName(imagePath.TrimEnd('\\', '/'));
                        if (string.IsNullOrWhiteSpace(folder))
                        {
                            continue;
                        }

                        // 配置文件目录名 ≠ 账户名：微软账户登录时目录名只是邮箱开头几位，
                        // 账户被改名后目录名也不会跟着变。优先用 SID 反查真正的账户名。
                        var account = SanitizeAccountName(ResolveAccountName(sid) ?? folder);
                        if (account.Length == 0)
                        {
                            continue;
                        }

                        candidates.Add((account, folder, File.GetLastWriteTime(hive)));
                    }
                }
            }

            candidates.Sort((a, b) => b.Stamp.CompareTo(a.Stamp));

            // 最近使用、且不是系统/内置账户的那一个（Find 未命中时返回全空的元组）
            var pick = candidates.Find(c => !ReservedProfileNames.Contains(c.Account));
            if (pick.Account is not null)
            {
                _log.Info(pick.Account.Equals(pick.Folder, StringComparison.OrdinalIgnoreCase)
                    ? $"检测到原系统用户名：{pick.Account}"
                    : $"检测到原系统用户名：{pick.Account}（配置文件目录 {pick.Folder}）");
                return pick.Account;
            }

            var anyCandidate = candidates.FirstOrDefault();
            if (anyCandidate.Account is not null)
            {
                // 常见于用内置 Administrator 登录的虚拟机：名字不能沿用，只能换一个
                _log.Warn($"原系统使用的是内置账户「{anyCandidate.Account}」，新系统里已经存在同名账户，"
                          + "不能重建，因此改用默认账户名；要指定名称请在高级设置里填写账户名。");
                return string.Empty;
            }

            // 兜底：当前登录用户（例如配置文件时间戳不可读时）
            if (!string.IsNullOrWhiteSpace(Environment.UserName) &&
                !ReservedProfileNames.Contains(Environment.UserName))
            {
                var fallback = SanitizeAccountName(Environment.UserName);
                if (fallback.Length > 0)
                {
                    _log.Info($"检测到原系统用户名：{fallback}");
                    return fallback;
                }
            }

            _log.Warn("未能确定原系统用户名（没有找到在用的用户配置文件），重装后将使用默认账户名。");
            return string.Empty;
        }
        catch (Exception ex)
        {
            _log.Warn($"检测原系统用户名失败（{ex.Message}），将使用默认账户名。");
            return string.Empty;
        }
    }

    /// <summary>
    /// 用配置文件注册表键名（SID 字符串）反查真正的账户名。
    /// 用户配置文件目录名并不等于账户名：微软账户登录时目录名是邮箱开头几位，
    /// 账户改名后目录名也不会变，所以用户名的"真相"在 SAM 里，用 SID 查最准。
    /// 查不到（或不是用户账户）时返回 null，调用方回退到目录名。
    /// </summary>
    private static string? ResolveAccountName(string sidString)
    {
        if (!ConvertStringSidToSid(sidString, out var sid))
        {
            return null;
        }

        try
        {
            var name = new StringBuilder(256);
            var domain = new StringBuilder(256);
            var nameLength = name.Capacity;
            var domainLength = domain.Capacity;

            if (!LookupAccountSid(null, sid, name, ref nameLength, domain, ref domainLength, out var use) ||
                use != SidTypeUser)
            {
                return null;
            }

            var value = name.ToString();
            return value.Length == 0 ? null : value;
        }
        finally
        {
            LocalFree(sid);
        }
    }

    private const int SidTypeUser = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountSid(
        string? systemName,
        IntPtr sid,
        StringBuilder name,
        ref int nameLength,
        StringBuilder domain,
        ref int domainLength,
        out int use);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>
    /// 规整成 Windows 允许的用户名：去掉非法字符、限长 20、不能以点结尾。
    /// 结果不可用时返回空字符串。
    /// </summary>
    public static string SanitizeAccountName(string name)
    {
        var invalid = new[] { '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '"' };
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0)
            {
                continue;
            }

            sb.Append(c);
            if (sb.Length >= 20)
            {
                break;
            }
        }

        var result = sb.ToString().Trim().TrimEnd('.');
        return result.Length == 0 || result.Trim('.').Length == 0 ? string.Empty : result;
    }

    /// <summary>
    /// 规整成 Windows 允许的计算机名：去掉空格与非法字符、限长 15（NetBIOS 上限）、
    /// 不以连字符或点结尾（DNS 主机名规则）。结果不可用时返回空字符串，由调用方随机生成。
    /// </summary>
    public static string SanitizeComputerName(string name)
    {
        var invalid = new[] { ' ', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '"' };
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (char.IsControl(c) || Array.IndexOf(invalid, c) >= 0)
            {
                continue;
            }

            sb.Append(c);
            if (sb.Length >= 15)
            {
                break;
            }
        }

        // 连字符与点在名字中间是合法的（MY-PC），但不能出现在首尾
        return sb.ToString().Trim().Trim('-', '.');
    }

    /// <summary>
    /// 识别系统家族。规则：
    /// 1. 老系统（7 / 8 / 8.1）的注册表 ProductName 是准确的产品名，优先用它；
    /// 2. Win10 与 Win11 的 ProductName 都写着 "Windows 10"，只能靠 Build 区分（22000+ 为 11）；
    /// 3. Build 区间兜底，覆盖注册表 ProductName 缺失或异常的情况。
    /// </summary>
    private static string ResolveFamily(string rawProductName, int build)
    {
        if (rawProductName.Contains("Server", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Server";
        }

        if (rawProductName.Contains("Windows 8.1", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 8.1";
        }

        if (rawProductName.Contains("Windows 8", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 8";
        }

        if (rawProductName.Contains("Windows 7", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 7";
        }

        if (build >= 22000)
        {
            return "Windows 11";
        }

        if (build >= 10240)
        {
            return "Windows 10";
        }

        // Build 区间兜底：老系统的注册表 ProductName 缺失时也能认出来
        return build switch
        {
            9600 => "Windows 8.1",
            9200 => "Windows 8",
            7600 or 7601 => "Windows 7",
            _ => string.IsNullOrWhiteSpace(rawProductName) ? "Windows" : rawProductName,
        };
    }

    private static string ResolveEdition(string editionId, string rawProductName)
    {
        var name = editionId switch
        {
            "Core" => "家庭版",
            "CoreN" => "家庭版 N",
            "CoreSingleLanguage" => "家庭单语言版",
            "CoreCountrySpecific" => "家庭中文版",
            "Professional" => "专业版",
            "ProfessionalN" => "专业版 N",
            "ProfessionalWorkstation" => "专业工作站版",
            "ProfessionalWorkstationN" => "专业工作站版 N",
            "ProfessionalEducation" => "专业教育版",
            "Education" => "教育版",
            "EducationN" => "教育版 N",
            "Enterprise" => "企业版",
            "EnterpriseN" => "企业版 N",
            "EnterpriseS" => "企业版 LTSC",
            "EnterpriseSN" => "企业版 LTSC N",
            "IoTEnterprise" => "IoT 企业版",
            "ServerStandard" => "Server 标准版",
            "ServerDatacenter" => "Server 数据中心版",
            _ => string.Empty,
        };

        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        // 从 "Windows 10 Pro" 这样的字符串里取末段
        var idx = rawProductName.LastIndexOf(' ');
        return idx > 0 ? rawProductName[(idx + 1)..] : rawProductName;
    }

    private static int ParseInt(object? value)
    {
        return value switch
        {
            int i => i,
            string s when int.TryParse(s, out var r) => r,
            _ => 0,
        };
    }
}

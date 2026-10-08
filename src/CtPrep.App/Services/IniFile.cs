using System.Text;

namespace CtPrep.App.Services;

/// <summary>极简 INI 解析器：支持 [Section]、key=value、; 与 # 注释。</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections =
        new(StringComparer.OrdinalIgnoreCase);

    public static IniFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"配置文件不存在：{path}", path);
        }

        return Parse(DecodeLines(path));
    }

    /// <summary>
    /// 读取配置文件的每一行：有 BOM 按 BOM 编码；无 BOM 时先按 UTF-8 严格校验，
    /// 校验失败说明是 GBK 等 ANSI 编码，回退到控制台编码（GBK），避免中文被解成乱码。
    /// </summary>
    private static string[] DecodeLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return File.ReadAllLines(path, new UTF8Encoding(true));
        }

        if (IsValidUtf8(bytes))
        {
            return File.ReadAllLines(path, new UTF8Encoding(false));
        }

        return File.ReadAllLines(path, Encodings.Console);
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if (b < 0x80)
            {
                continue;
            }

            int count;
            if (b is >= 0xC2 and <= 0xDF)
            {
                count = 1;
            }
            else if (b is >= 0xE0 and <= 0xEF)
            {
                count = 2;
            }
            else if (b is >= 0xF0 and <= 0xF4)
            {
                count = 3;
            }
            else
            {
                return false; // 非法首字节（含孤立的 0x80-0xBF）
            }

            if (i + count >= bytes.Length)
            {
                return false;
            }

            for (var j = 1; j <= count; j++)
            {
                var cb = bytes[i + j];
                if (cb is < 0x80 or > 0xBF)
                {
                    return false;
                }
            }

            i += count;
        }

        return true;
    }

    public static IniFile Parse(IEnumerable<string> lines)
    {
        var ini = new IniFile();
        Dictionary<string, string>? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                if (end <= 1)
                {
                    continue;
                }

                var sectionName = line[1..end].Trim();
                current = ini.GetOrAddSection(sectionName);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            // 行内注释：仅当 " ;" 或 " #" 前存在空格时截断，避免破坏 URL
            current[key] = StripInlineComment(value);
        }

        return ini;
    }

    private static string StripInlineComment(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if ((value[i] == ';' || value[i] == '#') && char.IsWhiteSpace(value[i - 1]))
            {
                return value[..i].TrimEnd();
            }
        }

        return value;
    }

    private Dictionary<string, string> GetOrAddSection(string name)
    {
        if (!_sections.TryGetValue(name, out var section))
        {
            section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[name] = section;
        }

        return section;
    }

    /// <summary>取整个 section；不存在返回空字典。</summary>
    public IReadOnlyDictionary<string, string> Section(string name) =>
        _sections.TryGetValue(name, out var section) ? section : new Dictionary<string, string>();

    public string? Get(string section, string key)
    {
        return _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;
    }

    public string GetString(string section, string key, string fallback = "") =>
        Get(section, key) is { Length: > 0 } v ? v : fallback;

    public int GetInt(string section, string key, int fallback)
    {
        var v = Get(section, key);
        return int.TryParse(v, out var result) ? result : fallback;
    }

    public bool GetBool(string section, string key, bool fallback = false)
    {
        var v = Get(section, key);
        if (string.IsNullOrWhiteSpace(v))
        {
            return fallback;
        }

        return v.Trim() switch
        {
            "1" or "true" or "True" or "TRUE" or "yes" or "Yes" or "on" or "On" => true,
            "0" or "false" or "False" or "FALSE" or "no" or "No" or "off" or "Off" => false,
            _ => fallback,
        };
    }
}

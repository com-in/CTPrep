using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CtPrep.App.Services;

/// <summary>界面语言选项。</summary>
public sealed record LanguageOption(string Code, string Display);

/// <summary>
/// 界面文案服务。文案放在 <c>Assets/i18n/&lt;语言&gt;.json</c> 里（嵌入资源），
/// 切换语言时通知 WPF 刷新所有 <c>{Binding [key]}</c> 绑定。
/// </summary>
/// <remarks>
/// 取值失败时依次回退：当前语言 → 简体中文 → 直接返回 key（界面上会看到 key，便于定位漏翻项）。
/// </remarks>
public sealed class LocalizationService : INotifyPropertyChanged
{
    /// <summary>回退语言（也是内置默认）。</summary>
    public const string FallbackLanguage = "zh-CN";

    /// <summary>配置为 auto（或留空）时按系统语言自动选择。</summary>
    public const string AutoValue = "auto";

    private static readonly string[] SupportedCodes = { "zh-CN", "en-US" };

    // 单例，语言表缓存是静态的：Languages 的字段初始化也要用到 Load。
    // 注意：静态字段按声明顺序初始化，缓存必须声明在 Current 之前，
    // 否则构造单例时 _cache 还是 null（会抛 TypeInitializationException 导致程序起不来）。
    private static readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>单一实例：界面绑定与视图模型共用。</summary>
    public static LocalizationService Current { get; } = new();

    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private string _language = FallbackLanguage;

    public event PropertyChangedEventHandler? PropertyChanged;

    private LocalizationService()
    {
        _strings = Load(FallbackLanguage);
    }

    /// <summary>当前语言代码。</summary>
    public string Language => _language;

    /// <summary>可选语言（显示名取自各语言文件里的 Language.Name）。</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } = SupportedCodes
        .Select(code => new LanguageOption(code, Load(code).GetValueOrDefault("Language.Name", code)))
        .ToArray();

    /// <summary>按 key 取文案，供 XAML 的 <c>{Binding [key]}</c> 使用。</summary>
    public string this[string key] => T(key);

    /// <summary>取文案并按需格式化（模板里的 {0} 占位符）。</summary>
    public string T(string key, params object?[] args)
    {
        var template = Lookup(key);
        if (args is null || args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // 占位符与参数不匹配时至少把原文显示出来
            return template;
        }
    }

    /// <summary>切换语言；传入空或 auto 时按系统语言判定。</summary>
    public void SetLanguage(string? code)
    {
        var resolved = Resolve(code);
        if (string.Equals(resolved, _language, StringComparison.OrdinalIgnoreCase) && _strings.Count > 0)
        {
            return;
        }

        _language = resolved;
        _strings = Load(resolved);

        // "Item[]" 会让所有绑定到索引器的目标一起刷新
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    /// <summary>把配置值/系统语言解析成支持的语言代码。</summary>
    public static string Resolve(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) ||
            requested.Trim().Equals(AutoValue, StringComparison.OrdinalIgnoreCase))
        {
            return DetectFromSystem();
        }

        var value = requested.Trim();
        var exact = SupportedCodes.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // 近似匹配：zh-Hans / zh-TW → zh-CN，en-GB → en-US
        var prefix = value.Split('-')[0];
        var byPrefix = SupportedCodes.FirstOrDefault(c => c.Split('-')[0].Equals(prefix, StringComparison.OrdinalIgnoreCase));
        return byPrefix ?? FallbackLanguage;
    }

    /// <summary>界面上改过语言后的持久化文件（ASCII，位于运行时目录）。</summary>
    public static string SavedLanguageFile(string runtimeDir) => Path.Combine(runtimeDir, "ui-language.txt");

    /// <summary>读取持久化的语言；没有返回 null。</summary>
    public static string? ReadSaved(string runtimeDir)
    {
        try
        {
            var path = SavedLanguageFile(runtimeDir);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>持久化语言选择；写失败不影响当前会话。</summary>
    public static void Save(string runtimeDir, string code)
    {
        try
        {
            Directory.CreateDirectory(runtimeDir);
            File.WriteAllText(SavedLanguageFile(runtimeDir), code, System.Text.Encoding.ASCII);
        }
        catch
        {
            // 忽略：语言只是偏好设置
        }
    }

    private static string DetectFromSystem()
    {
        var name = CultureInfo.CurrentUICulture.Name;
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
    }

    private string Lookup(string key)
    {
        if (!string.IsNullOrEmpty(key) && _strings.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_strings.Count > 0 && !string.Equals(_language, FallbackLanguage, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = Load(FallbackLanguage);
            if (fallback.TryGetValue(key ?? string.Empty, out var fb))
            {
                return fb;
            }
        }

        return key ?? string.Empty;
    }

    private static Dictionary<string, string> Load(string code)
    {
        // 兜底：即便在静态字段初始化完成之前被调用也不会炸
        var cache = _cache ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (cache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var suffix = $"i18n.{code}.json";
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            if (resourceName is not null)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var document = JsonDocument.Parse(stream);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    map[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // 语言文件缺失或损坏时退回空表，界面会显示 key，不至于崩溃
        }

        cache[code] = map;
        return map;
    }
}

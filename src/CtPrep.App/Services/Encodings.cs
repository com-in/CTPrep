using System.Text;

namespace CtPrep.App.Services;

/// <summary>进程输出的编码。中文 Windows 的 cmd / dism / diskpart 默认输出 GBK(936)。</summary>
internal static class Encodings
{
    static Encodings()
    {
        // 必须先注册代码页提供程序，否则下方 GetEncoding(936) 会在 .NET Core 上抛异常
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 忽略：极端情况下回退到 UTF-8
        }

        Console = TryGetGbk() ?? Encoding.UTF8;
    }

    /// <summary>控制台输出编码（优先 GBK）。</summary>
    public static Encoding Console { get; }

    private static Encoding? TryGetGbk()
    {
        try
        {
            return Encoding.GetEncoding(936);
        }
        catch
        {
            return null;
        }
    }
}

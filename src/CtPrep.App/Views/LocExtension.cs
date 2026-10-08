using System.Windows.Data;
using System.Windows.Markup;
using CtPrep.App.Services;

namespace CtPrep.App.Views;

/// <summary>
/// XAML 取文案的标记扩展：<c>{loc:Loc Image.Browse}</c>。
/// 返回一条绑定到 <see cref="LocalizationService"/> 索引器的 Binding，
/// 因此切换语言时界面会自动刷新。
/// </summary>
[MarkupExtensionReturnType(typeof(BindingExpression))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    /// <summary>文案 key。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 必须把 Binding 交给 <see cref="Binding.ProvideValue(IServiceProvider)"/> 去建绑定。
    /// 直接返回 Binding 对象的话，XAML 加载器会把它当成一个普通值赋给目标属性，
    /// 于是报「System.Windows.Data.Binding 不是 Title/Text 的有效值」。
    /// </summary>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Current,
            Mode = BindingMode.OneWay,
        };

        return serviceProvider is null ? binding : binding.ProvideValue(serviceProvider);
    }
}

using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace PMCL.Localization;

/// <summary>
/// XAML markup extension: <c>{loc:Tr Key.Name}</c>. The extension binds to a
/// cached <see cref="LocalizedString"/> so changing language updates the UI.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
            return string.Empty;

        return new Binding(nameof(LocalizedString.Value))
        {
            Source = LocalizationService.Instance.Bind(Key),
            Mode = BindingMode.OneWay
        };
    }
}

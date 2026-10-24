using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Platform;

namespace PMCL.Localization;

/// <summary>
/// A language the launcher can display. The native name is intentionally not
/// localized so users can always find their language.
/// </summary>
public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// One key bound from XAML. Keeping an object per key lets language files be
/// reloaded without re-creating the visual tree.
/// </summary>
public sealed class LocalizedString : INotifyPropertyChanged
{
    private readonly LocalizationService _owner;
    private readonly string _key;

    internal LocalizedString(LocalizationService owner, string key)
    {
        _owner = owner;
        _key = key;
    }

    public string Value => _owner.Get(_key);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

/// <summary>
/// Loads <c>Assets/i18n/&lt;language&gt;.json</c> and exposes translated text.
/// Missing entries fall back to the default language, then to the key itself.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string DefaultLanguage = "zh-CN";

    private readonly Dictionary<string, LocalizedString> _boundStrings = new(StringComparer.Ordinal);
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    private LocalizationService()
    {
    }

    public static LocalizationService Instance { get; } = new();

    public IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("zh-CN", "简体中文"),
        new LanguageOption("en-US", "English")
    };

    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string this[string key] => Get(key);

    /// <summary>
    /// Returns the translation for <paramref name="key"/>. Empty translations
    /// deliberately fall back so an unfinished language file is still usable.
    /// </summary>
    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        if (_values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;

        return _fallback.TryGetValue(key, out var fallback) && !string.IsNullOrWhiteSpace(fallback)
            ? fallback
            : key;
    }

    public string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Loads the language saved in settings. Called once before any view model
    /// is created so every initial string is already localized.
    /// </summary>
    public void Initialize(string? language)
    {
        _fallback = LoadDictionary(DefaultLanguage);
        CurrentLanguage = Normalize(language);
        _values = CurrentLanguage == DefaultLanguage
            ? new Dictionary<string, string>(_fallback, StringComparer.Ordinal)
            : LoadDictionary(CurrentLanguage);
    }

    /// <summary>
    /// Switches language at runtime. The settings page currently asks for a
    /// restart, but keeping this method makes future live switching trivial.
    /// </summary>
    public void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        if (string.Equals(normalized, CurrentLanguage, StringComparison.Ordinal))
            return;

        _fallback = LoadDictionary(DefaultLanguage);
        CurrentLanguage = normalized;
        _values = normalized == DefaultLanguage
            ? new Dictionary<string, string>(_fallback, StringComparer.Ordinal)
            : LoadDictionary(normalized);

        foreach (var localized in _boundStrings.Values)
            localized.Refresh();

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns a bindable translation source for a XAML markup extension.
    /// </summary>
    public LocalizedString Bind(string key)
    {
        if (!_boundStrings.TryGetValue(key, out var localized))
        {
            localized = new LocalizedString(this, key);
            _boundStrings[key] = localized;
        }

        return localized;
    }

    private static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return DefaultLanguage;

        var trimmed = language.Trim();
        var match = Instance.Languages.FirstOrDefault(option =>
            string.Equals(option.Code, trimmed, StringComparison.OrdinalIgnoreCase));

        return match?.Code ?? DefaultLanguage;
    }

    private static Dictionary<string, string> LoadDictionary(string language)
    {
        try
        {
            var uri = new Uri($"avares://PMCL/Assets/i18n/{language}.json");
            using var stream = AssetLoader.Open(uri);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                stream,
                new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            return values is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(values, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load language '{language}': {ex}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}

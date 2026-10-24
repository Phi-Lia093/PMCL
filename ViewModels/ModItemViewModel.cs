using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// One row in the per-version mod list. Enable/disable, delete and update
/// operate on the jar file through callbacks owned by
/// <see cref="VersionSettingsViewModel"/>. Loader metadata and the Modrinth
/// enrichment arrive asynchronously and refresh the derived display properties.
/// </summary>
public sealed partial class ModItemViewModel : ViewModelBase
{
    private static readonly string[] DerivedPropertyNames =
    [
        nameof(Metadata),
        nameof(Enrichment),
        nameof(HasMetadata),
        nameof(DisplayName),
        nameof(IconInitial),
        nameof(FileName),
        nameof(SizeText),
        nameof(StatusText),
        nameof(IsEnabled),
        nameof(VersionText),
        nameof(LoaderText),
        nameof(HasLoaderText),
        nameof(AuthorText),
        nameof(HasAuthorText),
        nameof(DetailText),
        nameof(DescriptionText),
        nameof(HasDescription),
        nameof(ToggleLabel),
        nameof(ToggleHint),
        nameof(HasUpdate),
        nameof(LatestVersionText),
        nameof(UpdateBadgeText),
        nameof(UpdateLabel),
        nameof(HasNoCompatibleVersion),
        nameof(HasLoaderMismatch),
        nameof(HasGameVersionMismatch),
        nameof(WarningText),
        nameof(HasWarning),
        nameof(HasErrorWarning)
    ];

    private readonly Func<ModItemViewModel, Task> _toggleAsync;
    private readonly Func<ModItemViewModel, Task> _deleteAsync;
    private readonly Func<ModItemViewModel, Task> _updateAsync;
    private readonly Action<ModItemViewModel>? _selectionChanged;
    private readonly ModLoaderKind? _targetLoader;
    private readonly string _targetGameVersion;
    private LocalModMetadata? _metadata;
    private ModEnrichment? _enrichment;
    private bool _suppressSelectionNotify;

    public ModItemViewModel(
        LocalModInfo info,
        LocalModMetadata? metadata,
        ModLoaderKind? targetLoader,
        string? targetGameVersion,
        ModEnrichment? enrichment,
        Func<ModItemViewModel, Task> toggleAsync,
        Func<ModItemViewModel, Task> deleteAsync,
        Func<ModItemViewModel, Task> updateAsync,
        Action<ModItemViewModel>? selectionChanged = null,
        bool isSelected = false)
    {
        Info = info;
        _metadata = metadata;
        _targetLoader = targetLoader;
        _targetGameVersion = targetGameVersion?.Trim() ?? string.Empty;
        _enrichment = enrichment;
        _toggleAsync = toggleAsync;
        _deleteAsync = deleteAsync;
        _updateAsync = updateAsync;
        _selectionChanged = selectionChanged;

        ToggleCommand = new AsyncRelayCommand(() => _toggleAsync(this));
        ConfirmDeleteCommand = new AsyncRelayCommand(() => _deleteAsync(this));
        UpdateCommand = new AsyncRelayCommand(() => _updateAsync(this), () => HasUpdate && !IsUpdating);

        Icon = enrichment?.Icon;
        _suppressSelectionNotify = true;
        try
        {
            IsSelected = isSelected;
        }
        finally
        {
            _suppressSelectionNotify = false;
        }
    }

    public LocalModInfo Info { get; private set; }

    public LocalModMetadata? Metadata => _metadata;

    public ModEnrichment? Enrichment => _enrichment;

    public bool HasMetadata => _metadata is not null;

    public string FilePath => Info.FilePath;

    public string DisplayName
    {
        get
        {
            if (_metadata is { } metadata && !string.IsNullOrWhiteSpace(metadata.Name))
                return metadata.Name;

            if (_enrichment?.Project is { } project && !string.IsNullOrWhiteSpace(project.DisplayTitle))
                return project.DisplayTitle;

            return Info.DisplayName;
        }
    }

    public string IconInitial
    {
        get
        {
            var name = DisplayName.Trim();
            return name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString();
        }
    }

    public string FileName => Info.FileName;

    public string SizeText => Info.SizeText;

    public string StatusText => Info.StatusText;

    public bool IsEnabled => Info.IsEnabled;

    public string VersionText => !string.IsNullOrWhiteSpace(_metadata?.Version)
        ? _metadata!.Version
        : _enrichment?.InstalledVersionText ?? string.Empty;

    public string LoaderText => _metadata?.Loader?.ToDisplayName() ?? string.Empty;

    public bool HasLoaderText => LoaderText.Length > 0;

    public string AuthorText
    {
        get
        {
            var authors = _metadata?.AuthorText;
            if (!string.IsNullOrWhiteSpace(authors))
                return authors;

            return _enrichment?.Author ?? string.Empty;
        }
    }

    public bool HasAuthorText => AuthorText.Length > 0;

    /// <summary>Secondary line under the file name: loader, version, author, size.</summary>
    public string DetailText
    {
        get
        {
            var parts = new List<string>(4);
            if (HasLoaderText)
                parts.Add(LoaderText);
            if (VersionText.Length > 0)
                parts.Add(VersionText);
            if (HasAuthorText)
                parts.Add(AuthorText);
            parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }

    public string DescriptionText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_metadata?.Description))
                return _metadata!.Description;

            return _enrichment?.Project?.Description ?? string.Empty;
        }
    }

    public bool HasDescription => DescriptionText.Length > 0;

    public string ToggleLabel => Info.IsEnabled
        ? L.Tr("Mod.Disable")
        : L.Tr("Mod.Enable");

    public string ToggleHint => Info.IsEnabled
        ? L.Tr("Mod.DisableHint")
        : L.Tr("Mod.EnableHint");

    public bool HasUpdate => _enrichment?.IsOutdated == true;

    public string LatestVersionText => _enrichment?.LatestVersion?.VersionNumber ?? string.Empty;

    public string UpdateBadgeText => HasUpdate
        ? L.Format("VS.Mods.UpdateBadge", LatestVersionText)
        : string.Empty;

    public string UpdateLabel => IsUpdating
        ? L.Tr("VS.Mods.Updating")
        : L.Tr("VS.Mods.Update");

    /// <summary>Modrinth knows the project but has no file for this game version and loader.</summary>
    public bool HasNoCompatibleVersion => _enrichment?.HasNoCompatibleVersion == true;

    public bool HasLoaderMismatch => _metadata?.Loader is { } loader
                                     && loader != ModLoaderKind.Vanilla
                                     && loader != _targetLoader;

    public bool HasGameVersionMismatch => _metadata is { } metadata
                                          && metadata.GameVersions.Count > 0
                                          && _targetGameVersion.Length > 0
                                          && !metadata.SupportsGameVersion(_targetGameVersion);

    public bool HasErrorWarning => HasLoaderMismatch || HasGameVersionMismatch || HasNoCompatibleVersion;

    public bool HasWarning => HasErrorWarning || HasUpdate;

    public string WarningText
    {
        get
        {
            var parts = new List<string>(4);

            if (HasLoaderMismatch)
            {
                parts.Add(L.Format(
                    "VS.Mods.WarnLoader",
                    _metadata!.Loader!.Value.ToDisplayName(),
                    _targetLoader?.ToDisplayName() ?? L.Tr("Loader.Vanilla")));
            }

            if (HasGameVersionMismatch)
            {
                parts.Add(L.Format(
                    "VS.Mods.WarnGameVersion",
                    string.Join(", ", _metadata!.GameVersions.Take(4)),
                    _targetGameVersion));
            }

            if (HasNoCompatibleVersion)
                parts.Add(L.Tr("VS.Mods.WarnNoCompatible"));

            if (HasUpdate)
            {
                parts.Add(L.Format(
                    "VS.Mods.WarnUpdate",
                    VersionText.Length > 0 ? VersionText : L.Tr("VS.Mods.UnknownVersion"),
                    LatestVersionText));
            }

            return string.Join("   ", parts);
        }
    }

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand ConfirmDeleteCommand { get; }

    public IAsyncRelayCommand UpdateCommand { get; }

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsDeleteConfirmVisible { get; set; }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    partial void OnIsUpdatingChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateLabel));
        UpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_suppressSelectionNotify)
            return;

        _selectionChanged?.Invoke(this);
    }

    [RelayCommand]
    private void RequestDelete() => IsDeleteConfirmVisible = true;

    [RelayCommand]
    private void CancelDelete() => IsDeleteConfirmVisible = false;

    public void ApplyMetadata(LocalModMetadata? metadata)
    {
        _metadata = metadata;
        RefreshDerived();
    }

    public void ApplyEnrichment(ModEnrichment enrichment)
    {
        _enrichment = enrichment;
        Icon = enrichment.Icon;
        RefreshDerived();
        UpdateCommand.NotifyCanExecuteChanged();
    }

    public void Update(LocalModInfo info)
    {
        Info = info;
        RefreshDerived();
    }

    private void RefreshDerived()
    {
        foreach (var name in DerivedPropertyNames)
            OnPropertyChanged(name);
    }
}


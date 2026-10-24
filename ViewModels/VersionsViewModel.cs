using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

using PMCL.Localization;

namespace PMCL.ViewModels;

/// <summary>
/// Version browser with search, type filter and per-row actions. Downloading
/// always goes through the mod loader picker so a version is installed either
/// vanilla or with exactly one loader.
/// </summary>
public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly GameLaunchCoordinator _coordinator;
    private readonly Func<Task> _refreshVersionsAsync;

    public VersionsViewModel(
        LauncherSession session,
        GameLaunchCoordinator coordinator,
        ModLoaderService modLoaderService,
        MinecraftService minecraft,
        StoragePickerService picker,
        Func<Task> refreshVersionsAsync)
    {
        Session = session;
        _coordinator = coordinator;
        _refreshVersionsAsync = refreshVersionsAsync;
        LoaderSelector = new ModLoaderSelectorViewModel(modLoaderService, minecraft, picker);

        Session.Versions.CollectionChanged += OnVersionsChanged;
        RebuildItems();
    }

    public LauncherSession Session { get; }

    public ModLoaderSelectorViewModel LoaderSelector { get; }

    public void CloseLoaderDialog()
    {
        if (LoaderSelector.IsOpen && !LoaderSelector.IsInstalling)
            LoaderSelector.CloseCommand.Execute(null);
    }

    public ObservableCollection<VersionItemViewModel> Items { get; } = new();

    public IReadOnlyList<string> TypeFilters { get; } = new[]
    {
        L.Tr("Versions.Filter.All"),
        L.Tr("Version.Type.Release"),
        L.Tr("Version.Type.Snapshot"),
        L.Tr("Versions.Filter.Old")
    };

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedTypeIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowInstalledOnly { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = L.Tr("Versions.NotLoaded");

    [ObservableProperty]
    public partial VersionItemViewModel? SelectedItem { get; set; }

    private GameVersion? PendingBaseVersion { get; set; }

    private string? PendingSourceVersionName { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _refreshVersionsAsync();
        RebuildItems();
    }

    [RelayCommand]
    private Task InstallAsync(VersionItemViewModel? item)
        => item is null ? Task.CompletedTask : OpenDownloadAsync(item.Version);

    private async Task OpenDownloadAsync(GameVersion version)
    {
        var baseVersion = version;
        if (version.IsModded)
        {
            baseVersion = Session.FindVersion(version.BaseVersionName)
                          ?? new GameVersion(
                              version.BaseVersionName,
                              version.Type,
                              version.ReleaseTime,
                              isInstalled: false);
        }

        PendingBaseVersion = baseVersion;
        PendingSourceVersionName = version.Name;
        var initialJava = Session.Settings.GetVersionSettings(version.Name).JavaPath;
        await LoaderSelector.OpenAsync(
            baseVersion,
            Session.Settings.GameDirectory,
            allowVanilla: true,
            initialJava,
            InstallSelectionAsync);
    }

    [RelayCommand]
    private void SelectVersion(VersionItemViewModel? item)
    {
        if (item is not null)
            Session.SelectedVersion = item.Version;
    }

    private async Task<bool> InstallSelectionAsync(ModLoaderSelection selection)
    {
        var baseVersion = PendingBaseVersion;
        if (baseVersion is null)
            return false;

        var installedName = await _coordinator.InstallModLoaderAsync(
            Session,
            baseVersion,
            selection.Kind,
            selection.LoaderVersion,
            selection.JavaPath,
            selection.ExistingLoaderNames,
            PendingSourceVersionName);

        if (string.IsNullOrWhiteSpace(installedName))
            return false;

        await _refreshVersionsAsync();
        Session.SelectVersion(installedName);
        RebuildItems();
        return Session.Versions.Any(version =>
            string.Equals(version.Name, installedName, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSearchTextChanged(string value) => RebuildItems();

    partial void OnSelectedTypeIndexChanged(int value) => RebuildItems();

    partial void OnShowInstalledOnlyChanged(bool value) => RebuildItems();

    partial void OnSelectedItemChanged(VersionItemViewModel? value)
    {
        if (value is not null)
            Session.SelectedVersion = value.Version;
    }

    private void OnVersionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildItems();

    /// <summary>
    /// Refreshes the visible rows after the shared version list was replaced.
    /// </summary>
    public void RebuildFromSession() => RebuildItems();

    private void RebuildItems()
    {
        IEnumerable<GameVersion> filtered = Session.Versions;

        filtered = SelectedTypeIndex switch
        {
            1 => filtered.Where(version => version.Type.Equals("release", StringComparison.OrdinalIgnoreCase)),
            2 => filtered.Where(version => version.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase)),
            3 => filtered.Where(version =>
                version.Type.StartsWith("old_", StringComparison.OrdinalIgnoreCase)),
            _ => filtered
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var keyword = SearchText.Trim();
            filtered = filtered.Where(version =>
                version.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (ShowInstalledOnly)
            filtered = filtered.Where(version => version.IsInstalled);

        Items.Clear();
        SelectedItem = null;
        foreach (var version in filtered)
        {
            Items.Add(new VersionItemViewModel(
                version,
                OpenDownloadAsync));
        }

        Summary = Session.Versions.Count == 0
            ? L.Tr("Versions.NotLoaded")
            : L.Format("Versions.FilteredSummary", Items.Count, Session.Versions.Count);
    }
}

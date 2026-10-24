using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Local and Modrinth management of one content type inside the version
/// settings page. Resource packs, shader packs and data packs share this view
/// model; the kind decides the folder, the accepted archives and the Modrinth
/// project type.
/// </summary>
public sealed partial class ContentPackGroupViewModel : ViewModelBase
{
    private readonly ContentPackService _service;
    private readonly ModrinthService _modrinthService;
    private readonly ModIconCache _iconCache;
    private readonly StoragePickerService _picker;
    private readonly LauncherSession _session;
    private readonly Func<GameVersion?> _versionAccessor;
    private readonly Func<string> _gameDirectoryAccessor;
    private readonly List<LocalContentPack> _allPacks = new();
    private List<ContentPackTarget> _targets = new();

    public ContentPackGroupViewModel(
        ContentPackKind kind,
        ContentPackService service,
        ModrinthService modrinthService,
        ModIconCache iconCache,
        StoragePickerService picker,
        LauncherSession session,
        Func<GameVersion?> versionAccessor,
        Func<string> gameDirectoryAccessor)
    {
        Kind = kind;
        _service = service;
        _modrinthService = modrinthService;
        _iconCache = iconCache;
        _picker = picker;
        _session = session;
        _versionAccessor = versionAccessor;
        _gameDirectoryAccessor = gameDirectoryAccessor;
    }

    public ContentPackKind Kind { get; }

    public string Title => Kind.ToDisplayName();

    public string Hint => Kind switch
    {
        ContentPackKind.ResourcePack => L.Tr("VS.Content.ResourcePacksHint"),
        ContentPackKind.ShaderPack => L.Tr("VS.Content.ShadersHint"),
        _ => L.Tr("VS.Content.DataPacksHint")
    };

    public bool IsVersionInstalled => _versionAccessor() is { IsInstalled: true };

    /// <summary>Folder of the currently selected install target.</summary>
    public string FolderText => SelectedTarget?.DirectoryPath ?? string.Empty;

    public string SelectedTargetPath => SelectedTarget?.DirectoryPath ?? string.Empty;

    public ObservableCollection<ContentPackItemViewModel> Items { get; } = new();

    public ObservableCollection<ModrinthProjectItemViewModel> Results { get; } = new();

    public ObservableCollection<string> Targets { get; } = new();

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => !HasItems;

    /// <summary>The empty hint is pointless while the version itself is missing.</summary>
    public bool ShowEmptyHint => HasNoItems && IsVersionInstalled;

    public bool HasResults => Results.Count > 0;

    public bool HasMultipleTargets => _targets.Count > 1;

    public bool CanSearch => IsVersionInstalled && !IsSearching;

    public string SearchButtonText => IsSearching ? L.Tr("VS.Searching") : L.Tr("VS.Search");

    private ContentPackTarget? SelectedTarget
    {
        get
        {
            if (_targets.Count == 0)
                return null;

            var index = Math.Clamp(SelectedTargetIndex, 0, _targets.Count - 1);
            return _targets[index];
        }
    }

    [ObservableProperty]
    public partial int SelectedTargetIndex { get; set; }

    [ObservableProperty]
    public partial string LocalSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowDisabled { get; set; } = true;

    [ObservableProperty]
    public partial string StatusText { get; set; } = L.Tr("VS.Content.NotInstalled");

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    [ObservableProperty]
    public partial string SearchStatusText { get; set; } = string.Empty;

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(CanSearch));
    }

    partial void OnSelectedTargetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(SelectedTargetPath));
    }

    partial void OnLocalSearchTextChanged(string value) => Rebuild();

    partial void OnShowDisabledChanged(bool value) => Rebuild();

    /// <summary>
    /// Re-reads the folders after the page was opened for another version.
    /// Search results belong to the previous version and are dropped.
    /// </summary>
    public void SyncVersion()
    {
        SearchText = string.Empty;
        Results.Clear();
        SearchStatusText = string.Empty;
        IsSearching = false;
        OnPropertyChanged(nameof(HasResults));
        Reload();
    }

    public void Reload()
    {
        var previousPath = SelectedTargetPath;

        _allPacks.Clear();
        _targets = new List<ContentPackTarget>();
        Targets.Clear();

        if (_versionAccessor() is { IsInstalled: true })
        {
            try
            {
                _targets = _service.GetTargets(Kind, _gameDirectoryAccessor()).ToList();
                _allPacks.AddRange(_service.ScanTargets(Kind, _targets));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = L.Format("VS.Content.ReadFailed", ex.Message);
                Rebuild(withStatus: false);
                return;
            }
        }

        foreach (var target in _targets)
            Targets.Add(target.DisplayLabel);

        var index = _targets.FindIndex(target =>
            string.Equals(target.DirectoryPath, previousPath, StringComparison.OrdinalIgnoreCase));
        SelectedTargetIndex = index >= 0 ? index : 0;

        OnPropertyChanged(nameof(HasMultipleTargets));
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(SelectedTargetPath));
        Rebuild();
    }

    private void Rebuild(bool withStatus = true)
    {
        Items.Clear();
        var filter = LocalSearchText?.Trim() ?? string.Empty;

        foreach (var pack in _allPacks)
        {
            if (!ShowDisabled && !pack.IsEnabled)
                continue;

            if (filter.Length > 0
                && !pack.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !pack.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Items.Add(new ContentPackItemViewModel(pack, ToggleAsync, DeleteAsync));
        }

        if (withStatus)
        {
            var enabled = _allPacks.Count(pack => pack.IsEnabled);
            StatusText = !IsVersionInstalled
                ? L.Tr("VS.Content.NotInstalled")
                : _allPacks.Count == 0
                    ? L.Tr("VS.Content.Empty")
                    : L.Format("VS.Content.Summary", enabled, _allPacks.Count - enabled) +
                      (Items.Count != _allPacks.Count
                          ? L.Format("VS.Content.Filtered", Items.Count)
                          : string.Empty);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(IsVersionInstalled));
        OnPropertyChanged(nameof(CanSearch));
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void OpenFolder()
    {
        var path = SelectedTargetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _session.SetError(L.Tr("VS.Content.NotInstalled"));
            return;
        }

        try
        {
            FolderLauncher.Open(path, create: true);
            _session.SetStatus(L.Format("VS.Folder.Opened", path));
        }
        catch (Exception ex)
        {
            _session.SetError(L.Format("VS.Folder.OpenFailed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task InstallLocalAsync()
    {
        if (_versionAccessor() is not { IsInstalled: true } || _session.IsBusy)
            return;

        var targetDirectory = SelectedTargetPath;
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        var patterns = Kind.GetFileExtensions()
            .Select(extension => "*" + extension)
            .ToArray();
        var files = await _picker.PickContentPackFilesAsync(
            L.Format("VS.Content.PickLocalTitle", Title),
            patterns);
        if (files.Count == 0)
            return;

        var imported = 0;
        var skipped = 0;
        string? firstError = null;

        _session.BeginBusy(L.Tr("VS.Content.Importing"));
        try
        {
            await Task.Run(() => Directory.CreateDirectory(targetDirectory));
            foreach (var source in files)
            {
                try
                {
                    var fileName = Path.GetFileName(source);
                    if (!_service.IsSupportedFileName(Kind, fileName))
                    {
                        skipped++;
                        continue;
                    }

                    var destination = Path.Combine(targetDirectory, fileName);
                    if (string.Equals(destination, source, StringComparison.OrdinalIgnoreCase)
                        || File.Exists(destination)
                        || File.Exists(destination + LocalContentPack.DisabledSuffix))
                    {
                        skipped++;
                        continue;
                    }

                    await Task.Run(() => File.Copy(source, destination));
                    imported++;
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                }
            }
        }
        finally
        {
            _session.EndBusy();
        }

        Reload();
        _session.SetStatus(L.Format("VS.Content.Imported", imported, skipped));
        if (firstError is not null)
            _session.SetError(L.Format("VS.Content.ImportFailed", firstError));
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_versionAccessor() is not { IsInstalled: true } version || IsSearching || _session.IsBusy)
            return;

        IsSearching = true;
        SearchStatusText = L.Tr("VS.Content.Searching");
        var versionName = version.Name;
        try
        {
            var result = await _modrinthService.SearchContentAsync(
                Kind,
                version.BaseVersionName,
                SearchText);

            var current = _versionAccessor();
            if (current is null || !string.Equals(current.Name, versionName, StringComparison.Ordinal))
                return;

            Results.Clear();
            var items = new List<ModrinthProjectItemViewModel>(result.Hits.Count);
            foreach (var hit in result.Hits)
            {
                var item = new ModrinthProjectItemViewModel(hit, InstallProjectAsync);
                items.Add(item);
                Results.Add(item);
            }

            SearchStatusText = result.Hits.Count == 0
                ? L.Tr("VS.Content.NoResults")
                : L.Format("VS.Content.Results", result.TotalHits, result.Hits.Count);
            OnPropertyChanged(nameof(HasResults));
            _session.SetStatus(SearchStatusText);

            _ = LoadIconsAsync(items);
        }
        catch (Exception ex)
        {
            SearchStatusText = L.Format("VS.Content.SearchFailed", ex.Message);
            _session.SetError(SearchStatusText);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>
    /// Fills in the project icons after the result list is already visible, so
    /// a slow image never delays the search.
    /// </summary>
    private async Task LoadIconsAsync(IReadOnlyList<ModrinthProjectItemViewModel> items)
    {
        var loads = items.Select(async item =>
        {
            try
            {
                var icon = await _iconCache.GetAsync(item.Project.IconUrl).ConfigureAwait(true);
                if (icon is not null && Results.Contains(item))
                    item.Icon = icon;
            }
            catch (Exception)
            {
                // A missing icon keeps the letter placeholder.
            }
        });

        await Task.WhenAll(loads).ConfigureAwait(true);
    }

    private async Task InstallProjectAsync(ModrinthProjectItemViewModel item)
    {
        if (_versionAccessor() is not { IsInstalled: true } version
            || item.IsInstalling
            || _session.IsBusy)
        {
            return;
        }

        var targetDirectory = SelectedTargetPath;
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return;

        item.IsInstalling = true;
        _session.BeginBusy(L.Format("VS.Content.Installing", item.Title));
        try
        {
            var progress = new Progress<string>(message => _session.SetStatus(message));
            var report = await _modrinthService.InstallContentProjectAsync(
                Kind,
                item.Project.ProjectId,
                targetDirectory,
                version.BaseVersionName,
                progress);

            Reload();
            var message = report.InstalledFiles == 0
                ? L.Format("VS.Content.InstallSkipped", item.Title)
                : L.Format("VS.Content.Installed", report.InstalledFiles);
            SearchStatusText = message;
            _session.SetStatus(message);
        }
        catch (Exception ex)
        {
            SearchStatusText = L.Format("VS.Content.InstallFailed", ex.Message);
            _session.SetError(SearchStatusText);
        }
        finally
        {
            _session.EndBusy();
            item.IsInstalling = false;
        }
    }

    private async Task ToggleAsync(ContentPackItemViewModel item)
    {
        if (_session.IsBusy)
            return;

        try
        {
            var updated = await Task.Run(() => _service.SetEnabled(item.Info, !item.Info.IsEnabled));
            Reload();
            _session.SetStatus(updated.IsEnabled
                ? L.Format("VS.Content.Enabled", updated.DisplayName)
                : L.Format("VS.Content.Disabled", updated.DisplayName));
        }
        catch (Exception ex)
        {
            _session.SetError(L.Format("VS.Content.ToggleFailed", ex.Message));
        }
    }

    private async Task DeleteAsync(ContentPackItemViewModel item)
    {
        if (_session.IsBusy)
            return;

        try
        {
            var name = item.DisplayName;
            await Task.Run(() => _service.Delete(item.Info));
            Reload();
            _session.SetStatus(L.Format("VS.Content.Deleted", name));
        }
        catch (Exception ex)
        {
            _session.SetError(L.Format("VS.Content.DeleteFailed", ex.Message));
        }
    }
}


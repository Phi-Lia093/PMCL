using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>Left navigation sections of the version settings page.</summary>
public enum VersionSettingsSection
{
    Settings,
    Mods,
    ModDownloads,
    ContentPacks,
    Advanced
}

/// <summary>
/// A Modrinth category shown in the mod download filter. The slug is sent
/// through the API's <c>categories:</c> facet.
/// </summary>
public sealed record ModrinthCategoryOption(string DisplayName, string? Slug);

/// <summary>
/// Per-installed-version settings page: files, identity, Java, memory, login,
/// server and launch arguments. Edits are auto-saved with a short debounce.
/// </summary>
public sealed partial class VersionSettingsViewModel : ViewModelBase
{
    private readonly LauncherSession _session;
    private readonly GameLaunchCoordinator _coordinator;
    private readonly SettingsService _settingsService;
    private readonly MinecraftService _minecraft;
    private readonly StoragePickerService _picker;
    private readonly JavaRuntimeService _javaRuntimes;
    private readonly ModService _modService;
    private readonly ModrinthService _modrinthService;
    private readonly ContentPackService _contentPackService = new();
    private readonly Func<Task> _refreshVersionsAsync;
    private readonly Action _goBack;
    private readonly List<LocalModInfo> _allMods = new();
    private readonly LocalModScanner _modScanner = new();
    private readonly ModIconCache _iconCache = new();
    private readonly Dictionary<string, LocalModMetadata?> _modMetadataCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _modHashes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModEnrichment> _modEnrichmentCache =
        new(StringComparer.OrdinalIgnoreCase);
    // Hashes Modrinth could not resolve (unknown file and no exact project match)
    // are remembered so re-filtering the list never re-queries them again.
    private readonly HashSet<string> _modEnrichmentMisses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedModPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private GameVersion? _version;
    private GameVersionSettings _model = new();
    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _modScanCts;
    private int _modScanGeneration;
    private bool _loading;
    private bool _syncingJavaSelection;
    private VersionSettingsSection _selectedSection = VersionSettingsSection.Settings;

    public VersionSettingsViewModel(
        LauncherSession session,
        GameLaunchCoordinator coordinator,
        SettingsService settingsService,
        MinecraftService minecraft,
        StoragePickerService picker,
        JavaRuntimeService javaRuntimes,
        ModLoaderService modLoaderService,
        ModService modService,
        ModrinthService modrinthService,
        Func<Task> refreshVersionsAsync,
        Action goBack)
    {
        _session = session;
        Session = session;
        _coordinator = coordinator;
        _settingsService = settingsService;
        _minecraft = minecraft;
        _picker = picker;
        _javaRuntimes = javaRuntimes;
        _modService = modService;
        _modrinthService = modrinthService;
        _refreshVersionsAsync = refreshVersionsAsync;
        _goBack = goBack;
        LoaderSelector = new ModLoaderSelectorViewModel(
            modLoaderService,
            minecraft,
            picker,
            javaRuntimes);

        ContentPackGroups = new[]
        {
            CreateContentPackGroup(ContentPackKind.ResourcePack),
            CreateContentPackGroup(ContentPackKind.ShaderPack),
            CreateContentPackGroup(ContentPackKind.DataPack)
        };
        ActiveContentGroup = ContentPackGroups[0];
    }

    private ContentPackGroupViewModel CreateContentPackGroup(ContentPackKind kind)
        => new(
            kind,
            _contentPackService,
            _modrinthService,
            _iconCache,
            _picker,
            _session,
            () => _version,
            () => GameDirectory);

    public IReadOnlyList<ContentPackGroupViewModel> ContentPackGroups { get; }

    public LauncherSession Session { get; }

    public ModLoaderSelectorViewModel LoaderSelector { get; }

    public void CloseLoaderDialog()
    {
        if (LoaderSelector.IsOpen && !LoaderSelector.IsInstalling)
            LoaderSelector.CloseCommand.Execute(null);
    }

    public GameVersion? Version
    {
        get => _version;
        private set
        {
            if (SetProperty(ref _version, value))
                NotifyVersionDerived();
        }
    }

    public bool HasVersion => _version is not null;

    public string VersionStatusText => _version is { IsInstalled: true }
        ? L.Tr("Version.Installed")
        : L.Tr("Version.NotInstalled");

    public string VersionTypeLabel => _version?.TypeLabel ?? string.Empty;

    public bool HasModLoader => _version?.IsModded == true;

    public bool IsVanillaVersion => _version is { IsModded: false };

    public bool CanManageModLoader => _version is { IsInstalled: true };

    /// <summary>
    /// The loader card only appears on the general settings tab and only for
    /// installed versions.
    /// </summary>
    public bool IsModLoaderCardVisible => IsSettingsSection && CanManageModLoader;

    public string CurrentLoaderLabel => _version?.LoaderLabel ?? L.Tr("Loader.Vanilla");

    public string LoaderBaseVersionLabel => _version is { IsModded: true }
        ? L.Format("VS.LoaderBase", _version.BaseVersionName)
        : L.Tr("VS.LoaderVanillaHint");

    public string VersionDirectory => _version is null
        ? string.Empty
        : _minecraft.GetVersionDirectory(_session.Settings.GameDirectory, _version.Name);

    public string SavesDirectory => _version is null
        ? string.Empty
        : _minecraft.GetSavesDirectory(_session.Settings.GameDirectory, _version.Name);

    public string ScreenshotsDirectory => _version is null
        ? string.Empty
        : _minecraft.GetScreenshotsDirectory(_session.Settings.GameDirectory, _version.Name);

    /// <summary>
    /// Every version runs inside its own folder, so the working directory is
    /// always <c>versions/&lt;name&gt;</c>.
    /// </summary>
    public string GameDirectory => _version is null
        ? string.Empty
        : _minecraft.GetGameDirectory(_session.Settings.GameDirectory, _version.Name);

    public string ModsDirectory => _version is null
        ? string.Empty
        : _minecraft.GetModsDirectory(_session.Settings.GameDirectory, _version.Name);

    public string ConfigDirectory => _version is null
        ? string.Empty
        : _minecraft.GetConfigDirectory(_session.Settings.GameDirectory, _version.Name);

    public bool CanImportSharedData => _version is { IsInstalled: true };

    public string DeleteWarningText => L.Tr("VS.DeleteWarning");

    public ObservableCollection<JavaRuntimeOption> JavaRuntimeOptions { get; } = new();

    [ObservableProperty]
    public partial JavaRuntimeOption? SelectedJavaRuntime { get; set; }

    [ObservableProperty]
    public partial bool IsScanningJava { get; set; }

    [ObservableProperty]
    public partial string JavaScanStatus { get; set; } = L.Tr("Java.NotScanned");

    public IReadOnlyList<string> LoginModes { get; } = new[]
    {
        L.Tr("VS.Login.Current"),
        L.Tr("VS.Login.Microsoft"),
        L.Tr("VS.Login.Offline")
    };

    public IReadOnlyList<ModrinthCategoryOption> ModrinthCategoryOptions { get; } = new[]
    {
        new ModrinthCategoryOption(L.Tr("Versions.Filter.All"), null),
        new ModrinthCategoryOption(L.Tr("VS.Category.Technology"), "technology"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Magic"), "magic"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Utility"), "utility"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Adventure"), "adventure"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Decoration"), "decoration"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Worldgen"), "worldgen"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Storage"), "storage"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Optimization"), "optimization"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Library"), "library"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Mobs"), "mobs"),
        new ModrinthCategoryOption(L.Tr("VS.Category.Equipment"), "equipment")
    };

    public IReadOnlyList<string> ModrinthSourceOptions { get; } = new[]
    {
        L.Tr("VS.Source.Modrinth"),
        L.Tr("VS.Source.CurseForge")
    };

    public VersionSettingsSection SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (!SetProperty(ref _selectedSection, value))
                return;

            OnPropertyChanged(nameof(IsSettingsSection));
            OnPropertyChanged(nameof(IsModsSection));
            OnPropertyChanged(nameof(IsModDownloadsSection));
            OnPropertyChanged(nameof(IsContentSection));
            OnPropertyChanged(nameof(IsAdvancedSection));
            OnPropertyChanged(nameof(IsModLoaderCardVisible));

            if (value == VersionSettingsSection.Mods)
                QueueModScan();
            else if (value == VersionSettingsSection.ContentPacks)
                ActiveContentGroup?.Reload();
        }
    }

    public bool IsSettingsSection => SelectedSection == VersionSettingsSection.Settings;

    public bool IsModsSection => SelectedSection == VersionSettingsSection.Mods;

    public bool IsModDownloadsSection => SelectedSection == VersionSettingsSection.ModDownloads;

    public bool IsContentSection => SelectedSection == VersionSettingsSection.ContentPacks;

    public bool IsAdvancedSection => SelectedSection == VersionSettingsSection.Advanced;

    [ObservableProperty]
    public partial int SelectedContentPackKindIndex { get; set; }

    [ObservableProperty]
    public partial ContentPackGroupViewModel? ActiveContentGroup { get; set; }

    public bool IsResourcePacksSelected => SelectedContentPackKindIndex == 0;

    public bool IsShaderPacksSelected => SelectedContentPackKindIndex == 1;

    public bool IsDataPacksSelected => SelectedContentPackKindIndex == 2;

    // The generated hook keeps the sub-tab buttons and the visible group in
    // sync; the groups themselves cache their folders until the version changes.
    partial void OnSelectedContentPackKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsResourcePacksSelected));
        OnPropertyChanged(nameof(IsShaderPacksSelected));
        OnPropertyChanged(nameof(IsDataPacksSelected));

        ActiveContentGroup = ContentPackGroups.Count == 0
            ? null
            : ContentPackGroups[Math.Clamp(value, 0, ContentPackGroups.Count - 1)];
    }

    [RelayCommand]
    private void SelectContentPackKind(string? kindIndex)
    {
        if (!int.TryParse(kindIndex, out var index))
            return;

        SelectedContentPackKindIndex = index;
        ActiveContentGroup?.Reload();
    }

    [ObservableProperty]
    public partial int SelectedModrinthCategoryIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedModrinthSourceIndex { get; set; }

    public ObservableCollection<ModItemViewModel> LocalMods { get; } = new();

    public ObservableCollection<ModrinthProjectItemViewModel> ModrinthResults { get; } = new();

    public bool HasLocalMods => LocalMods.Count > 0;

    public bool HasNoLocalMods => !HasLocalMods;

    /// <summary>
    /// Modrinth mods can only be installed into Fabric / Forge / NeoForge
    /// profiles; vanilla and OptiFine cannot load them.
    /// </summary>
    public bool CanUseModrinth => _version is { IsInstalled: true, IsModded: true }
                                  && ModrinthService.SupportsLoader(_version.LoaderKind);

    public bool CanSearchModrinth =>
        CanUseModrinth
        && IsModrinthSourceSelected
        && !IsModrinthSearching
        && !IsModrinthInstalling;

    public bool IsModrinthSourceSelected => SelectedModrinthSourceIndex == 0;

    public string ModrinthSearchButtonText => IsModrinthSearching
        ? L.Tr("VS.Searching")
        : IsModrinthSourceSelected
            ? L.Tr("VS.Search")
            : L.Tr("VS.ComingSoon");

    public string ModrinthCompatibilityText
    {
        get
        {
            if (_version is null)
                return string.Empty;
            if (!_version.IsInstalled)
                return L.Tr("VS.Compat.NotInstalled");
            if (_version.LoaderKind == ModLoaderKind.OptiFine)
                return L.Tr("VS.Compat.OptiFine");
            if (!_version.IsModded)
                return L.Tr("VS.Compat.Vanilla");

            return L.Format(
                "VS.Compat.Supported",
                _version.BaseVersionName,
                _version.LoaderKind.ToDisplayName());
        }
    }

    [ObservableProperty]
    public partial string NewVersionName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JavaPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MinimumRamMb { get; set; } = GameVersionSettings.DefaultMinimumRamMb;

    [ObservableProperty]
    public partial int MaximumRamMb { get; set; } = GameVersionSettings.DefaultMaximumRamMb;

    [ObservableProperty]
    public partial int WindowWidth { get; set; } = GameVersionSettings.DefaultWindowWidth;

    [ObservableProperty]
    public partial int WindowHeight { get; set; } = GameVersionSettings.DefaultWindowHeight;

    [ObservableProperty]
    public partial bool FullScreen { get; set; }

    [ObservableProperty]
    public partial int SelectedLoginModeIndex { get; set; }

    [ObservableProperty]
    public partial string OfflineUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoJoinServer { get; set; }

    [ObservableProperty]
    public partial string ServerAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ServerPort { get; set; } = GameVersionSettings.DefaultServerPort;

    [ObservableProperty]
    public partial string JvmArguments { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GameArguments { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreLaunchCommand { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalModSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowDisabledMods { get; set; } = true;

    [ObservableProperty]
    public partial string LocalModStatusText { get; set; } = L.Tr("VS.LocalMods.NotScanned");

    [ObservableProperty]
    public partial string ModrinthSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModrinthStatusText { get; set; } = L.Tr("VS.Modrinth.Hint");

    [ObservableProperty]
    public partial bool IsModrinthSearching { get; set; }

    [ObservableProperty]
    public partial bool IsModrinthInstalling { get; set; }

    [ObservableProperty]
    public partial bool IsModEnriching { get; set; }

    [ObservableProperty]
    public partial string ModUpdateStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBatchDeleteConfirmVisible { get; set; }

    public bool HasModUpdateStatus => ModUpdateStatusText.Length > 0;

    public int SelectedModCount => LocalMods.Count(mod => mod.IsSelected);

    public bool HasSelectedMods => SelectedModCount > 0;

    public string SelectedModsText => L.Format("VS.Mods.SelectedCount", SelectedModCount);

    public bool CanSelectAllMods => LocalMods.Count > 0;

    public string SelectAllModsLabel => LocalMods.Count > 0 && SelectedModCount >= LocalMods.Count
        ? L.Tr("VS.Mods.ClearSelection")
        : L.Tr("VS.Mods.SelectAll");

    public bool HasAnyModUpdates => LocalMods.Any(mod => mod.HasUpdate);

    /// <summary>Batch update is only meaningful when a selected jar has a newer file.</summary>
    public bool CanUpdateSelectedMods => LocalMods.Any(mod => mod.IsSelected && mod.HasUpdate);

    [ObservableProperty]
    public partial bool IsDeleteConfirmVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRemoveLoaderConfirmVisible { get; set; }

    [ObservableProperty]
    public partial string SaveState { get; set; } = L.Tr("VS.Save.Auto");

    public bool IsOfflineLogin => SelectedLoginModeIndex == 2;

    public bool IsMicrosoftLogin => SelectedLoginModeIndex == 1;

    public void Open(GameVersion version)
    {
        if (_version is not null
            && _session.Settings.VersionSettings.ContainsKey(_version.Name))
        {
            _saveDebounce?.Cancel();
            SaveNow(reportStatus: false);
        }

        _loading = true;
        SelectedSection = VersionSettingsSection.Settings;

        _version = version;
        OnPropertyChanged(nameof(Version));
        NotifyVersionDerived();

        _model = _session.Settings.GetVersionSettings(version.Name);
        NewVersionName = version.Name;
        JavaPath = _model.JavaPath;
        RefreshJavaRuntimeOptions(JavaPath);
        MinimumRamMb = _model.MinimumRamMb;
        MaximumRamMb = _model.MaximumRamMb;
        WindowWidth = _model.WindowWidth;
        WindowHeight = _model.WindowHeight;
        FullScreen = _model.FullScreen;
        SelectedLoginModeIndex = _model.LoginMode switch
        {
            VersionLoginMode.Microsoft => 1,
            VersionLoginMode.Offline => 2,
            _ => 0
        };
        OfflineUsername = _model.OfflineUsername;
        AutoJoinServer = _model.AutoJoinServer;
        ServerAddress = _model.ServerAddress;
        ServerPort = _model.ServerPort;
        JvmArguments = _model.JvmArguments;
        GameArguments = _model.GameArguments;
        PreLaunchCommand = _model.PreLaunchCommand;
        IsDeleteConfirmVisible = false;
        IsRemoveLoaderConfirmVisible = false;
        SaveState = L.Tr("VS.Save.Auto");

        LocalModSearchText = string.Empty;
        LocalModStatusText = L.Tr("VS.LocalMods.NotScanned");
        ModrinthSearchText = string.Empty;
        ModrinthResults.Clear();
        SelectedModrinthCategoryIndex = 0;
        SelectedModrinthSourceIndex = 0;
        ModrinthStatusText = L.Tr("VS.Modrinth.Hint");

        _loading = false;
        OnPropertyChanged(nameof(IsOfflineLogin));
        OnPropertyChanged(nameof(IsMicrosoftLogin));
        OnPropertyChanged(nameof(ModrinthCompatibilityText));
        OnPropertyChanged(nameof(CanUseModrinth));
        ReloadLocalMods();

        SelectedContentPackKindIndex = 0;
        ActiveContentGroup = ContentPackGroups.Count == 0 ? null : ContentPackGroups[0];
        foreach (var group in ContentPackGroups)
            group.SyncVersion();
    }

    public void Flush()
    {
        if (_version is null)
            return;

        _saveDebounce?.Cancel();
        SaveNow(reportStatus: false);
    }

    [RelayCommand]
    private void Back()
    {
        Flush();
        _goBack();
    }

    [RelayCommand]
    private void SelectSection(string? sectionName)
    {
        SelectedSection = Enum.TryParse<VersionSettingsSection>(sectionName, out var section)
            ? section
            : VersionSettingsSection.Settings;
    }

    [RelayCommand]
    private void Save()
    {
        if (SaveNow(reportStatus: true))
            Session.SetStatus(L.Format("VS.Saved", _version?.Name));
    }

    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        var file = await _picker.PickJavaExecutableAsync(L.Tr("VS.PickJava"));
        if (!string.IsNullOrWhiteSpace(file))
            JavaPath = file;
    }

    [RelayCommand]
    private async Task ScanJavaAsync()
    {
        if (IsScanningJava)
            return;

        IsScanningJava = true;
        JavaScanStatus = L.Tr("Java.Scanning");

        try
        {
            var result = await _javaRuntimes
                .ScanAsync(_session.Settings.GameDirectory)
                .ConfigureAwait(true);

            RefreshJavaRuntimeOptions(JavaPath);
            JavaScanStatus = L.Format("Java.ScanComplete", result.Runtimes.Count);
        }
        catch (Exception ex)
        {
            JavaScanStatus = L.Format("Java.ScanFailed", ex.Message);
        }
        finally
        {
            IsScanningJava = false;
        }
    }

    [RelayCommand]
    private void ClearJavaPath() => JavaPath = string.Empty;

    [RelayCommand]
    private async Task AddOrChangeLoaderAsync()
    {
        if (_version is null || !_version.IsInstalled || Session.IsBusy)
            return;

        _saveDebounce?.Cancel();
        if (!SaveNow(reportStatus: false))
        {
            Session.SetError(L.Format("VS.AddLoaderFailed", SaveState));
            return;
        }

        var sourceVersionName = _version.Name;
        var baseVersionName = _version.BaseVersionName;
        var baseVersion = _session.FindVersion(baseVersionName)
                          ?? new GameVersion(
                              baseVersionName,
                              "release",
                              DateTimeOffset.Now,
                              isInstalled: false);
        var initialJava = JavaPath;

        await LoaderSelector.OpenAsync(
            baseVersion,
            _session.Settings.GameDirectory,
            allowVanilla: false,
            initialJava,
            async selection =>
            {
                var installedName = await _coordinator.InstallModLoaderAsync(
                    _session,
                    baseVersion,
                    selection.Kind,
                    selection.LoaderVersion,
                    selection.JavaPath,
                    selection.ExistingLoaderNames,
                    sourceVersionName);

                if (string.IsNullOrWhiteSpace(installedName))
                    return false;

                await _refreshVersionsAsync();
                var installedVersion = _session.FindVersion(installedName);
                if (installedVersion is null)
                    return false;

                _session.SelectVersion(installedName);
                Open(installedVersion);
                return true;
            });
    }

    [RelayCommand]
    private void RequestRemoveLoader()
    {
        if (_version is { IsModded: true } && !Session.IsBusy)
            IsRemoveLoaderConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelRemoveLoader() => IsRemoveLoaderConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmRemoveLoaderAsync()
    {
        if (_version is not { IsModded: true } || Session.IsBusy)
            return;

        var baseVersionName = _version.BaseVersionName;
        IsRemoveLoaderConfirmVisible = false;
        _saveDebounce?.Cancel();
        SaveNow(reportStatus: false);

        var removed = await _coordinator.RemoveModLoaderAsync(Session, _version);
        if (!removed)
            return;

        await _refreshVersionsAsync();
        var baseVersion = _session.FindVersion(baseVersionName);
        if (baseVersion is { IsInstalled: true })
        {
            _session.SelectVersion(baseVersion.Name);
            Open(baseVersion);
            return;
        }

        _goBack();
    }



    [RelayCommand]
    private void OpenVersionFolder() => OpenFolder(VersionDirectory, create: false);

    [RelayCommand]
    private void OpenSavesFolder() => OpenFolder(SavesDirectory, create: true);

    [RelayCommand]
    private void OpenScreenshotsFolder() => OpenFolder(ScreenshotsDirectory, create: true);

    [RelayCommand]
    private void OpenGameFolder() => OpenFolder(GameDirectory, create: true);

    [RelayCommand]
    private void OpenModsFolder() => OpenFolder(ModsDirectory, create: true);

    [RelayCommand]
    private void OpenConfigFolder() => OpenFolder(ConfigDirectory, create: true);

    [RelayCommand]
    private void RefreshMods() => ReloadLocalMods();

    [RelayCommand]
    private async Task SearchModrinthAsync()
    {
        if (_version is null || !CanUseModrinth || IsModrinthSearching)
            return;

        if (!IsModrinthSourceSelected)
        {
            ModrinthStatusText = L.Tr("VS.CurseForgeUnsupported");
            return;
        }

        IsModrinthSearching = true;
        ModrinthStatusText = L.Tr("VS.Modrinth.Searching");
        var versionName = _version.Name;
        var category = ModrinthCategoryOptions.ElementAtOrDefault(SelectedModrinthCategoryIndex)?.Slug;
        try
        {
            var result = await _modrinthService.SearchAsync(
                _version.BaseVersionName,
                _version.LoaderKind,
                ModrinthSearchText,
                category: category);

            if (_version is null
                || !string.Equals(_version.Name, versionName, StringComparison.Ordinal))
            {
                return;
            }

            ModrinthResults.Clear();
            var items = new List<ModrinthProjectItemViewModel>(result.Hits.Count);
            foreach (var hit in result.Hits)
            {
                var item = new ModrinthProjectItemViewModel(hit, InstallModrinthProjectAsync);
                items.Add(item);
                ModrinthResults.Add(item);
            }

            ModrinthStatusText = result.Hits.Count == 0
                ? L.Tr("VS.Modrinth.NoResults")
                : L.Format("VS.Modrinth.Results", result.TotalHits, result.Hits.Count);
            Session.SetStatus(ModrinthStatusText);

            _ = LoadModrinthIconsAsync(items);
        }
        catch (Exception ex)
        {
            ModrinthStatusText = L.Tr("VS.Modrinth.SearchFailed");
            Session.SetError(L.Format("VS.Modrinth.SearchFailedDetail", ex.Message));
        }
        finally
        {
            IsModrinthSearching = false;
        }
    }

    /// <summary>
    /// Fills in the project icons of the current search results. Runs after the
    /// list is already visible so a slow image never delays the search, and it
    /// stops as soon as another search replaces the result set.
    /// </summary>
    private async Task LoadModrinthIconsAsync(IReadOnlyList<ModrinthProjectItemViewModel> items)
    {
        var loads = items.Select(async item =>
        {
            try
            {
                var icon = await _iconCache.GetAsync(item.Project.IconUrl).ConfigureAwait(true);
                if (icon is not null && ModrinthResults.Contains(item))
                    item.Icon = icon;
            }
            catch (Exception)
            {
                // A missing icon simply keeps the letter placeholder.
            }
        });

        await Task.WhenAll(loads).ConfigureAwait(true);
    }

    private async Task InstallModrinthProjectAsync(ModrinthProjectItemViewModel item)
    {
        if (_version is null || item.IsInstalling || IsModrinthInstalling || Session.IsBusy)
            return;

        item.IsInstalling = true;
        IsModrinthInstalling = true;
        Session.BeginBusy(L.Format("VS.Modrinth.Installing", item.Title));
        try
        {
            var progress = new Progress<string>(message => Session.SetStatus(message));
            var report = await _modrinthService.InstallProjectAsync(
                item.Project.ProjectId,
                ModsDirectory,
                _version.BaseVersionName,
                _version.LoaderKind,
                progress);

            ReloadLocalMods();

            var message = report.InstalledFiles == 0
                ? L.Format("VS.Modrinth.NothingNew", report.SkippedFiles)
                : report.SkippedFiles > 0
                    ? L.Format(
                        "VS.Modrinth.InstalledWithSkipped",
                        report.InstalledFiles,
                        report.SkippedFiles)
                    : L.Format("VS.Modrinth.Installed", report.InstalledFiles);
            if (report.HasWarnings)
            {
                message += " " + L.Format(
                    "VS.Modrinth.Warnings",
                    report.Warnings.Count,
                    report.Warnings[0]);
            }

            Session.SetStatus(message);
            ModrinthStatusText = message;
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Modrinth.InstallFailed", ex.Message));
        }
        finally
        {
            Session.EndBusy();
            IsModrinthInstalling = false;
            item.IsInstalling = false;
        }
    }

    private async Task ToggleModAsync(ModItemViewModel item)
    {
        if (Session.IsBusy)
            return;

        try
        {
            var updated = await Task.Run(() => _modService.SetEnabled(item.Info, !item.Info.IsEnabled));
            ReloadLocalMods();
            Session.SetStatus(updated.IsEnabled
                ? L.Format("VS.Mod.Enabled", updated.DisplayName)
                : L.Format("VS.Mod.Disabled", updated.DisplayName));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Mod.ToggleFailed", ex.Message));
        }
    }

    private async Task DeleteModAsync(ModItemViewModel item)
    {
        if (Session.IsBusy)
            return;

        try
        {
            var name = item.DisplayName;
            _selectedModPaths.Remove(item.FilePath);
            await Task.Run(() => _modService.Delete(item.Info));
            ReloadLocalMods();
            Session.SetStatus(L.Format("VS.Mod.Deleted", name));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Mod.DeleteFailed", ex.Message));
        }
    }

    private async Task UpdateModAsync(ModItemViewModel item)
    {
        if (_version is null || Session.IsBusy || item.IsUpdating)
            return;

        var latest = item.Enrichment?.LatestVersion;
        if (latest is null)
            return;

        item.IsUpdating = true;
        Session.BeginBusy(L.Format("VS.Mods.UpdatingOne", item.DisplayName));
        try
        {
            var installedPath = await _modrinthService.InstallVersionFileAsync(
                latest,
                ModsDirectory,
                item.FilePath);

            ReloadLocalMods();
            Session.SetStatus(installedPath is null
                ? L.Format("VS.Mods.UpdateSkipped", item.DisplayName)
                : L.Format("VS.Mods.UpdatedOne", item.DisplayName, latest.VersionNumber));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Mods.UpdateFailed", item.DisplayName, ex.Message));
        }
        finally
        {
            Session.EndBusy();
            item.IsUpdating = false;
        }
    }

    [RelayCommand]
    private async Task UpdateSelectedModsAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        var targets = LocalMods
            .Where(mod => mod.IsSelected && mod.HasUpdate)
            .ToList();

        if (targets.Count == 0)
        {
            Session.SetStatus(L.Tr("VS.Mods.NoSelectedUpdates"));
            return;
        }

        Session.BeginBusy(L.Tr("VS.Mods.Updating"));
        var updated = 0;
        string? firstError = null;
        foreach (var target in targets)
            target.IsUpdating = true;

        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                var latest = targets[index].Enrichment?.LatestVersion;
                if (latest is null)
                    continue;

                Session.SetStatus(L.Format("VS.Mods.UpdatingMany", index + 1, targets.Count));
                try
                {
                    var installedPath = await _modrinthService.InstallVersionFileAsync(
                        latest,
                        ModsDirectory,
                        targets[index].FilePath);
                    if (installedPath is not null)
                        updated++;
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                }
            }
        }
        finally
        {
            foreach (var target in targets)
                target.IsUpdating = false;

            Session.EndBusy();
        }

        ReloadLocalMods();
        Session.SetStatus(L.Format("VS.Mods.UpdatedMany", updated));
        if (firstError is not null)
            Session.SetError(L.Format("VS.Mods.BatchUpdateFailed", firstError));
    }

    [RelayCommand]
    private void ToggleSelectAllMods()
    {
        var selectAll = LocalMods.Count > 0 && LocalMods.Any(mod => !mod.IsSelected);
        foreach (var mod in LocalMods)
            mod.IsSelected = selectAll;

        UpdateSelectionState();
    }

    [RelayCommand]
    private void RequestDeleteSelectedMods() => IsBatchDeleteConfirmVisible = HasSelectedMods;

    [RelayCommand]
    private void CancelDeleteSelectedMods() => IsBatchDeleteConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmDeleteSelectedModsAsync()
    {
        if (Session.IsBusy)
            return;

        var targets = LocalMods
            .Where(mod => mod.IsSelected)
            .Select(mod => mod.Info)
            .ToList();

        IsBatchDeleteConfirmVisible = false;
        if (targets.Count == 0)
            return;

        Session.BeginBusy(L.Format("VS.Mods.DeletingMany", targets.Count));
        var deleted = 0;
        string? firstError = null;
        try
        {
            foreach (var info in targets)
            {
                try
                {
                    await Task.Run(() => _modService.Delete(info));
                    _selectedModPaths.Remove(info.FilePath);
                    deleted++;
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                }
            }
        }
        finally
        {
            Session.EndBusy();
        }

        ReloadLocalMods();
        Session.SetStatus(L.Format("VS.Mods.DeletedMany", deleted));
        if (firstError is not null)
            Session.SetError(L.Format("VS.Mod.DeleteFailed", firstError));
    }

    [RelayCommand]
    private async Task InstallFromLocalAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        if (!_version.IsInstalled)
        {
            Session.SetError(L.Tr("VS.LocalMods.NotInstalled"));
            return;
        }

        var files = await _picker.PickModFilesAsync(L.Tr("VS.Mods.PickLocalTitle"));
        if (files.Count == 0)
            return;

        var modsDirectory = ModsDirectory;
        var installed = 0;
        var skipped = 0;
        string? firstError = null;

        Session.BeginBusy(L.Tr("VS.Mods.ImportingLocal"));
        try
        {
            foreach (var sourcePath in files)
            {
                try
                {
                    var fileName = Path.GetFileName(sourcePath);
                    var destination = Path.Combine(modsDirectory, fileName);
                    if (string.Equals(destination, sourcePath, StringComparison.OrdinalIgnoreCase)
                        || File.Exists(destination))
                    {
                        skipped++;
                        continue;
                    }

                    Directory.CreateDirectory(modsDirectory);
                    await Task.Run(() => File.Copy(sourcePath, destination));
                    installed++;
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                }
            }
        }
        finally
        {
            Session.EndBusy();
        }

        ReloadLocalMods();
        Session.SetStatus(L.Format("VS.Mods.LocalImported", installed, skipped));
        if (firstError is not null)
            Session.SetError(L.Format("VS.Mods.LocalImportFailed", firstError));
    }

    [RelayCommand]
    private void OpenModDownloads()
    {
        if (_version is null)
            return;

        if (!CanUseModrinth)
        {
            Session.SetError(L.Tr("VS.Modrinth.Unsupported"));
            return;
        }

        SelectedSection = VersionSettingsSection.ModDownloads;
        if (ModrinthResults.Count == 0)
            ModrinthStatusText = L.Tr("VS.Modrinth.Hint");
    }

    private void ReloadLocalMods()
    {
        _allMods.Clear();
        CancelModScan();
        _modMetadataCache.Clear();
        _modHashes.Clear();
        _modEnrichmentMisses.Clear();

        if (_version is { IsInstalled: true })
        {
            try
            {
                _allMods.AddRange(_modService.Scan(ModsDirectory));
            }
            catch (Exception ex)
            {
                LocalModStatusText = L.Format("VS.LocalMods.ReadFailed", ex.Message);
                RebuildLocalMods();
                return;
            }
        }

        _selectedModPaths.RemoveWhere(path => !_allMods.Any(info =>
            string.Equals(info.FilePath, path, StringComparison.OrdinalIgnoreCase)));

        RebuildLocalMods();
    }

    private void RebuildLocalMods()
    {
        LocalMods.Clear();
        var filter = LocalModSearchText?.Trim() ?? string.Empty;
        var loader = _version?.LoaderKind;
        var gameVersion = _version?.BaseVersionName;

        foreach (var info in _allMods)
        {
            if (!ShowDisabledMods && !info.IsEnabled)
                continue;

            _modMetadataCache.TryGetValue(info.FilePath, out var metadata);
            if (filter.Length > 0 && !MatchesModFilter(info, metadata, filter))
                continue;

            LocalMods.Add(new ModItemViewModel(
                info,
                metadata,
                loader,
                gameVersion,
                FindEnrichment(info.FilePath),
                ToggleModAsync,
                DeleteModAsync,
                UpdateModAsync,
                OnModSelectionChanged,
                _selectedModPaths.Contains(info.FilePath)));
        }

        var enabled = _allMods.Count(info => info.IsEnabled);
        LocalModStatusText = _version is not { IsInstalled: true }
            ? L.Tr("VS.Mods.NotInstalled")
            : _allMods.Count == 0
                ? L.Tr("VS.Mods.Empty")
                : L.Format("VS.Mods.Summary", enabled, _allMods.Count - enabled) +
                  (LocalMods.Count != _allMods.Count
                      ? L.Format("VS.Mods.Filtered", LocalMods.Count)
                      : string.Empty);

        OnPropertyChanged(nameof(HasLocalMods));
        OnPropertyChanged(nameof(HasNoLocalMods));
        OnPropertyChanged(nameof(HasAnyModUpdates));
        UpdateSelectionState();
        QueueModScan();
    }

    [RelayCommand]
    private async Task ImportSharedDataAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        if (!_version.IsInstalled)
        {
            Session.SetError(L.Tr("VS.Import.NotInstalled"));
            return;
        }

        var gameDirectory = _session.Settings.GameDirectory;
        var versionName = _version.Name;

        Session.BeginBusy(L.Tr("VS.Import.Busy"));
        try
        {
            var result = await Task.Run(
                () => VersionIsolationService.ImportSharedData(gameDirectory, versionName));

            Session.SetStatus(result.CopiedFiles == 0
                ? L.Tr("VS.Import.Nothing")
                : L.Format("VS.Import.Done", result.CopiedFiles, result.SkippedFiles));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Import.Failed", ex.Message));
        }
        finally
        {
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private async Task RepairAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        _saveDebounce?.Cancel();
        if (!SaveNow(reportStatus: false))
        {
            Session.SetError(L.Format("VS.CompleteFailed", SaveState));
            return;
        }

        await _coordinator.RepairAsync(Session, _version);
        NotifyVersionDerived();
    }

    [RelayCommand]
    private async Task ExportLaunchScriptAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        _saveDebounce?.Cancel();
        if (!SaveNow(reportStatus: false))
        {
            Session.SetError(L.Format("VS.ExportScriptFailed", SaveState));
            return;
        }

        var suggestedName = SanitizeFileName(_version.Name) + "-start.bat";
        var targetPath = await _picker.PickSaveFileAsync(
            L.Tr("VS.ExportScript"),
            suggestedName,
            "bat",
            L.Tr("VS.ExportScript.Batch"),
            "*.bat");

        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        if (!Path.HasExtension(targetPath))
            targetPath += ".bat";

        await _coordinator.ExportLaunchScriptAsync(Session, _version, targetPath);
    }

    [RelayCommand]
    private async Task RenameVersionAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        var oldName = _version.Name;
        var newName = NewVersionName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(newName))
        {
            Session.SetError(L.Tr("VS.Rename.Empty"));
            return;
        }

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            Session.SetStatus(L.Tr("VS.Rename.Unchanged"));
            return;
        }

        try
        {
            MinecraftService.ValidateVersionName(newName);
        }
        catch (Exception ex)
        {
            Session.SetError(ex.Message);
            return;
        }

        _saveDebounce?.Cancel();
        if (!SaveNow(reportStatus: false))
        {
            Session.SetError(L.Format("VS.Rename.Failed", SaveState));
            return;
        }

        Session.BeginBusy(L.Format("VS.Rename.Busy", oldName));

        try
        {
            var gameDirectory = _session.Settings.GameDirectory;
            await Task.Run(() => _minecraft.RenameVersion(gameDirectory, oldName, newName));

            _session.RenameVersion(oldName, newName);
            NewVersionName = newName;

            if (!_settingsService.TrySave(_session.Settings, out var saveError))
                throw new InvalidOperationException(
                    L.Format("VS.Rename.SaveFailed", saveError));

            await _refreshVersionsAsync();
            if (Session.LastError is null)
            {
                var renamedVersion = _session.FindVersion(newName);
                if (renamedVersion is not null)
                    Open(renamedVersion);
            }

            SaveState = L.Tr("VS.Rename.Done");
            NotifyVersionDerived();
            Session.SetStatus(L.Format("VS.Rename.Success", newName));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Rename.Error", ex.Message));
        }
        finally
        {
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private void RequestDelete() => IsDeleteConfirmVisible = true;

    [RelayCommand]
    private void CancelDelete() => IsDeleteConfirmVisible = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        var version = _version;
        if (!version.IsModded)
        {
            var loaders = _minecraft.GetInstalledLoaderProfiles(
                _session.Settings.GameDirectory,
                version.Name);
            if (loaders.Count > 0)
            {
                IsDeleteConfirmVisible = false;
                Session.SetError(L.Tr("VS.Delete.HasLoaders"));
                return;
            }
        }

        _saveDebounce?.Cancel();
        Session.BeginBusy(L.Format("VS.Delete.Busy", version.Name));

        try
        {
            var gameDirectory = _session.Settings.GameDirectory;
            await Task.Run(() => _minecraft.DeleteVersion(gameDirectory, version.Name));

            _session.RemoveVersion(version);
            _settingsService.SaveSilently(_session.Settings);
            Session.SetStatus(L.Format("VS.Delete.Done", version.Name));

            _saveDebounce?.Cancel();
            _version = null;
            IsDeleteConfirmVisible = false;
            OnPropertyChanged(nameof(Version));
            NotifyVersionDerived();
            _goBack();
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Delete.Failed", ex.Message));
        }
        finally
        {
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        await _refreshVersionsAsync();
        var refreshed = _session.InstalledVersions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, _version.Name, StringComparison.Ordinal));

        if (refreshed is not null)
            Open(refreshed);
    }

    private void QueueSave()
    {
        if (_loading || _version is null)
            return;

        _saveDebounce?.Cancel();
        var source = new CancellationTokenSource();
        _saveDebounce = source;
        SaveState = L.Tr("VS.Saving");
        _ = SaveAfterDelayAsync(source);
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(500, source.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (source.IsCancellationRequested || _loading || _version is null)
            return;

        SaveNow(reportStatus: false);
    }

    private bool SaveNow(bool reportStatus)
    {
        if (_version is null)
            return false;

        if (MaximumRamMb < MinimumRamMb)
        {
            SaveState = L.Tr("VS.RamInvalid");
            if (reportStatus)
                Session.SetError(L.Tr("VS.RamInvalidDetail"));
            return false;
        }

        _model.JavaPath = JavaPath?.Trim() ?? string.Empty;
        _model.MinimumRamMb = Math.Clamp(MinimumRamMb, 256, 65536);
        _model.MaximumRamMb = Math.Clamp(MaximumRamMb, _model.MinimumRamMb, 65536);
        _model.WindowWidth = Math.Clamp(WindowWidth, 854, 7680);
        _model.WindowHeight = Math.Clamp(WindowHeight, 480, 4320);
        _model.FullScreen = FullScreen;
        _model.LoginMode = SelectedLoginModeIndex switch
        {
            1 => VersionLoginMode.Microsoft,
            2 => VersionLoginMode.Offline,
            _ => VersionLoginMode.CurrentAccount
        };
        _model.OfflineUsername = OfflineUsername?.Trim() ?? string.Empty;
        _model.AutoJoinServer = AutoJoinServer;
        _model.ServerAddress = ServerAddress?.Trim() ?? string.Empty;
        _model.ServerPort = Math.Clamp(ServerPort, 1, 65535);
        _model.JvmArguments = JvmArguments?.Trim() ?? string.Empty;
        _model.GameArguments = GameArguments?.Trim() ?? string.Empty;
        _model.PreLaunchCommand = PreLaunchCommand?.Trim() ?? string.Empty;

        if (!_settingsService.TrySave(_session.Settings, out var error))
        {
            SaveState = L.Tr("VS.SaveFailed");
            Session.SetError(L.Format("VS.SaveFailedDetail", error));
            return false;
        }

        SaveState = L.Format("VS.Save.Timestamp", DateTime.Now.ToString("HH:mm:ss"));
        return true;
    }

    private void OpenFolder(string path, bool create)
    {
        try
        {
            FolderLauncher.Open(path, create);
            Session.SetStatus(L.Format("VS.Folder.Opened", path));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("VS.Folder.OpenFailed", ex.Message));
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = name.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(characters);
    }

    private void RefreshJavaRuntimeOptions(string? path)
    {
        var desiredPath = path?.Trim() ?? string.Empty;
        var options = new List<JavaRuntimeOption>
        {
            new(string.Empty, L.Tr("Java.AutoDownload"))
        };

        foreach (var runtime in _javaRuntimes.CachedRuntimes)
        {
            if (options.Any(option => string.Equals(
                    option.Path,
                    runtime.Path,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            options.Add(new JavaRuntimeOption(runtime.Path, runtime.DisplayName));
        }

        if (desiredPath.Length > 0
            && options.All(option => !string.Equals(
                option.Path,
                desiredPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new JavaRuntimeOption(
                desiredPath,
                L.Format("Java.CustomRuntime", desiredPath)));
        }

        _syncingJavaSelection = true;
        try
        {
            JavaRuntimeOptions.Clear();
            foreach (var option in options)
                JavaRuntimeOptions.Add(option);

            SelectedJavaRuntime = JavaRuntimeOptions.FirstOrDefault(option =>
                                    string.Equals(
                                        option.Path,
                                        desiredPath,
                                        StringComparison.OrdinalIgnoreCase))
                                ?? JavaRuntimeOptions[0];
        }
        finally
        {
            _syncingJavaSelection = false;
        }

        RefreshJavaScanStatus();
    }

    private void RefreshJavaScanStatus()
    {
        var lastScannedAt = _javaRuntimes.LastScannedAt;
        JavaScanStatus = lastScannedAt is null
            ? L.Tr("Java.NotScanned")
            : L.Format(
                "Java.LastScan",
                lastScannedAt.Value.LocalDateTime.ToString("g"),
                _javaRuntimes.CachedRuntimes.Count);
    }

    private static bool MatchesModFilter(LocalModInfo info, LocalModMetadata? metadata, string filter)
    {
        if (info.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || info.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return metadata is not null
               && (Contains(metadata.Name, filter)
                   || Contains(metadata.ModId, filter)
                   || Contains(metadata.AuthorText, filter));

        static bool Contains(string? value, string filter) =>
            value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEnrichmentKey(
        string hash,
        ModLoaderKind loaderKind,
        string? gameVersion) =>
        $"{hash}|{loaderKind}|{gameVersion}";

    private ModEnrichment? FindEnrichment(string filePath)
    {
        if (_version is null || !_modHashes.TryGetValue(filePath, out var hash))
            return null;

        return _modEnrichmentCache.GetValueOrDefault(
            BuildEnrichmentKey(hash, _version.LoaderKind, _version.BaseVersionName));
    }

    private void RememberEnrichment(string key, ModEnrichment enrichment)
    {
        if (_modEnrichmentCache.Count > 256)
            _modEnrichmentCache.Clear();

        _modEnrichmentCache[key] = enrichment;
    }

    private void OnModSelectionChanged(ModItemViewModel item)
    {
        if (item.IsSelected)
            _selectedModPaths.Add(item.FilePath);
        else
            _selectedModPaths.Remove(item.FilePath);

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        OnPropertyChanged(nameof(SelectedModCount));
        OnPropertyChanged(nameof(HasSelectedMods));
        OnPropertyChanged(nameof(SelectedModsText));
        OnPropertyChanged(nameof(CanSelectAllMods));
        OnPropertyChanged(nameof(SelectAllModsLabel));
        OnPropertyChanged(nameof(CanUpdateSelectedMods));
    }

    private async Task<ModEnrichment?> BuildEnrichmentAsync(
        string filePath,
        string hash,
        string gameVersion,
        ModLoaderKind loaderKind,
        CancellationToken token)
    {
        var metadata = _modMetadataCache.GetValueOrDefault(filePath);
        var installed = await _modrinthService.GetVersionByHashAsync(hash, token)
            .ConfigureAwait(true);
        ModrinthProject? project = null;
        ModrinthVersion? latest = null;
        var author = string.Empty;

        if (installed is not null)
        {
            project = await _modrinthService.GetProjectAsync(installed.ProjectId, token)
                .ConfigureAwait(true);
            latest = await _modrinthService.GetLatestCompatibleVersionAsync(
                    installed.ProjectId,
                    gameVersion,
                    loaderKind,
                    token)
                .ConfigureAwait(true);
        }
        else
        {
            var query = !string.IsNullOrWhiteSpace(metadata?.ModId)
                ? metadata!.ModId
                : metadata?.Name ?? string.Empty;

            var hit = await _modrinthService.FindProjectAsync(query, gameVersion, loaderKind, token)
                .ConfigureAwait(true);

            if (hit is not null)
            {
                author = hit.Author;
                project = await _modrinthService.GetProjectAsync(hit.ProjectId, token)
                    .ConfigureAwait(true);
                latest = await _modrinthService.GetLatestCompatibleVersionAsync(
                        hit.ProjectId,
                        gameVersion,
                        loaderKind,
                        token)
                    .ConfigureAwait(true);
            }
        }

        if (project is null && latest is null)
            return null;

        // The installed jar is itself the newest compatible build when Modrinth
        // reports no separate match for this profile; never warn in that case.
        if (latest is null && installed is not null && IsCompatibleWith(installed, gameVersion, loaderKind))
            latest = installed;

        var icon = await _iconCache.GetAsync(project?.IconUrl, token).ConfigureAwait(true);
        return new ModEnrichment(project, installed, latest, UpdatesChecked: true, author, icon);
    }

    private static bool IsCompatibleWith(
        ModrinthVersion version,
        string gameVersion,
        ModLoaderKind loaderKind)
    {
        var loaderName = ModrinthService.GetLoaderName(loaderKind);
        if (loaderName is null)
            return false;

        var matchesGameVersion = version.GameVersions.Any(value =>
            string.Equals(value, gameVersion, StringComparison.OrdinalIgnoreCase));
        var matchesLoader = version.Loaders.Any(value =>
            string.Equals(value, loaderName, StringComparison.OrdinalIgnoreCase));

        return matchesGameVersion && matchesLoader;
    }

    private void ApplyMetadataToVisible(string filePath, LocalModMetadata? metadata)
    {
        foreach (var mod in LocalMods)
        {
            if (string.Equals(mod.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                mod.ApplyMetadata(metadata);
        }
    }

    private void ApplyEnrichmentToVisible(string filePath, ModEnrichment enrichment)
    {
        foreach (var mod in LocalMods)
        {
            if (string.Equals(mod.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                mod.ApplyEnrichment(enrichment);
        }

        OnPropertyChanged(nameof(HasAnyModUpdates));
        OnPropertyChanged(nameof(CanUpdateSelectedMods));
    }

    private void RefreshModUpdateStatus(int failed = 0)
    {
        if (_version is not { IsInstalled: true })
        {
            ModUpdateStatusText = string.Empty;
            return;
        }

        if (!CanUseModrinth)
        {
            ModUpdateStatusText = L.Tr("VS.Mods.EnrichUnsupported");
            return;
        }

        var updates = LocalMods.Count(mod => mod.HasUpdate);
        var text = updates > 0
            ? L.Format("VS.Mods.UpdatesFound", updates)
            : failed > 0
                ? L.Format("VS.Mods.EnrichPartial", failed)
                : L.Tr("VS.Mods.AllUpToDate");

        if (updates > 0 && failed > 0)
            text += " " + L.Format("VS.Mods.EnrichPartial", failed);

        ModUpdateStatusText = text;
        OnPropertyChanged(nameof(HasAnyModUpdates));
    }

    private void CancelModScan()
    {
        _modScanGeneration++;
        _modScanCts?.Cancel();
        _modScanCts?.Dispose();
        _modScanCts = null;
        IsModEnriching = false;
    }

    /// <summary>
    /// Starts a background pass over the visible mods: jar metadata first (local
    /// I/O), then the Modrinth lookup. Every call supersedes the previous pass so
    /// typing in the filter never stacks overlapping scans.
    /// </summary>
    private void QueueModScan()
    {
        if (_version is not { IsInstalled: true })
        {
            ModUpdateStatusText = string.Empty;
            return;
        }

        CancelModScan();
        var cts = new CancellationTokenSource();
        _modScanCts = cts;
        var generation = _modScanGeneration;
        _ = ScanModsAsync(generation, cts.Token);
    }

    private async Task ScanModsAsync(int generation, CancellationToken token)
    {
        try
        {
            var pending = LocalMods
                .Select(mod => mod.FilePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !_modMetadataCache.ContainsKey(path))
                .ToList();

            foreach (var path in pending)
            {
                if (token.IsCancellationRequested || generation != _modScanGeneration)
                    return;

                LocalModMetadata? metadata = null;
                string hash = string.Empty;
                try
                {
                    metadata = await Task.Run(() => _modScanner.ReadMetadata(path), token)
                        .ConfigureAwait(true);
                    hash = await Task.Run(() => _modScanner.ComputeSha1(path), token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // A locked or corrupt jar simply stays without enriched info.
                    metadata = null;
                    hash = string.Empty;
                }

                if (generation != _modScanGeneration)
                    return;

                _modMetadataCache[path] = metadata;
                if (hash.Length > 0)
                {
                    _modHashes[path] = hash;
                    if (FindEnrichment(path) is { } cachedEnrichment)
                        ApplyEnrichmentToVisible(path, cachedEnrichment);
                }

                ApplyMetadataToVisible(path, metadata);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _modScanGeneration)
            return;

        await EnrichModsAsync(generation, token).ConfigureAwait(true);
    }

    private async Task EnrichModsAsync(int generation, CancellationToken token)
    {
        var version = _version;
        if (version is not { IsInstalled: true, IsModded: true } || !CanUseModrinth)
        {
            ModUpdateStatusText = string.Empty;
            return;
        }

        var mcVersion = version.BaseVersionName;
        var loaderKind = version.LoaderKind;

        var pending = LocalMods
            .Where(mod => mod.Enrichment is null)
            .Select(mod => mod.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => _modHashes.ContainsKey(path))
            .ToList();

        if (pending.Count == 0)
        {
            RefreshModUpdateStatus();
            return;
        }

        IsModEnriching = true;
        ModUpdateStatusText = L.Tr("VS.Mods.CheckingUpdates");
        var failed = 0;
        try
        {
            foreach (var path in pending)
            {
                if (token.IsCancellationRequested || generation != _modScanGeneration)
                    return;

                var key = BuildEnrichmentKey(_modHashes[path], loaderKind, mcVersion);
                if (_modEnrichmentCache.TryGetValue(key, out var cached))
                {
                    ApplyEnrichmentToVisible(path, cached);
                    continue;
                }

                // Modrinth has no data for this file: keep the local metadata and
                // never spend another request on the same hash.
                if (_modEnrichmentMisses.Contains(key))
                {
                    failed++;
                    continue;
                }

                ModEnrichment? enrichment;
                try
                {
                    enrichment = await BuildEnrichmentAsync(
                            path,
                            _modHashes[path],
                            mcVersion,
                            loaderKind,
                            token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    failed++;
                    continue;
                }

                if (token.IsCancellationRequested || generation != _modScanGeneration)
                    return;

                if (enrichment is null)
                {
                    if (_modEnrichmentMisses.Count > 512)
                        _modEnrichmentMisses.Clear();

                    _modEnrichmentMisses.Add(key);
                    failed++;
                    continue;
                }

                RememberEnrichment(key, enrichment);
                ApplyEnrichmentToVisible(path, enrichment);
            }
        }
        finally
        {
            if (generation == _modScanGeneration)
            {
                IsModEnriching = false;
                RefreshModUpdateStatus(failed);
            }
        }
    }

    private void NotifyVersionDerived()
    {
        OnPropertyChanged(nameof(HasVersion));
        OnPropertyChanged(nameof(VersionStatusText));
        OnPropertyChanged(nameof(VersionTypeLabel));
        OnPropertyChanged(nameof(VersionDirectory));
        OnPropertyChanged(nameof(SavesDirectory));
        OnPropertyChanged(nameof(ScreenshotsDirectory));
        OnPropertyChanged(nameof(GameDirectory));
        OnPropertyChanged(nameof(ModsDirectory));
        OnPropertyChanged(nameof(ConfigDirectory));
        OnPropertyChanged(nameof(CanImportSharedData));
        OnPropertyChanged(nameof(DeleteWarningText));
        OnPropertyChanged(nameof(IsOfflineLogin));
        OnPropertyChanged(nameof(IsMicrosoftLogin));
        OnPropertyChanged(nameof(HasModLoader));
        OnPropertyChanged(nameof(IsVanillaVersion));
        OnPropertyChanged(nameof(CanManageModLoader));
        OnPropertyChanged(nameof(IsModLoaderCardVisible));
        OnPropertyChanged(nameof(CurrentLoaderLabel));
        OnPropertyChanged(nameof(LoaderBaseVersionLabel));
        OnPropertyChanged(nameof(CanUseModrinth));
        OnPropertyChanged(nameof(CanSearchModrinth));
        OnPropertyChanged(nameof(ModrinthCompatibilityText));
    }

    partial void OnJavaPathChanged(string value)
    {
        QueueSave();

        if (!_loading && !_syncingJavaSelection)
            RefreshJavaRuntimeOptions(value);
    }

    partial void OnSelectedJavaRuntimeChanged(JavaRuntimeOption? value)
    {
        if (_loading || _syncingJavaSelection || value is null)
            return;

        var path = value.IsAuto ? string.Empty : value.Path;
        if (string.Equals(JavaPath, path, StringComparison.Ordinal))
            return;

        _syncingJavaSelection = true;
        try
        {
            JavaPath = path;
        }
        finally
        {
            _syncingJavaSelection = false;
        }
    }

    partial void OnMinimumRamMbChanged(int value) => QueueSave();

    partial void OnMaximumRamMbChanged(int value) => QueueSave();

    partial void OnWindowWidthChanged(int value) => QueueSave();

    partial void OnWindowHeightChanged(int value) => QueueSave();

    partial void OnFullScreenChanged(bool value) => QueueSave();

    partial void OnSelectedLoginModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOfflineLogin));
        OnPropertyChanged(nameof(IsMicrosoftLogin));
        QueueSave();
    }

    partial void OnOfflineUsernameChanged(string value) => QueueSave();

    partial void OnAutoJoinServerChanged(bool value) => QueueSave();

    partial void OnServerAddressChanged(string value) => QueueSave();

    partial void OnServerPortChanged(int value) => QueueSave();

    partial void OnJvmArgumentsChanged(string value) => QueueSave();

    partial void OnGameArgumentsChanged(string value) => QueueSave();

    partial void OnPreLaunchCommandChanged(string value) => QueueSave();

    partial void OnLocalModSearchTextChanged(string value) => RebuildLocalMods();

    partial void OnShowDisabledModsChanged(bool value) => RebuildLocalMods();

    partial void OnModUpdateStatusTextChanged(string value)
        => OnPropertyChanged(nameof(HasModUpdateStatus));

    partial void OnIsModrinthSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSearchModrinth));
        OnPropertyChanged(nameof(ModrinthSearchButtonText));
    }

    partial void OnIsModrinthInstallingChanged(bool value)
        => OnPropertyChanged(nameof(CanSearchModrinth));

    partial void OnSelectedModrinthSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsModrinthSourceSelected));
        OnPropertyChanged(nameof(CanSearchModrinth));
        OnPropertyChanged(nameof(ModrinthSearchButtonText));

        if (value != 0)
        {
            ModrinthStatusText = L.Tr("VS.CurseForgeHint");
        }
        else if (ModrinthResults.Count == 0)
        {
            ModrinthStatusText = L.Tr("VS.Modrinth.Hint");
        }
    }

    partial void OnSelectedModrinthCategoryIndexChanged(int value)
    {
        if (ModrinthResults.Count == 0)
            return;

        ModrinthStatusText = L.Format(
            "VS.CategoryChanged",
            ModrinthCategoryOptions[value].DisplayName);
    }
}

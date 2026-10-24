using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>Left navigation sections of the version settings page.</summary>
public enum VersionSettingsSection
{
    Settings,
    Mods,
    ModDownloads,
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
    private readonly ModService _modService;
    private readonly ModrinthService _modrinthService;
    private readonly Func<Task> _refreshVersionsAsync;
    private readonly Action _goBack;
    private readonly List<LocalModInfo> _allMods = new();

    private GameVersion? _version;
    private GameVersionSettings _model = new();
    private CancellationTokenSource? _saveDebounce;
    private bool _loading;
    private VersionSettingsSection _selectedSection = VersionSettingsSection.Settings;

    public VersionSettingsViewModel(
        LauncherSession session,
        GameLaunchCoordinator coordinator,
        SettingsService settingsService,
        MinecraftService minecraft,
        StoragePickerService picker,
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
        _modService = modService;
        _modrinthService = modrinthService;
        _refreshVersionsAsync = refreshVersionsAsync;
        _goBack = goBack;
        LoaderSelector = new ModLoaderSelectorViewModel(modLoaderService, minecraft, picker);
    }

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

    public string VersionStatusText => _version is { IsInstalled: true } ? "已安装" : "未安装";

    public string VersionTypeLabel => _version?.TypeLabel ?? string.Empty;

    public bool HasModLoader => _version?.IsModded == true;

    public bool IsVanillaVersion => _version is { IsModded: false };

    public bool CanManageModLoader => _version is { IsInstalled: true };

    /// <summary>
    /// The loader card only appears on the general settings tab and only for
    /// installed versions.
    /// </summary>
    public bool IsModLoaderCardVisible => IsSettingsSection && CanManageModLoader;

    public string CurrentLoaderLabel => _version?.LoaderLabel ?? "原版";

    public string LoaderBaseVersionLabel => _version is { IsModded: true }
        ? $"基于原版 {_version.BaseVersionName}"
        : "当前为原版版本，可以添加一个模组加载器。";

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

    public string DeleteWarningText =>
        "删除此版本会移除整个版本文件夹，包括该版本隔离的存档、截图、模组与配置；共享的 libraries / assets / runtime 与其它版本不受影响。";

    public string DetectedJavaText
    {
        get
        {
            var detected = _minecraft.TryGetDefaultJavaPath(_session.Settings.GameDirectory);
            return string.IsNullOrWhiteSpace(detected)
                ? "未自动检测到 Java，请手动指定。"
                : $"自动检测：{detected}";
        }
    }

    public IReadOnlyList<string> LoginModes { get; } = new[]
    {
        "使用当前账户",
        "Microsoft 账户（需已登录）",
        "离线账户"
    };

    public IReadOnlyList<ModrinthCategoryOption> ModrinthCategoryOptions { get; } = new[]
    {
        new ModrinthCategoryOption("全部类型", null),
        new ModrinthCategoryOption("科技", "technology"),
        new ModrinthCategoryOption("魔法", "magic"),
        new ModrinthCategoryOption("实用", "utility"),
        new ModrinthCategoryOption("冒险", "adventure"),
        new ModrinthCategoryOption("装饰", "decoration"),
        new ModrinthCategoryOption("世界生成", "worldgen"),
        new ModrinthCategoryOption("存储", "storage"),
        new ModrinthCategoryOption("优化", "optimization"),
        new ModrinthCategoryOption("库 / 前置", "library"),
        new ModrinthCategoryOption("生物", "mobs"),
        new ModrinthCategoryOption("装备", "equipment")
    };

    public IReadOnlyList<string> ModrinthSourceOptions { get; } = new[]
    {
        "Modrinth（可用）",
        "CurseForge（即将支持）"
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
            OnPropertyChanged(nameof(IsAdvancedSection));
            OnPropertyChanged(nameof(IsModLoaderCardVisible));
        }
    }

    public bool IsSettingsSection => SelectedSection == VersionSettingsSection.Settings;

    public bool IsModsSection => SelectedSection == VersionSettingsSection.Mods;

    public bool IsModDownloadsSection => SelectedSection == VersionSettingsSection.ModDownloads;

    public bool IsAdvancedSection => SelectedSection == VersionSettingsSection.Advanced;

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
        ? "搜索中..."
        : IsModrinthSourceSelected ? "搜索" : "即将支持";

    public string ModrinthCompatibilityText
    {
        get
        {
            if (_version is null)
                return string.Empty;
            if (!_version.IsInstalled)
                return "请先安装该版本，再管理模组。";
            if (_version.LoaderKind == ModLoaderKind.OptiFine)
                return "OptiFine 版本不支持从 Modrinth 自动安装模组；请使用 Fabric / Forge / NeoForge，或手动把模组放入 mods 文件夹。";
            if (!_version.IsModded)
                return "原版版本无法加载模组。请先在「模组加载器」中添加 Fabric / Forge / NeoForge。";

            return $"只显示兼容 Minecraft {_version.BaseVersionName} 与 " +
                   $"{_version.LoaderKind.ToDisplayName()} 的模组；安装时会自动下载必需的前置模组。";
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
    public partial string LocalModStatusText { get; set; } = "尚未扫描模组文件夹。";

    [ObservableProperty]
    public partial string ModrinthSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModrinthStatusText { get; set; } =
        "搜索并安装兼容当前版本与加载器的模组，必需的前置模组会自动下载。";

    [ObservableProperty]
    public partial bool IsModrinthSearching { get; set; }

    [ObservableProperty]
    public partial bool IsModrinthInstalling { get; set; }

    [ObservableProperty]
    public partial bool IsDeleteConfirmVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRemoveLoaderConfirmVisible { get; set; }

    [ObservableProperty]
    public partial string SaveState { get; set; } = "修改会自动保存";

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
        SaveState = "修改会自动保存";

        LocalModSearchText = string.Empty;
        LocalModStatusText = "尚未扫描模组文件夹。";
        ModrinthSearchText = string.Empty;
        ModrinthResults.Clear();
        SelectedModrinthCategoryIndex = 0;
        SelectedModrinthSourceIndex = 0;
        ModrinthStatusText = "搜索并安装兼容当前版本与加载器的模组，必需的前置模组会自动下载。";

        _loading = false;
        OnPropertyChanged(nameof(IsOfflineLogin));
        OnPropertyChanged(nameof(IsMicrosoftLogin));
        OnPropertyChanged(nameof(ModrinthCompatibilityText));
        OnPropertyChanged(nameof(CanUseModrinth));
        ReloadLocalMods();
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
            Session.SetStatus($"版本 {_version?.Name} 的设置已保存");
    }

    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        var file = await _picker.PickJavaExecutableAsync("选择该版本使用的 Java");
        if (!string.IsNullOrWhiteSpace(file))
            JavaPath = file;
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
            Session.SetError($"无法添加模组加载器：{SaveState}");
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
            ModrinthStatusText = "CurseForge 下载源即将支持，请先选择 Modrinth。";
            return;
        }

        IsModrinthSearching = true;
        ModrinthStatusText = "正在从 Modrinth 搜索兼容模组...";
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
            foreach (var hit in result.Hits)
                ModrinthResults.Add(new ModrinthProjectItemViewModel(hit, InstallModrinthProjectAsync));

            ModrinthStatusText = result.Hits.Count == 0
                ? "没有找到兼容的模组。请检查网络或搜索关键词，并确认该版本使用 Fabric / Forge / NeoForge。"
                : $"共 {result.TotalHits} 个兼容结果，当前显示 {result.Hits.Count} 个。";
            Session.SetStatus(ModrinthStatusText);
        }
        catch (Exception ex)
        {
            ModrinthStatusText = "搜索 Modrinth 失败。";
            Session.SetError($"搜索 Modrinth 失败：{ex.Message}");
        }
        finally
        {
            IsModrinthSearching = false;
        }
    }

    private async Task InstallModrinthProjectAsync(ModrinthProjectItemViewModel item)
    {
        if (_version is null || item.IsInstalling || IsModrinthInstalling || Session.IsBusy)
            return;

        item.IsInstalling = true;
        IsModrinthInstalling = true;
        Session.BeginBusy($"正在从 Modrinth 安装 {item.Title}...");
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
                ? $"未下载新文件（{report.SkippedFiles} 个文件已存在）。"
                : $"已安装 {report.InstalledFiles} 个模组文件" +
                  (report.SkippedFiles > 0 ? $"，跳过 {report.SkippedFiles} 个已存在的文件" : string.Empty) + "。";
            if (report.HasWarnings)
                message += $" 有 {report.Warnings.Count} 个必需前置未自动安装：{report.Warnings[0]}";

            Session.SetStatus(message);
            ModrinthStatusText = message;
        }
        catch (Exception ex)
        {
            Session.SetError($"安装 Modrinth 模组失败：{ex.Message}");
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
                ? $"已启用模组 {updated.DisplayName}，下次启动游戏时生效"
                : $"已禁用模组 {updated.DisplayName}（文件已重命名为 .jar.disabled）");
        }
        catch (Exception ex)
        {
            Session.SetError($"切换模组状态失败：{ex.Message}");
        }
    }

    private async Task DeleteModAsync(ModItemViewModel item)
    {
        if (Session.IsBusy)
            return;

        try
        {
            var name = item.DisplayName;
            await Task.Run(() => _modService.Delete(item.Info));
            ReloadLocalMods();
            Session.SetStatus($"已删除模组 {name}");
        }
        catch (Exception ex)
        {
            Session.SetError($"删除模组失败：{ex.Message}");
        }
    }

    private void ReloadLocalMods()
    {
        _allMods.Clear();

        if (_version is { IsInstalled: true })
        {
            try
            {
                _allMods.AddRange(_modService.Scan(ModsDirectory));
            }
            catch (Exception ex)
            {
                LocalModStatusText = $"读取 mods 文件夹失败：{ex.Message}";
                RebuildLocalMods();
                return;
            }
        }

        RebuildLocalMods();
    }

    private void RebuildLocalMods()
    {
        LocalMods.Clear();
        var filter = LocalModSearchText?.Trim() ?? string.Empty;

        foreach (var info in _allMods)
        {
            if (!ShowDisabledMods && !info.IsEnabled)
                continue;

            if (filter.Length > 0
                && !info.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !info.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LocalMods.Add(new ModItemViewModel(info, ToggleModAsync, DeleteModAsync));
        }

        var enabled = _allMods.Count(info => info.IsEnabled);
        LocalModStatusText = _version is not { IsInstalled: true }
            ? "安装该版本后即可管理模组。"
            : _allMods.Count == 0
                ? "mods 文件夹中还没有模组。可以手动放入 .jar，或从下面 Modrinth 搜索下载。"
                : $"已启用 {enabled} 个，已禁用 {_allMods.Count - enabled} 个" +
                  (LocalMods.Count != _allMods.Count ? $"，当前显示 {LocalMods.Count} 个" : string.Empty);

        OnPropertyChanged(nameof(HasLocalMods));
        OnPropertyChanged(nameof(HasNoLocalMods));
    }

    [RelayCommand]
    private async Task ImportSharedDataAsync()
    {
        if (_version is null || Session.IsBusy)
            return;

        if (!_version.IsInstalled)
        {
            Session.SetError("请先安装该版本，再导入共享数据。");
            return;
        }

        var gameDirectory = _session.Settings.GameDirectory;
        var versionName = _version.Name;

        Session.BeginBusy("正在从共享目录导入存档与配置...");
        try
        {
            var result = await Task.Run(
                () => VersionIsolationService.ImportSharedData(gameDirectory, versionName));

            Session.SetStatus(result.CopiedFiles == 0
                ? "共享目录中没有可导入的数据，或目标文件已全部存在"
                : $"已从共享目录复制 {result.CopiedFiles} 个文件到本版本，跳过 {result.SkippedFiles} 个已存在的文件");
        }
        catch (Exception ex)
        {
            Session.SetError($"导入共享数据失败：{ex.Message}");
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
            Session.SetError($"无法补全文件：{SaveState}");
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
            Session.SetError($"无法导出启动脚本：{SaveState}");
            return;
        }

        var suggestedName = SanitizeFileName(_version.Name) + "-start.bat";
        var targetPath = await _picker.PickSaveFileAsync(
            "导出启动脚本",
            suggestedName,
            "bat",
            "批处理脚本",
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
            Session.SetError("版本名不能为空。");
            return;
        }

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            Session.SetStatus("版本名没有变化");
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
            Session.SetError($"无法重命名版本：{SaveState}");
            return;
        }

        Session.BeginBusy($"正在重命名版本 {oldName}...");

        try
        {
            var gameDirectory = _session.Settings.GameDirectory;
            await Task.Run(() => _minecraft.RenameVersion(gameDirectory, oldName, newName));

            _session.RenameVersion(oldName, newName);
            NewVersionName = newName;

            if (!_settingsService.TrySave(_session.Settings, out var saveError))
                throw new InvalidOperationException($"版本文件已重命名，但设置保存失败：{saveError}");

            await _refreshVersionsAsync();
            if (Session.LastError is null)
            {
                var renamedVersion = _session.FindVersion(newName);
                if (renamedVersion is not null)
                    Open(renamedVersion);
            }

            SaveState = "版本已重命名";
            NotifyVersionDerived();
            Session.SetStatus($"版本已重命名为 {newName}");
        }
        catch (Exception ex)
        {
            Session.SetError($"重命名版本失败：{ex.Message}");
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
                Session.SetError("该版本仍有模组加载器，请先在版本设置中移除加载器，再删除原版版本。");
                return;
            }
        }

        _saveDebounce?.Cancel();
        Session.BeginBusy($"正在删除版本 {version.Name}...");

        try
        {
            var gameDirectory = _session.Settings.GameDirectory;
            await Task.Run(() => _minecraft.DeleteVersion(gameDirectory, version.Name));

            _session.RemoveVersion(version);
            _settingsService.SaveSilently(_session.Settings);
            Session.SetStatus($"已删除版本 {version.Name}（该版本隔离的存档、截图与模组已一并删除）");

            _saveDebounce?.Cancel();
            _version = null;
            IsDeleteConfirmVisible = false;
            OnPropertyChanged(nameof(Version));
            NotifyVersionDerived();
            _goBack();
        }
        catch (Exception ex)
        {
            Session.SetError($"删除版本失败：{ex.Message}");
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
        SaveState = "正在保存...";
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
            SaveState = "最大内存不能小于最小内存";
            if (reportStatus)
                Session.SetError("最大内存不能小于最小内存。");
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
            SaveState = "保存失败";
            Session.SetError($"保存版本设置失败：{error}");
            return false;
        }

        SaveState = $"已保存 {DateTime.Now:HH:mm:ss}";
        return true;
    }

    private void OpenFolder(string path, bool create)
    {
        try
        {
            if (create)
                Directory.CreateDirectory(path);

            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"文件夹不存在：{path}");

            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true }
                    : new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true };

            Process.Start(startInfo);
            Session.SetStatus($"已在文件管理器中打开：{path}");
        }
        catch (Exception ex)
        {
            Session.SetError($"打开文件夹失败：{ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = name.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(characters);
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
        OnPropertyChanged(nameof(DetectedJavaText));
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

    partial void OnJavaPathChanged(string value) => QueueSave();

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
            ModrinthStatusText = "CurseForge 下载源即将支持；当前版本可继续使用 Modrinth。";
        }
        else if (ModrinthResults.Count == 0)
        {
            ModrinthStatusText = "搜索并安装兼容当前版本与加载器的模组，必需的前置模组会自动下载。";
        }
    }

    partial void OnSelectedModrinthCategoryIndexChanged(int value)
    {
        if (ModrinthResults.Count == 0)
            return;

        ModrinthStatusText = $"类型已切换为「{ModrinthCategoryOptions[value].DisplayName}」，点击搜索刷新结果。";
    }
}

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

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
    private readonly Func<Task> _refreshVersionsAsync;
    private readonly Action _goBack;

    private GameVersion? _version;
    private GameVersionSettings _model = new();
    private CancellationTokenSource? _saveDebounce;
    private bool _loading;

    public VersionSettingsViewModel(
        LauncherSession session,
        GameLaunchCoordinator coordinator,
        SettingsService settingsService,
        MinecraftService minecraft,
        StoragePickerService picker,
        ModLoaderService modLoaderService,
        Func<Task> refreshVersionsAsync,
        Action goBack)
    {
        _session = session;
        Session = session;
        _coordinator = coordinator;
        _settingsService = settingsService;
        _minecraft = minecraft;
        _picker = picker;
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

    public string CurrentLoaderLabel => _version?.LoaderLabel ?? "原版";

    public string LoaderBaseVersionLabel => _version is { IsModded: true }
        ? $"基于原版 {_version.BaseVersionName}"
        : "当前为原版版本，可以添加一个模组加载器。";

    public string VersionDirectory => _version is null
        ? string.Empty
        : _minecraft.GetVersionDirectory(_session.Settings.GameDirectory, _version.Name);

    public string SavesDirectory => _minecraft.GetSavesDirectory(_session.Settings.GameDirectory);

    public string ScreenshotsDirectory => _minecraft.GetScreenshotsDirectory(_session.Settings.GameDirectory);

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

        _loading = false;
        OnPropertyChanged(nameof(IsOfflineLogin));
        OnPropertyChanged(nameof(IsMicrosoftLogin));
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
            Session.SetStatus($"已删除版本 {version.Name}（存档与截图未删除）");

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

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });

            Session.SetStatus($"已在资源管理器中打开：{path}");
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
        OnPropertyChanged(nameof(DetectedJavaText));
        OnPropertyChanged(nameof(IsOfflineLogin));
        OnPropertyChanged(nameof(IsMicrosoftLogin));
        OnPropertyChanged(nameof(HasModLoader));
        OnPropertyChanged(nameof(IsVanillaVersion));
        OnPropertyChanged(nameof(CanManageModLoader));
        OnPropertyChanged(nameof(CurrentLoaderLabel));
        OnPropertyChanged(nameof(LoaderBaseVersionLabel));
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
}

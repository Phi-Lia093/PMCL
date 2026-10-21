using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Editable copy of <see cref="Models.LauncherSettings"/> with explicit save.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly StoragePickerService _picker;
    private readonly MinecraftService _minecraft;
    private readonly Func<Task> _refreshVersionsAsync;

    public SettingsViewModel(
        LauncherSession session,
        SettingsService settingsService,
        StoragePickerService picker,
        MinecraftService minecraft,
        Func<Task> refreshVersionsAsync)
    {
        Session = session;
        _settingsService = settingsService;
        _picker = picker;
        _minecraft = minecraft;
        _refreshVersionsAsync = refreshVersionsAsync;

        var settings = session.Settings;
        GameDirectory = settings.GameDirectory;
        JavaPath = settings.JavaPath;
        MinimumRamMb = settings.MinimumRamMb;
        MaximumRamMb = settings.MaximumRamMb;
        WindowWidth = settings.WindowWidth;
        WindowHeight = settings.WindowHeight;
        FullScreen = settings.FullScreen;
    }

    public LauncherSession Session { get; }

    [ObservableProperty]
    public partial string GameDirectory { get; set; }

    [ObservableProperty]
    public partial string JavaPath { get; set; }

    [ObservableProperty]
    public partial int MinimumRamMb { get; set; }

    [ObservableProperty]
    public partial int MaximumRamMb { get; set; }

    [ObservableProperty]
    public partial int WindowWidth { get; set; }

    [ObservableProperty]
    public partial int WindowHeight { get; set; }

    [ObservableProperty]
    public partial bool FullScreen { get; set; }

    [RelayCommand]
    private async Task BrowseGameDirectoryAsync()
    {
        var folder = await _picker.PickFolderAsync("选择 .minecraft 游戏目录");
        if (!string.IsNullOrWhiteSpace(folder))
            GameDirectory = folder;
    }

    [RelayCommand]
    private async Task BrowseJavaAsync()
    {
        var file = await _picker.PickJavaExecutableAsync("选择 Java 可执行文件");
        if (!string.IsNullOrWhiteSpace(file))
            JavaPath = file;
    }

    [RelayCommand]
    private void ResetGameDirectory() => GameDirectory = AppPaths.DefaultGameDirectory;

    [RelayCommand]
    private void ClearJavaPath() => JavaPath = string.Empty;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Session.IsBusy)
            return;

        if (MaximumRamMb < MinimumRamMb)
        {
            Session.SetError("最大内存不能小于最小内存。");
            return;
        }

        var previousDirectory = Session.Settings.GameDirectory;

        Session.Settings.GameDirectory = GameDirectory;
        Session.Settings.JavaPath = JavaPath;
        Session.Settings.MinimumRamMb = MinimumRamMb;
        Session.Settings.MaximumRamMb = MaximumRamMb;
        Session.Settings.WindowWidth = WindowWidth;
        Session.Settings.WindowHeight = WindowHeight;
        Session.Settings.FullScreen = FullScreen;

        if (!_settingsService.TrySave(Session.Settings, out var error))
        {
            Session.SetError($"保存设置失败：{error}");
            return;
        }

        Session.DetectedJavaPath = _minecraft.TryGetDefaultJavaPath(Session.Settings.GameDirectory);
        Session.NotifyJavaSummaryChanged();

        if (!string.Equals(previousDirectory, Session.Settings.GameDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Session.VersionsLoaded = false;
            await _refreshVersionsAsync();
        }

        Session.SetStatus("设置已保存");
    }
}

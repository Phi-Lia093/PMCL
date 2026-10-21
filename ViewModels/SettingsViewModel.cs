using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Launcher-wide settings. Per-version Java/memory/launch options live on
/// <see cref="VersionSettingsViewModel"/>.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly StoragePickerService _picker;
    private readonly AppPaths _paths;
    private readonly Func<Task> _refreshVersionsAsync;

    public SettingsViewModel(
        LauncherSession session,
        SettingsService settingsService,
        StoragePickerService picker,
        AppPaths paths,
        Func<Task> refreshVersionsAsync)
    {
        Session = session;
        _settingsService = settingsService;
        _picker = picker;
        _paths = paths;
        _refreshVersionsAsync = refreshVersionsAsync;

        GameDirectory = session.Settings.GameDirectory;
        SelectedLanguageIndex = 0;
    }

    public LauncherSession Session { get; }

    public IReadOnlyList<string> LanguageOptions { get; } = new[] { "简体中文" };

    public string DataDirectory => _paths.DataDirectory;

    [ObservableProperty]
    public partial string GameDirectory { get; set; }

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    [RelayCommand]
    private async Task BrowseGameDirectoryAsync()
    {
        var folder = await _picker.PickFolderAsync("选择 .minecraft 游戏目录");
        if (!string.IsNullOrWhiteSpace(folder))
            GameDirectory = folder;
    }

    [RelayCommand]
    private void ResetGameDirectory() => GameDirectory = AppPaths.DefaultGameDirectory;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Session.IsBusy)
            return;

        string directory;
        try
        {
            directory = Path.GetFullPath(
                string.IsNullOrWhiteSpace(GameDirectory)
                    ? AppPaths.DefaultGameDirectory
                    : GameDirectory.Trim());
        }
        catch (Exception ex)
        {
            Session.SetError($"游戏目录无效：{ex.Message}");
            return;
        }

        var previousDirectory = Session.Settings.GameDirectory;
        Session.Settings.GameDirectory = directory;
        Session.Settings.Language = string.IsNullOrWhiteSpace(Session.Settings.Language)
            ? "zh-CN"
            : Session.Settings.Language;

        if (!_settingsService.TrySave(Session.Settings, out var error))
        {
            Session.SetError($"保存设置失败：{error}");
            return;
        }

        GameDirectory = directory;

        if (!string.Equals(previousDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            Session.VersionsLoaded = false;
            Session.ApplyVersions(Array.Empty<Models.GameVersion>());
            await _refreshVersionsAsync();
        }

        Session.SetStatus("全局设置已保存");
    }
}

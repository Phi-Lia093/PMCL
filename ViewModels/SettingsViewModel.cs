using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Models;
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
        SelectedLanguageIndex = FindLanguageIndex(session.Settings.Language);
        SelectedDownloadSourceIndex =
            session.Settings.DownloadSource == DownloadSourceKind.BmclApi ? 1 : 0;
        SelectedCloseActionIndex =
            session.Settings.CloseAction == LauncherCloseAction.Exit ? 1 : 0;
        SelectedGameLaunchActionIndex = session.Settings.OnGameLaunchAction switch
        {
            GameLaunchWindowAction.MinimizeToTray => 1,
            GameLaunchWindowAction.CloseLauncher => 2,
            _ => 0
        };
        RestoreAfterGameExit = session.Settings.RestoreAfterGameExit;
    }

    public LauncherSession Session { get; }

    public IReadOnlyList<string> LanguageOptions { get; } =
        LocalizationService.Instance.Languages.Select(option => option.DisplayName).ToArray();

    public IReadOnlyList<string> DownloadSourceOptions { get; } = new[]
    {
        L.Tr("Settings.DownloadSource.Official"),
        L.Tr("Settings.DownloadSource.BmclApi")
    };

    public IReadOnlyList<string> CloseActionOptions { get; } = new[]
    {
        L.Tr("Settings.CloseAction.Minimize"),
        L.Tr("Settings.CloseAction.Exit")
    };

    public IReadOnlyList<string> GameLaunchActionOptions { get; } = new[]
    {
        L.Tr("Settings.LaunchAction.None"),
        L.Tr("Settings.LaunchAction.Minimize"),
        L.Tr("Settings.LaunchAction.Exit")
    };

    public string DownloadSourceDescription => GetSelectedDownloadSource().ToDescription();

    public string DataDirectory => _paths.DataDirectory;

    [ObservableProperty]
    public partial string GameDirectory { get; set; }

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedDownloadSourceIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedCloseActionIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedGameLaunchActionIndex { get; set; }

    [ObservableProperty]
    public partial bool RestoreAfterGameExit { get; set; }

    [RelayCommand]
    private async Task BrowseGameDirectoryAsync()
    {
        var folder = await _picker.PickFolderAsync(L.Tr("Settings.PickGameDirectory"));
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
            Session.SetError(L.Format("Settings.InvalidGameDirectory", ex.Message));
            return;
        }

        var previousDirectory = Session.Settings.GameDirectory;
        var previousLanguage = LocalizationService.Instance.CurrentLanguage;
        Session.Settings.GameDirectory = directory;
        Session.Settings.Language = GetSelectedLanguage();
        Session.Settings.DownloadSource = GetSelectedDownloadSource();
        Session.Settings.CloseAction = GetSelectedCloseAction();
        Session.Settings.OnGameLaunchAction = GetSelectedGameLaunchAction();
        Session.Settings.RestoreAfterGameExit = RestoreAfterGameExit;
        Session.RefreshDownloadSourceText();

        if (!_settingsService.TrySave(Session.Settings, out var error))
        {
            Session.SetError(L.Format("Settings.SaveFailed", error));
            return;
        }

        GameDirectory = directory;

        if (!string.Equals(previousDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            Session.VersionsLoaded = false;
            Session.ApplyVersions(Array.Empty<Models.GameVersion>());
            await _refreshVersionsAsync();
        }

        Session.SetStatus(string.Equals(previousLanguage, Session.Settings.Language, StringComparison.OrdinalIgnoreCase)
            ? L.Tr("Settings.Saved")
            : L.Tr("Settings.SavedRestart"));
    }

    private string GetSelectedLanguage()
        => LocalizationService.Instance.Languages[
            Math.Clamp(SelectedLanguageIndex, 0, LocalizationService.Instance.Languages.Count - 1)].Code;

    private static int FindLanguageIndex(string? language)
    {
        for (var i = 0; i < LocalizationService.Instance.Languages.Count; i++)
        {
            if (string.Equals(LocalizationService.Instance.Languages[i].Code, language, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private DownloadSourceKind GetSelectedDownloadSource()
        => SelectedDownloadSourceIndex == 1
            ? DownloadSourceKind.BmclApi
            : DownloadSourceKind.Official;

    private LauncherCloseAction GetSelectedCloseAction()
        => SelectedCloseActionIndex == 1
            ? LauncherCloseAction.Exit
            : LauncherCloseAction.MinimizeToTray;

    private GameLaunchWindowAction GetSelectedGameLaunchAction()
        => SelectedGameLaunchActionIndex switch
        {
            1 => GameLaunchWindowAction.MinimizeToTray,
            2 => GameLaunchWindowAction.CloseLauncher,
            _ => GameLaunchWindowAction.None
        };

    partial void OnSelectedDownloadSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DownloadSourceDescription));
        Session.Settings.DownloadSource = GetSelectedDownloadSource();
        Session.RefreshDownloadSourceText();
    }

    partial void OnSelectedCloseActionIndexChanged(int value)
        => Session.Settings.CloseAction = GetSelectedCloseAction();

    partial void OnSelectedGameLaunchActionIndexChanged(int value)
        => Session.Settings.OnGameLaunchAction = GetSelectedGameLaunchAction();

    partial void OnRestoreAfterGameExitChanged(bool value)
        => Session.Settings.RestoreAfterGameExit = value;
}

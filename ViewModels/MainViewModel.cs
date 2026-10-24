using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Shell view model: owns navigation, pages and application startup.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly MinecraftService _minecraft;

    public MainViewModel(
        LauncherSession session,
        AuthenticationService authentication,
        SettingsService settingsService,
        MinecraftService minecraft,
        StoragePickerService picker,
        AppPaths paths)
    {
        Session = session;
        _minecraft = minecraft;
        var modLoaderService = new ModLoaderService(minecraft);
        var coordinator = new GameLaunchCoordinator(minecraft, settingsService, modLoaderService);

        Account = new AccountViewModel(session, authentication, settingsService);
        VersionSettings = new VersionSettingsViewModel(
            session,
            coordinator,
            settingsService,
            minecraft,
            picker,
            modLoaderService,
            new ModService(),
            new ModrinthService(),
            RefreshVersionsAsync,
            NavigateHome);
        Home = new HomeViewModel(
            session,
            coordinator,
            RefreshVersionsAsync,
            () => CurrentPage = Account,
            OpenVersionSettings);
        Versions = new VersionsViewModel(
            session,
            coordinator,
            modLoaderService,
            minecraft,
            picker,
            RefreshVersionsAsync);
        Settings = new SettingsViewModel(session, settingsService, picker, paths, RefreshVersionsAsync);

        CurrentPage = Home;
    }

    public LauncherSession Session { get; }

    public HomeViewModel Home { get; }

    public VersionsViewModel Versions { get; }

    public AccountViewModel Account { get; }

    public SettingsViewModel Settings { get; }

    public VersionSettingsViewModel VersionSettings { get; }

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; }

    public bool IsHomeSelected => ReferenceEquals(CurrentPage, Home)
                                  || ReferenceEquals(CurrentPage, VersionSettings);

    public bool IsVersionsSelected => ReferenceEquals(CurrentPage, Versions);

    public bool IsAccountSelected => ReferenceEquals(CurrentPage, Account);

    public bool IsSettingsSelected => ReferenceEquals(CurrentPage, Settings);

    public string PageTitle => CurrentPage switch
    {
        HomeViewModel => L.Tr("Nav.Home"),
        VersionsViewModel => L.Tr("Nav.Versions"),
        AccountViewModel => L.Tr("Nav.Account"),
        SettingsViewModel => L.Tr("Nav.SettingsPage"),
        VersionSettingsViewModel => L.Tr("Nav.VersionSettings"),
        _ => "PMCL"
    };

    public string PageSubtitle => CurrentPage switch
    {
        HomeViewModel => L.Tr("Page.Subtitle.Home"),
        VersionsViewModel => L.Tr("Page.Subtitle.Versions"),
        AccountViewModel => L.Tr("Page.Subtitle.Account"),
        SettingsViewModel => L.Tr("Page.Subtitle.Settings"),
        VersionSettingsViewModel => L.Tr("Page.Subtitle.VersionSettings"),
        _ => "Philia093's MineCraft Launcher"
    };

    public string AppVersionText => "PMCL 0.1.0 · Avalonia + CmlLib";

    public async Task InitializeAsync()
    {
        Account.ReloadSavedAccounts();
        await RefreshVersionsAsync();
        await RestoreAccountAsync();
    }

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        if (Session.IsLoadingVersions)
            return;

        Session.IsLoadingVersions = true;
        Session.LastError = null;

        try
        {
            var versions = await _minecraft.GetVersionsAsync(Session.Settings.GameDirectory);
            Session.ApplyVersions(versions);
            Session.EnsureInstalledSelection();
            Session.VersionsLoaded = true;
            Versions.RebuildFromSession();
            Session.SetStatus(L.Format(
                "Versions.LoadedSummary",
                versions.Count,
                Session.InstalledVersions.Count));
        }
        catch (Exception ex)
        {
            Session.VersionsLoaded = false;
            Session.SetError(L.Format("Versions.LoadFailed", ex.Message));
        }
        finally
        {
            Session.IsLoadingVersions = false;
        }
    }

    public void FlushPendingSettings() => VersionSettings.Flush();

    [RelayCommand]
    private void NavigateHome()
    {
        VersionSettings.Flush();
        Versions.CloseLoaderDialog();
        VersionSettings.CloseLoaderDialog();
        Session.EnsureInstalledSelection();
        CurrentPage = Home;
    }

    private void OpenVersionSettings(GameVersion version)
    {
        if (!version.IsInstalled)
            return;

        Versions.CloseLoaderDialog();
        VersionSettings.CloseLoaderDialog();
        VersionSettings.Open(version);
        CurrentPage = VersionSettings;
    }

    [RelayCommand]
    private void NavigateVersions()
    {
        VersionSettings.Flush();
        Versions.CloseLoaderDialog();
        VersionSettings.CloseLoaderDialog();
        CurrentPage = Versions;
    }

    [RelayCommand]
    private void NavigateAccount()
    {
        VersionSettings.Flush();
        Versions.CloseLoaderDialog();
        VersionSettings.CloseLoaderDialog();
        CurrentPage = Account;
    }

    [RelayCommand]
    private void NavigateSettings()
    {
        VersionSettings.Flush();
        Versions.CloseLoaderDialog();
        VersionSettings.CloseLoaderDialog();
        CurrentPage = Settings;
    }

    private async Task RestoreAccountAsync()
    {
        try
        {
            await Account.RestoreLastAccountAsync();
        }
        catch (Exception ex)
        {
            Session.SetStatus(L.Format("Account.RestoreFailed", ex.Message));
        }
    }

    partial void OnCurrentPageChanged(ViewModelBase value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsVersionsSelected));
        OnPropertyChanged(nameof(IsAccountSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }
}

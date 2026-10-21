using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Shell view model: owns navigation, pages and application startup.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly AuthenticationService _authentication;
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
        _authentication = authentication;
        _minecraft = minecraft;
        var coordinator = new GameLaunchCoordinator(minecraft, settingsService);

        Account = new AccountViewModel(session, authentication, settingsService);
        VersionSettings = new VersionSettingsViewModel(
            session,
            coordinator,
            settingsService,
            minecraft,
            picker,
            RefreshVersionsAsync,
            NavigateHome);
        Home = new HomeViewModel(
            session,
            coordinator,
            RefreshVersionsAsync,
            () => CurrentPage = Account,
            OpenVersionSettings);
        Versions = new VersionsViewModel(session, coordinator, RefreshVersionsAsync);
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
        HomeViewModel => "主页",
        VersionsViewModel => "版本库",
        AccountViewModel => "账户",
        SettingsViewModel => "全局设置",
        VersionSettingsViewModel => "版本设置",
        _ => "PMCL"
    };

    public string PageSubtitle => CurrentPage switch
    {
        HomeViewModel => "欢迎使用 PMCL，准备好开始游戏了吗？",
        VersionsViewModel => "浏览 Mojang 官方版本，下载后即可在主页启动。",
        AccountViewModel => "使用 Microsoft 正版账户，或创建本地离线账户。",
        SettingsViewModel => "管理游戏目录、界面语言与启动器数据。",
        VersionSettingsViewModel => "为此版本单独配置 Java、内存、登录方式与启动参数。",
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
            Session.SetStatus($"已加载 {versions.Count} 个 Minecraft 版本，本地已安装 {Session.InstalledVersions.Count} 个");
        }
        catch (Exception ex)
        {
            Session.VersionsLoaded = false;
            Session.SetError($"获取版本列表失败：{ex.Message}");
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
        Session.EnsureInstalledSelection();
        CurrentPage = Home;
    }

    private void OpenVersionSettings(GameVersion version)
    {
        if (!version.IsInstalled)
            return;

        VersionSettings.Open(version);
        CurrentPage = VersionSettings;
    }

    [RelayCommand]
    private void NavigateVersions()
    {
        VersionSettings.Flush();
        CurrentPage = Versions;
    }

    [RelayCommand]
    private void NavigateAccount()
    {
        VersionSettings.Flush();
        CurrentPage = Account;
    }

    [RelayCommand]
    private void NavigateSettings()
    {
        VersionSettings.Flush();
        CurrentPage = Settings;
    }

    private async Task RestoreAccountAsync()
    {
        if (Session.IsBusy)
            return;

        try
        {
            if (Session.Settings.LastAccountKind == nameof(AccountKind.Microsoft))
            {
                Session.BeginBusy("正在恢复 Microsoft 登录...");
                try
                {
                    var microsoftSession = await _authentication.LoginMicrosoftSilentlyAsync();
                    Session.SetAccount(microsoftSession, AccountKind.Microsoft);
                    Account.ReloadSavedAccounts();
                    Session.SetStatus($"已恢复 Microsoft 账户 {microsoftSession.Username}");
                }
                catch (Exception)
                {
                    Session.EndBusy();
                    Session.SetStatus("Microsoft 自动登录已失效，请在账户页重新登录。");
                }
                finally
                {
                    if (Session.IsBusy)
                        Session.EndBusy();
                }
            }
            else if (Session.Settings.LastAccountKind == nameof(AccountKind.Offline)
                     && !string.IsNullOrWhiteSpace(Session.Settings.OfflineUsername))
            {
                var offlineSession = AuthenticationService.CreateOfflineSession(Session.Settings.OfflineUsername);
                Session.SetAccount(offlineSession, AccountKind.Offline);
                Session.SetStatus($"已恢复离线账户 {offlineSession.Username}");
            }
        }
        catch (Exception ex)
        {
            Session.SetStatus($"恢复账户失败：{ex.Message}");
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

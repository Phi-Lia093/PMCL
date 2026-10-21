using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Microsoft and offline account management.
/// </summary>
public sealed partial class AccountViewModel : ViewModelBase
{
    private readonly AuthenticationService _authentication;
    private readonly SettingsService _settingsService;

    public AccountViewModel(
        LauncherSession session,
        AuthenticationService authentication,
        SettingsService settingsService)
    {
        Session = session;
        _authentication = authentication;
        _settingsService = settingsService;
        OfflineUsername = session.Settings.OfflineUsername;
        ReloadSavedAccounts();
    }

    public LauncherSession Session { get; }

    [ObservableProperty]
    public partial string OfflineUsername { get; set; }

    [ObservableProperty]
    public partial SavedMicrosoftAccount? SelectedSavedAccount { get; set; }

    [ObservableProperty]
    public partial bool HasSavedAccounts { get; set; }

    public void ReloadSavedAccounts()
    {
        var accounts = _authentication.GetSavedAccounts();
        Session.SavedAccounts.Clear();
        foreach (var account in accounts)
            Session.SavedAccounts.Add(account);

        HasSavedAccounts = accounts.Count > 0;
        SelectedSavedAccount = accounts.FirstOrDefault();
    }

    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        if (Session.IsBusy)
            return;

        Session.BeginBusy("正在打开 Microsoft 登录窗口...");

        try
        {
            var session = await _authentication.LoginMicrosoftAsync();
            Session.SetAccount(session, AccountKind.Microsoft);
            ReloadSavedAccounts();
            _settingsService.SaveSilently(Session.Settings);
            Session.SetStatus($"已登录 Microsoft 账户 {session.Username}");
        }
        catch (Exception ex)
        {
            Session.SetError($"Microsoft 登录失败：{ex.Message}");
        }
        finally
        {
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private Task LoginOfflineAsync()
    {
        if (Session.IsBusy)
            return Task.CompletedTask;

        var username = OfflineUsername?.Trim() ?? string.Empty;
        if (username.Length is < 3 or > 16)
        {
            Session.SetError("离线用户名长度必须为 3 到 16 个字符。");
            return Task.CompletedTask;
        }

        try
        {
            var session = AuthenticationService.CreateOfflineSession(username);
            Session.SetAccount(session, AccountKind.Offline);
            Session.Settings.OfflineUsername = username;
            _settingsService.SaveSilently(Session.Settings);
            Session.SetStatus($"已使用离线账户 {username}");
        }
        catch (Exception ex)
        {
            Session.SetError($"离线登录失败：{ex.Message}");
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task UseSavedAccountAsync(SavedMicrosoftAccount? account)
    {
        if (account is null || Session.IsBusy)
            return;

        Session.BeginBusy($"正在验证 {account.Username} 的登录状态...");

        try
        {
            var session = await _authentication.LoginSavedMicrosoftAccountAsync(account.Identifier);
            Session.SetAccount(session, AccountKind.Microsoft);
            ReloadSavedAccounts();
            _settingsService.SaveSilently(Session.Settings);
            Session.SetStatus($"已登录 Microsoft 账户 {session.Username}");
        }
        catch (Exception ex)
        {
            Session.SetError($"自动登录失败，请重新使用 Microsoft 登录：{ex.Message}");
        }
        finally
        {
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private async Task SignoutAsync()
    {
        if (Session.IsBusy)
            return;

        Session.BeginBusy("正在退出登录...");

        try
        {
            if (Session.Account?.Kind == AccountKind.Microsoft)
                await _authentication.SignoutAsync();

            Session.ClearAccount();
            Session.Settings.LastAccountKind = string.Empty;
            _settingsService.SaveSilently(Session.Settings);
            ReloadSavedAccounts();
            Session.SetStatus("已退出登录");
        }
        catch (Exception ex)
        {
            Session.SetError($"退出登录失败：{ex.Message}");
        }
        finally
        {
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private void RefreshSavedAccounts()
    {
        ReloadSavedAccounts();
        Session.SetStatus($"已加载 {Session.SavedAccounts.Count} 个已保存的 Microsoft 账户");
    }
}

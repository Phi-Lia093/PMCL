using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Multi-account management: device-code Microsoft login, offline accounts,
/// switching between saved accounts and removing stale accounts.
/// </summary>
public sealed partial class AccountViewModel : ViewModelBase
{
    private readonly AuthenticationService _authentication;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _microsoftLoginCancellation;

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
    public partial SavedAccount? SelectedAccount { get; set; }

    [ObservableProperty]
    public partial bool HasSavedAccounts { get; set; }

    [ObservableProperty]
    public partial bool IsMicrosoftLoginPending { get; set; }

    [ObservableProperty]
    public partial string MicrosoftLoginUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MicrosoftLoginCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MicrosoftLoginHint { get; set; } = string.Empty;

    public bool HasSelectedAccount => SelectedAccount is not null;

    public bool HasMicrosoftLoginUrl => !string.IsNullOrWhiteSpace(MicrosoftLoginUrl);

    public bool HasMicrosoftLoginHint => !string.IsNullOrWhiteSpace(MicrosoftLoginHint);

    public string SavedAccountsSummary => Session.SavedAccounts.Count switch
    {
        0 => "暂无已保存账户",
        1 => "1 个已保存账户",
        var count => $"{count} 个已保存账户"
    };

    public void ReloadSavedAccounts(string? selectAccountId = null)
    {
        var accounts = _authentication.GetSavedAccounts();
        Session.SavedAccounts.Clear();

        foreach (var account in accounts)
        {
            account.IsActive = string.Equals(
                account.Id,
                Session.ActiveAccountId,
                StringComparison.Ordinal);
            Session.SavedAccounts.Add(account);
        }

        HasSavedAccounts = accounts.Count > 0;
        SelectedAccount =
            accounts.FirstOrDefault(account =>
                !string.IsNullOrWhiteSpace(selectAccountId)
                && string.Equals(account.Id, selectAccountId, StringComparison.Ordinal))
            ?? accounts.FirstOrDefault(account => account.IsActive)
            ?? accounts.FirstOrDefault();

        OnPropertyChanged(nameof(SavedAccountsSummary));
    }

    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        if (Session.IsBusy || IsMicrosoftLoginPending)
            return;

        _microsoftLoginCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _microsoftLoginCancellation = cancellation;

        try
        {
            Session.BeginBusy("正在获取 Microsoft 登录代码...");
            var challenge = await _authentication.BeginMicrosoftDeviceLoginAsync(
                cancellation.Token);

            MicrosoftLoginCode = challenge.UserCode;
            MicrosoftLoginUrl = challenge.VerificationUriComplete;
            MicrosoftLoginHint =
                $"已打开系统默认浏览器，网页会尝试自动填入代码 {challenge.UserCode}。"
                + "若页面没有填入，请手动输入该代码；授权成功后启动器会自动完成登录。";
            IsMicrosoftLoginPending = true;
            Session.SetStatus("等待 Microsoft 授权...");

            if (!SystemBrowserService.TryOpen(
                    challenge.VerificationUriComplete,
                    out var browserError))
            {
                MicrosoftLoginHint =
                    $"无法自动打开浏览器（{browserError}）。请手动打开 {challenge.VerificationUri} "
                    + $"并输入代码 {challenge.UserCode}。";
            }

            var result = await _authentication.CompleteMicrosoftDeviceLoginAsync(
                challenge,
                cancellation.Token);
            ApplyLoginResult(result, $"已登录 Microsoft 账户 {result.Account.DisplayName}");
            CloseMicrosoftLoginPanel();
        }
        catch (OperationCanceledException)
        {
            Session.SetStatus("已取消 Microsoft 登录");
        }
        catch (Exception ex)
        {
            var message = $"Microsoft 登录失败：{ex.Message}";
            Session.SetError(message);
            MicrosoftLoginHint = message;
            IsMicrosoftLoginPending = false;
        }
        finally
        {
            if (ReferenceEquals(_microsoftLoginCancellation, cancellation))
                _microsoftLoginCancellation = null;

            cancellation.Dispose();
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private void OpenMicrosoftLoginUrl()
    {
        if (string.IsNullOrWhiteSpace(MicrosoftLoginUrl))
            return;

        if (!SystemBrowserService.TryOpen(MicrosoftLoginUrl, out var error))
            MicrosoftLoginHint = $"无法打开浏览器（{error}）。请复制链接后手动打开。";
        else
            MicrosoftLoginHint = "已重新打开系统浏览器。授权成功后启动器会自动完成登录。";
    }

    [RelayCommand]
    private void CancelMicrosoftLogin()
    {
        _microsoftLoginCancellation?.Cancel();
        _authentication.CancelMicrosoftLogin();
        CloseMicrosoftLoginPanel();
        Session.SetStatus("已取消 Microsoft 登录");
    }

    private void CloseMicrosoftLoginPanel()
    {
        IsMicrosoftLoginPending = false;
        MicrosoftLoginUrl = string.Empty;
        MicrosoftLoginCode = string.Empty;
        MicrosoftLoginHint = string.Empty;
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
            var result = _authentication.AddOrUpdateOfflineAccount(username);
            ApplyLoginResult(result, $"已使用离线账户 {result.Account.DisplayName}");
        }
        catch (Exception ex)
        {
            Session.SetError($"离线登录失败：{ex.Message}");
        }

        return Task.CompletedTask;
    }
    [RelayCommand]
    private async Task UseSavedAccountAsync(SavedAccount? account)
    {
        account ??= SelectedAccount;
        if (account is null || Session.IsBusy)
            return;

        if (string.Equals(account.Id, Session.ActiveAccountId, StringComparison.Ordinal))
        {
            Session.SetStatus($"{account.DisplayName} 已是当前账户");
            return;
        }

        Session.BeginBusy($"正在切换到 {account.DisplayName}...");
        try
        {
            var result = await _authentication.LoginSavedAccountAsync(account.Id);
            ApplyLoginResult(result, $"已切换到 {result.Account.DisplayName}");
        }
        catch (Exception ex)
        {
            Session.SetError($"切换账户失败：{ex.Message}");
        }
        finally
        {
            Session.EndBusy();
        }
    }

    [RelayCommand]
    private void RemoveSavedAccount(SavedAccount? account)
    {
        account ??= SelectedAccount;
        if (account is null || Session.IsBusy)
            return;

        var wasActive = string.Equals(account.Id, Session.ActiveAccountId, StringComparison.Ordinal);
        if (!_authentication.RemoveAccount(account.Id))
        {
            Session.SetError("删除账户失败，账户可能已不存在。");
            ReloadSavedAccounts();
            return;
        }

        if (wasActive)
            Session.ClearAccount();

        _settingsService.SaveSilently(Session.Settings);
        ReloadSavedAccounts();

        var message = $"已删除账户 {account.DisplayName}";
        if (wasActive)
            message += "，当前已退出该账户";
        Session.SetStatus(message);
    }

    [RelayCommand]
    private void Signout()
    {
        if (Session.IsBusy)
            return;

        var name = Session.AccountName;
        if (IsMicrosoftLoginPending)
            CancelMicrosoftLogin();

        Session.ClearAccount();
        _settingsService.SaveSilently(Session.Settings);
        ReloadSavedAccounts();
        Session.SetStatus($"已退出当前账户 {name}，保存的账户仍可随时切换");
    }

    [RelayCommand]
    private void RefreshSavedAccounts()
    {
        ReloadSavedAccounts(Session.ActiveAccountId);
        Session.SetStatus($"已加载 {Session.SavedAccounts.Count} 个账户");
    }

    /// <summary>
    /// Restores the last active account after startup. Cached Minecraft tokens
    /// are reused when possible; expired Microsoft tokens are refreshed silently.
    /// </summary>
    public async Task RestoreLastAccountAsync()
    {
        if (Session.IsBusy)
            return;

        ReloadSavedAccounts();

        var account = !string.IsNullOrWhiteSpace(Session.Settings.LastAccountId)
            ? _authentication.GetSavedAccount(Session.Settings.LastAccountId)
            : null;

        // Migrated CmlLib accounts have no LastAccountId yet; restore the most
        // recently used Microsoft account when the old setting asked for one.
        if (account is null
            && Session.Settings.LastAccountKind == nameof(AccountKind.Microsoft))
        {
            account = Session.SavedAccounts.FirstOrDefault(saved => saved.IsMicrosoft);
        }

        if (account is null
            && Session.Settings.LastAccountKind == nameof(AccountKind.Offline)
            && !string.IsNullOrWhiteSpace(Session.Settings.OfflineUsername))
        {
            try
            {
                account = _authentication
                    .AddOrUpdateOfflineAccount(Session.Settings.OfflineUsername)
                    .Account;
            }
            catch
            {
                // Ignore invalid legacy offline names.
            }
        }

        if (account is null)
        {
            if (Session.Settings.LastAccountKind == nameof(AccountKind.Microsoft))
                Session.SetStatus("上次使用的 Microsoft 账户已不存在，请重新登录或选择其他账户。");
            return;
        }

        Session.BeginBusy($"正在恢复账户 {account.DisplayName}...");
        try
        {
            var result = await _authentication.LoginSavedAccountAsync(account.Id);
            ApplyLoginResult(result, $"已恢复账户 {result.Account.DisplayName}");
        }
        catch (Exception ex)
        {
            Session.SetStatus($"自动恢复账户失败：{ex.Message}");
        }
        finally
        {
            Session.EndBusy();
        }
    }

    private void ApplyLoginResult(AccountLoginResult result, string successMessage)
    {
        Session.SetAccount(
            result.Session,
            result.Account.Kind,
            result.Account.Id,
            result.Account.Gamertag);
        Session.Settings.OfflineUsername =
            result.Account.Kind == AccountKind.Offline
                ? result.Account.Username
                : Session.Settings.OfflineUsername;
        OfflineUsername = Session.Settings.OfflineUsername;
        _settingsService.SaveSilently(Session.Settings);
        ReloadSavedAccounts(result.Account.Id);
        Session.SetStatus(successMessage);
    }

    partial void OnSelectedAccountChanged(SavedAccount? value)
        => OnPropertyChanged(nameof(HasSelectedAccount));

    partial void OnMicrosoftLoginUrlChanged(string value)
        => OnPropertyChanged(nameof(HasMicrosoftLoginUrl));

    partial void OnMicrosoftLoginHintChanged(string value)
        => OnPropertyChanged(nameof(HasMicrosoftLoginHint));

    partial void OnIsMicrosoftLoginPendingChanged(bool value)
    {
        if (!value)
            _authentication.CancelMicrosoftLogin();
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
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
        0 => L.Tr("Account.SavedCount.None"),
        1 => L.Tr("Account.SavedCount.One"),
        var count => L.Format("Account.SavedCount.Many", count)
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
            Session.BeginBusy(L.Tr("Account.GettingCode"));
            var challenge = await _authentication.BeginMicrosoftDeviceLoginAsync(
                cancellation.Token);

            MicrosoftLoginCode = challenge.UserCode;
            MicrosoftLoginUrl = challenge.VerificationUriComplete;
            MicrosoftLoginHint =
                L.Format("Account.BrowserOpened", challenge.UserCode);
            IsMicrosoftLoginPending = true;
            Session.SetStatus(L.Tr("Account.WaitingAuth"));

            if (!SystemBrowserService.TryOpen(
                    challenge.VerificationUriComplete,
                    out var browserError))
            {
                MicrosoftLoginHint = L.Format(
                    "Account.BrowserOpenFailed",
                    browserError,
                    challenge.VerificationUri,
                    challenge.UserCode);
            }

            var result = await _authentication.CompleteMicrosoftDeviceLoginAsync(
                challenge,
                cancellation.Token);
            ApplyLoginResult(result, L.Format("Account.LoggedIn", result.Account.DisplayName));
            CloseMicrosoftLoginPanel();
        }
        catch (OperationCanceledException)
        {
            Session.SetStatus(L.Tr("Account.LoginCancelled"));
        }
        catch (Exception ex)
        {
            var message = L.Format("Account.LoginFailed", ex.Message);
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
            MicrosoftLoginHint = L.Format("Account.BrowserOpenFailedHint", error);
        else
            MicrosoftLoginHint = L.Tr("Account.BrowserReopened");
    }

    [RelayCommand]
    private void CancelMicrosoftLogin()
    {
        _microsoftLoginCancellation?.Cancel();
        _authentication.CancelMicrosoftLogin();
        CloseMicrosoftLoginPanel();
        Session.SetStatus(L.Tr("Account.LoginCancelled"));
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
            Session.SetError(L.Tr("Account.OfflineNameInvalid"));
            return Task.CompletedTask;
        }

        try
        {
            var result = _authentication.AddOrUpdateOfflineAccount(username);
            ApplyLoginResult(result, L.Format("Account.OfflineLoginSuccess", result.Account.DisplayName));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("Account.OfflineLoginFailed", ex.Message));
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
            Session.SetStatus(L.Format("Account.AlreadyCurrent", account.DisplayName));
            return;
        }

        Session.BeginBusy(L.Format("Account.Switching", account.DisplayName));
        try
        {
            var result = await _authentication.LoginSavedAccountAsync(account.Id);
            ApplyLoginResult(result, L.Format("Account.Switched", result.Account.DisplayName));
        }
        catch (Exception ex)
        {
            Session.SetError(L.Format("Account.SwitchFailed", ex.Message));
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
            Session.SetError(L.Tr("Account.DeleteFailed"));
            ReloadSavedAccounts();
            return;
        }

        if (wasActive)
            Session.ClearAccount();

        _settingsService.SaveSilently(Session.Settings);
        ReloadSavedAccounts();

        var message = L.Format("Account.Deleted", account.DisplayName);
        if (wasActive)
            message += L.Tr("Account.DeletedSignedOut");
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
        Session.SetStatus(L.Format("Account.SignedOut", name));
    }

    [RelayCommand]
    private void RefreshSavedAccounts()
    {
        ReloadSavedAccounts(Session.ActiveAccountId);
        Session.SetStatus(L.Format("Account.Loaded", Session.SavedAccounts.Count));
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
                Session.SetStatus(L.Tr("Account.LastMicrosoftMissing"));
            return;
        }

        Session.BeginBusy(L.Format("Account.Restoring", account.DisplayName));
        try
        {
            var result = await _authentication.LoginSavedAccountAsync(account.Id);
            ApplyLoginResult(result, L.Format("Account.Restored", result.Account.DisplayName));
        }
        catch (Exception ex)
        {
            Session.SetStatus(L.Format("Account.RestoreFailed", ex.Message));
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

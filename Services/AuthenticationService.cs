using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using PMCL.Models;
using XboxAuthNet.Game.Accounts;

namespace PMCL.Services;

/// <summary>
/// Wraps CmlLib's Microsoft (Xbox Live) and offline authentication.
/// </summary>
public sealed class AuthenticationService
{
    private readonly JELoginHandler _loginHandler;

    public AuthenticationService(AppPaths paths)
    {
        _loginHandler = new JELoginHandlerBuilder()
            .WithAccountManager(paths.AccountsFile)
            .Build();
    }

    /// <summary>
    /// Opens the Microsoft sign-in window (WebView2). Must not run on the Avalonia UI thread.
    /// </summary>
    public Task<MSession> LoginMicrosoftAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => _loginHandler.AuthenticateInteractively(cancellationToken), cancellationToken);

    /// <summary>
    /// Refreshes the default saved Microsoft account without showing a window.
    /// </summary>
    public Task<MSession> LoginMicrosoftSilentlyAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => _loginHandler.AuthenticateSilently(cancellationToken), cancellationToken);

    /// <summary>
    /// Refreshes a specific saved Microsoft account without showing a window.
    /// </summary>
    public Task<MSession> LoginSavedMicrosoftAccountAsync(
        string identifier,
        CancellationToken cancellationToken = default)
    {
        var account = FindAccount(identifier)
            ?? throw new InvalidOperationException("找不到该 Microsoft 账户，请重新登录。");

        return Task.Run(() => _loginHandler.AuthenticateSilently(account, cancellationToken), cancellationToken);
    }

    public static MSession CreateOfflineSession(string username)
    {
        var session = MSession.CreateOfflineSession(username);
        session.UserType = "legacy";
        return session;
    }

    public IReadOnlyList<SavedMicrosoftAccount> GetSavedAccounts()
    {
        try
        {
            return _loginHandler.AccountManager
                .GetAccounts()
                .OfType<JEGameAccount>()
                .Where(account => account.Profile is not null)
                .Select(account => new SavedMicrosoftAccount(
                    account.Identifier ?? string.Empty,
                    account.Profile!.Username ?? string.Empty,
                    account.Profile.UUID ?? string.Empty,
                    account.LastAccess))
                .OrderByDescending(account => account.LastAccess)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<SavedMicrosoftAccount>();
        }
    }

    public bool HasSavedAccounts()
    {
        try
        {
            return _loginHandler.AccountManager.GetAccounts().Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task SignoutAsync(CancellationToken cancellationToken = default)
    {
        if (!HasSavedAccounts())
            return;

        await Task.Run(() => _loginHandler.Signout(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private IXboxGameAccount? FindAccount(string identifier)
    {
        var accounts = _loginHandler.AccountManager.GetAccounts();
        return accounts.TryGetAccount(identifier, out var account) ? account : null;
    }
}

using PMCL.Localization;

namespace PMCL.Models;

/// <summary>
/// Lightweight account information used by the UI.
/// The authenticated <see cref="CmlLib.Core.Auth.MSession"/> is kept by the launcher session.
/// </summary>
public sealed class AccountProfile
{
    public AccountProfile(
        string username,
        string uuid,
        AccountKind kind,
        string? accountId = null,
        string? gamertag = null)
    {
        Username = username;
        Uuid = uuid;
        Kind = kind;
        AccountId = accountId;
        Gamertag = gamertag;
    }

    public string Username { get; }

    public string Uuid { get; }

    public AccountKind Kind { get; }

    public string? AccountId { get; }

    public string? Gamertag { get; }

    public string KindLabel => Kind == AccountKind.Microsoft
        ? L.Tr("Account.KindMicrosoft")
        : L.Tr("Account.KindOffline");

    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();

    public string Detail => Kind == AccountKind.Microsoft && !string.IsNullOrWhiteSpace(Gamertag)
        ? L.Format("Account.XboxDetail", Gamertag)
        : string.IsNullOrWhiteSpace(Uuid)
            ? KindLabel
            : L.Format("Account.UuidDetail", Uuid);
}

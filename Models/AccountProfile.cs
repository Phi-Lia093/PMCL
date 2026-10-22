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

    public string KindLabel => Kind == AccountKind.Microsoft ? "Microsoft 账户" : "离线账户";

    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();

    public string Detail => Kind == AccountKind.Microsoft && !string.IsNullOrWhiteSpace(Gamertag)
        ? $"Xbox：{Gamertag}"
        : string.IsNullOrWhiteSpace(Uuid)
            ? KindLabel
            : $"UUID：{Uuid}";
}

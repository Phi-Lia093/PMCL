namespace PMCL.Models;

/// <summary>
/// A Microsoft account cached by CmlLib.Core.Auth.Microsoft.
/// </summary>
public sealed class SavedMicrosoftAccount
{
    public SavedMicrosoftAccount(string identifier, string username, string uuid, DateTime lastAccess)
    {
        Identifier = identifier;
        Username = username;
        Uuid = uuid;
        LastAccess = lastAccess;
    }

    public string Identifier { get; }

    public string Username { get; }

    public string Uuid { get; }

    public DateTime LastAccess { get; }

    public string LastAccessText => LastAccess == default
        ? "未知"
        : LastAccess.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();
}

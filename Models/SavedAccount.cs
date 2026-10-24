using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using PMCL.Localization;

namespace PMCL.Models;

/// <summary>
/// An account persisted by PMCL. Microsoft accounts keep the refresh token and
/// a short-lived Minecraft access token so switching accounts can be instant.
/// </summary>
public sealed partial class SavedAccount : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AccountKind Kind { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Uuid { get; set; } = string.Empty;

    public string Gamertag { get; set; } = string.Empty;

    public string Xuid { get; set; } = string.Empty;

    /// <summary>
    /// Microsoft OAuth refresh token. Empty for offline accounts.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Cached Minecraft Services access token. Empty when the account has never
    /// completed a login or the token was invalidated.
    /// </summary>
    public string MinecraftAccessToken { get; set; } = string.Empty;

    public DateTimeOffset? MinecraftTokenExpiresAt { get; set; }

    public DateTimeOffset LastAccess { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// UI-only marker; not persisted.
    /// </summary>
    [JsonIgnore]
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [JsonIgnore]
    public bool IsMicrosoft => Kind == AccountKind.Microsoft;

    [JsonIgnore]
    public bool HasGamertag => !string.IsNullOrWhiteSpace(Gamertag);

    [JsonIgnore]
    public bool HasValidMinecraftToken =>
        !string.IsNullOrWhiteSpace(MinecraftAccessToken)
        && MinecraftTokenExpiresAt is { } expiresAt
        && expiresAt > DateTimeOffset.UtcNow.AddMinutes(2);

    [JsonIgnore]
    public bool CanRefreshMicrosoft =>
        Kind == AccountKind.Microsoft && !string.IsNullOrWhiteSpace(RefreshToken);

    [JsonIgnore]
    public string KindLabel => Kind == AccountKind.Microsoft
        ? L.Tr("Account.KindMicrosoft")
        : L.Tr("Account.KindOffline");

    [JsonIgnore]
    public string KindBadge => Kind == AccountKind.Microsoft ? "MS" : "OFF";

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Username)
        ? "?"
        : Username[..1].ToUpperInvariant();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Username)
        ? KindLabel
        : Username;

    [JsonIgnore]
    public string LastAccessText => LastAccess == default
        ? L.Tr("Account.NeverUsed")
        : LastAccess.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public string TokenStateText => Kind == AccountKind.Offline
        ? L.Tr("Account.Local")
        : HasValidMinecraftToken
            ? L.Tr("Account.TokenValid")
            : CanRefreshMicrosoft
                ? L.Tr("Account.TokenRefreshable")
                : L.Tr("Account.TokenNeedsLogin");

    public static string CreateOfflineUuid(string username)
    {
        // Vanilla-compatible offline UUID: MD5("OfflinePlayer:" + name),
        // with the RFC 4122 version/variant bits applied.
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

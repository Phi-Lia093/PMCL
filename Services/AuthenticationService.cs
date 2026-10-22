using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CmlLib.Core.Auth;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Microsoft (OAuth → Xbox Live → Minecraft Services) and offline account
/// management. Microsoft sign-in uses the system browser, so this service no
/// longer depends on WebView2 or on CmlLib's Windows-only login handler.
/// </summary>
public sealed class AuthenticationService
{
    private const string XboxUserAuthEndpoint = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsEndpoint = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginEndpoint =
        "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileEndpoint =
        "https://api.minecraftservices.com/minecraft/profile";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AccountStore _accountStore;
    private readonly MicrosoftOAuthClient _oauth;
    private readonly HttpClient _httpClient;
    private PendingMicrosoftDeviceLogin? _pendingDeviceLogin;

    public AuthenticationService(AppPaths paths, HttpClient? httpClient = null)
    {
        _accountStore = new AccountStore(paths);
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        _oauth = new MicrosoftOAuthClient(_httpClient);
    }

    public IReadOnlyList<SavedAccount> GetSavedAccounts() => _accountStore.GetAccounts();

    public SavedAccount? GetSavedAccount(string id) => _accountStore.Find(id);

    public void CancelMicrosoftLogin() => _pendingDeviceLogin = null;

    /// <summary>
    /// Starts the device-code flow used by the account page. The user only has
    /// to approve the sign-in in the system browser; the launcher polls
    /// Microsoft and completes the Xbox/Minecraft exchange automatically.
    /// </summary>
    public async Task<MicrosoftDeviceLoginChallenge> BeginMicrosoftDeviceLoginAsync(
        CancellationToken cancellationToken = default)
    {
        var challenge = await _oauth.RequestDeviceCodeAsync(cancellationToken)
            .ConfigureAwait(false);

        _pendingDeviceLogin = new PendingMicrosoftDeviceLogin(
            challenge.DeviceCode,
            challenge.ExpiresAt);
        return challenge;
    }

    public async Task<AccountLoginResult> CompleteMicrosoftDeviceLoginAsync(
        MicrosoftDeviceLoginChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        var pending = _pendingDeviceLogin
            ?? throw new MicrosoftAuthException(
                "没有正在进行的 Microsoft 登录，请重新获取登录代码。");

        if (!string.Equals(pending.DeviceCode, challenge.DeviceCode, StringComparison.Ordinal))
            throw new MicrosoftAuthException("登录会话已失效，请重新获取登录代码。");

        if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _pendingDeviceLogin = null;
            throw new MicrosoftAuthException("Microsoft 登录代码已过期，请重新获取。");
        }

        try
        {
            var tokens = await _oauth.PollDeviceCodeAsync(challenge, cancellationToken)
                .ConfigureAwait(false);

            var account = new SavedAccount { Kind = AccountKind.Microsoft };
            return await AuthenticateMicrosoftAsync(tokens, account, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // A device code is single-use. A failed or cancelled attempt must
            // not leave a stale challenge that can be completed later.
            _pendingDeviceLogin = null;
        }
    }

    public async Task<AccountLoginResult> LoginSavedAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var account = _accountStore.Find(accountId)
            ?? throw new AccountLoginException("找不到该账户，可能已被删除。");

        if (account.Kind == AccountKind.Offline)
        {
            account.LastAccess = DateTimeOffset.UtcNow;
            var saved = _accountStore.Upsert(account);
            return new AccountLoginResult(
                saved,
                CreateOfflineSession(saved.Username, saved.Uuid));
        }

        if (account.HasValidMinecraftToken && !string.IsNullOrWhiteSpace(account.Uuid))
        {
            account.LastAccess = DateTimeOffset.UtcNow;
            var saved = _accountStore.Upsert(account);
            return new AccountLoginResult(saved, CreateSession(saved));
        }

        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new AccountLoginException(
                $"账户 {account.DisplayName} 的登录已失效，请重新使用 Microsoft 登录。");
        }

        MicrosoftOAuthTokens tokens;
        try
        {
            tokens = await _oauth.RefreshAsync(account.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MicrosoftAuthException ex)
        {
            throw new AccountLoginException(
                $"账户 {account.DisplayName} 的登录已失效，请重新登录：{ex.Message}", ex);
        }

        return await AuthenticateMicrosoftAsync(tokens, account, cancellationToken)
            .ConfigureAwait(false);
    }

    public AccountLoginResult AddOrUpdateOfflineAccount(string username)
    {
        username = username.Trim();
        if (username.Length is < 3 or > 16)
            throw new AccountLoginException("离线用户名长度必须为 3 到 16 个字符。");

        var existing = _accountStore.GetAccounts().FirstOrDefault(account =>
            account.Kind == AccountKind.Offline
            && string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase));

        var account = new SavedAccount
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Kind = AccountKind.Offline,
            Username = existing?.Username ?? username,
            Uuid = SavedAccount.CreateOfflineUuid(existing?.Username ?? username),
            LastAccess = DateTimeOffset.UtcNow
        };

        var saved = _accountStore.Upsert(account);
        return new AccountLoginResult(
            saved,
            CreateOfflineSession(saved.Username, saved.Uuid));
    }

    public bool RemoveAccount(string id) => _accountStore.Remove(id);

    public static MSession CreateOfflineSession(string username, string? uuid = null)
    {
        var session = MSession.CreateOfflineSession(username);
        session.UserType = "legacy";
        session.UUID = string.IsNullOrWhiteSpace(uuid)
            ? SavedAccount.CreateOfflineUuid(username)
            : uuid;
        return session;
    }

    public static MSession CreateSession(SavedAccount account)
    {
        if (account.Kind == AccountKind.Offline)
            return CreateOfflineSession(account.Username, account.Uuid);

        if (string.IsNullOrWhiteSpace(account.MinecraftAccessToken))
            throw new AccountLoginException("该 Microsoft 账户没有可用的 Minecraft 登录令牌，请重新登录。");

        return new MSession
        {
            Username = account.Username,
            UUID = account.Uuid,
            AccessToken = account.MinecraftAccessToken,
            UserType = "msa",
            Xuid = account.Xuid
        };
    }

    private sealed record PendingMicrosoftDeviceLogin(
        string DeviceCode,
        DateTimeOffset ExpiresAt);

    private sealed record MicrosoftLoginData(
        string Username,
        string Uuid,
        string Gamertag,
        string Xuid,
        string AccessToken,
        DateTimeOffset ExpiresAt);

    private async Task<AccountLoginResult> AuthenticateMicrosoftAsync(
        MicrosoftOAuthTokens tokens,
        SavedAccount account,
        CancellationToken cancellationToken)
    {
        var login = await AuthenticateMinecraftServicesAsync(tokens.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        account.Kind = AccountKind.Microsoft;
        account.Username = login.Username;
        account.Uuid = login.Uuid;
        account.Gamertag = login.Gamertag;
        account.Xuid = login.Xuid;
        account.RefreshToken = string.IsNullOrWhiteSpace(tokens.RefreshToken)
            ? account.RefreshToken
            : tokens.RefreshToken;
        account.MinecraftAccessToken = login.AccessToken;
        account.MinecraftTokenExpiresAt = login.ExpiresAt;
        account.LastAccess = DateTimeOffset.UtcNow;

        var saved = _accountStore.Upsert(account);
        return new AccountLoginResult(saved, CreateSession(saved));
    }

    private async Task<MicrosoftLoginData> AuthenticateMinecraftServicesAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        var xbox = await RequestXboxUserTokenAsync(microsoftAccessToken, cancellationToken)
            .ConfigureAwait(false);
        var xsts = await RequestXstsAsync(xbox.Token, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(xsts.Token) || string.IsNullOrWhiteSpace(xsts.Uhs))
            throw new MicrosoftAuthException("Xbox Live 没有返回有效的身份令牌，请稍后重试。");

        var minecraft = await RequestMinecraftTokenAsync(xsts.Uhs, xsts.Token, cancellationToken)
            .ConfigureAwait(false);
        var profile = await RequestMinecraftProfileAsync(minecraft.AccessToken, cancellationToken)
            .ConfigureAwait(false);

        return new MicrosoftLoginData(
            profile.Username,
            profile.Uuid,
            xsts.Gamertag,
            xsts.Xuid,
            minecraft.AccessToken,
            minecraft.ExpiresAt);
    }

    private async Task<XboxTokenResult> RequestXboxUserTokenAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        // Live Connect accepts the raw access token, while some Azure-issued
        // tokens use the "d=" RPS ticket prefix. Try both so account types and
        // Microsoft endpoint changes keep working.
        try
        {
            return await RequestXboxUserTokenCoreAsync(
                    microsoftAccessToken,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MicrosoftAuthException)
        {
            return await RequestXboxUserTokenCoreAsync(
                    "d=" + microsoftAccessToken,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<XboxTokenResult> RequestXboxUserTokenCoreAsync(
        string rpsTicket,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = rpsTicket
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };

        using var document = await PostJsonAsync(
                XboxUserAuthEndpoint,
                payload,
                "Xbox Live 用户验证",
                "0",
                cancellationToken)
            .ConfigureAwait(false);

        var token = GetString(document.RootElement, "Token");
        if (string.IsNullOrWhiteSpace(token))
            throw new MicrosoftAuthException("Xbox Live 没有返回用户令牌，请稍后重试。");

        return new XboxTokenResult(token, GetUhs(document.RootElement));
    }

    private async Task<XstsResult> RequestXstsAsync(string xboxToken, CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };

        using var document = await PostJsonAsync(
                XstsEndpoint,
                payload,
                "Xbox Live 安全令牌验证",
                "1",
                cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        var token = GetString(root, "Token");
        if (string.IsNullOrWhiteSpace(token))
            throw new MicrosoftAuthException("Xbox Live 没有返回安全令牌，请稍后重试。");

        var uhs = GetUhs(root);
        var xuid = GetXuiValue(root, "xid") ?? string.Empty;
        var gamertag = GetXuiValue(root, "gtg")
                       ?? GetXuiValue(root, "mgt")
                       ?? GetXuiValue(root, "umg")
                       ?? string.Empty;
        return new XstsResult(token, uhs, xuid, gamertag);
    }

    private async Task<MinecraftTokenResult> RequestMinecraftTokenAsync(
        string uhs,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            identityToken = $"XBL3.0 x={uhs};{xstsToken}"
        };

        using var document = await PostJsonAsync(
                MinecraftLoginEndpoint,
                payload,
                "Minecraft 登录",
                null,
                cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        var accessToken = GetString(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new MicrosoftAuthException("Minecraft 服务没有返回登录令牌，请稍后重试。");

        var expiresIn = GetInt32(root, "expires_in") ?? 3600;
        return new MinecraftTokenResult(
            accessToken,
            GetString(root, "username") ?? string.Empty,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)));
    }

    private async Task<MinecraftProfileResult> RequestMinecraftProfileAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "PMCL/0.1.0");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MicrosoftAuthException($"获取 Minecraft 档案失败，请检查网络：{ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new MicrosoftAuthException(
                    "该 Microsoft 账户没有 Minecraft: Java Edition 档案（可能未购买游戏或尚未创建角色）。");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MicrosoftAuthException(
                    $"获取 Minecraft 档案失败（HTTP {(int)response.StatusCode}），请确认账户拥有 Java 版游戏。");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var uuid = GetString(root, "id");
            var username = GetString(root, "name");
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(username))
                throw new MicrosoftAuthException("Minecraft 服务返回的账户档案不完整，请稍后重试。");

            return new MinecraftProfileResult(username, uuid);
        }
    }

    private async Task<JsonDocument> PostJsonAsync(
        string endpoint,
        object payload,
        string operation,
        string? contractVersion,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "PMCL/0.1.0");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US");
        if (!string.IsNullOrEmpty(contractVersion))
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", contractVersion);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MicrosoftAuthException($"{operation}失败，请检查网络：{ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var xboxError = TryGetXboxError(body);
                if (xboxError is not null)
                    throw new MicrosoftAuthException(MapXboxError(xboxError));

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    throw new MicrosoftAuthException("Microsoft / Xbox 服务请求过于频繁，请稍后重试。");

                throw new MicrosoftAuthException(
                    $"{operation}失败（HTTP {(int)response.StatusCode}）：{Truncate(body)}");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new MicrosoftAuthException($"{operation}返回了无法识别的数据，请稍后重试。");
            }
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;

        return property.ValueKind == JsonValueKind.String
               && int.TryParse(property.GetString(), out value)
            ? value
            : null;
    }

    private static string GetUhs(JsonElement root)
    {
        var uhs = GetXuiValue(root, "uhs");
        if (string.IsNullOrWhiteSpace(uhs))
            throw new MicrosoftAuthException("Xbox Live 没有返回用户哈希（UHS），请稍后重试。");
        return uhs;
    }

    private static string? GetXuiValue(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty("DisplayClaims", out var displayClaims)
            || !displayClaims.TryGetProperty("xui", out var xui)
            || xui.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var claim in xui.EnumerateArray())
        {
            var value = GetString(claim, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? TryGetXboxError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("XErr", out var error))
                return null;

            return error.ValueKind == JsonValueKind.Number
                ? error.GetRawText()
                : error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private static string MapXboxError(string errorCode)
        => errorCode switch
        {
            "2148916233" =>
                "该 Microsoft 账户尚未创建 Xbox 档案。请先在 xbox.com 登录并创建档案后重试。",
            "2148916235" =>
                "当前国家或地区不支持 Xbox Live，无法完成 Minecraft 登录。",
            "2148916236" or "2148916237" =>
                "该 Microsoft 账户需要完成成人验证后才能使用 Xbox Live。",
            "2148916238" =>
                "该账户是儿童账户，需要由 Microsoft 家庭组织者授权后才能登录。",
            "2148916227" =>
                "该 Xbox 账户已被封禁，无法登录 Minecraft。",
            _ => $"Xbox Live 验证失败（错误码 {errorCode}），请检查账户状态后重试。"
        };

    private static string Truncate(string value)
    {
        value = value.Trim();
        return value.Length <= 300 ? value : value[..300] + "...";
    }
}

public sealed record AccountLoginResult(SavedAccount Account, MSession Session);

public sealed class AccountLoginException : Exception
{
    public AccountLoginException(string message) : base(message)
    {
    }

    public AccountLoginException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal sealed record XboxTokenResult(string Token, string Uhs);

internal sealed record XstsResult(string Token, string Uhs, string Xuid, string Gamertag);

internal sealed record MinecraftTokenResult(string AccessToken, string Username, DateTimeOffset ExpiresAt);

internal sealed record MinecraftProfileResult(string Username, string Uuid);

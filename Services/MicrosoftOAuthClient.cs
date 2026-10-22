using System.Text.Json;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Small OAuth 2.0 client for the Microsoft account (Live Connect) endpoints.
/// The primary flow is device code: the user approves in the system browser and
/// the launcher polls for the token, so no callback URL or token is copied and
/// no WebView2 runtime is required.
/// </summary>
public sealed class MicrosoftOAuthClient
{
    public const string ClientId = "00000000402b5328";
    public const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    private const string DeviceCodeEndpoint = "https://login.live.com/oauth20_connect.srf";
    private const string TokenEndpoint = "https://login.live.com/oauth20_token.srf";
    private const string DefaultVerificationUri = "https://www.microsoft.com/link";
    private const string DeviceCodeGrantType =
        "urn:ietf:params:oauth:grant-type:device_code";
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    private readonly HttpClient _httpClient;

    public MicrosoftOAuthClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public Task<MicrosoftOAuthTokens> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new MicrosoftAuthException("该 Microsoft 账户没有可用的刷新令牌，请重新登录。");

        return RequestTokenAsync(
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope
            },
            cancellationToken);
    }

    /// <summary>
    /// Starts Microsoft's device-code flow with the official Minecraft client
    /// id. The returned short code is entered on Microsoft's page in the
    /// system browser; the launcher then polls the token endpoint and receives
    /// the token automatically, so no callback URL or token is copied.
    /// </summary>
    public async Task<MicrosoftDeviceLoginChallenge> RequestDeviceCodeAsync(
        CancellationToken cancellationToken = default)
    {
        var (response, body) = await PostFormAsync(
                DeviceCodeEndpoint,
                new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["scope"] = Scope,
                    ["response_type"] = "device_code"
                },
                cancellationToken)
            .ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    throw new MicrosoftAuthException("Microsoft 登录请求过于频繁，请稍后重试。");

                var error = TryReadError(body);
                throw new MicrosoftAuthException(MapOAuthError(error.Error, error.Description));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new MicrosoftAuthException(
                    "Microsoft 登录服务返回了无法识别的设备码数据，请稍后重试。");
            }

            using (document)
            {
                var root = document.RootElement;
                var deviceCode = GetString(root, "device_code");
                var userCode = GetString(root, "user_code");
                if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode))
                {
                    var error = TryReadError(body);
                    throw new MicrosoftAuthException(MapOAuthError(error.Error, error.Description));
                }

                var verificationUri = GetString(root, "verification_uri");
                if (string.IsNullOrWhiteSpace(verificationUri)
                    || !Uri.TryCreate(verificationUri, UriKind.Absolute, out _))
                {
                    verificationUri = DefaultVerificationUri;
                }

                var expiresIn = GetInt32(root, "expires_in") ?? 900;
                var interval = GetInt32(root, "interval") ?? 5;

                return new MicrosoftDeviceLoginChallenge(
                    deviceCode,
                    userCode,
                    verificationUri,
                    BuildVerificationUri(verificationUri, userCode),
                    DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)),
                    TimeSpan.FromSeconds(Math.Max(1, interval)));
            }
        }
    }

    /// <summary>
    /// Polls the token endpoint until the user finishes signing in. Microsoft
    /// returns <c>authorization_pending</c> (or <c>slow_down</c>) while the
    /// browser flow is still in progress; both are expected and retried.
    /// </summary>
    public async Task<MicrosoftOAuthTokens> PollDeviceCodeAsync(
        MicrosoftDeviceLoginChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        var interval = challenge.PollInterval < TimeSpan.FromSeconds(1)
            ? TimeSpan.FromSeconds(1)
            : challenge.PollInterval;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (challenge.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new MicrosoftAuthException("Microsoft 登录代码已过期，请重新获取。");

            var (response, body) = await PostFormAsync(
                    TokenEndpoint,
                    new Dictionary<string, string>
                    {
                        ["client_id"] = ClientId,
                        ["grant_type"] = DeviceCodeGrantType,
                        ["device_code"] = challenge.DeviceCode,
                        ["scope"] = Scope
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            string error;
            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var tokens = TryReadTokens(body);
                    if (tokens is not null)
                        return tokens;

                    var parsedError = TryReadError(body);
                    error = string.IsNullOrWhiteSpace(parsedError.Error)
                        ? "unknown_error"
                        : parsedError.Error;

                    if (!IsDeviceCodePending(error))
                        throw new MicrosoftAuthException(
                            MapOAuthError(error, parsedError.Description));
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Treat rate limiting like an explicit slow-down request so
                    // a temporary 429 does not discard the pending sign-in.
                    error = "slow_down";
                }
                else
                {
                    var parsedError = TryReadError(body);
                    error = string.IsNullOrWhiteSpace(parsedError.Error)
                        ? "unknown_error"
                        : parsedError.Error;

                    if (!IsDeviceCodePending(error))
                        throw new MicrosoftAuthException(
                            MapOAuthError(error, parsedError.Description));
                }
            }

            if (string.Equals(error, "slow_down", StringComparison.Ordinal))
                interval += TimeSpan.FromSeconds(5);

            var remaining = challenge.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new MicrosoftAuthException("Microsoft 登录代码已过期，请重新获取。");

            var delay = interval < remaining ? interval : remaining;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MicrosoftOAuthTokens> RequestTokenAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var (response, body) = await PostFormAsync(TokenEndpoint, form, cancellationToken)
            .ConfigureAwait(false);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    throw new MicrosoftAuthException("Microsoft 登录请求过于频繁，请稍后重试。");

                var error = TryReadError(body);
                throw new MicrosoftAuthException(MapOAuthError(error.Error, error.Description));
            }

            var tokens = TryReadTokens(body);
            if (tokens is not null)
                return tokens;

            var parsedError = TryReadError(body);
            throw new MicrosoftAuthException(
                MapOAuthError(parsedError.Error, parsedError.Description));
        }
    }

    private async Task<(HttpResponseMessage Response, string Body)> PostFormAsync(
        string endpoint,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            var response = await _httpClient.PostAsync(endpoint, content, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return (response, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MicrosoftAuthException(
                $"无法连接 Microsoft 登录服务，请检查网络：{ex.Message}", ex);
        }
    }

    private static MicrosoftOAuthTokens? TryReadTokens(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var accessToken = GetString(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                return null;

            var refreshToken = GetString(root, "refresh_token") ?? string.Empty;
            var expiresIn = GetInt32(root, "expires_in") ?? 3600;
            var scope = GetString(root, "scope") ?? Scope;

            return new MicrosoftOAuthTokens(
                accessToken,
                refreshToken,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)),
                scope);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsDeviceCodePending(string error)
        => string.Equals(error, "authorization_pending", StringComparison.Ordinal)
           || string.Equals(error, "slow_down", StringComparison.Ordinal);

    private static string BuildVerificationUri(string verificationUri, string userCode)
    {
        var separator = verificationUri.Contains('?') ? '&' : '?';
        return $"{verificationUri}{separator}code={Uri.EscapeDataString(userCode)}";
    }

    private static (string Error, string? Description) TryReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return (
                GetString(root, "error") ?? "unknown_error",
                GetString(root, "error_description"));
        }
        catch
        {
            return ("unknown_error", body.Trim());
        }
    }

    private static string MapOAuthError(string error, string? description)
    {
        var detail = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : $"（{description.Trim()}）";

        return error switch
        {
            "invalid_grant" => "Microsoft 登录授权已过期或已被使用，请重新获取登录代码。" + detail,
            "invalid_client" => "Microsoft 登录客户端配置无效，请稍后重试。" + detail,
            "unauthorized_client" => "该 Microsoft 账户无法使用当前登录方式。" + detail,
            "access_denied" or "authorization_declined" =>
                "Microsoft 登录已被取消。" + detail,
            "consent_required" => "需要同意 Microsoft 账户权限后才能继续。" + detail,
            "interaction_required" => "需要重新登录 Microsoft 账户。" + detail,
            "authorization_pending" => "正在等待浏览器中的 Microsoft 授权。" + detail,
            "slow_down" => "Microsoft 要求降低登录轮询频率，请稍候。" + detail,
            "expired_token" => "Microsoft 登录代码已过期，请重新获取。" + detail,
            "bad_verification_code" => "Microsoft 登录代码无效，请重新获取。" + detail,
            _ => $"Microsoft 登录失败：{error}{detail}"
        };
    }

    internal static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    internal static int? GetInt32(JsonElement element, string propertyName)
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
}

public sealed record MicrosoftOAuthTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string Scope);

public sealed class MicrosoftAuthException : Exception
{
    public MicrosoftAuthException(string message) : base(message)
    {
    }

    public MicrosoftAuthException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

namespace PMCL.Models;

/// <summary>
/// A Microsoft device-code sign-in challenge. The launcher opens
/// <see cref="VerificationUriComplete"/> in the system browser and polls the
/// token endpoint; the user never has to copy a callback URL or token.
/// </summary>
public sealed class MicrosoftDeviceLoginChallenge
{
    public MicrosoftDeviceLoginChallenge(
        string deviceCode,
        string userCode,
        string verificationUri,
        string verificationUriComplete,
        DateTimeOffset expiresAt,
        TimeSpan pollInterval)
    {
        DeviceCode = deviceCode;
        UserCode = userCode;
        VerificationUri = verificationUri;
        VerificationUriComplete = verificationUriComplete;
        ExpiresAt = expiresAt;
        PollInterval = pollInterval;
    }

    /// <summary>
    /// Secret code used only between the launcher and Microsoft while polling.
    /// It is never shown in the UI.
    /// </summary>
    public string DeviceCode { get; }

    /// <summary>
    /// Short code shown in the launcher. It is safe to display and is also
    /// embedded in <see cref="VerificationUriComplete"/>.
    /// </summary>
    public string UserCode { get; }

    public string VerificationUri { get; }

    /// <summary>
    /// Microsoft's verification page with the user code pre-filled when the
    /// page supports the <c>code</c> query parameter.
    /// </summary>
    public string VerificationUriComplete { get; }

    public DateTimeOffset ExpiresAt { get; }

    public TimeSpan PollInterval { get; }
}

namespace PMCL.Models;

/// <summary>
/// How a specific installed version authenticates when it is launched.
/// </summary>
public enum VersionLoginMode
{
    /// <summary>Reuse the account that is currently active in the launcher.</summary>
    CurrentAccount,

    /// <summary>Require the currently active account to be a Microsoft account.</summary>
    Microsoft,

    /// <summary>Launch with a local offline session configured for this version.</summary>
    Offline
}

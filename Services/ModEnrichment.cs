using Avalonia.Media.Imaging;

namespace PMCL.Services;

/// <summary>
/// Extra information pulled from Modrinth for one installed jar: the owning
/// project, the exact version that matches the file hash, the newest version
/// that still supports the selected game version/loader, and the project icon.
/// </summary>
public sealed record ModEnrichment(
    ModrinthProject? Project,
    ModrinthVersion? InstalledVersion,
    ModrinthVersion? LatestVersion,
    bool UpdatesChecked,
    string Author,
    Bitmap? Icon)
{
    public bool HasProject => Project is not null;

    /// <summary>
    /// True when Modrinth returned a newer file than the one the local hash
    /// resolved to. Only hash-matched mods are compared so a differently named
    /// build never produces a false "update available" hint.
    /// </summary>
    public bool IsOutdated => UpdatesChecked
                              && InstalledVersion is not null
                              && LatestVersion is not null
                              && !string.Equals(
                                  InstalledVersion.Id,
                                  LatestVersion.Id,
                                  StringComparison.OrdinalIgnoreCase);

    public string InstalledVersionText => InstalledVersion?.VersionNumber ?? string.Empty;

    public bool HasNoCompatibleVersion => UpdatesChecked && HasProject && LatestVersion is null;
}

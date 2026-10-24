using PMCL.Localization;

namespace PMCL.Models;

/// <summary>
/// Selects where Minecraft and mod-loader files are downloaded from.
/// <see cref="Official"/> is the default and never depends on BMCLAPI.
/// </summary>
public enum DownloadSourceKind
{
    Official,
    BmclApi
}

public static class DownloadSourceKindExtensions
{
    public static string ToDisplayName(this DownloadSourceKind source) => source switch
    {
        DownloadSourceKind.BmclApi => L.Tr("Settings.DownloadSource.BmclApi"),
        _ => L.Tr("DownloadSource.OfficialName")
    };

    public static string ToDescription(this DownloadSourceKind source) => source switch
    {
        DownloadSourceKind.BmclApi => L.Tr("DownloadSource.BmclApiDescription"),
        _ => L.Tr("DownloadSource.OfficialDescription")
    };
}

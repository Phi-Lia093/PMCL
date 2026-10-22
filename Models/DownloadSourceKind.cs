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
        DownloadSourceKind.BmclApi => "BMCLAPI 镜像",
        _ => "官方源"
    };

    public static string ToDescription(this DownloadSourceKind source) => source switch
    {
        DownloadSourceKind.BmclApi =>
            "优先使用 BMCLAPI / 国内镜像加速，镜像不可用时自动回退到官方地址。",
        _ =>
            "优先使用 Mojang、Fabric、Forge、NeoForge 与 OptiFine 的官方地址；官方请求失败时才会尝试镜像回退，不依赖 BMCLAPI。"
    };
}

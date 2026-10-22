namespace PMCL.Models;

/// <summary>
/// The mod loader layer installed on top of a vanilla Minecraft version.
/// A version folder can contain only one loader at a time.
/// </summary>
public enum ModLoaderKind
{
    Vanilla,
    Fabric,
    Forge,
    NeoForge,
    OptiFine
}

public static class ModLoaderKindExtensions
{
    public static bool IsModded(this ModLoaderKind kind) => kind != ModLoaderKind.Vanilla;

    public static string FormatLoaderVersion(this ModLoaderKind kind, string version)
        => kind == ModLoaderKind.OptiFine
            ? version.Replace('|', '_').Replace('_', ' ').Trim()
            : version;

    public static string ToDisplayName(this ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "Fabric",
        ModLoaderKind.Forge => "Forge",
        ModLoaderKind.NeoForge => "NeoForge",
        ModLoaderKind.OptiFine => "OptiFine",
        _ => "原版"
    };
}

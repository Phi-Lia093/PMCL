using PMCL.Localization;

namespace PMCL.Models;

/// <summary>
/// Additional per-version content a launcher can manage next to mods. Resource
/// packs, shader packs and data packs are all plain zip archives; the game
/// ignores any other file extension in these folders.
/// </summary>
public enum ContentPackKind
{
    ResourcePack,
    ShaderPack,
    DataPack
}

public static class ContentPackKindExtensions
{
    public static string ToDisplayName(this ContentPackKind kind) => kind switch
    {
        ContentPackKind.ResourcePack => L.Tr("Content.Kind.ResourcePack"),
        ContentPackKind.ShaderPack => L.Tr("Content.Kind.ShaderPack"),
        _ => L.Tr("Content.Kind.DataPack")
    };

    /// <summary>Folder inside the isolated version directory that holds the content.</summary>
    public static string GetFolderName(this ContentPackKind kind) => kind switch
    {
        ContentPackKind.ResourcePack => "resourcepacks",
        ContentPackKind.ShaderPack => "shaderpacks",
        _ => "datapacks"
    };

    /// <summary>
    /// Extensions the game accepts for this content type. The first entry is
    /// also used when a download arrives without a file extension.
    /// </summary>
    public static IReadOnlyList<string> GetFileExtensions(this ContentPackKind kind)
        => new[] { ".zip" };

    public static string GetModrinthProjectType(this ContentPackKind kind) => kind switch
    {
        ContentPackKind.ResourcePack => "resourcepack",
        ContentPackKind.ShaderPack => "shader",
        _ => "datapack"
    };

    /// <summary>
    /// Loader facet used by the Modrinth version endpoint. Resource packs and
    /// shader packs have no meaningful loader distinction (their files are read
    /// by the game or the shader mod itself), so only data packs are filtered.
    /// </summary>
    public static string? GetModrinthLoader(this ContentPackKind kind)
        => kind == ContentPackKind.DataPack ? "datapack" : null;
}

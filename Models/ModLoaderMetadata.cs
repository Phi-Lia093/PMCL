namespace PMCL.Models;

/// <summary>
/// Persisted inside a loader version folder as <c>pmcl-loader.json</c>.
/// Keeping this next to the version makes loader detection survive launcher
/// restarts, settings resets and version folder renames.
/// </summary>
public sealed class ModLoaderMetadata
{
    public ModLoaderKind Loader { get; set; } = ModLoaderKind.Vanilla;

    public string LoaderVersion { get; set; } = string.Empty;

    public string MinecraftVersion { get; set; } = string.Empty;
}

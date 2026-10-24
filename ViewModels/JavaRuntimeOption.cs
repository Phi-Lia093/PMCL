namespace PMCL.ViewModels;

/// <summary>
/// One entry of the Java picker. An empty <see cref="Path"/> is the "auto"
/// choice, which lets CmlLib resolve (and download, if needed) the runtime
/// required by the selected Minecraft version.
/// </summary>
public sealed record JavaRuntimeOption(string Path, string DisplayName)
{
    public bool IsAuto => string.IsNullOrWhiteSpace(Path);
}

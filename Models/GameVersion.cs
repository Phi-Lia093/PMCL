using CommunityToolkit.Mvvm.ComponentModel;
using PMCL.Localization;

namespace PMCL.Models;

/// <summary>
/// A Minecraft version shown in the launcher.
/// </summary>
public sealed partial class GameVersion : ObservableObject
{
    public GameVersion(
        string name,
        string type,
        DateTimeOffset releaseTime,
        bool isInstalled,
        ModLoaderKind loaderKind = ModLoaderKind.Vanilla,
        string loaderVersion = "",
        string baseVersionName = "")
    {
        Name = name;
        Type = type;
        ReleaseTime = releaseTime;
        IsInstalled = isInstalled;
        LoaderKind = loaderKind;
        LoaderVersion = loaderVersion;
        BaseVersionName = string.IsNullOrWhiteSpace(baseVersionName) ? name : baseVersionName;
    }

    public string Type { get; }

    public DateTimeOffset ReleaseTime { get; }

    public ModLoaderKind LoaderKind { get; }

    public string LoaderVersion { get; }

    /// <summary>
    /// The vanilla version this loader profile inherits from. Equal to
    /// <see cref="Name"/> for vanilla versions.
    /// </summary>
    public string BaseVersionName { get; }

    public bool IsModded => LoaderKind.IsModded();

    public string LoaderLabel => LoaderKind switch
    {
        ModLoaderKind.Vanilla => L.Tr("Loader.Vanilla"),
        _ when string.IsNullOrWhiteSpace(LoaderVersion) => LoaderKind.ToDisplayName(),
        _ => $"{LoaderKind.ToDisplayName()} {LoaderKind.FormatLoaderVersion(LoaderVersion)}"
    };

    public string LoaderBadge => IsModded ? LoaderLabel : L.Tr("Loader.Vanilla");

    public string BaseVersionLabel => IsModded
        ? L.Format("Version.BasedOn", BaseVersionName)
        : L.Tr("Version.VanillaVersion");

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);

    public string TypeLabel => Type.ToLowerInvariant() switch
    {
        "release" => L.Tr("Version.Type.Release"),
        "snapshot" => L.Tr("Version.Type.Snapshot"),
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Type
    };

    public string ReleaseDateText => ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");
}

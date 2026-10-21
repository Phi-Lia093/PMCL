using CommunityToolkit.Mvvm.ComponentModel;

namespace PMCL.Models;

/// <summary>
/// A Minecraft version shown in the launcher.
/// </summary>
public sealed partial class GameVersion : ObservableObject
{
    public GameVersion(string name, string type, DateTimeOffset releaseTime, bool isInstalled)
    {
        Name = name;
        Type = type;
        ReleaseTime = releaseTime;
        IsInstalled = isInstalled;
    }

    public string Name { get; }

    public string Type { get; }

    public DateTimeOffset ReleaseTime { get; }

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);

    public string TypeLabel => Type.ToLowerInvariant() switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Type
    };

    public string ReleaseDateText => ReleaseTime.ToLocalTime().ToString("yyyy-MM-dd");
}

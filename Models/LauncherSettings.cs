using CommunityToolkit.Mvvm.ComponentModel;

namespace PMCL.Models;

/// <summary>
/// User settings persisted to <c>%APPDATA%\PMCL\settings.json</c>.
/// </summary>
public sealed partial class LauncherSettings : ObservableObject
{
    [ObservableProperty]
    public partial string GameDirectory { get; set; } = CmlLib.Core.MinecraftPath.GetOSDefaultPath();

    [ObservableProperty]
    public partial string JavaPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MinimumRamMb { get; set; } = 1024;

    [ObservableProperty]
    public partial int MaximumRamMb { get; set; } = 4096;

    [ObservableProperty]
    public partial int WindowWidth { get; set; } = 1280;

    [ObservableProperty]
    public partial int WindowHeight { get; set; } = 720;

    [ObservableProperty]
    public partial bool FullScreen { get; set; }

    [ObservableProperty]
    public partial string LastVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OfflineUsername { get; set; } = "Player";

    /// <summary>
    /// Stores the account kind used on the previous run: "Offline", "Microsoft" or empty.
    /// </summary>
    [ObservableProperty]
    public partial string LastAccountKind { get; set; } = string.Empty;
}

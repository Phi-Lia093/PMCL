using CommunityToolkit.Mvvm.ComponentModel;

namespace PMCL.Models;

/// <summary>
/// User settings persisted to <c>%APPDATA%\PMCL\settings.json</c>.
/// Only launcher-wide values live here; launch options are stored per version.
/// </summary>
public sealed partial class LauncherSettings : ObservableObject
{
    [ObservableProperty]
    public partial string GameDirectory { get; set; } = CmlLib.Core.MinecraftPath.GetOSDefaultPath();

    /// <summary>
    /// UI language code. Translation files live in <c>Assets/i18n</c>.
    /// </summary>
    [ObservableProperty]
    public partial string Language { get; set; } = "zh-CN";

    [ObservableProperty]
    public partial string LastVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OfflineUsername { get; set; } = "Player";

    /// <summary>
    /// Stores the account kind used on the previous run: "Offline", "Microsoft" or empty.
    /// </summary>
    [ObservableProperty]
    public partial string LastAccountKind { get; set; } = string.Empty;

    /// <summary>
    /// Identifier of the saved account that should be restored on the next launch.
    /// Empty means no account should be restored automatically.
    /// </summary>
    [ObservableProperty]
    public partial string LastAccountId { get; set; } = string.Empty;

    /// <summary>
    /// Source used for metadata and file downloads. Official is the default
    /// and never requires BMCLAPI.
    /// </summary>
    [ObservableProperty]
    public partial DownloadSourceKind DownloadSource { get; set; } = DownloadSourceKind.Official;

    /// <summary>
    /// Window close behaviour. Minimizing keeps the launcher alive in the
    /// notification area, while Exit terminates it completely.
    /// </summary>
    [ObservableProperty]
    public partial LauncherCloseAction CloseAction { get; set; } =
        LauncherCloseAction.MinimizeToTray;

    /// <summary>
    /// Window action applied after the game process has been started.
    /// </summary>
    [ObservableProperty]
    public partial GameLaunchWindowAction OnGameLaunchAction { get; set; } =
        GameLaunchWindowAction.MinimizeToTray;

    /// <summary>
    /// Restores and activates the launcher when Minecraft exits, as long as the
    /// launcher is still hidden in the tray.
    /// </summary>
    [ObservableProperty]
    public partial bool RestoreAfterGameExit { get; set; } = true;

    /// <summary>
    /// Launch options keyed by the installed version folder name.
    /// </summary>
    public Dictionary<string, GameVersionSettings> VersionSettings { get; set; } = new();

    /// <summary>
    /// Returns the settings for <paramref name="versionName"/>, creating defaults on demand.
    /// </summary>
    public GameVersionSettings GetVersionSettings(string versionName)
    {
        VersionSettings ??= new Dictionary<string, GameVersionSettings>();

        if (!VersionSettings.TryGetValue(versionName, out var settings) || settings is null)
        {
            settings = new GameVersionSettings();
            VersionSettings[versionName] = settings;
        }

        return settings;
    }

    public void MoveVersionSettings(string oldName, string newName)
    {
        VersionSettings ??= new Dictionary<string, GameVersionSettings>();
        if (VersionSettings.Remove(oldName, out var settings))
            VersionSettings[newName] = settings;
    }

    public void RemoveVersionSettings(string versionName)
    {
        VersionSettings?.Remove(versionName);
    }
}

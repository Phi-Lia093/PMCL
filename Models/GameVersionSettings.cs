using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using PMCL.Localization;

namespace PMCL.Models;

/// <summary>
/// Per-installed-version launch settings, persisted inside
/// <see cref="LauncherSettings.VersionSettings"/>.
/// </summary>
public sealed partial class GameVersionSettings : ObservableObject
{
    public const int DefaultMinimumRamMb = 1024;
    public const int DefaultMaximumRamMb = 4096;
    public const int DefaultWindowWidth = 1280;
    public const int DefaultWindowHeight = 720;
    public const int DefaultServerPort = 25565;

    [ObservableProperty]
    public partial string JavaPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MinimumRamMb { get; set; } = DefaultMinimumRamMb;

    [ObservableProperty]
    public partial int MaximumRamMb { get; set; } = DefaultMaximumRamMb;

    [ObservableProperty]
    public partial int WindowWidth { get; set; } = DefaultWindowWidth;

    [ObservableProperty]
    public partial int WindowHeight { get; set; } = DefaultWindowHeight;

    [ObservableProperty]
    public partial bool FullScreen { get; set; }

    [ObservableProperty]
    public partial VersionLoginMode LoginMode { get; set; } = VersionLoginMode.CurrentAccount;

    [ObservableProperty]
    public partial string OfflineUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AutoJoinServer { get; set; }

    [ObservableProperty]
    public partial string ServerAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ServerPort { get; set; } = DefaultServerPort;

    [ObservableProperty]
    public partial string JvmArguments { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GameArguments { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreLaunchCommand { get; set; } = string.Empty;

    [JsonIgnore]
    public string JavaDisplay => string.IsNullOrWhiteSpace(JavaPath)
        ? L.Tr("Version.AutoJava")
        : JavaPath;

    [JsonIgnore]
    public string MemoryDisplay => $"{MinimumRamMb} - {MaximumRamMb} MB";

    partial void OnJavaPathChanged(string value) => OnPropertyChanged(nameof(JavaDisplay));

    partial void OnMinimumRamMbChanged(int value) => OnPropertyChanged(nameof(MemoryDisplay));

    partial void OnMaximumRamMbChanged(int value) => OnPropertyChanged(nameof(MemoryDisplay));

    /// <summary>
    /// Copies every persisted launch option. Used when a loader profile is
    /// created, replaced or removed so the user keeps Java/RAM/login settings.
    /// </summary>
    public void CopyFrom(GameVersionSettings source)
    {
        JavaPath = source.JavaPath;
        MinimumRamMb = source.MinimumRamMb;
        MaximumRamMb = source.MaximumRamMb;
        WindowWidth = source.WindowWidth;
        WindowHeight = source.WindowHeight;
        FullScreen = source.FullScreen;
        LoginMode = source.LoginMode;
        OfflineUsername = source.OfflineUsername;
        AutoJoinServer = source.AutoJoinServer;
        ServerAddress = source.ServerAddress;
        ServerPort = source.ServerPort;
        JvmArguments = source.JvmArguments;
        GameArguments = source.GameArguments;
        PreLaunchCommand = source.PreLaunchCommand;
    }

    public GameVersionSettings Clone()
    {
        var copy = new GameVersionSettings();
        copy.CopyFrom(this);
        return copy;
    }
}

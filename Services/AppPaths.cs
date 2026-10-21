using CmlLib.Core;

namespace PMCL.Services;

/// <summary>
/// Centralized file system locations used by PMCL.
/// </summary>
public sealed class AppPaths
{
    public AppPaths()
    {
        DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PMCL");
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string AccountsFile => Path.Combine(DataDirectory, "accounts.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultGameDirectory => MinecraftPath.GetOSDefaultPath();
}

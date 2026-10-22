using CmlLib.Core;

namespace PMCL.Services;

/// <summary>
/// Centralized file system locations used by PMCL.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PMCL")
            : Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string AccountsFile => Path.Combine(DataDirectory, "accounts.json");

    /// <summary>
    /// Backup of the account file written by CmlLib before PMCL moved to its own
    /// multi-account store.
    /// </summary>
    public string LegacyAccountsBackupFile => Path.Combine(DataDirectory, "accounts.legacy.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultGameDirectory => MinecraftPath.GetOSDefaultPath();
}

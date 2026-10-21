using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Wraps CmlLib.Core for version discovery, installation and process building.
/// </summary>
public sealed class MinecraftService
{
    private readonly Dictionary<string, MinecraftLauncher> _launchers =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<GameVersion>> GetVersionsAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = CreatePath(gameDirectory);
        var launcher = GetLauncher(path);
        var collection = await launcher.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);

        return collection
            .Select(metadata => new GameVersion(
                metadata.Name,
                metadata.Type ?? string.Empty,
                metadata.ReleaseTime,
                IsInstalled(path, metadata.Name)))
            .ToList();
    }

    public bool IsInstalled(string gameDirectory, string versionName)
        => IsInstalled(CreatePath(gameDirectory), versionName);

    public async Task InstallAsync(
        string gameDirectory,
        string versionName,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress = null,
        IProgress<ByteProgress>? byteProgress = null,
        CancellationToken cancellationToken = default)
    {
        var launcher = GetLauncher(CreatePath(gameDirectory));
        await launcher.InstallAsync(versionName, fileProgress, byteProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Process> BuildProcessAsync(
        string gameDirectory,
        string versionName,
        MSession session,
        LauncherSettings settings,
        CancellationToken cancellationToken = default)
    {
        var path = CreatePath(gameDirectory);
        var launcher = GetLauncher(path);

        var option = new MLaunchOption
        {
            Path = path,
            Session = session,
            MaximumRamMb = settings.MaximumRamMb,
            MinimumRamMb = settings.MinimumRamMb,
            JavaPath = string.IsNullOrWhiteSpace(settings.JavaPath) ? null : settings.JavaPath,
            ScreenWidth = settings.WindowWidth,
            ScreenHeight = settings.WindowHeight,
            FullScreen = settings.FullScreen,
            GameLauncherName = "PMCL",
            GameLauncherVersion = "0.1.0",
            VersionType = "PMCL"
        };

        return await launcher.BuildProcessAsync(versionName, option, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the auto-detected Java executable, or null when no Java installation was found.
    /// </summary>
    public string? TryGetDefaultJavaPath(string gameDirectory)
    {
        try
        {
            var path = CreatePath(gameDirectory);
            var javaPath = GetLauncher(path).GetDefaultJavaPath();
            return string.IsNullOrWhiteSpace(javaPath) ? null : javaPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private MinecraftLauncher GetLauncher(MinecraftPath path)
    {
        if (_launchers.TryGetValue(path.BasePath, out var launcher))
            return launcher;

        path.CreateDirs();
        launcher = new MinecraftLauncher(path);
        _launchers[path.BasePath] = launcher;
        return launcher;
    }

    private static MinecraftPath CreatePath(string gameDirectory)
    {
        var fullPath = Path.GetFullPath(gameDirectory);
        return new MinecraftPath(fullPath);
    }

    private static bool IsInstalled(MinecraftPath path, string versionName)
    {
        var jsonPath = Path.Combine(path.Versions, versionName, versionName + ".json");
        return File.Exists(jsonPath);
    }
}

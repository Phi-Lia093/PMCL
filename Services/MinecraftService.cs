using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        GameVersionSettings settings,
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
            ExtraJvmArguments = CommandLineSplitter.Split(settings.JvmArguments)
                .Select(argument => new MArgument(argument))
                .ToList(),
            ExtraGameArguments = CommandLineSplitter.Split(settings.GameArguments)
                .Select(argument => new MArgument(argument))
                .ToList(),
            GameLauncherName = "PMCL",
            GameLauncherVersion = "0.1.0",
            VersionType = "PMCL"
        };

        if (settings.AutoJoinServer && !string.IsNullOrWhiteSpace(settings.ServerAddress))
        {
            option.ServerIp = settings.ServerAddress.Trim();
            option.ServerPort = settings.ServerPort;
        }

        return await launcher.BuildProcessAsync(versionName, option, cancellationToken)
            .ConfigureAwait(false);
    }

    public string GetVersionDirectory(string gameDirectory, string versionName)
        => Path.Combine(CreatePath(gameDirectory).Versions, versionName);

    public string GetSavesDirectory(string gameDirectory)
        => Path.Combine(CreatePath(gameDirectory).BasePath, "saves");

    public string GetScreenshotsDirectory(string gameDirectory)
        => Path.Combine(CreatePath(gameDirectory).BasePath, "screenshots");

    /// <summary>
    /// Renames an installed version folder, its metadata/jar files and the id in
    /// version.json. Child versions that inherit from it are updated as well.
    /// </summary>
    public void RenameVersion(string gameDirectory, string oldName, string newName)
    {
        ValidateVersionName(newName);

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return;

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("暂不支持只修改版本名的大小写。");

        var path = CreatePath(gameDirectory);
        var oldDirectory = Path.Combine(path.Versions, oldName);
        var newDirectory = Path.Combine(path.Versions, newName);

        if (!Directory.Exists(oldDirectory))
            throw new DirectoryNotFoundException($"找不到版本文件夹：{oldDirectory}");
        if (Directory.Exists(newDirectory) || File.Exists(newDirectory))
            throw new InvalidOperationException($"版本文件夹 {newName} 已存在。");

        var oldJsonPath = Path.Combine(oldDirectory, oldName + ".json");
        if (!File.Exists(oldJsonPath))
            throw new FileNotFoundException($"版本缺少元数据文件：{oldJsonPath}");

        var originalJson = File.ReadAllText(oldJsonPath);
        if (JsonNode.Parse(originalJson) is not JsonObject metadata)
            throw new InvalidDataException($"版本元数据格式无效：{oldJsonPath}");

        Directory.Move(oldDirectory, newDirectory);

        try
        {
            metadata["id"] = newName;
            var newJsonPath = Path.Combine(newDirectory, newName + ".json");
            File.WriteAllText(newJsonPath, metadata.ToJsonString(IndentedJsonOptions));
            File.Delete(Path.Combine(newDirectory, oldName + ".json"));

            var oldJarPath = Path.Combine(newDirectory, oldName + ".jar");
            if (File.Exists(oldJarPath))
            {
                var newJarPath = Path.Combine(newDirectory, newName + ".jar");
                if (!File.Exists(newJarPath))
                    File.Move(oldJarPath, newJarPath);
            }
        }
        catch
        {
            TryRollbackRename(newDirectory, oldDirectory, oldName, newName, originalJson);
            throw;
        }

        UpdateInheritedVersionReferences(path, newDirectory, oldName, newName);
        _launchers.Remove(path.BasePath);
    }

    /// <summary>
    /// Deletes a version folder. Saves, screenshots and other shared game data are kept.
    /// </summary>
    public void DeleteVersion(string gameDirectory, string versionName)
    {
        var path = CreatePath(gameDirectory);
        var versionDirectory = Path.Combine(path.Versions, versionName);

        if (Directory.Exists(versionDirectory))
            Directory.Delete(versionDirectory, recursive: true);

        _launchers.Remove(path.BasePath);
    }

    public static void ValidateVersionName(string versionName)
    {
        var name = versionName.Trim();

        if (name.Length is 0 or > 96)
            throw new InvalidOperationException("版本名长度必须为 1 到 96 个字符。");
        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidOperationException("版本名不能以点或空格结尾。");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("版本名包含文件系统不允许的字符。");
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

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private static void TryRollbackRename(
        string newDirectory,
        string oldDirectory,
        string oldName,
        string newName,
        string originalJson)
    {
        try
        {
            if (!Directory.Exists(newDirectory) || Directory.Exists(oldDirectory))
                return;

            var newJsonPath = Path.Combine(newDirectory, newName + ".json");
            if (File.Exists(newJsonPath))
                File.Delete(newJsonPath);

            File.WriteAllText(Path.Combine(newDirectory, oldName + ".json"), originalJson);

            var newJarPath = Path.Combine(newDirectory, newName + ".jar");
            var oldJarPath = Path.Combine(newDirectory, oldName + ".jar");
            if (File.Exists(newJarPath) && !File.Exists(oldJarPath))
                File.Move(newJarPath, oldJarPath);

            Directory.Move(newDirectory, oldDirectory);
        }
        catch
        {
            // Best-effort rollback; the original exception is more useful to the caller.
        }
    }

    private static void UpdateInheritedVersionReferences(
        MinecraftPath path,
        string renamedDirectory,
        string oldName,
        string newName)
    {
        var renamedPrefix = renamedDirectory.TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

        foreach (var jsonFile in Directory.EnumerateFiles(
                     path.Versions,
                     "*.json",
                     SearchOption.AllDirectories))
        {
            if (jsonFile.StartsWith(renamedPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (JsonNode.Parse(File.ReadAllText(jsonFile)) is not JsonObject metadata)
                    continue;

                if (metadata["inheritsFrom"] is JsonValue value
                    && value.TryGetValue<string>(out var parent)
                    && string.Equals(parent, oldName, StringComparison.Ordinal))
                {
                    metadata["inheritsFrom"] = newName;
                    File.WriteAllText(jsonFile, metadata.ToJsonString(IndentedJsonOptions));
                }
            }
            catch
            {
                // A malformed third-party version must not break the rename operation.
            }
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

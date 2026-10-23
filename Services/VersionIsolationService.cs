namespace PMCL.Services;

/// <summary>
/// Result of copying user data between the shared <c>.minecraft</c> root and a
/// per-version isolated game directory.
/// </summary>
public sealed record IsolationDataResult(int CopiedFiles, int SkippedFiles)
{
    public static readonly IsolationDataResult Empty = new(0, 0);

    public bool HasCopiedFiles => CopiedFiles > 0;
}

/// <summary>
/// Computes the per-version game directory and migrates user data.
///
/// Every version runs with <c>--gameDir</c> pointing at
/// <c>versions/&lt;name&gt;</c>. The version directory therefore owns
/// <c>saves</c>, <c>screenshots</c>, <c>mods</c>, <c>config</c>,
/// <c>resourcepacks</c>, <c>shaderpacks</c> and <c>options.txt</c>, while
/// <c>libraries</c>, <c>assets</c> and <c>runtime</c> stay in the shared root
/// and are never duplicated. Isolation is mandatory so mods can never leak
/// into another version.
/// </summary>
public static class VersionIsolationService
{
    /// <summary>User data folders copied when importing shared data.</summary>
    public static readonly IReadOnlyList<string> UserDataFolders = new[]
    {
        "saves",
        "screenshots",
        "mods",
        "config",
        "defaultconfigs",
        "resourcepacks",
        "shaderpacks"
    };

    /// <summary>User data files copied when importing shared data.</summary>
    public static readonly IReadOnlyList<string> UserDataFiles = new[]
    {
        "options.txt",
        "optionsof.txt",
        "optionsshaders.txt",
        "servers.dat"
    };

    public static string GetVersionsRoot(string gameDirectory)
        => Path.Combine(Path.GetFullPath(gameDirectory), "versions");

    /// <summary>
    /// Physical version folder, which is also the isolated game directory.
    /// </summary>
    public static string GetIsolatedGameDirectory(string gameDirectory, string versionName)
        => Path.Combine(GetVersionsRoot(gameDirectory), versionName);

    /// <summary>
    /// Game directory used for launching <paramref name="versionName"/>. The
    /// launcher always isolates versions, so this is their version folder.
    /// </summary>
    public static string GetEffectiveGameDirectory(string gameDirectory, string versionName)
        => GetIsolatedGameDirectory(gameDirectory, versionName);

    /// <summary>
    /// Returns a user data path (for example <c>saves</c>) inside the effective
    /// game directory of a version.
    /// </summary>
    public static string GetEffectiveDirectory(
        string gameDirectory,
        string versionName,
        string childDirectory)
        => Path.Combine(
            GetEffectiveGameDirectory(gameDirectory, versionName),
            childDirectory);

    /// <summary>
    /// Copies the classic shared <c>.minecraft</c> user data into an isolated
    /// version folder. Existing files are never overwritten, so switching a
    /// version back and forth cannot destroy newer isolated data.
    /// </summary>
    public static IsolationDataResult ImportSharedData(string gameDirectory, string versionName)
    {
        var targetRoot = GetIsolatedGameDirectory(gameDirectory, versionName);
        return CopyUserData(Path.GetFullPath(gameDirectory), targetRoot);
    }

    /// <summary>
    /// Copies user data from one version folder to another. Used when a mod
    /// loader is installed, replaced or removed so the user keeps
    /// saves/mods even though the old version folder is deleted.
    /// </summary>
    public static IsolationDataResult MergeVersionData(
        string gameDirectory,
        string sourceVersionName,
        string targetGameDirectory)
    {
        var sourceRoot = GetIsolatedGameDirectory(gameDirectory, sourceVersionName);
        return CopyUserData(sourceRoot, Path.GetFullPath(targetGameDirectory));
    }

    private static IsolationDataResult CopyUserData(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot)
            || string.Equals(
                Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return IsolationDataResult.Empty;
        }

        var copied = 0;
        var skipped = 0;

        foreach (var folder in UserDataFolders)
        {
            var source = Path.Combine(sourceRoot, folder);
            if (!Directory.Exists(source))
                continue;

            CopyDirectory(source, Path.Combine(targetRoot, folder), ref copied, ref skipped);
        }

        foreach (var file in UserDataFiles)
        {
            var source = Path.Combine(sourceRoot, file);
            if (!File.Exists(source))
                continue;

            if (TryCopyFile(source, Path.Combine(targetRoot, file)))
                copied++;
            else
                skipped++;
        }

        return new IsolationDataResult(copied, skipped);
    }

    private static void CopyDirectory(string source, string target, ref int copied, ref int skipped)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (TryCopyFile(file, Path.Combine(target, Path.GetFileName(file))))
                copied++;
            else
                skipped++;
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), ref copied, ref skipped);
        }
    }

    private static bool TryCopyFile(string source, string target)
    {
        try
        {
            if (File.Exists(target))
                return false;

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.Copy(source, target, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file must not abort the whole import; report it as skipped.
            return false;
        }
    }
}


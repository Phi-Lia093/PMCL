using PMCL.Localization;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// A folder that content packs can be installed into. Data packs are loaded per
/// world, so every save gets its own <c>datapacks</c> target next to the
/// version-level staging folder.
/// </summary>
public sealed record ContentPackTarget(string DirectoryPath, string Label, bool IsWorldScoped)
{
    public bool IsVersionRoot => !IsWorldScoped;

    public string DisplayLabel => IsWorldScoped
        ? L.Format("VS.Content.TargetWorld", Label)
        : L.Tr("VS.Content.TargetVersionRoot");
}

/// <summary>
/// One archive inside a content folder. Disabled files keep a <c>.disabled</c>
/// suffix so the game ignores them while the archive stays on disk.
/// </summary>
public sealed record LocalContentPack(
    ContentPackKind Kind,
    string FileName,
    string FilePath,
    long SizeBytes,
    bool IsEnabled,
    string ContainerLabel)
{
    public const string DisabledSuffix = ".disabled";

    /// <summary>File name without the archive extension and optional suffix.</summary>
    public string DisplayName
    {
        get
        {
            var name = FileName;
            if (name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^DisabledSuffix.Length];

            var extension = Kind.GetFileExtensions()
                .FirstOrDefault(candidate =>
                    name.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (extension is not null)
                name = name[..^extension.Length];

            return name.Length == 0 ? FileName : name;
        }
    }

    public string SizeText => DownloadSpeedTracker.FormatBytes(SizeBytes);

    public string StatusText => IsEnabled
        ? L.Tr("Mod.StatusEnabled")
        : L.Tr("Mod.StatusDisabled");

    public bool HasContainer => !string.IsNullOrWhiteSpace(ContainerLabel);
}

/// <summary>
/// Scans and mutates the per-version <c>resourcepacks</c>, <c>shaderpacks</c>
/// and <c>datapacks</c> folders. Enabling/disabling is a pure rename between
/// <c>name.zip</c> and <c>name.zip.disabled</c>, so the archive is never
/// rewritten and every loader keeps working.
/// </summary>
public sealed class ContentPackService
{
    private const string SavesFolderName = "saves";
    private const string LevelFileName = "level.dat";

    /// <summary>
    /// Folders that can receive this content type. Resource packs and shaders
    /// only use the version folder; data packs additionally expose every save
    /// so a pack can be installed where the game actually reads it.
    /// </summary>
    public IReadOnlyList<ContentPackTarget> GetTargets(ContentPackKind kind, string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
            return Array.Empty<ContentPackTarget>();

        var targets = new List<ContentPackTarget>
        {
            new(Path.Combine(gameDirectory, kind.GetFolderName()), string.Empty, false)
        };

        if (kind != ContentPackKind.DataPack)
            return targets;

        var savesDirectory = Path.Combine(gameDirectory, SavesFolderName);
        if (!Directory.Exists(savesDirectory))
            return targets;

        try
        {
            foreach (var world in Directory.EnumerateDirectories(savesDirectory))
            {
                if (!File.Exists(Path.Combine(world, LevelFileName)))
                    continue;

                targets.Add(new ContentPackTarget(
                    Path.Combine(world, kind.GetFolderName()),
                    Path.GetFileName(world),
                    true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A single unreadable save folder must not hide the other targets.
        }

        return targets;
    }

    public IReadOnlyList<LocalContentPack> Scan(ContentPackKind kind, string gameDirectory)
        => ScanTargets(kind, GetTargets(kind, gameDirectory));

    public IReadOnlyList<LocalContentPack> ScanTargets(
        ContentPackKind kind,
        IEnumerable<ContentPackTarget> targets)
    {
        var packs = new List<LocalContentPack>();
        foreach (var target in targets)
        {
            if (!Directory.Exists(target.DirectoryPath))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(target.DirectoryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var info = TryDescribe(kind, file, target.Label);
                if (info is not null)
                    packs.Add(info);
            }
        }

        return packs
            .OrderByDescending(pack => pack.IsEnabled)
            .ThenBy(pack => pack.ContainerLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pack => pack.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when the file name carries an extension this content type accepts.</summary>
    public bool IsSupportedFileName(ContentPackKind kind, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var name = fileName.EndsWith(LocalContentPack.DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^LocalContentPack.DisabledSuffix.Length]
            : fileName;

        return kind.GetFileExtensions().Any(extension =>
            name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns content information for an archive or disabled archive path, or
    /// <c>null</c> when the file is not a pack of this kind.
    /// </summary>
    public LocalContentPack? TryDescribe(ContentPackKind kind, string path, string containerLabel = "")
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fileName = Path.GetFileName(path);
        if (!IsSupportedFileName(kind, fileName))
            return null;

        var info = new FileInfo(path);
        if (!info.Exists)
            return null;

        var enabled = !fileName.EndsWith(
            LocalContentPack.DisabledSuffix,
            StringComparison.OrdinalIgnoreCase);

        return new LocalContentPack(
            kind,
            fileName,
            info.FullName,
            info.Length,
            enabled,
            containerLabel);
    }

    /// <summary>
    /// Enables or disables a pack by renaming the archive. Returns the updated entry.
    /// </summary>
    public LocalContentPack SetEnabled(LocalContentPack pack, bool enabled)
    {
        if (pack.IsEnabled == enabled)
            return pack;

        var targetPath = enabled
            ? pack.FilePath[..^LocalContentPack.DisabledSuffix.Length]
            : pack.FilePath + LocalContentPack.DisabledSuffix;

        if (File.Exists(targetPath))
            throw new IOException(L.Format("Content.TargetExists", targetPath));

        File.Move(pack.FilePath, targetPath);
        return TryDescribe(pack.Kind, targetPath, pack.ContainerLabel)
               ?? throw new IOException(L.Format("Content.RenameUnrecognized", targetPath));
    }

    /// <summary>
    /// Permanently deletes an archive (enabled or disabled).
    /// </summary>
    public void Delete(LocalContentPack pack)
    {
        var verified = TryDescribe(pack.Kind, pack.FilePath, pack.ContainerLabel)
                       ?? throw new InvalidOperationException(
                           L.Format("Content.InvalidFile", pack.FilePath));

        File.Delete(verified.FilePath);
    }
}

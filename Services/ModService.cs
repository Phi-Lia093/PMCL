namespace PMCL.Services;

/// <summary>
/// A jar inside a version's <c>mods</c> folder. Disabled mods keep their
/// <c>.disabled</c> suffix so the game loader ignores them but the file stays
/// available for later re-enabling.
/// </summary>
public sealed record LocalModInfo(
    string FileName,
    string FilePath,
    long SizeBytes,
    bool IsEnabled)
{
    public const string JarExtension = ".jar";
    public const string DisabledSuffix = ".disabled";

    /// <summary>
    /// File name without the <c>.jar</c> and optional <c>.disabled</c> suffix.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var name = FileName;
            if (name.EndsWith(JarExtension + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^(JarExtension.Length + DisabledSuffix.Length)];
            else if (name.EndsWith(JarExtension, StringComparison.OrdinalIgnoreCase))
                name = name[..^JarExtension.Length];

            return name.Length == 0 ? FileName : name;
        }
    }

    public string SizeText => DownloadSpeedTracker.FormatBytes(SizeBytes);

    public string StatusText => IsEnabled ? "已启用" : "已禁用";
}

/// <summary>
/// Scans and mutates the per-version <c>mods</c> folder. Enable/disable is a
/// pure rename between <c>name.jar</c> and <c>name.jar.disabled</c>, so it works
/// for every mod loader and never rewrites the jar itself.
/// </summary>
public sealed class ModService
{
    public IReadOnlyList<LocalModInfo> Scan(string modsDirectory)
    {
        if (string.IsNullOrWhiteSpace(modsDirectory) || !Directory.Exists(modsDirectory))
            return Array.Empty<LocalModInfo>();

        var mods = new List<LocalModInfo>();
        foreach (var file in Directory.EnumerateFiles(modsDirectory))
        {
            var info = TryDescribe(file);
            if (info is not null)
                mods.Add(info);
        }

        return mods
            .OrderByDescending(info => info.IsEnabled)
            .ThenBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns mod information for a jar or disabled jar path, or null when the
    /// file is not a mod jar.
    /// </summary>
    public LocalModInfo? TryDescribe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fileName = Path.GetFileName(path);
        var enabled = fileName.EndsWith(LocalModInfo.JarExtension, StringComparison.OrdinalIgnoreCase);
        var disabled = fileName.EndsWith(
            LocalModInfo.JarExtension + LocalModInfo.DisabledSuffix,
            StringComparison.OrdinalIgnoreCase);

        if (!enabled && !disabled)
            return null;

        var info = new FileInfo(path);
        if (!info.Exists)
            return null;

        return new LocalModInfo(fileName, info.FullName, info.Length, enabled);
    }

    /// <summary>
    /// Enables or disables a mod by renaming the file. Returns the updated entry.
    /// </summary>
    public LocalModInfo SetEnabled(LocalModInfo mod, bool enabled)
    {
        if (mod.IsEnabled == enabled)
            return mod;

        var targetPath = enabled
            ? mod.FilePath[..^LocalModInfo.DisabledSuffix.Length]
            : mod.FilePath + LocalModInfo.DisabledSuffix;

        if (File.Exists(targetPath))
            throw new IOException($"无法{(enabled ? "启用" : "禁用")}模组：目标文件已存在 {targetPath}");

        File.Move(mod.FilePath, targetPath);
        return TryDescribe(targetPath)
               ?? throw new IOException($"无法识别重命名后的模组文件：{targetPath}");
    }

    /// <summary>
    /// Permanently deletes a mod jar (enabled or disabled).
    /// </summary>
    public void Delete(LocalModInfo mod)
    {
        var verified = TryDescribe(mod.FilePath)
                       ?? throw new InvalidOperationException($"不是有效的模组文件：{mod.FilePath}");

        File.Delete(verified.FilePath);
    }
}

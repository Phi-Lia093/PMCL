using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PMCL.Localization;

namespace PMCL.Services;

/// <summary>
/// Uses the Avalonia storage provider of the main window to pick folders and files.
/// </summary>
public sealed class StoragePickerService
{
    private readonly Func<Window?> _windowProvider;

    public StoragePickerService(Func<Window?> windowProvider)
    {
        _windowProvider = windowProvider;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var window = _windowProvider();
        if (window?.StorageProvider is not { CanPickFolder: true } storage)
            return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickJavaExecutableAsync(string title)
    {
        var window = _windowProvider();
        if (window?.StorageProvider is not { CanOpen: true } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L.Tr("Storage.JavaExecutable"))
                {
                    Patterns = new[] { "java.exe", "javaw.exe", "java" }
                }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickModFilesAsync(string title)
    {
        return await PickArchiveFilesAsync(
                title,
                L.Tr("Storage.ModArchive"),
                new[] { "*.jar" })
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Picks resource pack, shader or data pack archives. The accepted patterns
    /// come from the content kind so a data pack picker also allows jars.
    /// </summary>
    public async Task<IReadOnlyList<string>> PickContentPackFilesAsync(
        string title,
        IReadOnlyList<string> patterns)
    {
        return await PickArchiveFilesAsync(
                title,
                L.Tr("Storage.ContentArchive"),
                patterns)
            .ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<string>> PickArchiveFilesAsync(
        string title,
        string fileTypeName,
        IReadOnlyList<string> patterns)
    {
        var window = _windowProvider();
        if (window?.StorageProvider is not { CanOpen: true } storage)
            return Array.Empty<string>();

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(fileTypeName)
                {
                    Patterns = patterns.ToArray()
                }
            }
        });

        if (files.Count == 0)
            return Array.Empty<string>();

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }

        return paths;
    }

    public async Task<string?> PickSaveFileAsync(
        string title,
        string suggestedFileName,
        string defaultExtension,
        string fileTypeName,
        params string[] patterns)
    {
        var window = _windowProvider();
        if (window?.StorageProvider is not { CanSave: true } storage)
            return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(fileTypeName)
                {
                    Patterns = patterns
                }
            }
        });

        return file?.TryGetLocalPath();
    }
}

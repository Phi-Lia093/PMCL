using Avalonia.Controls;
using Avalonia.Platform.Storage;

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
                new FilePickerFileType("Java 可执行文件")
                {
                    Patterns = new[] { "java.exe", "javaw.exe", "java" }
                }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}

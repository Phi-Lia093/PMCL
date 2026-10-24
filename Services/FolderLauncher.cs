using System.Diagnostics;
using PMCL.Localization;

namespace PMCL.Services;

/// <summary>
/// Opens a folder in the platform file manager. Shared by the version settings
/// page and the content pack groups so every "open folder" button behaves the
/// same way on Windows, macOS and Linux.
/// </summary>
public static class FolderLauncher
{
    public static void Open(string path, bool create)
    {
        if (create)
            Directory.CreateDirectory(path);

        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(L.Format("VS.Folder.NotFound", path));

        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true };

        Process.Start(startInfo);
    }
}

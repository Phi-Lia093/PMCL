using System.Text.Json;
using System.Text.Json.Serialization;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Loads and persists <see cref="LauncherSettings"/> as JSON.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly AppPaths _paths;

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
    }

    public LauncherSettings Load()
    {
        LauncherSettings? settings = null;

        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                var json = File.ReadAllText(_paths.SettingsFile);
                settings = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
            }
        }
        catch (Exception)
        {
            // A broken settings file must never prevent the launcher from starting.
            settings = null;
        }

        return Normalize(settings ?? new LauncherSettings());
    }

    public bool TrySave(LauncherSettings settings, out string? error)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
            var tempFile = _paths.SettingsFile + ".tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, _paths.SettingsFile, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void SaveSilently(LauncherSettings settings) => TrySave(settings, out _);

    private static LauncherSettings Normalize(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GameDirectory))
            settings.GameDirectory = AppPaths.DefaultGameDirectory;
        else
        {
            try
            {
                settings.GameDirectory = Path.GetFullPath(settings.GameDirectory);
            }
            catch (Exception)
            {
                settings.GameDirectory = AppPaths.DefaultGameDirectory;
            }
        }

        settings.JavaPath = settings.JavaPath?.Trim() ?? string.Empty;
        settings.OfflineUsername = string.IsNullOrWhiteSpace(settings.OfflineUsername)
            ? "Player"
            : settings.OfflineUsername.Trim();
        settings.LastVersion ??= string.Empty;
        settings.LastAccountKind ??= string.Empty;

        settings.MaximumRamMb = Math.Clamp(settings.MaximumRamMb, 512, 65536);
        settings.MinimumRamMb = Math.Clamp(settings.MinimumRamMb, 256, settings.MaximumRamMb);
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 854, 7680);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 480, 4320);

        return settings;
    }
}

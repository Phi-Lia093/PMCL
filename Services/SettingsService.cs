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
        string? json = null;

        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                json = File.ReadAllText(_paths.SettingsFile);
                settings = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
            }
        }
        catch (Exception)
        {
            // A broken settings file must never prevent the launcher from starting.
            settings = null;
        }

        settings ??= new LauncherSettings();
        MigrateLegacyLaunchSettings(json, settings);
        return Normalize(settings);
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

        settings.Language = string.IsNullOrWhiteSpace(settings.Language)
            ? "zh-CN"
            : settings.Language.Trim();
        settings.OfflineUsername = string.IsNullOrWhiteSpace(settings.OfflineUsername)
            ? "Player"
            : settings.OfflineUsername.Trim();
        settings.LastVersion ??= string.Empty;
        settings.LastAccountKind ??= string.Empty;
        settings.LastAccountId ??= string.Empty;
        if (!Enum.IsDefined(settings.DownloadSource))
            settings.DownloadSource = DownloadSourceKind.Official;
        if (!Enum.IsDefined(settings.CloseAction))
            settings.CloseAction = LauncherCloseAction.MinimizeToTray;
        if (!Enum.IsDefined(settings.OnGameLaunchAction))
            settings.OnGameLaunchAction = GameLaunchWindowAction.MinimizeToTray;
        settings.VersionSettings ??= new Dictionary<string, GameVersionSettings>();

        foreach (var versionName in settings.VersionSettings.Keys.ToList())
        {
            var versionSettings = settings.VersionSettings[versionName];
            if (versionSettings is null)
            {
                settings.VersionSettings[versionName] = new GameVersionSettings();
                continue;
            }

            NormalizeVersionSettings(versionSettings);
        }

        return settings;
    }

    private static void NormalizeVersionSettings(GameVersionSettings settings)
    {
        settings.JavaPath = settings.JavaPath?.Trim() ?? string.Empty;
        settings.OfflineUsername = settings.OfflineUsername?.Trim() ?? string.Empty;
        settings.ServerAddress = settings.ServerAddress?.Trim() ?? string.Empty;
        settings.JvmArguments = settings.JvmArguments?.Trim() ?? string.Empty;
        settings.GameArguments = settings.GameArguments?.Trim() ?? string.Empty;
        settings.PreLaunchCommand = settings.PreLaunchCommand?.Trim() ?? string.Empty;

        settings.MinimumRamMb = Math.Clamp(settings.MinimumRamMb, 256, 65536);
        settings.MaximumRamMb = Math.Clamp(settings.MaximumRamMb, settings.MinimumRamMb, 65536);
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 854, 7680);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 480, 4320);
        settings.ServerPort = Math.Clamp(settings.ServerPort, 1, 65535);

        if (!Enum.IsDefined(settings.LoginMode))
            settings.LoginMode = VersionLoginMode.CurrentAccount;
    }

    /// <summary>
    /// Moves the pre-0.2 global Java/RAM/window values into the last selected version
    /// so users do not lose their configuration after the per-version settings change.
    /// </summary>
    private static void MigrateLegacyLaunchSettings(string? json, LauncherSettings settings)
    {
        settings.VersionSettings ??= new Dictionary<string, GameVersionSettings>();

        if (string.IsNullOrWhiteSpace(json)
            || settings.VersionSettings is { Count: > 0 }
            || string.IsNullOrWhiteSpace(settings.LastVersion))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var found = false;
            var migrated = new GameVersionSettings();

            if (TryGetString(root, "javaPath", out var javaPath) && !string.IsNullOrWhiteSpace(javaPath))
            {
                migrated.JavaPath = javaPath;
                found = true;
            }

            if (TryGetInt32(root, "minimumRamMb", out var minimumRam))
            {
                migrated.MinimumRamMb = minimumRam;
                found = true;
            }

            if (TryGetInt32(root, "maximumRamMb", out var maximumRam))
            {
                migrated.MaximumRamMb = maximumRam;
                found = true;
            }

            if (TryGetInt32(root, "windowWidth", out var windowWidth))
            {
                migrated.WindowWidth = windowWidth;
                found = true;
            }

            if (TryGetInt32(root, "windowHeight", out var windowHeight))
            {
                migrated.WindowHeight = windowHeight;
                found = true;
            }

            if (root.TryGetProperty("fullScreen", out var fullScreen)
                && fullScreen.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                migrated.FullScreen = fullScreen.GetBoolean();
                found = true;
            }

            if (found)
                settings.VersionSettings[settings.LastVersion] = migrated;
        }
        catch
        {
            // Legacy migration is best-effort only.
        }
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt32(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }
}

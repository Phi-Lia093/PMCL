using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Detects the mod loader layer of installed version folders. New installations
/// write <c>pmcl-loader.json</c>; the name/json heuristics keep older Forge,
/// Fabric and OptiFine folders working as well.
/// </summary>
public static partial class ModLoaderDetector
{
    public const string MetadataFileName = "pmcl-loader.json";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [GeneratedRegex(@"^fabric-loader-(?<loader>.+)-(?<mc>\d+\.\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex FabricNameRegex();

    [GeneratedRegex(@"^(?<mc>.+?)-OptiFine_(?<loader>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OptiFineNameRegex();

    [GeneratedRegex(@"^neoforge-(?<loader>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NeoForgeNameRegex();

    [GeneratedRegex(@"^(?<mc>.+?)-OptiFine", RegexOptions.IgnoreCase)]
    private static partial Regex OptiFineBaseRegex();

    public static string GetMetadataPath(string versionDirectory)
        => Path.Combine(versionDirectory, MetadataFileName);

    public static ModLoaderMetadata ReadMetadata(string versionDirectory)
    {
        var path = GetMetadataPath(versionDirectory);
        if (!File.Exists(path))
            return new ModLoaderMetadata();

        try
        {
            return JsonSerializer.Deserialize<ModLoaderMetadata>(
                       File.ReadAllText(path),
                       MetadataJsonOptions)
                   ?? new ModLoaderMetadata();
        }
        catch
        {
            return new ModLoaderMetadata();
        }
    }

    public static void WriteMetadata(string versionDirectory, ModLoaderMetadata metadata)
    {
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            GetMetadataPath(versionDirectory),
            JsonSerializer.Serialize(metadata, MetadataJsonOptions));
    }

    public static ModLoaderMetadata Detect(string versionDirectory, string versionName)
    {
        var metadata = ReadMetadata(versionDirectory);
        var json = ReadVersionJson(versionDirectory, versionName);
        var inheritsFrom = json?.InheritsFrom;
        var baseVersion = !string.IsNullOrWhiteSpace(metadata.MinecraftVersion)
            ? metadata.MinecraftVersion
            : !string.IsNullOrWhiteSpace(inheritsFrom)
                ? inheritsFrom
                : versionName;

        if (metadata.Loader.IsModded())
        {
            metadata.MinecraftVersion = baseVersion;
            metadata.LoaderVersion = string.IsNullOrWhiteSpace(metadata.LoaderVersion)
                ? InferLoaderVersion(versionName, metadata.Loader)
                : metadata.LoaderVersion;
            return metadata;
        }

        var detected = DetectFromName(versionName, baseVersion);
        if (detected.Loader.IsModded())
            return detected;

        return new ModLoaderMetadata
        {
            Loader = ModLoaderKind.Vanilla,
            LoaderVersion = string.Empty,
            MinecraftVersion = versionName
        };
    }


    public static IReadOnlyList<InstalledLoaderProfile> GetInstalledLoaderProfiles(
        string gameDirectory,
        string baseVersionName)
    {
        try
        {
            var versionsDirectory = Path.Combine(Path.GetFullPath(gameDirectory), "versions");
            if (!Directory.Exists(versionsDirectory))
                return Array.Empty<InstalledLoaderProfile>();

            var profiles = new List<InstalledLoaderProfile>();

            foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
            {
                var versionName = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(versionName)
                    || !File.Exists(Path.Combine(directory, versionName + ".json")))
                {
                    continue;
                }

                var metadata = Detect(directory, versionName);
                if (!metadata.Loader.IsModded())
                    continue;

                if (!string.Equals(metadata.MinecraftVersion, baseVersionName, StringComparison.OrdinalIgnoreCase))
                    continue;

                profiles.Add(new InstalledLoaderProfile(
                    versionName,
                    metadata.Loader,
                    metadata.LoaderVersion,
                    metadata.MinecraftVersion));
            }

            return profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<InstalledLoaderProfile>();
        }
    }

    public static void UpdateBaseVersion(
        string versionsDirectory,
        string oldBaseVersion,
        string newBaseVersion)
    {
        if (string.Equals(oldBaseVersion, newBaseVersion, StringComparison.Ordinal))
            return;

        foreach (var jsonFile in Directory.EnumerateFiles(
                     versionsDirectory,
                     "*.json",
                     SearchOption.AllDirectories))
        {
            if (!string.Equals(
                    Path.GetFileName(jsonFile),
                    MetadataFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(File.ReadAllText(jsonFile)) is not JsonObject json)
                    continue;

                if (json["minecraftVersion"] is JsonValue value
                    && value.TryGetValue<string>(out var minecraftVersion)
                    && string.Equals(minecraftVersion, oldBaseVersion, StringComparison.Ordinal))
                {
                    json["minecraftVersion"] = newBaseVersion;
                    File.WriteAllText(jsonFile, json.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
                }
            }
            catch
            {
                // A malformed metadata file must not break version renaming.
            }
        }
    }


    private static ModLoaderMetadata DetectFromName(string versionName, string inheritsFrom)
    {
        var neoForge = NeoForgeNameRegex().Match(versionName);
        if (neoForge.Success)
        {
            var loaderVersion = neoForge.Groups["loader"].Value;
            return new ModLoaderMetadata
            {
                Loader = ModLoaderKind.NeoForge,
                LoaderVersion = loaderVersion,
                MinecraftVersion = string.IsNullOrWhiteSpace(inheritsFrom)
                    ? InferNeoForgeMinecraftVersion(loaderVersion)
                    : inheritsFrom
            };
        }

        var fabric = FabricNameRegex().Match(versionName);
        if (fabric.Success)
        {
            return new ModLoaderMetadata
            {
                Loader = ModLoaderKind.Fabric,
                LoaderVersion = fabric.Groups["loader"].Value,
                MinecraftVersion = string.IsNullOrWhiteSpace(inheritsFrom)
                    ? fabric.Groups["mc"].Value
                    : inheritsFrom
            };
        }

        var optiFine = OptiFineNameRegex().Match(versionName);
        if (optiFine.Success)
        {
            return new ModLoaderMetadata
            {
                Loader = ModLoaderKind.OptiFine,
                LoaderVersion = optiFine.Groups["loader"].Value,
                MinecraftVersion = string.IsNullOrWhiteSpace(inheritsFrom)
                    ? optiFine.Groups["mc"].Value
                    : inheritsFrom
            };
        }

        var forge = TryDetectForge(versionName, inheritsFrom);
        if (forge is not null)
            return forge;

        return new ModLoaderMetadata
        {
            Loader = ModLoaderKind.Vanilla,
            LoaderVersion = string.Empty,
            MinecraftVersion = string.IsNullOrWhiteSpace(inheritsFrom)
                ? versionName
                : inheritsFrom
        };
    }

    private static ModLoaderMetadata? TryDetectForge(string versionName, string inheritsFrom)
    {
        var lower = versionName.ToLowerInvariant();
        if (lower.Contains("neoforge", StringComparison.Ordinal))
            return null;

        var forgeIndex = lower.IndexOf("forge", StringComparison.Ordinal);
        if (forgeIndex < 0)
            return null;

        var baseVersion = inheritsFrom;
        if (string.IsNullOrWhiteSpace(baseVersion))
        {
            var optiFineBase = OptiFineBaseRegex().Match(versionName);
            if (optiFineBase.Success && !lower.Contains("optifine-forge", StringComparison.Ordinal))
                baseVersion = optiFineBase.Groups["mc"].Value;
        }

        if (string.IsNullOrWhiteSpace(baseVersion))
        {
            var dashIndex = versionName.IndexOf('-');
            if (dashIndex > 0)
                baseVersion = versionName[..dashIndex];
        }

        var loaderVersion = versionName[(forgeIndex + "forge".Length)..].Trim('-', ' ');
        if (string.IsNullOrWhiteSpace(loaderVersion))
            loaderVersion = "unknown";

        if (!string.IsNullOrWhiteSpace(baseVersion))
        {
            var suffix = "-" + baseVersion;
            if (loaderVersion.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                loaderVersion = loaderVersion[..^suffix.Length];
        }

        return new ModLoaderMetadata
        {
            Loader = ModLoaderKind.Forge,
            LoaderVersion = loaderVersion,
            MinecraftVersion = string.IsNullOrWhiteSpace(baseVersion) ? versionName : baseVersion
        };
    }

    private static string InferLoaderVersion(string versionName, ModLoaderKind kind)
    {
        var detected = DetectFromName(versionName, string.Empty);
        return detected.Loader == kind ? detected.LoaderVersion : string.Empty;
    }

    private static string InferNeoForgeMinecraftVersion(string loaderVersion)
    {
        var numbers = loaderVersion.Split('.');
        if (numbers.Length < 2
            || !int.TryParse(numbers[0], out var major)
            || !int.TryParse(numbers[1], out var minor))
        {
            return "unknown";
        }

        // NeoForge uses 20.2.x -> 1.20.2, 21.1.x -> 1.21.1, 21.0.x -> 1.21
        // and calendar releases such as 26.1.x -> 26.1.
        if (major >= 26)
            return minor == 0 ? $"{major}" : $"{major}.{minor}";

        if (major >= 20)
            return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";

        return "unknown";
    }

    private static VersionJsonInfo? ReadVersionJson(string versionDirectory, string versionName)
    {
        var jsonPath = Path.Combine(versionDirectory, versionName + ".json");
        if (!File.Exists(jsonPath))
            return null;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(jsonPath)) is not JsonObject json)
                return null;

            var id = json["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText)
                ? idText
                : versionName;
            var inheritsFrom = json["inheritsFrom"] is JsonValue parentValue
                               && parentValue.TryGetValue<string>(out var parentText)
                ? parentText
                : string.Empty;

            return new VersionJsonInfo(id, inheritsFrom);
        }
        catch
        {
            return null;
        }
    }

    private sealed record VersionJsonInfo(string Id, string InheritsFrom);
}

public sealed record InstalledLoaderProfile(
    string Name,
    ModLoaderKind Kind,
    string LoaderVersion,
    string BaseVersionName)
{
    public string DisplayName => string.IsNullOrWhiteSpace(LoaderVersion)
        ? Kind.ToDisplayName()
        : $"{Kind.ToDisplayName()} {Kind.FormatLoaderVersion(LoaderVersion)}";
}

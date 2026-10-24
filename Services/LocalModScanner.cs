using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Metadata read from a mod jar. The values come from <c>fabric.mod.json</c>,
/// <c>META-INF/neoforge.mods.toml</c>, <c>META-INF/mods.toml</c> or
/// <c>META-INF/MANIFEST.MF</c>, in that order of preference.
/// </summary>
public sealed record LocalModMetadata(
    string ModId,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<string> Authors,
    ModLoaderKind? Loader,
    IReadOnlyList<string> GameVersions)
{
    public string AuthorText => string.Join(", ", Authors);

    /// <summary>
    /// True when the mod declares support for the given Minecraft version. A mod
    /// without any declared version constraint is treated as compatible.
    /// </summary>
    public bool SupportsGameVersion(string minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion) || GameVersions.Count == 0)
            return true;

        return GameVersions.Any(constraint =>
            ModVersionRange.Matches(constraint, minecraftVersion.Trim()));
    }
}

/// <summary>
/// Reads loader metadata from mod jars without extracting them. Every failure
/// is reported as <c>null</c> so a single broken jar never breaks the mod list.
/// </summary>
public sealed class LocalModScanner
{
    private const string FabricMetadataPath = "fabric.mod.json";
    private const string NeoForgeMetadataPath = "META-INF/neoforge.mods.toml";
    private const string ForgeMetadataPath = "META-INF/mods.toml";
    private const string ManifestPath = "META-INF/MANIFEST.MF";

    public LocalModMetadata? ReadMetadata(string jarPath)
    {
        if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
            return null;

        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            return ReadFabric(archive)
                   ?? ReadToml(archive, NeoForgeMetadataPath, ModLoaderKind.NeoForge)
                   ?? ReadToml(archive, ForgeMetadataPath, ModLoaderKind.Forge)
                   ?? ReadManifest(archive);
        }
        catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// SHA-1 of the jar file, the hash Modrinth uses to identify mod files.
    /// </summary>
    public string ComputeSha1(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static LocalModMetadata? ReadFabric(ZipArchive archive)
    {
        var entry = FindEntry(archive, FabricMetadataPath);
        if (entry is null)
            return null;

        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var modId = GetString(root, "id");
        var name = GetString(root, "name");
        var version = GetString(root, "version");
        var description = GetString(root, "description");
        var authors = ReadAuthors(root, "authors");
        var gameVersions = new List<string>();

        if (root.TryGetProperty("depends", out var depends)
            && depends.ValueKind == JsonValueKind.Object
            && depends.TryGetProperty("minecraft", out var minecraft))
        {
            gameVersions.AddRange(ReadVersionConstraints(minecraft));
        }

        return new LocalModMetadata(
            modId,
            name.Length > 0 ? name : modId,
            version,
            description,
            authors,
            ModLoaderKind.Fabric,
            gameVersions);
    }

    private static LocalModMetadata? ReadToml(
        ZipArchive archive,
        string path,
        ModLoaderKind loader)
    {
        var entry = FindEntry(archive, path);
        if (entry is null)
            return null;

        using var reader = new StreamReader(
            entry.Open(),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var sections = TomlLite.Parse(reader.ReadToEnd());

        var mods = sections.FirstOrDefault(section =>
            string.Equals(section.Name, "mods", StringComparison.OrdinalIgnoreCase));
        if (mods is null)
            return null;

        var modId = mods.Get("modId");
        var displayName = mods.Get("displayName");
        var name = displayName.Length > 0 ? displayName : modId;
        var authors = SplitAuthors(mods.Get("authors"));
        var gameVersions = sections
            .Where(section => section.Name.StartsWith(
                "dependencies.",
                StringComparison.OrdinalIgnoreCase))
            .Where(section => section.Get("modId")
                .Equals("minecraft", StringComparison.OrdinalIgnoreCase))
            .Select(section =>
            {
                var range = section.Get("versionRange");
                return range.Length > 0 ? range : section.Get("version");
            })
            .Where(range => range.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modId.Length == 0 && name.Length == 0)
            return null;

        return new LocalModMetadata(
            modId,
            name,
            mods.Get("version"),
            mods.Get("description"),
            authors,
            loader,
            gameVersions);
    }

    private static LocalModMetadata? ReadManifest(ZipArchive archive)
    {
        var entry = FindEntry(archive, ManifestPath);
        if (entry is null)
            return null;

        using var reader = new StreamReader(
            entry.Open(),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var attributes = ReadManifestAttributes(reader.ReadToEnd());

        var name = FirstValue(
            attributes,
            "Implementation-Title",
            "Specification-Title",
            "Bundle-Name");
        if (name.Length == 0)
            return null;

        var version = FirstValue(
            attributes,
            "Implementation-Version",
            "Specification-Version",
            "Bundle-Version");
        var vendor = FirstValue(
            attributes,
            "Implementation-Vendor",
            "Specification-Vendor",
            "Bundle-Vendor");

        ModLoaderKind? loader = null;
        if (attributes.ContainsKey("FMLModType")
            || attributes.ContainsKey("FMLCorePlugin")
            || attributes.ContainsKey("FMLCorePluginContainsFMLMod"))
        {
            loader = ModLoaderKind.Forge;
        }

        return new LocalModMetadata(
            string.Empty,
            name,
            version,
            string.Empty,
            SplitAuthors(vendor),
            loader,
            Array.Empty<string>());
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
        => archive.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, path, StringComparison.OrdinalIgnoreCase));


    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? (property.GetString() ?? string.Empty).Trim()
            : string.Empty;

    private static IReadOnlyList<string> ReadAuthors(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var authors))
            return Array.Empty<string>();

        if (authors.ValueKind == JsonValueKind.String)
            return SplitAuthors(authors.GetString() ?? string.Empty);

        if (authors.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var result = new List<string>();
        foreach (var author in authors.EnumerateArray())
        {
            if (author.ValueKind == JsonValueKind.String)
            {
                result.AddRange(SplitAuthors(author.GetString() ?? string.Empty));
            }
            else if (author.ValueKind == JsonValueKind.Object)
            {
                var name = GetString(author, "name");
                if (name.Length > 0)
                    result.Add(name);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> ReadVersionConstraints(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = (element.GetString() ?? string.Empty).Trim();
            return value.Length == 0 ? Array.Empty<string>() : new[] { value };
        }

        if (element.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return element
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => (item.GetString() ?? string.Empty).Trim())
            .Where(value => value.Length > 0)
            .ToList();
    }

    private static IReadOnlyList<string> SplitAuthors(string value)
        => value
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(author => author.Trim())
            .Where(author => author.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FirstValue(
        IReadOnlyDictionary<string, string> attributes,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (attributes.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }


    private static Dictionary<string, string> ReadManifestAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                currentKey = null;
                continue;
            }

            if ((rawLine[0] == ' ' || rawLine[0] == '\t') && currentKey is not null)
            {
                attributes[currentKey] += rawLine.Trim();
                continue;
            }

            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = rawLine[..separator].Trim();
            if (key.Length == 0)
                continue;

            attributes[key] = rawLine[(separator + 1)..].Trim();
            currentKey = key;
        }

        return attributes;
    }

    /// <summary>
    /// Minimal TOML reader used for the Forge/NeoForge metadata files. It only
    /// understands table headers, <c>key = value</c> pairs, comments and the
    /// multiline string forms used by <c>description</c>.
    /// </summary>
    private static class TomlLite
    {
        public static IReadOnlyList<TomlSection> Parse(string text)
        {
            var sections = new List<TomlSection>();
            TomlSection? current = null;
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                if (line[0] == '[')
                {
                    var name = line.TrimStart('[').TrimEnd(']').Trim();
                    current = new TomlSection(name);
                    sections.Add(current);
                    continue;
                }

                if (current is null)
                {
                    current = new TomlSection(string.Empty);
                    sections.Add(current);
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (key.Length == 0)
                    continue;

                current.Values[key] = ReadValue(value, lines, ref index);
            }

            return sections;
        }

        private static string ReadValue(string value, string[] lines, ref int index)
        {
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal)
                || value.StartsWith("'''", StringComparison.Ordinal))
            {
                var delimiter = value[..3];
                var remainder = value[3..];
                if (remainder.Contains(delimiter, StringComparison.Ordinal))
                {
                    return remainder[..remainder.IndexOf(delimiter, StringComparison.Ordinal)]
                        .Trim();
                }

                var builder = new StringBuilder(remainder.Trim());
                while (++index < lines.Length)
                {
                    var line = lines[index];
                    if (line.Contains(delimiter, StringComparison.Ordinal))
                    {
                        builder.Append('\n').Append(
                            line[..line.IndexOf(delimiter, StringComparison.Ordinal)]);
                        break;
                    }

                    builder.Append('\n').Append(line);
                }

                return builder.ToString().Trim();
            }

            return Unquote(StripComment(value));
        }

        private static string StripComment(string value)
        {
            char quote = '\0';
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (quote != '\0')
                {
                    if (character == quote && (index == 0 || value[index - 1] != '\\'))
                        quote = '\0';
                    continue;
                }

                if (character is '"' or '\'')
                {
                    quote = character;
                    continue;
                }

                if (character == '#')
                    return value[..index];
            }

            return value;
        }

        private static string Unquote(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length >= 2
                && ((trimmed[0] == '"' && trimmed[^1] == '"')
                    || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            {
                return trimmed[1..^1].Trim();
            }

            return trimmed;
        }
    }

    private sealed class TomlSection
    {
        public TomlSection(string name)
        {
            Name = name.Trim('"', '\'', ' ');
        }

        public string Name { get; }

        public Dictionary<string, string> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string Get(string key)
            => Values.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }
}


/// <summary>
/// Matches the version constraints found in mod metadata (<c>&gt;=1.20.1</c>,
/// <c>[1.20.1,1.21)</c>, <c>1.20.1</c>, ...) against a Minecraft version.
/// </summary>
internal static class ModVersionRange
{
    public static bool Matches(string? constraint, string version)
    {
        if (string.IsNullOrWhiteSpace(constraint))
            return true;

        var trimmed = constraint.Trim();
        if (trimmed is "*" or "any" or "x")
            return true;

        if (trimmed.Contains(' '))
        {
            return trimmed
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .All(part => Matches(part, version));
        }

        if (trimmed[0] is '[' or '(')
            return MatchesRange(trimmed, version);

        if (trimmed.Contains(','))
        {
            return trimmed
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => Matches(part, version));
        }

        if (TryReadComparison(trimmed, out var comparison, out var bound))
            return MatchesComparison(comparison, bound, version);

        return Compare(version, trimmed) == 0;
    }

    private static bool MatchesRange(string range, string version)
    {
        var lowerInclusive = range[0] == '[';
        var upperInclusive = range[^1] == ']';
        var body = range[1..^1];
        var separator = body.IndexOf(',');

        if (separator < 0)
            return Matches(body.Trim(), version);

        var lower = body[..separator].Trim();
        var upper = body[(separator + 1)..].Trim();

        if (lower.Length > 0)
        {
            var result = Compare(version, lower);
            if (result < 0 || (!lowerInclusive && result == 0))
                return false;
        }

        if (upper.Length > 0)
        {
            var result = Compare(version, upper);
            if (result > 0 || (!upperInclusive && result == 0))
                return false;
        }

        return true;
    }

    private static bool TryReadComparison(
        string value,
        out string comparison,
        out string bound)
    {
        foreach (var prefix in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                comparison = prefix;
                bound = value[prefix.Length..].Trim();
                return bound.Length > 0;
            }
        }

        comparison = string.Empty;
        bound = value;
        return false;
    }

    private static bool MatchesComparison(string comparison, string bound, string version)
    {
        var result = Compare(version, bound);
        return comparison switch
        {
            ">=" => result >= 0,
            "<=" => result <= 0,
            ">" => result > 0,
            "<" => result < 0,
            _ => result == 0
        };
    }

    private static int Compare(string left, string right)
    {
        if (TryParseVersion(left, out var leftVersion)
            && TryParseVersion(right, out var rightVersion))
        {
            return CompareVersions(leftVersion, rightVersion);
        }

        return string.Compare(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var metadata = text.IndexOf('+');
        if (metadata >= 0)
            text = text[..metadata];

        return Version.TryParse(text, out version!);
    }

    private static int CompareVersions(Version left, Version right)
    {
        var leftParts = new[]
        {
            left.Major,
            left.Minor,
            Math.Max(left.Build, 0),
            Math.Max(left.Revision, 0)
        };
        var rightParts = new[]
        {
            right.Major,
            right.Minor,
            Math.Max(right.Build, 0),
            Math.Max(right.Revision, 0)
        };

        for (var index = 0; index < leftParts.Length; index++)
        {
            if (leftParts[index] != rightParts[index])
                return leftParts[index].CompareTo(rightParts[index]);
        }

        return 0;
    }
}


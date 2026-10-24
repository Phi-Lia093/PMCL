using System.Net;
using System.Text;
using System.Text.Json;
using PMCL.Localization;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>One project row returned by the Modrinth search endpoint.</summary>
public sealed record ModrinthSearchHit(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    long Downloads,
    string IconUrl,
    IReadOnlyList<string> Versions,
    IReadOnlyList<string> Categories)
{
    public string DownloadsText => Downloads switch
    {
        >= 1_000_000 => L.Format("MR.Downloads", $"{Downloads / 1_000_000d:0.0}M"),
        >= 1_000 => L.Format("MR.Downloads", $"{Downloads / 1_000d:0.0}K"),
        _ => L.Format("MR.Downloads", Downloads)
    };

    public string VersionSummary => Versions.Count == 0
        ? L.Tr("MR.VersionsUnknown")
        : string.Join(" / ", Versions.Take(4)) + (Versions.Count > 4 ? " …" : string.Empty);

    public string AuthorText => string.IsNullOrWhiteSpace(Author)
        ? L.Tr("MR.AuthorUnknown")
        : L.Format("MR.Author", Author);
}

public sealed record ModrinthSearchResult(
    IReadOnlyList<ModrinthSearchHit> Hits,
    int TotalHits,
    int Offset,
    int Limit)
{
    public static readonly ModrinthSearchResult Empty = new(
        Array.Empty<ModrinthSearchHit>(),
        0,
        0,
        0);
}

public sealed record ModrinthFile(
    string Url,
    string FileName,
    long Size,
    bool Primary);

public sealed record ModrinthDependency(
    string? VersionId,
    string? ProjectId,
    string? FileName,
    string DependencyType);

public sealed record ModrinthVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    string VersionType,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    IReadOnlyList<ModrinthFile> Files,
    IReadOnlyList<ModrinthDependency> Dependencies)
{
    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(file => file.Primary && !string.IsNullOrWhiteSpace(file.Url))
        ?? Files.FirstOrDefault(file => !string.IsNullOrWhiteSpace(file.Url));
}

public sealed record ModrinthProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string IconUrl,
    long Downloads,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Slug : Title;

    public string DisplaysText => Downloads switch
    {
        >= 1_000_000 => L.Format("MR.Downloads", $"{Downloads / 1_000_000d:0.0}M"),
        >= 1_000 => L.Format("MR.Downloads", $"{Downloads / 1_000d:0.0}K"),
        _ => L.Format("MR.Downloads", Downloads)
    };
}

public sealed record ModInstallReport(
    int InstalledFiles,
    int SkippedFiles,
    IReadOnlyList<string> InstalledFileNames,
    IReadOnlyList<string> Warnings)
{
    public static readonly ModInstallReport Empty = new(
        0,
        0,
        Array.Empty<string>(),
        Array.Empty<string>());

    public bool HasWarnings => Warnings.Count > 0;
}

/// <summary>
/// Modrinth API client. Search results are filtered server-side by the exact
/// Minecraft version and mod loader of the selected version, and installing a
/// project also installs its required dependencies recursively.
/// </summary>
public sealed class ModrinthService : IDisposable
{
    public const string ApiBase = "https://api.modrinth.com/v2";

    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;
    private readonly bool _ownsClients;

    public ModrinthService()
    {
        _apiClient = CreateHttpClient(TimeSpan.FromSeconds(30));
        _downloadClient = CreateHttpClient(TimeSpan.FromMinutes(15));
        _ownsClients = true;
    }

    /// <summary>
    /// Creates a service with caller-provided clients (used by the headless
    /// harness with a fake HTTP handler).
    /// </summary>
    public ModrinthService(HttpClient apiClient, HttpClient downloadClient)
    {
        _apiClient = apiClient;
        _downloadClient = downloadClient;
        _ownsClients = false;
    }

    /// <summary>
    /// Modrinth only exposes mods for loaders that implement the Fabric/Forge
    /// mod format. Vanilla and OptiFine cannot load these mods.
    /// </summary>
    public static bool SupportsLoader(ModLoaderKind kind) => GetLoaderName(kind) is not null;

    public static string? GetLoaderName(ModLoaderKind kind) => kind switch
    {
        ModLoaderKind.Fabric => "fabric",
        ModLoaderKind.Forge => "forge",
        ModLoaderKind.NeoForge => "neoforge",
        _ => null
    };

    /// <summary>
    /// Facets sent to <c>/v2/search</c>: mods only, exact game version and the
    /// selected loader category.
    /// </summary>
    public static string BuildFacetJson(
        string minecraftVersion,
        ModLoaderKind loaderKind,
        string? category = null)
    {
        var loader = GetLoaderName(loaderKind)
            ?? throw new InvalidOperationException(L.Tr("MR.NoLoaderSupport"));

        var facets = new List<string[]>
        {
            new[] { "project_type:mod" },
            new[] { "versions:" + minecraftVersion.Trim() },
            new[] { "categories:" + loader },
            new[] { "client_side:required", "client_side:optional" }
        };

        if (!string.IsNullOrWhiteSpace(category))
            facets.Add(new[] { "categories:" + category.Trim() });

        return JsonSerializer.Serialize(facets);
    }

    /// <summary>
    /// Facets for the content searches: exact project type (resource pack,
    /// shader or data pack) and the Minecraft version of the selected profile.
    /// Modrinth matches data pack projects by every type they support, so a mod
    /// that also ships a data pack still shows up in the data pack search.
    /// </summary>
    public static string BuildContentFacetJson(ContentPackKind kind, string minecraftVersion)
    {
        var facets = new List<string[]>
        {
            new[] { "project_type:" + kind.GetModrinthProjectType() },
            new[] { "versions:" + minecraftVersion.Trim() }
        };

        return JsonSerializer.Serialize(facets);
    }

    /// <summary>
    /// Searches Modrinth for mods compatible with the version and loader.
    /// An empty query returns the most relevant results. The optional category
    /// uses Modrinth's own category slugs (technology, magic, utility, ...).
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string minecraftVersion,
        ModLoaderKind loaderKind,
        string query,
        int offset = 0,
        int limit = 20,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        var facets = BuildFacetJson(minecraftVersion, loaderKind, category);
        return await SearchCoreAsync(facets, query, offset, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Searches Modrinth for resource packs, shaders or data packs that support
    /// the given Minecraft version.
    /// </summary>
    public async Task<ModrinthSearchResult> SearchContentAsync(
        ContentPackKind kind,
        string minecraftVersion,
        string query,
        int offset = 0,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var facets = BuildContentFacetJson(kind, minecraftVersion);
        return await SearchCoreAsync(facets, query, offset, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ModrinthSearchResult> SearchCoreAsync(
        string facets,
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var url =
            $"{ApiBase}/search?query={Uri.EscapeDataString(query?.Trim() ?? string.Empty)}" +
            $"&limit={Math.Clamp(limit, 1, 50)}&offset={Math.Max(0, offset)}&index=relevance" +
            $"&facets={Uri.EscapeDataString(facets)}";

        var json = await _apiClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseSearchResult(json);
    }

    /// <summary>
    /// Installs the newest compatible file of <paramref name="projectId"/> plus
    /// every required dependency into <paramref name="modsDirectory"/>.
    /// Existing files with the same name are never overwritten.
    /// </summary>
    public async Task<ModInstallReport> InstallProjectAsync(
        string projectId,
        string modsDirectory,
        string minecraftVersion,
        ModLoaderKind loaderKind,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var loader = GetLoaderName(loaderKind)
            ?? throw new InvalidOperationException(L.Tr("MR.VersionUnsupported"));

        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(L.Tr("MR.MissingProjectId"));

        Directory.CreateDirectory(modsDirectory);

        var installedFiles = new List<string>();
        var warnings = new List<string>();
        var skipped = 0;
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await InstallProjectCoreAsync(projectId, isDependency: false)
            .ConfigureAwait(false);

        return new ModInstallReport(
            installedFiles.Count,
            skipped,
            installedFiles,
            warnings);

        async Task InstallProjectCoreAsync(string targetProjectId, bool isDependency)
        {
            if (!visitedProjects.Add(targetProjectId))
                return;

            progress?.Report(L.Format("MR.QueryingVersions", targetProjectId));
            var version = await GetCompatibleVersionAsync(
                    targetProjectId,
                    minecraftVersion,
                    loader,
                    cancellationToken)
                .ConfigureAwait(false);

            if (version is null)
            {
                if (isDependency)
                {
                    warnings.Add(L.Format(
                        "MR.DependencyNoCompatible", targetProjectId, minecraftVersion, loader));
                    return;
                }

                throw new InvalidOperationException(
                    L.Format("MR.NoCompatible", minecraftVersion, loader));
            }

            await InstallVersionCoreAsync(version)
                .ConfigureAwait(false);
        }

        async Task InstallVersionCoreAsync(ModrinthVersion version)
        {
            if (!visitedVersions.Add(version.Id))
                return;

            var file = version.PrimaryFile;
            if (file is null)
            {
                warnings.Add(L.Format("MR.NoDownloadableFile", version.Name));
                return;
            }

            progress?.Report(L.Format("MR.Downloading", version.Name));
            if (await DownloadFileAsync(
                    file,
                    modsDirectory,
                    new[] { ".jar" },
                    cancellationToken)
                .ConfigureAwait(false))
            {
                installedFiles.Add(file.FileName);
            }
            else
            {
                skipped++;
            }

            foreach (var dependency in version.Dependencies)
            {
                if (!string.Equals(dependency.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(dependency.VersionId))
                {
                    var dependencyVersion = await GetVersionAsync(
                            dependency.VersionId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (dependencyVersion is null)
                    {
                        warnings.Add(L.Format(
                            "MR.DependencyFetchFailed",
                            dependency.ProjectId ?? dependency.VersionId));
                        continue;
                    }

                    if (IsCompatible(dependencyVersion, minecraftVersion, loader))
                    {
                        await InstallVersionCoreAsync(dependencyVersion).ConfigureAwait(false);
                        continue;
                    }

                    // The pinned file is for another loader/version; fall back
                    // to the newest compatible file of the dependency project.
                    if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                    {
                        await InstallProjectCoreAsync(dependency.ProjectId, isDependency: true)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        warnings.Add(L.Format("MR.DependencyIncompatible", dependencyVersion.Name));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(dependency.ProjectId))
                {
                    await InstallProjectCoreAsync(dependency.ProjectId, isDependency: true)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Fetches a single project by id or slug. Returns <c>null</c> when Modrinth
    /// does not know the project; other HTTP failures are propagated.
    /// </summary>
    public async Task<ModrinthProject?> GetProjectAsync(
        string idOrSlug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idOrSlug))
            return null;

        var url = $"{ApiBase}/project/{Uri.EscapeDataString(idOrSlug.Trim())}";
        try
        {
            var json = await _apiClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return ParseProject(json);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks a local file up by its SHA-1 hash. Returns the version that owns
    /// the exact file, or <c>null</c> when Modrinth has never seen it.
    /// </summary>
    public async Task<ModrinthVersion?> GetVersionByHashAsync(
        string sha1,
        CancellationToken cancellationToken = default)
    {
        var hash = (sha1 ?? string.Empty).Trim();
        if (hash.Length != 40)
            return null;

        var url = $"{ApiBase}/version_file/{hash.ToLowerInvariant()}";
        try
        {
            var json = await _apiClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return ParseVersion(json);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the project whose slug or title matches <paramref name="query"/>
    /// exactly (case and separators ignored). Used as a fallback when a local
    /// jar carries a Modrinth project id but its hash is unknown.
    /// </summary>
    public async Task<ModrinthSearchHit?> FindProjectAsync(
        string query,
        string minecraftVersion,
        ModLoaderKind loaderKind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var key = NormalizeProjectKey(query);
        if (key.Length == 0)
            return null;

        var result = await SearchAsync(
                minecraftVersion,
                loaderKind,
                query.Trim(),
                offset: 0,
                limit: 5,
                category: null,
                cancellationToken)
            .ConfigureAwait(false);

        return result.Hits.FirstOrDefault(hit =>
            string.Equals(NormalizeProjectKey(hit.Slug), key, StringComparison.Ordinal)
            || string.Equals(NormalizeProjectKey(hit.Title), key, StringComparison.Ordinal));
    }

    private static string NormalizeProjectKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private async Task<ModrinthVersion?> GetCompatibleVersionAsync(
        string projectId,
        string minecraftVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        var gameVersions = Uri.EscapeDataString(
            JsonSerializer.Serialize(new[] { minecraftVersion }));

        var url =
            $"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version" +
            $"?game_versions={gameVersions}";

        if (!string.IsNullOrWhiteSpace(loader))
        {
            var loaders = Uri.EscapeDataString(
                JsonSerializer.Serialize(new[] { loader }));
            url += $"&loaders={loaders}";
        }

        var json = await _apiClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var versions = ParseVersions(json);
        return versions.FirstOrDefault(version =>
                   IsCompatible(version, minecraftVersion, loader) && version.PrimaryFile is not null)
               ?? versions.FirstOrDefault(version =>
                   IsCompatible(version, minecraftVersion, loader));
    }

    /// <summary>
    /// Resolves the newest file of a project that runs on the given game
    /// version and loader. Returns <c>null</c> when nothing is compatible.
    /// </summary>
    public async Task<ModrinthVersion?> GetLatestCompatibleVersionAsync(
        string projectId,
        string minecraftVersion,
        ModLoaderKind loaderKind,
        CancellationToken cancellationToken = default)
    {
        var loader = GetLoaderName(loaderKind);
        if (loader is null || string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(minecraftVersion))
            return null;

        try
        {
            return await GetCompatibleVersionAsync(
                    projectId.Trim(),
                    minecraftVersion.Trim(),
                    loader,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the newest file of a resource pack, shader or data pack project
    /// that runs on the given Minecraft version. Data packs are additionally
    /// restricted to datapack files, so a project that also ships a mod jar
    /// never installs the wrong artifact.
    /// </summary>
    public async Task<ModrinthVersion?> GetLatestContentVersionAsync(
        string projectId,
        string minecraftVersion,
        ContentPackKind kind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(minecraftVersion))
            return null;

        try
        {
            return await GetCompatibleVersionAsync(
                    projectId.Trim(),
                    minecraftVersion.Trim(),
                    kind.GetModrinthLoader(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Installs the newest compatible file of a resource pack, shader or data
    /// pack project into <paramref name="directory"/>. Content packs have no
    /// loader dependency graph, so only the project itself is downloaded.
    /// </summary>
    public async Task<ModInstallReport> InstallContentProjectAsync(
        ContentPackKind kind,
        string projectId,
        string directory,
        string minecraftVersion,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(L.Tr("MR.MissingProjectId"));

        Directory.CreateDirectory(directory);

        progress?.Report(L.Format("MR.QueryingVersions", projectId));
        var version = await GetLatestContentVersionAsync(
                projectId,
                minecraftVersion,
                kind,
                cancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            throw new InvalidOperationException(
                L.Format("MR.NoCompatibleContent", minecraftVersion, kind.ToDisplayName()));
        }

        // Some projects ship their data pack as a plugin jar. Refusing anything
        // that is not a zip keeps the folder free of files the game ignores.
        var file = version.PrimaryFile
            ?? throw new InvalidOperationException(L.Format("MR.NoDownloadableFile", version.Name));
        var extension = Path.GetExtension(Path.GetFileName(file.FileName));
        if (extension.Length > 0
            && !kind.GetFileExtensions().Any(candidate =>
                candidate.Equals(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                L.Format("MR.NoCompatibleContent", minecraftVersion, kind.ToDisplayName()));
        }

        progress?.Report(L.Format("MR.Downloading", version.Name));
        var installedPath = await InstallContentVersionFileAsync(
                kind,
                version,
                directory,
                replaceFilePath: null,
                cancellationToken)
            .ConfigureAwait(false);

        return installedPath is null
            ? new ModInstallReport(0, 1, Array.Empty<string>(), Array.Empty<string>())
            : new ModInstallReport(
                1,
                0,
                new[] { Path.GetFileName(installedPath) },
                Array.Empty<string>());
    }

    /// <summary>
    /// Downloads the primary file of <paramref name="version"/> into
    /// <paramref name="modsDirectory"/>. When <paramref name="replaceFilePath"/>
    /// points at an existing jar the old file is removed after the new one has
    /// been downloaded, which is how in-place updates avoid a half-written mods
    /// folder. Returns the installed path, or <c>null</c> when the target file
    /// already exists and nothing needed replacing.
    /// </summary>
    public Task<string?> InstallVersionFileAsync(
        ModrinthVersion version,
        string directory,
        string? replaceFilePath = null,
        CancellationToken cancellationToken = default)
        => InstallVersionFileCoreAsync(
            version,
            directory,
            replaceFilePath,
            new[] { ".jar" },
            cancellationToken);

    /// <summary>
    /// Downloads the primary file of a content pack version. The archive keeps
    /// its original extension; a download without one falls back to the first
    /// extension the content kind accepts.
    /// </summary>
    public Task<string?> InstallContentVersionFileAsync(
        ContentPackKind kind,
        ModrinthVersion version,
        string directory,
        string? replaceFilePath = null,
        CancellationToken cancellationToken = default)
        => InstallVersionFileCoreAsync(
            version,
            directory,
            replaceFilePath,
            kind.GetFileExtensions(),
            cancellationToken);

    private async Task<string?> InstallVersionFileCoreAsync(
        ModrinthVersion version,
        string directory,
        string? replaceFilePath,
        IReadOnlyList<string> allowedExtensions,
        CancellationToken cancellationToken)
    {
        var file = version.PrimaryFile
            ?? throw new InvalidOperationException(L.Format("MR.NoDownloadableFile", version.Name));

        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = version.ProjectId + "-" + version.VersionNumber;
        if (!allowedExtensions.Any(extension =>
                fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            fileName += allowedExtensions[0];
        }

        fileName = SanitizeFileName(fileName);

        var targetPath = Path.Combine(directory, fileName);
        var replacing = !string.IsNullOrWhiteSpace(replaceFilePath)
                        && !string.Equals(replaceFilePath, targetPath, StringComparison.OrdinalIgnoreCase);

        if (!replacing && File.Exists(targetPath))
            return null;

        var tempPath = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".download");
        try
        {
            using var response = await _downloadClient.GetAsync(
                    file.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = File.Create(tempPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (replacing && File.Exists(replaceFilePath))
                File.Delete(replaceFilePath!);

            File.Move(tempPath, targetPath, overwrite: true);
            return targetPath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // A locked temporary file is harmless.
            }
        }
    }

    private async Task<ModrinthVersion?> GetVersionAsync(
        string versionId,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/version/{Uri.EscapeDataString(versionId)}";
        var json = await _apiClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        return ParseVersion(json);
    }

    private async Task<bool> DownloadFileAsync(
        ModrinthFile file,
        string directory,
        IReadOnlyList<string> allowedExtensions,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "content-" + Guid.NewGuid().ToString("N");
        if (!allowedExtensions.Any(extension =>
                fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            fileName += allowedExtensions[0];
        }

        fileName = SanitizeFileName(fileName);

        var targetPath = Path.Combine(directory, fileName);
        if (File.Exists(targetPath))
            return false;

        var tempPath = targetPath + ".download";
        try
        {
            using var response = await _downloadClient.GetAsync(
                    file.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content
                             .ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = File.Create(tempPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            // Another download may have created the same file while this one
            // was running; keep the existing copy in that case.
            if (File.Exists(targetPath))
                return false;

            File.Move(tempPath, targetPath);
            return true;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // A locked temporary file is harmless.
            }
        }
    }

    internal static bool IsCompatible(ModrinthVersion version, string minecraftVersion, string? loader)
        => (version.GameVersions.Count == 0
            || version.GameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase))
           && (string.IsNullOrWhiteSpace(loader)
               || version.Loaders.Count == 0
               || version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase));

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = fileName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray();
        return new string(characters);
    }

    public void Dispose()
    {
        if (!_ownsClients)
            return;

        _apiClient.Dispose();
        _downloadClient.Dispose();
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient
        {
            Timeout = timeout
        };

        // Modrinth requires a descriptive User-Agent for API requests.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PMCL/0.1.0 (Minecraft launcher)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static ModrinthSearchResult ParseSearchResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var hits = new List<ModrinthSearchHit>();

        if (root.TryGetProperty("hits", out var hitsElement)
            && hitsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hitsElement.EnumerateArray())
            {
                hits.Add(new ModrinthSearchHit(
                    GetString(hit, "project_id"),
                    GetString(hit, "slug"),
                    GetString(hit, "title"),
                    GetString(hit, "description"),
                    GetString(hit, "author"),
                    GetInt64(hit, "downloads"),
                    GetString(hit, "icon_url"),
                    GetStringArray(hit, "versions"),
                    GetStringArray(hit, "categories")));
            }
        }

        return new ModrinthSearchResult(
            hits,
            (int)GetInt64(root, "total_hits"),
            (int)GetInt64(root, "offset"),
            (int)GetInt64(root, "limit"));
    }

    private static IReadOnlyList<ModrinthVersion> ParseVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<ModrinthVersion>();

        return document.RootElement
            .EnumerateArray()
            .Select(ParseVersionElement)
            .OfType<ModrinthVersion>()
            .ToList();
    }

    private static ModrinthVersion? ParseVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        return ParseVersionElement(document.RootElement);
    }

    private static ModrinthVersion? ParseVersionElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(element, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var files = new List<ModrinthFile>();
        if (element.TryGetProperty("files", out var filesElement)
            && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                var url = GetString(file, "url");
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                files.Add(new ModrinthFile(
                    url,
                    GetString(file, "filename"),
                    GetInt64(file, "size"),
                    GetBool(file, "primary")));
            }
        }

        var dependencies = new List<ModrinthDependency>();
        if (element.TryGetProperty("dependencies", out var dependenciesElement)
            && dependenciesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var dependency in dependenciesElement.EnumerateArray())
            {
                var versionId = GetStringOrNull(dependency, "version_id");
                var projectId = GetStringOrNull(dependency, "project_id");
                if (string.IsNullOrWhiteSpace(versionId) && string.IsNullOrWhiteSpace(projectId))
                    continue;

                dependencies.Add(new ModrinthDependency(
                    versionId,
                    projectId,
                    GetStringOrNull(dependency, "file_name"),
                    GetString(dependency, "dependency_type")));
            }
        }

        return new ModrinthVersion(
            id,
            GetString(element, "project_id"),
            GetString(element, "name"),
            GetString(element, "version_number"),
            GetString(element, "version_type"),
            GetStringArray(element, "game_versions"),
            GetStringArray(element, "loaders"),
            files,
            dependencies);
    }

    private static ModrinthProject? ParseProject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var id = GetString(root, "id");
            if (id.Length == 0)
                return null;

            return new ModrinthProject(
                id,
                GetString(root, "slug"),
                GetString(root, "title"),
                GetString(root, "description"),
                GetString(root, "icon_url"),
                GetInt64(root, "downloads"),
                GetStringArray(root, "categories"),
                GetStringArray(root, "game_versions"),
                GetStringArray(root, "loaders"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
        => GetStringOrNull(element, propertyName) ?? string.Empty;

    private static string? GetStringOrNull(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long GetInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt64(out var value)
            ? value
            : 0;

    private static bool GetBool(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
    }
}

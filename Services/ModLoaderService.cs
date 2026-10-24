using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CmlLib.Core;
using CmlLib.Core.Installers;
using PMCL.Models;
using PMCL.Localization;

namespace PMCL.Services;

public sealed record ModLoaderVersionOption(
    string Version,
    string DisplayName,
    string Note,
    bool IsRecommended = false);

public sealed record ModLoaderCatalogEntry(
    ModLoaderKind Kind,
    string DisplayName,
    string Description,
    IReadOnlyList<ModLoaderVersionOption> Versions,
    string? Error = null)
{
    public bool IsAvailable => Versions.Count > 0;
}

public sealed record ModLoaderInstallResult(
    string VersionName,
    ModLoaderKind Kind,
    string LoaderVersion,
    string MinecraftVersion);

public sealed record ModLoaderSelection(
    ModLoaderKind Kind,
    string? LoaderVersion,
    string JavaPath,
    IReadOnlyList<string> ExistingLoaderNames);

/// <summary>
/// Discovers and installs Fabric, Forge, NeoForge and OptiFine layers for a
/// vanilla Minecraft version. Official metadata and files are used by default;
/// BMCLAPI is an optional accelerated source with automatic official fallback.
/// </summary>
public sealed class ModLoaderService
{
    private const string FabricMetaOfficial = DownloadSourceRouter.FabricMetaOfficial;
    private const string FabricMetaMirror = DownloadSourceRouter.BmclApiBase + "/fabric-meta";
    private const string BmclApiBase = DownloadSourceRouter.BmclApiBase;
    private const string BmclMirrorBase = DownloadSourceRouter.BmclMavenBase;
    private const string BmclMirrorFallback = DownloadSourceRouter.BmclMavenFallback;
    private const string ForgeMavenBase = DownloadSourceRouter.ForgeMavenBase;
    private const string NeoForgeMavenBase = DownloadSourceRouter.NeoForgeMavenBase;
    private const string OptiFineBase = "https://optifine.net";

    private readonly MinecraftService _minecraft;
    private readonly HttpClient _catalogClient;
    private readonly HttpClient _downloadClient;
    private readonly Dictionary<string, CachedCatalog> _catalogCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public ModLoaderService(MinecraftService minecraft)
    {
        _minecraft = minecraft;
        _catalogClient = DownloadSourceRouter.CreateHttpClient(
            () => _minecraft.DownloadSource,
            TimeSpan.FromSeconds(25));
        _downloadClient = DownloadSourceRouter.CreateHttpClient(
            () => _minecraft.DownloadSource,
            TimeSpan.FromMinutes(15));
    }

    public async Task<IReadOnlyList<ModLoaderCatalogEntry>> GetCatalogAsync(
        string gameDirectory,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var cacheKey =
            $"{Path.GetFullPath(gameDirectory)}|{minecraftVersion}|{_minecraft.DownloadSource}";
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalogCache.TryGetValue(cacheKey, out var cached))
            {
                var ttl = cached.Entries.All(entry => string.IsNullOrWhiteSpace(entry.Error))
                    ? TimeSpan.FromMinutes(10)
                    : TimeSpan.FromSeconds(30);
                if (DateTimeOffset.UtcNow - cached.CreatedAt < ttl)
                    return cached.Entries;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        var loaders = new[]
        {
            LoadVanillaAsync(minecraftVersion, cancellationToken),
            LoadFabricAsync(minecraftVersion, cancellationToken),
            LoadForgeAsync(minecraftVersion, cancellationToken),
            LoadNeoForgeAsync(minecraftVersion, cancellationToken),
            LoadOptiFineAsync(minecraftVersion, cancellationToken)
        };

        var entries = await Task.WhenAll(loaders).ConfigureAwait(false);

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _catalogCache[cacheKey] = new CachedCatalog(DateTimeOffset.UtcNow, entries);
        }
        finally
        {
            _cacheLock.Release();
        }

        return entries;
    }

    public IReadOnlyList<InstalledLoaderProfile> GetExistingLoaders(
        string gameDirectory,
        string baseVersionName)
        => _minecraft.GetInstalledLoaderProfiles(gameDirectory, baseVersionName);

    public static string GetVersionName(
        string minecraftVersion,
        ModLoaderKind kind,
        string? loaderVersion)
        => kind switch
        {
            ModLoaderKind.Vanilla => minecraftVersion,
            ModLoaderKind.Fabric =>
                $"fabric-loader-{RequireLoaderVersion(loaderVersion)}-{minecraftVersion}",
            ModLoaderKind.Forge =>
                $"{minecraftVersion}-forge-{RequireLoaderVersion(loaderVersion)}",
            ModLoaderKind.NeoForge => $"neoforge-{RequireLoaderVersion(loaderVersion)}",
            ModLoaderKind.OptiFine =>
                $"{minecraftVersion}-OptiFine_{NormalizeOptiFineVersion(RequireLoaderVersion(loaderVersion))}",
            _ => minecraftVersion
        };

    public static string NormalizeOptiFineVersion(string loaderVersion)
        => loaderVersion.Replace('|', '_');

    private static string RequireLoaderVersion(string? loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidOperationException(L.Tr("ML.SelectLoaderVersion"));
        return loaderVersion.Trim();
    }

    public async Task<ModLoaderInstallResult> InstallAsync(
        string gameDirectory,
        string minecraftVersion,
        ModLoaderKind kind,
        string? loaderVersion,
        string javaPath,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress = null,
        IProgress<ByteProgress>? byteProgress = null,
        IProgress<string>? installerOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new InvalidOperationException(L.Tr("ML.MinecraftVersionRequired"));

        var versionName = GetVersionName(minecraftVersion, kind, loaderVersion);
        MinecraftService.ValidateVersionName(versionName);
        var existed = _minecraft.IsInstalled(gameDirectory, versionName);

        try
        {
            switch (kind)
            {
                case ModLoaderKind.Vanilla:
                    await _minecraft.InstallAsync(
                            gameDirectory,
                            minecraftVersion,
                            fileProgress,
                            byteProgress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return new ModLoaderInstallResult(versionName, kind, string.Empty, minecraftVersion);

                case ModLoaderKind.Fabric:
                    await InstallFabricAsync(
                            gameDirectory,
                            minecraftVersion,
                            RequireLoaderVersion(loaderVersion),
                            versionName,
                            fileProgress,
                            byteProgress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ModLoaderKind.Forge:
                    await InstallForgeAsync(
                            gameDirectory,
                            minecraftVersion,
                            RequireLoaderVersion(loaderVersion),
                            versionName,
                            javaPath,
                            fileProgress,
                            byteProgress,
                            installerOutput,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ModLoaderKind.NeoForge:
                    await InstallNeoForgeAsync(
                            gameDirectory,
                            minecraftVersion,
                            RequireLoaderVersion(loaderVersion),
                            versionName,
                            javaPath,
                            fileProgress,
                            byteProgress,
                            installerOutput,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case ModLoaderKind.OptiFine:
                    await InstallOptiFineAsync(
                            gameDirectory,
                            minecraftVersion,
                            RequireLoaderVersion(loaderVersion),
                            versionName,
                            javaPath,
                            byteProgress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, L.Tr("ML.UnknownKind"));
            }

            _minecraft.WriteLoaderMetadata(
                gameDirectory,
                versionName,
                kind,
                loaderVersion ?? string.Empty,
                minecraftVersion);

            return new ModLoaderInstallResult(
                versionName,
                kind,
                loaderVersion ?? string.Empty,
                minecraftVersion);
        }
        catch
        {
            if (!existed)
                TryDeleteVersion(gameDirectory, versionName);
            throw;
        }
    }


    private async Task InstallFabricAsync(
        string gameDirectory,
        string minecraftVersion,
        string loaderVersion,
        string versionName,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken)
    {
        var relative = $"/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/" +
                       $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var json = await GetStringWithFallbackAsync(
                GetFabricMetaUrls(relative),
                cancellationToken)
            .ConfigureAwait(false);

        if (JsonNode.Parse(json) is not JsonObject profile)
            throw new InvalidDataException(L.Tr("ML.FabricInvalidProfile"));

        profile["id"] = versionName;
        if (_minecraft.DownloadSource == DownloadSourceKind.BmclApi)
            RewriteFabricLibraryUrls(profile);
        var profileJson = profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        await _minecraft.InstallFabricProfileAsync(
                gameDirectory,
                versionName,
                profileJson,
                fileProgress,
                byteProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task InstallForgeAsync(
        string gameDirectory,
        string minecraftVersion,
        string loaderVersion,
        string versionName,
        string javaPath,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress,
        IProgress<string>? installerOutput,
        CancellationToken cancellationToken)
    {
        var coordinates = $"{minecraftVersion}-{loaderVersion}";
        var officialUrl =
            $"{ForgeMavenBase}/net/minecraftforge/forge/{coordinates}/forge-{coordinates}-installer.jar";
        var mirrorUrl =
            $"{BmclMirrorBase}/net/minecraftforge/forge/{coordinates}/forge-{coordinates}-installer.jar";
        var mirrorFallbackUrl =
            $"{BmclMirrorFallback}/net/minecraftforge/forge/{coordinates}/forge-{coordinates}-installer.jar";
        var embeddedJar = string.Equals(minecraftVersion, "1.12.2", StringComparison.Ordinal)
            ? $"maven/net/minecraftforge/forge/{coordinates}/forge-{coordinates}.jar"
            : null;

        var tempDirectory = CreateTempDirectory("pmcl-forge-");
        var localInstaller = Path.Combine(tempDirectory, "forge-installer.jar");

        try
        {
            await DownloadFirstAvailableAsync(
                    OrderSourceUrls(officialUrl, mirrorUrl, mirrorFallbackUrl),
                    localInstaller,
                    byteProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            await _minecraft.InstallLoaderInstallerAsync(
                    gameDirectory,
                    minecraftVersion,
                    loaderVersion,
                    versionName,
                    officialUrl,
                    localInstaller,
                    embeddedJar,
                    javaPath,
                    fileProgress,
                    byteProgress,
                    installerOutput,
                    remoteFileDownloader: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }


    private async Task InstallNeoForgeAsync(
        string gameDirectory,
        string minecraftVersion,
        string loaderVersion,
        string versionName,
        string javaPath,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress,
        IProgress<string>? installerOutput,
        CancellationToken cancellationToken)
    {
        var relativePath =
            $"/net/neoforged/neoforge/{Uri.EscapeDataString(loaderVersion)}/" +
            $"neoforge-{Uri.EscapeDataString(loaderVersion)}-installer.jar";
        var officialUrl = NeoForgeMavenBase + "/releases" + relativePath;
        var mirrorUrl = BmclMirrorBase + relativePath;
        var mirrorFallbackUrl = BmclMirrorFallback + relativePath;
        var tempDirectory = CreateTempDirectory("pmcl-neoforge-");
        var localInstaller = Path.Combine(tempDirectory, "neoforge-installer.jar");

        try
        {
            await DownloadFirstAvailableAsync(
                    OrderSourceUrls(officialUrl, mirrorUrl, mirrorFallbackUrl),
                    localInstaller,
                    byteProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            await _minecraft.InstallLoaderInstallerAsync(
                    gameDirectory,
                    minecraftVersion,
                    loaderVersion,
                    versionName,
                    officialUrl,
                    localInstaller,
                    embeddedVersionJar: null,
                    javaPath,
                    fileProgress,
                    byteProgress,
                    installerOutput,
                    remoteFileDownloader: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task InstallOptiFineAsync(
        string gameDirectory,
        string minecraftVersion,
        string loaderVersion,
        string versionName,
        string javaPath,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken)
    {
        var parts = loaderVersion.Split('|', 2);
        var type = parts.Length == 2 ? parts[0] : "HD_U";
        var patch = parts.Length == 2 ? parts[1] : parts[0];

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "pmcl-optifine-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var installerPath = Path.Combine(tempDirectory, $"OptiFine_{minecraftVersion}_{type}_{patch}.jar");

        try
        {
            await DownloadOptiFineInstallerAsync(
                    minecraftVersion,
                    type,
                    patch,
                    installerPath,
                    byteProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            await _minecraft.InstallOptiFineAsync(
                    gameDirectory,
                    minecraftVersion,
                    NormalizeOptiFineVersion(loaderVersion),
                    versionName,
                    installerPath,
                    javaPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Temp files are best-effort.
            }
        }
    }

    private async Task DownloadOptiFineInstallerAsync(
        string minecraftVersion,
        string type,
        string patch,
        string targetPath,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken)
    {
        var fileName = $"OptiFine_{minecraftVersion}_{type}_{patch}.jar";
        var bmclUrl =
            $"{BmclApiBase}/optifine/{Uri.EscapeDataString(minecraftVersion)}/" +
            $"{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}";

        if (_minecraft.DownloadSource == DownloadSourceKind.BmclApi)
        {
            try
            {
                await DownloadFileAsync(bmclUrl, targetPath, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !cancellationToken.IsCancellationRequested)
            {
                // The official download below is the fallback.
            }
        }

        try
        {
            await DownloadOptiFineOfficialAsync(
                    fileName,
                    targetPath,
                    byteProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (_minecraft.DownloadSource != DownloadSourceKind.BmclApi
                                   && (ex is not OperationCanceledException
                                       || !cancellationToken.IsCancellationRequested))
        {
            // Official OptiFine is unavailable; use the mirror as a last resort.
        }

        await DownloadFileAsync(bmclUrl, targetPath, byteProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DownloadOptiFineOfficialAsync(
        string fileName,
        string targetPath,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken)
    {
        var adPageUrl = $"{OptiFineBase}/adloadx?f={Uri.EscapeDataString(fileName)}";
        using var response = await _downloadClient.GetAsync(
                adPageUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(
            html,
            @"href\s*=\s*['""](?<url>downloadx\?[^'""]+)['""]",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidDataException(
                L.Tr("ML.OptiFineNoDownloadLink"));
        }

        var relative = WebUtility.HtmlDecode(match.Groups["url"].Value);
        var downloadUrl = new Uri(new Uri(OptiFineBase + "/"), relative).AbsoluteUri;
        await DownloadFileAsync(
                downloadUrl,
                targetPath,
                byteProgress,
                cancellationToken,
                adPageUrl)
            .ConfigureAwait(false);
    }

    private async Task DownloadFileAsync(
        string url,
        string targetPath,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken,
        string? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(referer))
            request.Headers.Referrer = new Uri(referer);

        using var response = await _downloadClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);

        var buffer = new byte[81920];
        long progressedBytes = 0;
        var lastReport = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            progressedBytes += read;

            var now = Environment.TickCount64;
            if (totalBytes > 0
                && (progressedBytes >= totalBytes || now - lastReport >= 100))
            {
                byteProgress?.Report(new ByteProgress(totalBytes, progressedBytes));
                lastReport = now;
            }
        }
    }

    private async Task DownloadFirstAvailableAsync(
        IReadOnlyList<string> urls,
        string targetPath,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                await DownloadFileAsync(url, targetPath, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            L.Tr("ML.DownloadInstallerFailed"),
            lastError);
    }

    private IReadOnlyList<string> OrderSourceUrls(string officialUrl, params string[] mirrorUrls)
    {
        if (_minecraft.DownloadSource == DownloadSourceKind.BmclApi)
            return mirrorUrls.Concat(new[] { officialUrl }).ToArray();

        return new[] { officialUrl }.Concat(mirrorUrls).ToArray();
    }

    private IReadOnlyList<string> GetFabricMetaUrls(string relativePath)
        => OrderSourceUrls(
            FabricMetaOfficial + relativePath,
            FabricMetaMirror + relativePath);

    private static string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Temporary installer folders are best-effort cleanup.
        }
    }

    private void TryDeleteVersion(string gameDirectory, string versionName)
    {
        try
        {
            _minecraft.DeleteVersion(gameDirectory, versionName);
        }
        catch
        {
            // A failed cleanup must not hide the original installation error.
        }
    }


    private static Task<ModLoaderCatalogEntry> LoadVanillaAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModLoaderCatalogEntry(
            ModLoaderKind.Vanilla,
            L.Tr("ML.Vanilla"),
            L.Tr("ML.VanillaDescription"),
            new[] { new ModLoaderVersionOption(minecraftVersion, minecraftVersion, L.Tr("ML.Vanilla")) }));
    }

    private async Task<ModLoaderCatalogEntry> LoadFabricAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var relative = $"/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}";
            var json = await GetStringWithFallbackAsync(
                    GetFabricMetaUrls(relative),
                    cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var options = new List<ModLoaderVersionOption>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("loader", out var loader)
                    || !loader.TryGetProperty("version", out var versionValue)
                    || versionValue.GetString() is not { Length: > 0 } version)
                {
                    continue;
                }

                var stable = loader.TryGetProperty("stable", out var stableValue)
                             && stableValue.ValueKind == JsonValueKind.True;
                options.Add(new ModLoaderVersionOption(
                    version,
                    version,
                    stable ? L.Tr("ML.Stable") : L.Tr("ML.Beta"),
                    stable));
            }

            if (options.Count == 0)
            {
                return Failure(
                    ModLoaderKind.Fabric,
                    "Fabric",
                    L.Tr("ML.FabricShortDescription"),
                    L.Tr("ML.FabricNoLoader"));
            }

            var ordered = options
                .OrderByDescending(option => option.IsRecommended)
                .Take(80)
                .ToArray();
            return new ModLoaderCatalogEntry(
                ModLoaderKind.Fabric,
                "Fabric",
                L.Tr("ML.FabricDescription"),
                ordered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.Fabric, "Fabric",
                L.Tr("ML.FabricDescription"), ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadForgeAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var description = L.Tr("ML.ForgeDescription");
        if (!IsModernForgeSupported(minecraftVersion))
        {
            return Failure(ModLoaderKind.Forge, "Forge", description,
                L.Tr("ML.ForgeUnsupportedVersion"));
        }

        ModLoaderCatalogEntry first;
        ModLoaderCatalogEntry second;
        if (_minecraft.DownloadSource == DownloadSourceKind.BmclApi)
        {
            first = await LoadForgeFromBmclAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
            if (first.IsAvailable)
                return first;
            second = await LoadForgeOfficialAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            first = await LoadForgeOfficialAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
            if (first.IsAvailable)
                return first;
            second = await LoadForgeFromBmclAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
        }

        return second.IsAvailable
            ? second
            : Failure(ModLoaderKind.Forge, "Forge", description, CombineErrors(first, second));
    }

    private async Task<ModLoaderCatalogEntry> LoadForgeOfficialAsync(
        string minecraftVersion,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{ForgeMavenBase}/net/minecraftforge/forge/maven-metadata.xml";
            var xml = await _catalogClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var prefix = minecraftVersion + "-";
            var versions = XDocument.Parse(xml)
                .Descendants("version")
                .Select(element => element.Value)
                .Where(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(value => value[prefix.Length..])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => (Version: value, Parsed: ParseMinecraftVersion(value)))
                .OrderByDescending(item => item.Parsed)
                .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Take(60)
                .Select((item, index) => new ModLoaderVersionOption(
                    item.Version,
                    item.Version,
                    index == 0 ? L.Tr("ML.Latest") : "Forge",
                    index == 0))
                .ToArray();

            return versions.Length > 0
                ? new ModLoaderCatalogEntry(ModLoaderKind.Forge, "Forge", description, versions)
                : Failure(ModLoaderKind.Forge, "Forge", description,
                    L.Tr("ML.ForgeOfficialMissing"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.Forge, "Forge", description, ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadForgeFromBmclAsync(
        string minecraftVersion,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{BmclApiBase}/forge/minecraft/{Uri.EscapeDataString(minecraftVersion)}";
            var json = await _catalogClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var versions = new List<(string Version, long Build)>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("version", out var versionValue)
                    || versionValue.GetString() is not { Length: > 0 } version)
                {
                    continue;
                }

                var build = item.TryGetProperty("build", out var buildValue)
                            && buildValue.TryGetInt64(out var buildNumber)
                    ? buildNumber
                    : 0;
                versions.Add((version, build));
            }

            if (versions.Count == 0)
            {
                return Failure(ModLoaderKind.Forge, "Forge", description,
                    L.Tr("ML.ForgeBmclMissing"));
            }

            var ordered = versions
                .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.Build).First())
                .OrderByDescending(item => item.Build)
                .Take(60)
                .Select((item, index) => new ModLoaderVersionOption(
                    item.Version,
                    item.Version,
                    index == 0 ? L.Tr("ML.Latest") : "Forge",
                    index == 0))
                .ToArray();
            return new ModLoaderCatalogEntry(ModLoaderKind.Forge, "Forge", description, ordered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.Forge, "Forge", description, ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadNeoForgeAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var description = L.Tr("ML.NeoForgeDescription");

        ModLoaderCatalogEntry first;
        ModLoaderCatalogEntry second;
        if (_minecraft.DownloadSource == DownloadSourceKind.BmclApi)
        {
            first = await LoadNeoForgeFromBmclAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
            if (first.IsAvailable)
                return first;
            second = await LoadNeoForgeOfficialAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            first = await LoadNeoForgeOfficialAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
            if (first.IsAvailable)
                return first;
            second = await LoadNeoForgeFromBmclAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
        }

        return second.IsAvailable
            ? second
            : Failure(ModLoaderKind.NeoForge, "NeoForge", description, CombineErrors(first, second));
    }

    private async Task<ModLoaderCatalogEntry> LoadNeoForgeOfficialAsync(
        string minecraftVersion,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{NeoForgeMavenBase}/releases/net/neoforged/neoforge/maven-metadata.xml";
            var xml = await _catalogClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            var prefix = GetNeoForgePrefix(minecraftVersion);
            var versions = XDocument.Parse(xml)
                .Descendants("version")
                .Select(element => element.Value)
                .Where(value => value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                                || value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                .Where(value => !value.Contains('-') && !value.Contains('+'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(ParseMinecraftVersion)
                .Take(60)
                .Select((version, index) => new ModLoaderVersionOption(
                    version,
                    version,
                    index == 0 ? L.Tr("ML.Latest") : "NeoForge",
                    index == 0))
                .ToArray();

            return versions.Length > 0
                ? new ModLoaderCatalogEntry(ModLoaderKind.NeoForge, "NeoForge", description, versions)
                : Failure(ModLoaderKind.NeoForge, "NeoForge", description,
                    L.Tr("ML.NeoForgeOfficialMissing"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.NeoForge, "NeoForge", description, ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadNeoForgeFromBmclAsync(
        string minecraftVersion,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{BmclApiBase}/neoforge/list/{Uri.EscapeDataString(minecraftVersion)}";
            var json = await _catalogClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var versions = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("version", out var versionValue)
                    && versionValue.GetString() is { Length: > 0 } version)
                {
                    versions.Add(version);
                }
            }

            if (versions.Count == 0)
            {
                return Failure(ModLoaderKind.NeoForge, "NeoForge", description,
                    L.Tr("ML.NeoForgeBmclMissing"));
            }

            var ordered = versions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(ParseMinecraftVersion)
                .Take(60)
                .Select((version, index) => new ModLoaderVersionOption(
                    version,
                    version,
                    index == 0 ? L.Tr("ML.Latest") : "NeoForge",
                    index == 0))
                .ToArray();
            return new ModLoaderCatalogEntry(ModLoaderKind.NeoForge, "NeoForge", description, ordered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.NeoForge, "NeoForge", description, ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadOptiFineAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var description = L.Tr("ML.OptiFineDescription");

        ModLoaderCatalogEntry first;
        ModLoaderCatalogEntry second;
        if (_minecraft.DownloadSource == DownloadSourceKind.BmclApi)
        {
            first = await LoadOptiFineFromBmclAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
            if (first.IsAvailable)
                return first;
            second = await LoadOptiFineOfficialAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            first = await LoadOptiFineOfficialAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
            if (first.IsAvailable)
                return first;
            second = await LoadOptiFineFromBmclAsync(minecraftVersion, description, cancellationToken)
                .ConfigureAwait(false);
        }

        return second.IsAvailable
            ? second
            : Failure(ModLoaderKind.OptiFine, "OptiFine", description, CombineErrors(first, second));
    }

    private async Task<ModLoaderCatalogEntry> LoadOptiFineOfficialAsync(
        string minecraftVersion,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await _catalogClient.GetStringAsync(
                    OptiFineBase + "/downloads",
                    cancellationToken)
                .ConfigureAwait(false);

            var options = new List<ModLoaderVersionOption>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = Regex.Matches(
                html,
                @"(?<!preview_)OptiFine_(?<mc>[^_""'/]+)_(?<type>HD_U)(?:_(?<patch>[A-Za-z0-9]+))?\.jar",
                RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                if (!string.Equals(
                        match.Groups["mc"].Value,
                        minecraftVersion,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = match.Groups["type"].Value;
                var patch = match.Groups["patch"].Value;
                if (patch.Length == 0
                    || patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var version = $"{type}|{patch}";
                if (!seen.Add(version))
                    continue;

                options.Add(new ModLoaderVersionOption(
                    version,
                    $"{type.Replace('_', ' ')} {patch}",
                    $"OptiFine_{minecraftVersion}_{type}_{patch}.jar",
                    options.Count == 0));
            }

            if (options.Count == 0)
            {
                return Failure(ModLoaderKind.OptiFine, "OptiFine", description,
                    L.Tr("ML.OptiFineNoStableInstaller"));
            }

            return new ModLoaderCatalogEntry(
                ModLoaderKind.OptiFine,
                "OptiFine",
                description,
                options.Take(40).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.OptiFine, "OptiFine", description, ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadOptiFineFromBmclAsync(
        string minecraftVersion,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{BmclApiBase}/optifine/{Uri.EscapeDataString(minecraftVersion)}";
            var json = await _catalogClient.GetStringAsync(url, cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var options = new List<ModLoaderVersionOption>();
            var items = document.RootElement.EnumerateArray().ToArray();
            for (var i = items.Length - 1; i >= 0; i--)
            {
                var item = items[i];
                var type = item.TryGetProperty("type", out var typeValue)
                    ? typeValue.GetString() ?? string.Empty
                    : string.Empty;
                var patch = item.TryGetProperty("patch", out var patchValue)
                    ? patchValue.GetString() ?? string.Empty
                    : string.Empty;
                var filename = item.TryGetProperty("filename", out var fileValue)
                    ? fileValue.GetString() ?? string.Empty
                    : string.Empty;

                if (type.Length == 0
                    || patch.Length == 0
                    || patch.StartsWith("pre", StringComparison.OrdinalIgnoreCase)
                    || !type.StartsWith("HD_U", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var displayName = $"{type.Replace('_', ' ')} {patch}";
                options.Add(new ModLoaderVersionOption(
                    $"{type}|{patch}",
                    displayName,
                    filename,
                    options.Count == 0));
            }

            if (options.Count == 0)
            {
                return Failure(ModLoaderKind.OptiFine, "OptiFine", description,
                    L.Tr("ML.OptiFineBmclMissing"));
            }

            return new ModLoaderCatalogEntry(
                ModLoaderKind.OptiFine,
                "OptiFine",
                description,
                options.Take(40).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.OptiFine, "OptiFine", description, ex.Message);
        }
    }

    private static ModLoaderCatalogEntry Failure(
        ModLoaderKind kind,
        string displayName,
        string description,
        string error)
        => new(kind, displayName, description, Array.Empty<ModLoaderVersionOption>(), error);

    private static bool IsModernForgeSupported(string minecraftVersion)
    {
        var baseVersion = minecraftVersion.Split('-')[0];
        return !Version.TryParse(baseVersion, out var parsed)
               || parsed >= new Version(1, 12, 2);
    }

    private static Version ParseMinecraftVersion(string value)
        => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    private static string GetNeoForgePrefix(string minecraftVersion)
    {
        var baseVersion = minecraftVersion.Split('-')[0];
        var parts = baseVersion.Split('.');
        if (parts.Length < 2)
            return baseVersion;

        if (string.Equals(parts[0], "1", StringComparison.Ordinal))
        {
            var minor = parts.Length >= 3 ? parts[2] : "0";
            return $"{parts[1]}.{minor}";
        }

        return $"{parts[0]}.{parts[1]}";
    }

    private static string CombineErrors(
        ModLoaderCatalogEntry first,
        ModLoaderCatalogEntry second)
    {
        var errors = new[] { first.Error, second.Error }
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return errors.Length == 0
            ? L.Tr("ML.LoaderVersionsUnavailable")
            : string.Join(L.Tr("ML.ErrorSeparator"), errors);
    }

    private async Task<string> GetStringWithFallbackAsync(
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                return await _catalogClient.GetStringAsync(url, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(L.Tr("ML.FabricMetaUnavailable"), lastError);
    }

    private static void RewriteFabricLibraryUrls(JsonObject profile)
    {
        if (profile["libraries"] is not JsonArray libraries)
            return;

        foreach (var node in libraries)
        {
            if (node is not JsonObject library
                || library["url"] is not JsonValue urlValue
                || !urlValue.TryGetValue<string>(out var url)
                || !url.StartsWith("https://maven.fabricmc.net", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            library["url"] = BmclMirrorBase + "/";
        }
    }

    private sealed record CachedCatalog(
        DateTimeOffset CreatedAt,
        IReadOnlyList<ModLoaderCatalogEntry> Entries);
}

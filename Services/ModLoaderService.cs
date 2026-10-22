using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Installers;
using PMCL.Models;

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
/// vanilla Minecraft version. Forge/NeoForge use BMCLAPI metadata and mirrors,
/// Fabric uses Fabric Meta and OptiFine uses BMCLAPI's installer mirror.
/// </summary>
public sealed class ModLoaderService
{
    private const string FabricMetaBase = "https://bmclapi2.bangbang93.com/fabric-meta";
    private const string FabricMetaFallback = "https://meta.fabricmc.net";
    private const string BmclApiBase = "https://bmclapi2.bangbang93.com";
    private const string BmclMirrorBase = "https://mirror.nju.edu.cn/bmclapi";
    private const string BmclMirrorFallback = "https://mirrors.cernet.edu.cn/bmclapi";
    private const string ForgeMavenBase = "https://maven.minecraftforge.net";

    private readonly MinecraftService _minecraft;
    private readonly HttpClient _catalogClient;
    private readonly HttpClient _downloadClient;
    private readonly Dictionary<string, CachedCatalog> _catalogCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public ModLoaderService(MinecraftService minecraft)
    {
        _minecraft = minecraft;
        _catalogClient = CreateHttpClient(TimeSpan.FromSeconds(25));
        _downloadClient = CreateHttpClient(TimeSpan.FromMinutes(15));
    }

    public async Task<IReadOnlyList<ModLoaderCatalogEntry>> GetCatalogAsync(
        string gameDirectory,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{Path.GetFullPath(gameDirectory)}|{minecraftVersion}";
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
            throw new InvalidOperationException("请选择模组加载器版本。");
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
            throw new InvalidOperationException("Minecraft 版本不能为空。");

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
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的模组加载器。");
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
        var url = $"{FabricMetaBase}/v2/versions/loader/" +
                  $"{Uri.EscapeDataString(minecraftVersion)}/" +
                  $"{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var json = await GetFabricStringAsync(url, cancellationToken).ConfigureAwait(false);

        if (JsonNode.Parse(json) is not JsonObject profile)
            throw new InvalidDataException("Fabric Meta 返回了无效的版本配置。");

        profile["id"] = versionName;
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
        var installerUrl =
            $"{BmclMirrorBase}/net/minecraftforge/forge/{coordinates}/forge-{coordinates}-installer.jar";
        var alternateUrl =
            $"{BmclMirrorFallback}/net/minecraftforge/forge/{coordinates}/forge-{coordinates}-installer.jar";
        var officialUrl =
            $"{ForgeMavenBase}/net/minecraftforge/forge/{coordinates}/forge-{coordinates}-installer.jar";
        var embeddedJar = string.Equals(minecraftVersion, "1.12.2", StringComparison.Ordinal)
            ? $"maven/net/minecraftforge/forge/{coordinates}/forge-{coordinates}.jar"
            : null;

        var tempDirectory = CreateTempDirectory("pmcl-forge-");
        var localInstaller = Path.Combine(tempDirectory, "forge-installer.jar");

        try
        {
            await DownloadFirstAvailableAsync(
                    new[] { installerUrl, alternateUrl, officialUrl },
                    localInstaller,
                    cancellationToken)
                .ConfigureAwait(false);

            await _minecraft.InstallLoaderInstallerAsync(
                    gameDirectory,
                    minecraftVersion,
                    loaderVersion,
                    versionName,
                    installerUrl,
                    localInstaller,
                    embeddedJar,
                    javaPath,
                    fileProgress,
                    byteProgress,
                    installerOutput,
                    DownloadMirroredFileAsync,
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
        var installerUrl =
            $"{BmclMirrorBase}/net/neoforged/neoforge/{Uri.EscapeDataString(loaderVersion)}/neoforge-{Uri.EscapeDataString(loaderVersion)}-installer.jar";
        var alternateUrl =
            $"{BmclMirrorFallback}/net/neoforged/neoforge/{Uri.EscapeDataString(loaderVersion)}/neoforge-{Uri.EscapeDataString(loaderVersion)}-installer.jar";
        var tempDirectory = CreateTempDirectory("pmcl-neoforge-");
        var localInstaller = Path.Combine(tempDirectory, "neoforge-installer.jar");

        try
        {
            await DownloadFirstAvailableAsync(
                    new[] { installerUrl, alternateUrl },
                    localInstaller,
                    cancellationToken)
                .ConfigureAwait(false);

            await _minecraft.InstallLoaderInstallerAsync(
                    gameDirectory,
                    minecraftVersion,
                    loaderVersion,
                    versionName,
                    installerUrl,
                    localInstaller,
                    embeddedVersionJar: null,
                    javaPath,
                    fileProgress,
                    byteProgress,
                    installerOutput,
                    DownloadMirroredFileAsync,
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
        CancellationToken cancellationToken)
    {
        var parts = loaderVersion.Split('|', 2);
        var type = parts.Length == 2 ? parts[0] : "HD_U";
        var patch = parts.Length == 2 ? parts[1] : parts[0];
        var downloadUrl =
            $"{BmclApiBase}/optifine/{Uri.EscapeDataString(minecraftVersion)}/" +
            $"{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}";

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "pmcl-optifine-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var installerPath = Path.Combine(tempDirectory, $"OptiFine_{minecraftVersion}_{type}_{patch}.jar");

        try
        {
            await DownloadFileAsync(downloadUrl, installerPath, cancellationToken).ConfigureAwait(false);
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

    private async Task DownloadFileAsync(
        string url,
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var response = await _downloadClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadMirroredFileAsync(
        string url,
        string targetPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await DownloadFileAsync(url, targetPath, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (url.StartsWith(BmclMirrorBase, StringComparison.OrdinalIgnoreCase))
        {
            // Try the alternate CERNET mirror below.
        }

        if (url.StartsWith(BmclMirrorBase, StringComparison.OrdinalIgnoreCase))
        {
            var fallbackUrl = BmclMirrorFallback + url[BmclMirrorBase.Length..];
            await DownloadFileAsync(fallbackUrl, targetPath, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        throw new InvalidOperationException("下载模组加载器文件失败。");
    }

    private async Task DownloadFirstAvailableAsync(
        IReadOnlyList<string> urls,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var url in urls)
        {
            try
            {
                await DownloadFileAsync(url, targetPath, cancellationToken).ConfigureAwait(false);
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
            "下载安装器失败，请检查网络后重试。",
            lastError);
    }

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
            "原版",
            "只安装 Mojang 原版游戏，不添加模组加载器。",
            new[] { new ModLoaderVersionOption(minecraftVersion, minecraftVersion, "原版") }));
    }

    private async Task<ModLoaderCatalogEntry> LoadFabricAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{FabricMetaBase}/v2/versions/loader/" +
                      $"{Uri.EscapeDataString(minecraftVersion)}";
            var json = await GetFabricStringAsync(url, cancellationToken)
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
                    stable ? "稳定版" : "测试版",
                    stable));
            }

            if (options.Count == 0)
            {
                return Failure(
                    ModLoaderKind.Fabric,
                    "Fabric",
                    "轻量、更新快的模组加载器。",
                    "该 Minecraft 版本没有可用的 Fabric Loader。");
            }

            var ordered = options
                .OrderByDescending(option => option.IsRecommended)
                .Take(80)
                .ToArray();
            return new ModLoaderCatalogEntry(
                ModLoaderKind.Fabric,
                "Fabric",
                "轻量、更新快的模组加载器，适合大多数 Fabric 模组。",
                ordered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.Fabric, "Fabric",
                "轻量、更新快的模组加载器，适合大多数 Fabric 模组。", ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadForgeAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        const string description = "传统 Forge 模组加载器，适合大型整合包与旧版本模组。";
        if (!IsModernForgeSupported(minecraftVersion))
        {
            return Failure(ModLoaderKind.Forge, "Forge", description,
                "Forge 自动安装目前支持 Minecraft 1.12.2 及以上版本。");
        }

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
                    "该 Minecraft 版本没有可用的 Forge 安装器。");
            }

            var ordered = versions
                .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.Build).First())
                .OrderByDescending(item => item.Build)
                .Take(60)
                .Select((item, index) => new ModLoaderVersionOption(
                    item.Version,
                    item.Version,
                    index == 0 ? "最新" : "Forge",
                    index == 0))
                .ToArray();
            return new ModLoaderCatalogEntry(ModLoaderKind.Forge, "Forge", description, ordered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.Forge, "Forge", description, ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadNeoForgeAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        const string description = "NeoForge 是 Forge 的现代分支，适合 1.20.2 及以上的新模组。";
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
                    "该 Minecraft 版本没有可用的 NeoForge 版本。");
            }

            var ordered = versions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(ParseMinecraftVersion)
                .Take(60)
                .Select((version, index) => new ModLoaderVersionOption(
                    version,
                    version,
                    index == 0 ? "最新" : "NeoForge",
                    index == 0))
                .ToArray();
            return new ModLoaderCatalogEntry(ModLoaderKind.NeoForge, "NeoForge", description, ordered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure(ModLoaderKind.NeoForge, "NeoForge", description, ex.Message);
        }
    }

    private async Task<ModLoaderCatalogEntry> LoadOptiFineAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        const string description = "OptiFine 提供光影、动态光源和性能优化；同一版本只保留一个加载器。";
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
                    "该 Minecraft 版本没有可用的 OptiFine 安装包。");
            }

            return new ModLoaderCatalogEntry(
                ModLoaderKind.OptiFine,
                "OptiFine",
                description,
                options.Take(40).ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PMCL", "0.1.0"));
        return client;
    }

    private async Task<string> GetFabricStringAsync(
        string primaryUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _catalogClient.GetStringAsync(primaryUrl, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out; try the official endpoint below.
        }
        catch (HttpRequestException)
        {
            // Mirror miss; try the official endpoint below.
        }

        var relative = primaryUrl.StartsWith(FabricMetaBase, StringComparison.OrdinalIgnoreCase)
            ? primaryUrl[FabricMetaBase.Length..]
            : primaryUrl;
        return await _catalogClient
            .GetStringAsync(FabricMetaFallback + relative, cancellationToken)
            .ConfigureAwait(false);
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

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;
using CmlLib.Core.VersionMetadata;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Wraps CmlLib.Core for version discovery, installation and process building.
/// </summary>
public sealed class MinecraftService
{
    private const string BmclMirrorBase = "https://mirror.nju.edu.cn/bmclapi";

    // Java 8-targeted helper that invokes OptiFine's headless installer entry
    // point. Shipping the class avoids requiring a JDK (source-file mode).
    private const string OptiFineHeadlessClassBase64 =
        "yv66vgAAADQAHQoAAgADBwAEDAAFAAYBABBqYXZhL2xhbmcvT2JqZWN0AQAGPGluaXQ+AQADKClWBwAIAQAMamF2YS9pby9GaWxlCgAHAAoMAAUACwEAFShMamF2YS9sYW5nL1N0cmluZzspVgoADQAOBwAPDAAQABEBABJvcHRpZmluZS9JbnN0YWxsZXIBAAlkb0luc3RhbGwBABEoTGphdmEvaW8vRmlsZTspVgcAEwEAFFBtY2xPcHRpRmluZUhlYWRsZXNzAQAEQ29kZQEAD0xpbmVOdW1iZXJUYWJsZQEABG1haW4BABYoW0xqYXZhL2xhbmcvU3RyaW5nOylWAQAKRXhjZXB0aW9ucwcAGgEAE2phdmEvbGFuZy9FeGNlcHRpb24BAApTb3VyY2VGaWxlAQAZUG1jbE9wdGlGaW5lSGVhZGxlc3MuamF2YQAxABIAAgAAAAAAAgABAAUABgABABQAAAAdAAEAAQAAAAUqtwABsQAAAAEAFQAAAAYAAQAAAAEACQAWABcAAgAUAAAAJgAEAAEAAAAOuwAHWSoDMrcACbgADLEAAAABABUAAAAGAAEAAAABABgAAAAEAAEAGQABABsAAAACABw=";

    private readonly Dictionary<string, MinecraftLauncher> _launchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DownloadSourceKind> _downloadSourceProvider;

    public MinecraftService(Func<DownloadSourceKind>? downloadSourceProvider = null)
    {
        _downloadSourceProvider = downloadSourceProvider
            ?? (static () => DownloadSourceKind.Official);
    }

    /// <summary>
    /// The source currently selected in the launcher settings. Official is the
    /// default and requires no BMCLAPI access.
    /// </summary>
    public DownloadSourceKind DownloadSource => _downloadSourceProvider();

    public async Task<IReadOnlyList<GameVersion>> GetVersionsAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = CreatePath(gameDirectory);
        var launcher = GetLauncher(path);
        var collection = await launcher.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);

        return collection
            .Select(metadata =>
            {
                var installed = IsInstalled(path, metadata.Name);
                var loader = installed
                    ? ModLoaderDetector.Detect(
                        Path.Combine(path.Versions, metadata.Name),
                        metadata.Name)
                    : new ModLoaderMetadata { MinecraftVersion = metadata.Name };

                return new GameVersion(
                    metadata.Name,
                    metadata.Type ?? string.Empty,
                    metadata.ReleaseTime,
                    installed,
                    loader.Loader,
                    loader.LoaderVersion,
                    loader.MinecraftVersion);
            })
            .ToList();
    }

    public bool IsInstalled(string gameDirectory, string versionName)
        => IsInstalled(CreatePath(gameDirectory), versionName);

    public async Task InstallAsync(
        string gameDirectory,
        string versionName,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress = null,
        IProgress<ByteProgress>? byteProgress = null,
        CancellationToken cancellationToken = default)
    {
        var launcher = GetLauncher(CreatePath(gameDirectory));
        await launcher.InstallAsync(versionName, fileProgress, byteProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a Fabric profile JSON next to the version and installs every
    /// inherited library/asset through CmlLib.
    /// </summary>
    public async Task<string> InstallFabricProfileAsync(
        string gameDirectory,
        string versionName,
        string profileJson,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress = null,
        IProgress<ByteProgress>? byteProgress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateVersionName(versionName);
        var path = CreatePath(gameDirectory);
        var versionDirectory = Path.Combine(path.Versions, versionName);
        Directory.CreateDirectory(versionDirectory);

        var jsonPath = Path.Combine(versionDirectory, versionName + ".json");
        await File.WriteAllTextAsync(jsonPath, profileJson, cancellationToken).ConfigureAwait(false);

        var launcher = GetLauncher(path);
        await launcher.InstallAsync(versionName, fileProgress, byteProgress, cancellationToken)
            .ConfigureAwait(false);
        InvalidateLauncher(path);
        return versionName;
    }

    /// <summary>
    /// Installs a modern Forge/NeoForge installer profile. The installer jar is
    /// downloaded by CmlLib, processors run with the supplied Java binary and
    /// the resulting version is published atomically.
    /// </summary>
    public async Task<string> InstallLoaderInstallerAsync(
        string gameDirectory,
        string minecraftVersion,
        string loaderVersion,
        string versionName,
        string installerUrl,
        string? localInstallerPath,
        string? embeddedVersionJar,
        string? javaPath,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress = null,
        IProgress<ByteProgress>? byteProgress = null,
        IProgress<string>? installerOutput = null,
        Func<string, string, CancellationToken, Task>? remoteFileDownloader = null,
        CancellationToken cancellationToken = default)
    {
        ValidateVersionName(versionName);
        var path = CreatePath(gameDirectory);
        var launcher = GetLauncher(path);

        await launcher.InstallAsync(
                minecraftVersion,
                fileProgress,
                byteProgress,
                cancellationToken)
            .ConfigureAwait(false);

        var options = new ForgeInstallOptions
        {
            JavaPath = await ResolveJavaPathAsync(javaPath, launcher, minecraftVersion, cancellationToken)
                .ConfigureAwait(false),
            FileProgress = fileProgress,
            ByteProgress = byteProgress,
            InstallerOutput = installerOutput,
            CancellationToken = cancellationToken,
            SkipIfAlreadyInstalled = false
        };

        var artifact = new ForgeV12VersionArtifact(
            minecraftVersion,
            loaderVersion,
            versionName,
            installerUrl,
            embeddedVersionJar);
        var installer = new ForgeV12Installer(artifact);
        var gameInstaller = string.IsNullOrWhiteSpace(localInstallerPath)
            ? launcher.GameInstaller
            : new LocalInstallerOverride(
                launcher.GameInstaller,
                installerUrl,
                localInstallerPath,
                remoteFileDownloader);
        await installer.Install(path, gameInstaller, options).ConfigureAwait(false);
        InvalidateLauncher(path);
        return versionName;
    }

    /// <summary>
    /// Runs OptiFine's own patcher headlessly against an installed vanilla
    /// version. The installer creates the loader profile and patched jar.
    /// </summary>
    public async Task<string> InstallOptiFineAsync(
        string gameDirectory,
        string minecraftVersion,
        string loaderVersion,
        string versionName,
        string installerPath,
        string? javaPath,
        CancellationToken cancellationToken = default)
    {
        ValidateVersionName(versionName);
        var path = CreatePath(gameDirectory);
        var launcher = GetLauncher(path);

        if (!IsInstalled(path, minecraftVersion))
        {
            await launcher.InstallAsync(minecraftVersion, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var launcherProfilesPath = Path.Combine(path.BasePath, "launcher_profiles.json");
        if (!File.Exists(launcherProfilesPath))
        {
            await File.WriteAllTextAsync(
                    launcherProfilesPath,
                    "{\n  \"profiles\": {}\n}",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var java = await ResolveJavaPathAsync(javaPath, launcher, minecraftVersion, cancellationToken)
            .ConfigureAwait(false);
        var helperDirectory = Path.Combine(Path.GetTempPath(), "pmcl-optifine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);

        try
        {
            await File.WriteAllBytesAsync(
                    Path.Combine(helperDirectory, "PmclOptiFineHeadless.class"),
                    Convert.FromBase64String(OptiFineHeadlessClassBase64),
                    cancellationToken)
                .ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = java,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = path.BasePath
            };
            startInfo.ArgumentList.Add("-cp");
            startInfo.ArgumentList.Add($"{installerPath}{Path.PathSeparator}{helperDirectory}");
            startInfo.ArgumentList.Add("PmclOptiFineHeadless");
            startInfo.ArgumentList.Add(path.BasePath);

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            process.OutputDataReceived += (_, args) => AppendInstallerOutput(output, args.Data);
            process.ErrorDataReceived += (_, args) => AppendInstallerOutput(output, args.Data);

            if (!process.Start())
                throw new InvalidOperationException("无法启动 OptiFine 安装器。");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }

                throw new TimeoutException("OptiFine 安装超时。");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"OptiFine 安装器退出码 {process.ExitCode}。{GetOutputTail(output)}");
            }

            var expectedJson = Path.Combine(path.Versions, versionName, versionName + ".json");
            if (!File.Exists(expectedJson))
            {
                throw new InvalidOperationException(
                    $"OptiFine 安装器未生成版本 {versionName}。{GetOutputTail(output)}");
            }

            InvalidateLauncher(path);
            return versionName;
        }
        finally
        {
            try
            {
                Directory.Delete(helperDirectory, recursive: true);
            }
            catch
            {
                // The temp helper is not important after installation.
            }
        }
    }

    public async Task<Process> BuildProcessAsync(
        string gameDirectory,
        string versionName,
        MSession session,
        GameVersionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var path = CreatePath(gameDirectory);
        var launcher = GetLauncher(path);

        var option = new MLaunchOption
        {
            Path = path,
            Session = session,
            MaximumRamMb = settings.MaximumRamMb,
            MinimumRamMb = settings.MinimumRamMb,
            JavaPath = string.IsNullOrWhiteSpace(settings.JavaPath) ? null : settings.JavaPath,
            ScreenWidth = settings.WindowWidth,
            ScreenHeight = settings.WindowHeight,
            FullScreen = settings.FullScreen,
            ExtraJvmArguments = CommandLineSplitter.Split(settings.JvmArguments)
                .Select(argument => new MArgument(argument))
                .ToList(),
            ExtraGameArguments = CommandLineSplitter.Split(settings.GameArguments)
                .Select(argument => new MArgument(argument))
                .ToList(),
            GameLauncherName = "PMCL",
            GameLauncherVersion = "0.1.0",
            VersionType = "PMCL"
        };

        if (settings.AutoJoinServer && !string.IsNullOrWhiteSpace(settings.ServerAddress))
        {
            option.ServerIp = settings.ServerAddress.Trim();
            option.ServerPort = settings.ServerPort;
        }

        return await launcher.BuildProcessAsync(versionName, option, cancellationToken)
            .ConfigureAwait(false);
    }

    public string GetVersionDirectory(string gameDirectory, string versionName)
        => Path.Combine(CreatePath(gameDirectory).Versions, versionName);

    public string GetSavesDirectory(string gameDirectory)
        => Path.Combine(CreatePath(gameDirectory).BasePath, "saves");

    public string GetScreenshotsDirectory(string gameDirectory)
        => Path.Combine(CreatePath(gameDirectory).BasePath, "screenshots");

    /// <summary>
    /// Renames an installed version folder, its metadata/jar files and the id in
    /// version.json. Child versions that inherit from it are updated as well.
    /// </summary>
    public void RenameVersion(string gameDirectory, string oldName, string newName)
    {
        ValidateVersionName(newName);

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
            return;

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("暂不支持只修改版本名的大小写。");

        var path = CreatePath(gameDirectory);
        var oldDirectory = Path.Combine(path.Versions, oldName);
        var newDirectory = Path.Combine(path.Versions, newName);

        if (!Directory.Exists(oldDirectory))
            throw new DirectoryNotFoundException($"找不到版本文件夹：{oldDirectory}");
        if (Directory.Exists(newDirectory) || File.Exists(newDirectory))
            throw new InvalidOperationException($"版本文件夹 {newName} 已存在。");

        var oldJsonPath = Path.Combine(oldDirectory, oldName + ".json");
        if (!File.Exists(oldJsonPath))
            throw new FileNotFoundException($"版本缺少元数据文件：{oldJsonPath}");

        var originalJson = File.ReadAllText(oldJsonPath);
        if (JsonNode.Parse(originalJson) is not JsonObject metadata)
            throw new InvalidDataException($"版本元数据格式无效：{oldJsonPath}");

        Directory.Move(oldDirectory, newDirectory);

        try
        {
            metadata["id"] = newName;
            var newJsonPath = Path.Combine(newDirectory, newName + ".json");
            File.WriteAllText(newJsonPath, metadata.ToJsonString(IndentedJsonOptions));
            File.Delete(Path.Combine(newDirectory, oldName + ".json"));

            var oldJarPath = Path.Combine(newDirectory, oldName + ".jar");
            if (File.Exists(oldJarPath))
            {
                var newJarPath = Path.Combine(newDirectory, newName + ".jar");
                if (!File.Exists(newJarPath))
                    File.Move(oldJarPath, newJarPath);
            }
        }
        catch
        {
            TryRollbackRename(newDirectory, oldDirectory, oldName, newName, originalJson);
            throw;
        }

        UpdateInheritedVersionReferences(path, newDirectory, oldName, newName);
        ModLoaderDetector.UpdateBaseVersion(path.Versions, oldName, newName);
        InvalidateLauncher(path);
    }

    /// <summary>
    /// Deletes a version folder. Saves, screenshots and other shared game data are kept.
    /// </summary>
    public void DeleteVersion(string gameDirectory, string versionName)
    {
        var path = CreatePath(gameDirectory);
        var versionDirectory = Path.Combine(path.Versions, versionName);

        if (Directory.Exists(versionDirectory))
            Directory.Delete(versionDirectory, recursive: true);

        InvalidateLauncher(path);
    }

    public static void ValidateVersionName(string versionName)
    {
        var name = versionName.Trim();

        if (name.Length is 0 or > 96)
            throw new InvalidOperationException("版本名长度必须为 1 到 96 个字符。");
        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidOperationException("版本名不能以点或空格结尾。");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("版本名包含文件系统不允许的字符。");
    }

    /// <summary>
    /// Returns the auto-detected Java executable, or null when no Java installation was found.
    /// </summary>
    public string? TryGetDefaultJavaPath(string gameDirectory)
    {
        try
        {
            var path = CreatePath(gameDirectory);
            var javaPath = GetLauncher(path).GetDefaultJavaPath();
            return string.IsNullOrWhiteSpace(javaPath) ? null : javaPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IReadOnlyList<InstalledLoaderProfile> GetInstalledLoaderProfiles(
        string gameDirectory,
        string baseVersionName)
        => ModLoaderDetector.GetInstalledLoaderProfiles(gameDirectory, baseVersionName);

    public ModLoaderMetadata GetLoaderMetadata(string gameDirectory, string versionName)
        => ModLoaderDetector.Detect(GetVersionDirectory(gameDirectory, versionName), versionName);

    public void WriteLoaderMetadata(
        string gameDirectory,
        string versionName,
        ModLoaderKind kind,
        string loaderVersion,
        string minecraftVersion)
    {
        var versionDirectory = GetVersionDirectory(gameDirectory, versionName);
        if (!Directory.Exists(versionDirectory))
            throw new DirectoryNotFoundException($"找不到版本文件夹：{versionDirectory}");

        ModLoaderDetector.WriteMetadata(versionDirectory, new ModLoaderMetadata
        {
            Loader = kind,
            LoaderVersion = loaderVersion,
            MinecraftVersion = minecraftVersion
        });
    }

    private static async Task<string> ResolveJavaPathAsync(
        string? javaPath,
        MinecraftLauncher launcher,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            var explicitPath = javaPath.Trim();
            if (!File.Exists(explicitPath))
            {
                throw new InvalidOperationException(
                    $"指定的 Java 不存在：{explicitPath}");
            }

            return explicitPath;
        }

        string? resolved = null;
        try
        {
            var version = await launcher.GetVersionAsync(minecraftVersion, cancellationToken)
                .ConfigureAwait(false);
            resolved = launcher.GetJavaPath(version);
        }
        catch
        {
            // Fall back to the launcher-wide default below.
        }

        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
            resolved = launcher.GetDefaultJavaPath();

        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
        {
            throw new InvalidOperationException(
                "未找到可用的 Java。请先在版本设置中指定 Java 可执行文件。");
        }

        return resolved;
    }

    private static void AppendInstallerOutput(StringBuilder output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (output)
        {
            if (output.Length >= 12000)
                return;

            output.AppendLine(line);
        }
    }

    private static string GetOutputTail(StringBuilder output)
    {
        string text;
        lock (output)
        {
            if (output.Length == 0)
                return string.Empty;
            text = output.ToString().Trim();
        }

        return text.Length <= 1200 ? $" 输出：{text}" : $" 输出：...{text[^1200..]}";
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private static void TryRollbackRename(
        string newDirectory,
        string oldDirectory,
        string oldName,
        string newName,
        string originalJson)
    {
        try
        {
            if (!Directory.Exists(newDirectory) || Directory.Exists(oldDirectory))
                return;

            var newJsonPath = Path.Combine(newDirectory, newName + ".json");
            if (File.Exists(newJsonPath))
                File.Delete(newJsonPath);

            File.WriteAllText(Path.Combine(newDirectory, oldName + ".json"), originalJson);

            var newJarPath = Path.Combine(newDirectory, newName + ".jar");
            var oldJarPath = Path.Combine(newDirectory, oldName + ".jar");
            if (File.Exists(newJarPath) && !File.Exists(oldJarPath))
                File.Move(newJarPath, oldJarPath);

            Directory.Move(newDirectory, oldDirectory);
        }
        catch
        {
            // Best-effort rollback; the original exception is more useful to the caller.
        }
    }

    private static void UpdateInheritedVersionReferences(
        MinecraftPath path,
        string renamedDirectory,
        string oldName,
        string newName)
    {
        var renamedPrefix = renamedDirectory.TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;

        foreach (var jsonFile in Directory.EnumerateFiles(
                     path.Versions,
                     "*.json",
                     SearchOption.AllDirectories))
        {
            if (jsonFile.StartsWith(renamedPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (JsonNode.Parse(File.ReadAllText(jsonFile)) is not JsonObject metadata)
                    continue;

                if (metadata["inheritsFrom"] is JsonValue value
                    && value.TryGetValue<string>(out var parent)
                    && string.Equals(parent, oldName, StringComparison.Ordinal))
                {
                    metadata["inheritsFrom"] = newName;
                    File.WriteAllText(jsonFile, metadata.ToJsonString(IndentedJsonOptions));
                }
            }
            catch
            {
                // A malformed third-party version must not break the rename operation.
            }
        }
    }

    private MinecraftLauncher GetLauncher(MinecraftPath path)
    {
        var source = DownloadSource;
        var cacheKey = $"{path.BasePath}|{source}";
        if (_launchers.TryGetValue(cacheKey, out var launcher))
            return launcher;

        path.CreateDirs();
        var httpClient = DownloadSourceRouter.CreateHttpClient(
            () => source,
            TimeSpan.FromMinutes(15));
        var parameters = MinecraftLauncherParameters.CreateDefault(path, httpClient);

        if (source == DownloadSourceKind.BmclApi)
            parameters.VersionLoader = new BmclVersionLoader(path, httpClient);

        launcher = new MinecraftLauncher(parameters);
        _launchers[cacheKey] = launcher;
        return launcher;
    }

    private void InvalidateLauncher(MinecraftPath path)
    {
        var prefix = path.BasePath + "|";
        foreach (var key in _launchers.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            _launchers.Remove(key);
        }
    }

    private static MinecraftPath CreatePath(string gameDirectory)
    {
        var fullPath = Path.GetFullPath(gameDirectory);
        return new MinecraftPath(fullPath);
    }

    private static bool IsInstalled(MinecraftPath path, string versionName)
    {
        var jsonPath = Path.Combine(path.Versions, versionName, versionName + ".json");
        return File.Exists(jsonPath);
    }

    private sealed class BmclVersionLoader : IVersionLoader
    {
        private const string ManifestUrl =
            DownloadSourceRouter.BmclApiBase + "/mc/game/version_manifest_v2.json";

        private readonly MinecraftPath _path;
        private readonly HttpClient _httpClient;

        public BmclVersionLoader(MinecraftPath path, HttpClient httpClient)
        {
            _path = path;
            _httpClient = httpClient;
        }

        public async ValueTask<VersionMetadataCollection> GetVersionMetadatasAsync(
            CancellationToken cancellationToken = default)
        {
            var local = await new LocalJsonVersionLoader(_path)
                .GetVersionMetadatasAsync(cancellationToken)
                .ConfigureAwait(false);

            VersionMetadataCollection remote;
            try
            {
                using var response = await _httpClient.GetAsync(
                        ManifestUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(
                        stream,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var items = new List<IVersionMetadata>();
                string? latestRelease = null;
                string? latestSnapshot = null;

                if (document.RootElement.TryGetProperty("latest", out var latest))
                {
                    latestRelease = GetString(latest, "release");
                    latestSnapshot = GetString(latest, "snapshot");
                }

                if (document.RootElement.TryGetProperty("versions", out var versions)
                    && versions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var version in versions.EnumerateArray())
                    {
                        var id = GetString(version, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        var officialUrl = GetString(version, "url");
                        var releaseTime = GetString(version, "releaseTime");
                        var model = new JsonVersionMetadataModel
                        {
                            Id = id,
                            Type = GetString(version, "type"),
                            ReleaseTime = DateTimeOffset.TryParse(
                                releaseTime,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal,
                                out var parsed)
                                ? parsed
                                : DateTimeOffset.MinValue,
                            Url = officialUrl
                        };

                        var primaryUrl = new Uri(
                            $"{DownloadSourceRouter.BmclApiBase}/version/{Uri.EscapeDataString(id)}/json");
                        var fallbackUrl = Uri.TryCreate(officialUrl, UriKind.Absolute, out var official)
                            ? official
                            : null;
                        items.Add(new BmclVersionMetadata(model, _httpClient, primaryUrl, fallbackUrl));
                    }
                }

                remote = new VersionMetadataCollection(items, latestRelease, latestSnapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       || !cancellationToken.IsCancellationRequested)
            {
                // The mirror manifest is unavailable: keep the official loader.
                return await new MojangJsonVersionLoaderV2(_path, _httpClient)
                    .GetVersionMetadatasAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return MergeLocal(local, remote);
        }

        private static VersionMetadataCollection MergeLocal(
            VersionMetadataCollection local,
            VersionMetadataCollection remote)
        {
            var names = new HashSet<string>(
                local.Select(version => version.Name),
                StringComparer.OrdinalIgnoreCase);
            var items = local.ToList();

            foreach (var version in remote)
            {
                if (names.Add(version.Name))
                    items.Add(version);
            }

            return new VersionMetadataCollection(items, remote.LatestReleaseName, remote.LatestSnapshotName);
        }

        private static string? GetString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private sealed class BmclVersionMetadata : JsonVersionMetadata
    {
        private readonly HttpClient _httpClient;
        private readonly Uri _primaryUrl;
        private readonly Uri? _fallbackUrl;

        public BmclVersionMetadata(
            JsonVersionMetadataModel model,
            HttpClient httpClient,
            Uri primaryUrl,
            Uri? fallbackUrl)
            : base(model)
        {
            _httpClient = httpClient;
            _primaryUrl = primaryUrl;
            _fallbackUrl = fallbackUrl;
        }

        protected override async ValueTask<Stream> GetVersionJsonStream(
            CancellationToken cancellationToken)
        {
            try
            {
                return await OpenVersionJsonAsync(_primaryUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_fallbackUrl is not null
                                       && !cancellationToken.IsCancellationRequested
                                       && ex is HttpRequestException or OperationCanceledException)
            {
                return await OpenVersionJsonAsync(_fallbackUrl, cancellationToken).ConfigureAwait(false);
            }
        }

        private async ValueTask<Stream> OpenVersionJsonAsync(
            Uri url,
            CancellationToken cancellationToken)
        {
            var response = await _httpClient.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Feeds a pre-downloaded Forge/NeoForge installer to CmlLib while
    /// delegating every other library download to the launcher's installer.
    /// This avoids mirror redirect/download issues in CmlLib's file client.
    /// </summary>
    private sealed class LocalInstallerOverride : IGameInstaller
    {
        private readonly IGameInstaller _inner;
        private readonly string _installerUrl;
        private readonly string _localInstallerPath;
        private readonly Func<string, string, CancellationToken, Task>? _remoteFileDownloader;

        public LocalInstallerOverride(
            IGameInstaller inner,
            string installerUrl,
            string localInstallerPath,
            Func<string, string, CancellationToken, Task>? remoteFileDownloader)
        {
            _inner = inner;
            _installerUrl = installerUrl;
            _localInstallerPath = localInstallerPath;
            _remoteFileDownloader = remoteFileDownloader;
        }

        public async ValueTask Install(
            IEnumerable<GameFile> gameFiles,
            IProgress<InstallerProgressChangedEventArgs>? fileProgress,
            IProgress<ByteProgress>? byteProgress,
            CancellationToken cancellationToken)
        {
            var remaining = new List<GameFile>();

            foreach (var file in gameFiles)
            {
                if (string.IsNullOrWhiteSpace(file.Path))
                {
                    remaining.Add(file);
                    continue;
                }

                if (string.Equals(file.Url, _installerUrl, StringComparison.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                    File.Copy(_localInstallerPath, file.Path, overwrite: true);
                    ReportFile(fileProgress, file);
                    continue;
                }

                if (_remoteFileDownloader is not null
                    && TryGetMirroredUrl(file.Url, out var mirroredUrl))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                    await _remoteFileDownloader(mirroredUrl, file.Path, cancellationToken)
                        .ConfigureAwait(false);
                    ReportFile(fileProgress, file);
                    continue;
                }

                remaining.Add(file);
            }

            if (remaining.Count > 0)
            {
                await _inner.Install(remaining, fileProgress, byteProgress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static void ReportFile(
            IProgress<InstallerProgressChangedEventArgs>? fileProgress,
            GameFile file)
        {
            fileProgress?.Report(new InstallerProgressChangedEventArgs(
                1,
                1,
                file.Name,
                InstallerEventType.Done));
        }

        private static bool TryGetMirroredUrl(string? url, out string mirroredUrl)
        {
            mirroredUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (!string.Equals(uri.Host, "maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Host, "maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var path = uri.AbsolutePath;
            if (path.StartsWith("/releases/", StringComparison.OrdinalIgnoreCase))
                path = path["/releases".Length..];
            else if (path.StartsWith("/snapshots/", StringComparison.OrdinalIgnoreCase))
                path = path["/snapshots".Length..];

            mirroredUrl = BmclMirrorBase + path;
            return true;
        }
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using CmlLib.Core;
using Microsoft.Win32;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>Result of an explicit Java scan requested by the user.</summary>
public sealed record JavaScanResult(IReadOnlyList<JavaRuntimeInfo> Runtimes, DateTimeOffset ScannedAt);

/// <summary>
/// Finds Java runtimes installed on the machine and caches them in
/// <c>%APPDATA%\PMCL\java-runtimes.json</c>. Startup only reads the cache;
/// the file system and registry are touched when <see cref="ScanAsync"/> is
/// called from the UI's "Scan Java" button.
/// </summary>
public sealed class JavaRuntimeService
{
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string[] WindowsExecutableNames = { "javaw.exe", "java.exe" };
    private static readonly string[] UnixExecutableNames = { "java" };

    private static readonly string[] RegistryValueNames =
    {
        "JavaHome", "JDK_HOME", "JRE_HOME", "InstallationPath", "InstallLocation", "Home", "Path"
    };

    private static readonly string[] RegistryRoots =
    {
        @"SOFTWARE\JavaSoft\Java Runtime Environment",
        @"SOFTWARE\JavaSoft\Java Development Kit",
        @"SOFTWARE\JavaSoft\JRE",
        @"SOFTWARE\JavaSoft\JDK",
        @"SOFTWARE\Eclipse Adoptium",
        @"SOFTWARE\Eclipse Foundation",
        @"SOFTWARE\AdoptOpenJDK",
        @"SOFTWARE\Microsoft\JDK",
        @"SOFTWARE\Azul Systems\Zulu",
        @"SOFTWARE\Amazon Corretto",
        @"SOFTWARE\BellSoft",
        @"SOFTWARE\Semeru",
        @"SOFTWARE\GraalVM"
    };

    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private JavaRuntimeCache? _cache;
    private bool _cacheLoaded;

    public JavaRuntimeService(AppPaths paths)
    {
        _paths = paths;
    }

    public string CacheFile => Path.Combine(_paths.DataDirectory, "java-runtimes.json");

    /// <summary>Runtimes from the last explicit scan. Never triggers a scan.</summary>
    public IReadOnlyList<JavaRuntimeInfo> CachedRuntimes
    {
        get
        {
            lock (_gate)
            {
                EnsureCacheLocked();
                return _cache!.Runtimes.ToArray();
            }
        }
    }

    /// <summary>Timestamp of the last explicit scan, or null when never scanned.</summary>
    public DateTimeOffset? LastScannedAt
    {
        get
        {
            lock (_gate)
            {
                EnsureCacheLocked();
                return _cache!.LastScannedAt;
            }
        }
    }

    /// <summary>
    /// Performs a full scan of system locations and the shared Mojang
    /// <c>runtime</c> folder, then updates the cache.
    /// </summary>
    public async Task<JavaScanResult> ScanAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(
                () => Scan(gameDirectory, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        lock (_gate)
        {
            _cache = new JavaRuntimeCache
            {
                LastScannedAt = result.ScannedAt,
                Runtimes = result.Runtimes.ToList()
            };
            _cacheLoaded = true;
            TrySaveCacheLocked(_cache);
        }

        return result;
    }

    private JavaScanResult Scan(string gameDirectory, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mojangRoots = new List<string>();

        AddMojangRuntimeCandidates(gameDirectory, candidates, mojangRoots);
        AddEnvironmentCandidates(candidates);
        AddKnownDirectoryCandidates(candidates);
        if (OperatingSystem.IsWindows())
            AddRegistryCandidates(candidates);

        var runtimes = new List<JavaRuntimeInfo>();
        foreach (var executable in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var info = Describe(executable, mojangRoots, cancellationToken);
                if (info is not null)
                    runtimes.Add(info);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A single broken runtime must not abort the whole scan.
            }
        }

        var ordered = runtimes
            .GroupBy(runtime => GetRuntimeHome(runtime.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(runtime => GetExecutablePriority(runtime.Path))
                .ThenBy(runtime => runtime.Path, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(runtime => runtime.MajorVersion)
            .ThenBy(runtime => runtime.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new JavaScanResult(ordered, DateTimeOffset.Now);
    }

    /// <summary>
    /// Returns the runtime folder for a Java executable so the same runtime is
    /// only listed once even when both javaw.exe and java.exe were discovered.
    /// </summary>
    private static string GetRuntimeHome(string executable)
    {
        try
        {
            var directory = Path.GetDirectoryName(executable);
            if (directory is not null
                && string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(directory) ?? directory;
            }

            return directory ?? executable;
        }
        catch
        {
            return executable;
        }
    }

    /// <summary>
    /// Prefers javaw.exe on Windows so launching a game does not open a console.
    /// </summary>
    private static int GetExecutablePriority(string executable)
    {
        var fileName = Path.GetFileName(executable);
        return fileName.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private void EnsureCacheLocked()
    {
        if (_cacheLoaded)
            return;

        _cacheLoaded = true;

        try
        {
            if (File.Exists(CacheFile))
            {
                var json = File.ReadAllText(CacheFile);
                var cache = JsonSerializer.Deserialize<JavaRuntimeCache>(json, CacheJsonOptions);
                if (cache is not null)
                {
                    cache.Runtimes = cache.Runtimes
                        .Where(runtime => runtime is not null
                                          && !string.IsNullOrWhiteSpace(runtime.Path)
                                          && File.Exists(runtime.Path))
                        .ToList();
                    _cache = cache;
                    return;
                }
            }
        }
        catch
        {
            // A broken cache is ignored; the next scan rewrites it.
        }

        _cache = new JavaRuntimeCache();
    }

    private void TrySaveCacheLocked(JavaRuntimeCache cache)
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            var tempFile = CacheFile + ".tmp";
            File.WriteAllText(tempFile, JsonSerializer.Serialize(cache, CacheJsonOptions));
            File.Move(tempFile, CacheFile, overwrite: true);
        }
        catch
        {
            // Caching is best-effort; a failed write only means the next start
            // shows the previous scan result.
        }
    }

    private static void AddMojangRuntimeCandidates(
        string gameDirectory,
        HashSet<string> candidates,
        List<string> mojangRoots)
    {
        var runtimeRoots = new List<string>();

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return;

            try
            {
                if (Directory.Exists(root))
                    runtimeRoots.Add(Path.GetFullPath(root));
            }
            catch
            {
                // Ignore invalid paths.
            }
        }

        AddRoot(Path.Combine(gameDirectory, "runtime"));
        AddRoot(Path.Combine(MinecraftPath.GetOSDefaultPath(), "runtime"));

        foreach (var root in runtimeRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!mojangRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                mojangRoots.Add(root);

            // CmlLib stores runtimes as runtime/<platform>/<component>/bin.
            // Only the first executable per runtime home is kept so javaw.exe
            // and java.exe do not appear as two separate dropdown entries.
            var runtimeHomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var executableName in GetExecutableNames())
            {
                try
                {
                    foreach (var executable in Directory.EnumerateFiles(
                                 root,
                                 executableName,
                                 SearchOption.AllDirectories))
                    {
                        var parent = Path.GetDirectoryName(executable);
                        if (parent is null
                            || !string.Equals(Path.GetFileName(parent), "bin", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var home = Path.GetDirectoryName(parent);
                        if (home is null || !runtimeHomes.Add(Path.GetFullPath(home)))
                            continue;

                        candidates.Add(Path.GetFullPath(executable));
                    }
                }
                catch
                {
                    // An unreadable runtime folder is skipped.
                }
            }
        }
    }

    private static void AddEnvironmentCandidates(HashSet<string> candidates)
    {
        AddJavaHome(candidates, Environment.GetEnvironmentVariable("JAVA_HOME"));
        AddJavaHome(candidates, Environment.GetEnvironmentVariable("JDK_HOME"));
        AddJavaHome(candidates, Environment.GetEnvironmentVariable("JRE_HOME"));

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return;

        foreach (var entry in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The Windows Store alias is not a real runtime and often fails to start.
            if (entry.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                continue;

            var directory = entry.Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            foreach (var executableName in GetExecutableNames())
            {
                try
                {
                    var executable = Path.Combine(directory, executableName);
                    if (File.Exists(executable))
                        candidates.Add(Path.GetFullPath(executable));
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }

            if (string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase))
                AddJavaHome(candidates, Path.GetDirectoryName(directory));
            else
                AddJavaHome(candidates, directory);
        }
    }

    private static void AddKnownDirectoryCandidates(HashSet<string> candidates)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var roots = new[]
            {
                Path.Combine(programFiles, "Java"),
                Path.Combine(programFilesX86, "Java"),
                Path.Combine(programFiles, "Eclipse Adoptium"),
                Path.Combine(programFiles, "Eclipse Foundation"),
                Path.Combine(programFiles, "AdoptOpenJDK"),
                Path.Combine(programFiles, "Microsoft"),
                Path.Combine(programFiles, "Amazon Corretto"),
                Path.Combine(programFiles, "Zulu"),
                Path.Combine(programFiles, "BellSoft"),
                Path.Combine(programFiles, "Semeru"),
                Path.Combine(programFiles, "GraalVM"),
                Path.Combine(localAppData, "Programs", "Eclipse Adoptium"),
                Path.Combine(localAppData, "Programs", "Microsoft"),
                Path.Combine(localAppData, "Programs", "Amazon Corretto"),
                Path.Combine(localAppData, "Programs", "Zulu"),
                Path.Combine(localAppData, "Programs", "BellSoft"),
                Path.Combine(userProfile, ".jdks"),
                Path.Combine(userProfile, ".gradle", "jdks"),
                Path.Combine(userProfile, ".sdkman", "candidates", "java"),
                Path.Combine(userProfile, "scoop", "apps")
            };

            foreach (var root in roots)
                AddHomesUnder(candidates, root, maxDepth: 3);

            // JetBrains runtime lives deep inside the Toolbox app folder.
            AddHomesUnder(
                candidates,
                Path.Combine(localAppData, "JetBrains", "Toolbox", "apps"),
                maxDepth: 6);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var roots = new[]
            {
                "/Library/Java/JavaVirtualMachines",
                "/System/Library/Java/JavaVirtualMachines",
                Path.Combine(userProfile, "Library", "Java", "JavaVirtualMachines")
            };

            foreach (var root in roots)
                AddHomesUnder(candidates, root, maxDepth: 4);

            return;
        }

        var linuxRoots = new[]
        {
            "/usr/lib/jvm",
            "/usr/java",
            "/usr/local/java",
            "/opt/java",
            "/opt/jdk",
            Path.Combine(userProfile, ".jdks")
        };

        foreach (var root in linuxRoots)
            AddHomesUnder(candidates, root, maxDepth: 3);
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryCandidates(HashSet<string> candidates)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var root in RegistryRoots)
                {
                    using var key = machine.OpenSubKey(root);
                    if (key is not null)
                        CollectRegistryHomes(key, depth: 2, candidates);
                }

                AddUninstallCandidates(machine, candidates);
            }
            catch
            {
                // Registry access can be blocked by policy; filesystem detection
                // above still runs.
            }

            try
            {
                using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                foreach (var root in RegistryRoots)
                {
                    using var key = currentUser.OpenSubKey(root);
                    if (key is not null)
                        CollectRegistryHomes(key, depth: 2, candidates);
                }

                AddUninstallCandidates(currentUser, candidates);
            }
            catch
            {
                // Ignore a missing/blocked HKCU hive.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CollectRegistryHomes(
        RegistryKey key,
        int depth,
        HashSet<string> candidates)
    {
        foreach (var valueName in RegistryValueNames)
        {
            try
            {
                if (key.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                    AddJavaHome(candidates, value);
            }
            catch
            {
                // Ignore unreadable values.
            }
        }

        if (depth <= 0)
            return;

        foreach (var subKeyName in SafeSubKeyNames(key))
        {
            try
            {
                using var child = key.OpenSubKey(subKeyName);
                if (child is not null)
                    CollectRegistryHomes(child, depth - 1, candidates);
            }
            catch
            {
                // Ignore unreadable keys.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddUninstallCandidates(RegistryKey baseKey, HashSet<string> candidates)
    {
        try
        {
            using var uninstall = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
                return;

            foreach (var subKeyName in SafeSubKeyNames(uninstall))
            {
                try
                {
                    using var app = uninstall.OpenSubKey(subKeyName);
                    if (app is null)
                        continue;

                    var displayName = app.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)
                        || (!displayName.Contains("Java", StringComparison.OrdinalIgnoreCase)
                            && !displayName.Contains("JDK", StringComparison.OrdinalIgnoreCase)
                            && !displayName.Contains("JRE", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (app.GetValue("InstallLocation") is string location
                        && !string.IsNullOrWhiteSpace(location))
                    {
                        AddJavaHome(candidates, location);
                        AddHomesUnder(candidates, location, maxDepth: 2);
                    }
                }
                catch
                {
                    // Ignore individual uninstall entries.
                }
            }
        }
        catch
        {
            // Ignore a missing uninstall key.
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> SafeSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void AddHomesUnder(HashSet<string> candidates, string? root, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        try
        {
            if (!Directory.Exists(root))
                return;

            AddHomeWithinDepth(candidates, Path.GetFullPath(root), depth: 0, maxDepth);
        }
        catch
        {
            // Ignore unreadable install roots.
        }
    }

    private static void AddHomeWithinDepth(
        HashSet<string> candidates,
        string directory,
        int depth,
        int maxDepth)
    {
        if (HasJavaHome(candidates, directory))
            return;

        if (depth >= maxDepth)
            return;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(directory);
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name is "bin" or "lib" or "conf" or "include" or "jmods" or "legal" or "man")
                continue;

            AddHomeWithinDepth(candidates, child, depth + 1, maxDepth);
        }
    }

    private static void AddJavaHome(HashSet<string> candidates, string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return;

        try
        {
            var directory = Path.GetFullPath(home.Trim().Trim('"'));
            if (!Directory.Exists(directory))
                return;

            foreach (var executableName in GetExecutableNames())
            {
                var executable = Path.Combine(directory, "bin", executableName);
                if (File.Exists(executable))
                {
                    candidates.Add(Path.GetFullPath(executable));
                    return;
                }
            }
        }
        catch
        {
            // Ignore malformed paths.
        }
    }

    private static bool HasJavaHome(HashSet<string> candidates, string directory)
    {
        foreach (var executableName in GetExecutableNames())
        {
            try
            {
                var executable = Path.Combine(directory, "bin", executableName);
                if (File.Exists(executable))
                {
                    candidates.Add(Path.GetFullPath(executable));
                    return true;
                }
            }
            catch
            {
                // Ignore malformed paths.
            }
        }

        return false;
    }

    private static JavaRuntimeInfo? Describe(
        string executable,
        IReadOnlyList<string> mojangRoots,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
            return null;

        var isMojang = mojangRoots.Any(root => IsUnder(executable, root));
        var home = Path.GetDirectoryName(Path.GetDirectoryName(executable));
        var (version, vendor, architecture) = ReadReleaseFile(home);

        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(architecture))
        {
            var probed = ProbeRuntime(executable, cancellationToken);
            if (string.IsNullOrWhiteSpace(version))
                version = probed.Version;
            if (string.IsNullOrWhiteSpace(vendor))
                vendor = probed.Vendor;
            if (string.IsNullOrWhiteSpace(architecture))
                architecture = probed.Architecture;
        }

        var normalizedArchitecture = NormalizeArchitecture(architecture);
        if (string.IsNullOrWhiteSpace(normalizedArchitecture))
            normalizedArchitecture = GuessArchitectureFromPath(executable);

        return new JavaRuntimeInfo(
            executable,
            version ?? string.Empty,
            ParseMajorVersion(version),
            normalizedArchitecture,
            vendor ?? string.Empty,
            isMojang ? JavaRuntimeSource.Mojang : JavaRuntimeSource.System);
    }

    private static (string Version, string Vendor, string Architecture) ReadReleaseFile(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
            return (string.Empty, string.Empty, string.Empty);

        var releaseFile = Path.Combine(home, "release");
        if (!File.Exists(releaseFile))
            return (string.Empty, string.Empty, string.Empty);

        var version = string.Empty;
        var vendor = string.Empty;
        var architecture = string.Empty;

        try
        {
            foreach (var line in File.ReadLines(releaseFile))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"');

                switch (key)
                {
                    case "JAVA_VERSION":
                        version = value;
                        break;
                    case "IMPLEMENTOR":
                        vendor = value;
                        break;
                    case "JAVA_VENDOR" when string.IsNullOrWhiteSpace(vendor):
                        vendor = value;
                        break;
                    case "OS_ARCH":
                        architecture = value;
                        break;
                }
            }
        }
        catch
        {
            // The probe below can still describe the runtime.
        }

        return (version, vendor, architecture);
    }

    private static (string Version, string Vendor, string Architecture) ProbeRuntime(
        string executable,
        CancellationToken cancellationToken)
    {
        var probe = executable;
        if (OperatingSystem.IsWindows()
            && Path.GetFileName(executable).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            var consoleExecutable = Path.Combine(
                Path.GetDirectoryName(executable) ?? string.Empty,
                "java.exe");
            if (File.Exists(consoleExecutable))
                probe = consoleExecutable;
        }

        try
        {
            var startInfo = new ProcessStartInfo(probe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-XshowSettings:properties");
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process is null)
                return (string.Empty, string.Empty, string.Empty);

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process might have exited between the timeout and the kill.
                }

                return (string.Empty, string.Empty, string.Empty);
            }

            var output = outputTask.GetAwaiter().GetResult()
                         + Environment.NewLine
                         + errorTask.GetAwaiter().GetResult();

            return (
                ReadProperty(output, "java.version"),
                ReadProperty(output, "java.vendor"),
                ReadProperty(output, "os.arch"));
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    private static string ReadProperty(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(name, StringComparison.Ordinal))
                continue;

            var next = trimmed.Length > name.Length ? trimmed[name.Length] : '\0';
            if (next is not ('=' or ' ' or '\t'))
                continue;

            var separator = trimmed.IndexOf('=');
            if (separator < 0)
                continue;

            return trimmed[(separator + 1)..].Trim();
        }

        return string.Empty;
    }

    private static int ParseMajorVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return 0;

        var text = version.Trim();
        if (text.StartsWith("1.", StringComparison.Ordinal))
            text = text[2..];

        var digits = 0;
        while (digits < text.Length && char.IsDigit(text[digits]))
            digits++;

        return digits > 0 && int.TryParse(text[..digits], out var major) ? major : 0;
    }

    private static string NormalizeArchitecture(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
            return string.Empty;

        return architecture.Trim().ToLowerInvariant() switch
        {
            "x86_64" or "amd64" or "x64" => "x64",
            "x86" or "i386" or "i686" => "x86",
            "aarch64" or "arm64" => "ARM64",
            _ => architecture.Trim()
        };
    }

    private static string GuessArchitectureFromPath(string path)
    {
        var text = path.ToLowerInvariant();

        if (text.Contains("arm64", StringComparison.Ordinal)
            || text.Contains("aarch64", StringComparison.Ordinal))
        {
            return "ARM64";
        }

        if (text.Contains("x86", StringComparison.Ordinal)
            && !text.Contains("x64", StringComparison.Ordinal)
            && !text.Contains("x86_64", StringComparison.Ordinal))
        {
            return "x86";
        }

        if (text.Contains("x64", StringComparison.Ordinal)
            || text.Contains("amd64", StringComparison.Ordinal)
            || text.Contains("x86_64", StringComparison.Ordinal))
        {
            return "x64";
        }

        return string.Empty;
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var prefix = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return Path.GetFullPath(path).StartsWith(prefix, comparison);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetExecutableNames() =>
        OperatingSystem.IsWindows() ? WindowsExecutableNames : UnixExecutableNames;

    private sealed class JavaRuntimeCache
    {
        public DateTimeOffset? LastScannedAt { get; set; }

        public List<JavaRuntimeInfo> Runtimes { get; set; } = new();
    }
}

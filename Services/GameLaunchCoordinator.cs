using System.Diagnostics;
using System.Text;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using PMCL.Models;
using PMCL.Localization;
using PMCL.ViewModels;

namespace PMCL.Services;

/// <summary>
/// Orchestrates installation, per-version launch preparation and game launching.
/// </summary>
public sealed class GameLaunchCoordinator
{
    private static readonly TimeSpan PreLaunchTimeout = TimeSpan.FromMinutes(5);

    private readonly MinecraftService _minecraft;
    private readonly SettingsService _settingsService;
    private readonly ModLoaderService _modLoaderService;

    public GameLaunchCoordinator(
        MinecraftService minecraft,
        SettingsService settingsService,
        ModLoaderService? modLoaderService = null)
    {
        _minecraft = minecraft;
        _settingsService = settingsService;
        _modLoaderService = modLoaderService ?? new ModLoaderService(minecraft);
    }

    public async Task InstallAsync(LauncherSession session, GameVersion version)
    {
        if (session.IsBusy)
            return;

        session.BeginBusy(L.Format("Launch.PreparingDownload", version.Name));

        try
        {
            await InstallCoreAsync(session, version, CancellationToken.None);
            session.MarkInstalled(version.Name);
            session.SetStatus(L.Format("Launch.DownloadComplete", version.Name));
        }
        catch (Exception ex)
        {
            session.SetError(L.Format("Launch.DownloadFailed", version.Name, ex.Message));
        }
        finally
        {
            session.EndBusy();
        }
    }

    /// <summary>
    /// Verifies/re-downloads the files of an already installed version.
    /// </summary>
    public async Task RepairAsync(LauncherSession session, GameVersion version)
    {
        if (session.IsBusy)
            return;

        session.BeginBusy(L.Format("Launch.RepairingFiles", version.Name));

        try
        {
            if (version.IsModded && !string.IsNullOrWhiteSpace(version.LoaderVersion))
            {
                var settings = session.Settings.GetVersionSettings(version.Name);
                var fileProgress = CreateFileProgress(session, version.Name);
                var byteProgress = CreateByteProgress(session);
                var installerOutput = new Progress<string>(line =>
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        session.SetStatus(line.Trim());
                });

                await _modLoaderService.InstallAsync(
                        session.Settings.GameDirectory,
                        version.BaseVersionName,
                        version.LoaderKind,
                        version.LoaderVersion,
                        settings.JavaPath,
                        fileProgress,
                        byteProgress,
                        installerOutput,
                        CancellationToken.None)
                    .ConfigureAwait(true);
            }
            else
            {
                await InstallCoreAsync(session, version, CancellationToken.None);
            }

            session.MarkInstalled(version.Name);
            session.SetStatus(L.Format("Launch.RepairComplete", version.Name));
        }
        catch (Exception ex)
        {
            session.SetError(L.Format("Launch.RepairFailed", version.Name, ex.Message));
        }
        finally
        {
            session.EndBusy();
        }
    }

    /// <summary>
    /// Installs a vanilla version or a mod loader profile and enforces one
    /// loader per base Minecraft version by removing older loader profiles.
    /// </summary>
    public async Task<string?> InstallModLoaderAsync(
        LauncherSession session,
        GameVersion baseVersion,
        ModLoaderKind kind,
        string? loaderVersion,
        string javaPath,
        IReadOnlyList<string> existingLoaderNames,
        string? sourceVersionName = null)
    {
        if (session.IsBusy)
            return null;

        session.BeginBusy(kind == ModLoaderKind.Vanilla
            ? L.Format("Launch.DownloadingVanilla", baseVersion.Name)
            : L.Format(
                "Launch.PreparingLoader",
                kind.ToDisplayName(),
                kind.FormatLoaderVersion(loaderVersion ?? string.Empty)));

        try
        {
            var effectiveSourceVersion = sourceVersionName ?? baseVersion.Name;
            if (kind != ModLoaderKind.Vanilla
                && string.Equals(effectiveSourceVersion, baseVersion.Name, StringComparison.OrdinalIgnoreCase))
            {
                var existingProfiles = _minecraft.GetInstalledLoaderProfiles(
                    session.Settings.GameDirectory,
                    baseVersion.Name);
                var preferredProfile = existingProfiles.FirstOrDefault(profile => profile.Kind == kind)
                                       ?? existingProfiles.FirstOrDefault();
                if (preferredProfile is not null)
                    effectiveSourceVersion = preferredProfile.Name;
            }

            var sourceSettings = session.Settings
                .GetVersionSettings(effectiveSourceVersion)
                .Clone();

            var fileProgress = CreateFileProgress(session, baseVersion.Name);
            var byteProgress = CreateByteProgress(session);
            var installerOutput = new Progress<string>(line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    session.SetStatus(line.Trim());
            });

            var result = await _modLoaderService.InstallAsync(
                    session.Settings.GameDirectory,
                    baseVersion.Name,
                    kind,
                    loaderVersion,
                    javaPath,
                    fileProgress,
                    byteProgress,
                    installerOutput,
                    CancellationToken.None)
                .ConfigureAwait(true);

            if (kind == ModLoaderKind.Vanilla)
            {
                session.MarkInstalled(baseVersion.Name);
                _settingsService.SaveSilently(session.Settings);
                session.SetStatus(L.Format("Launch.DownloadComplete", baseVersion.Name));
                return result.VersionName;
            }

            var installedSettings = session.Settings.GetVersionSettings(result.VersionName);
            installedSettings.CopyFrom(sourceSettings);

            var actualLoaders = _minecraft.GetInstalledLoaderProfiles(
                session.Settings.GameDirectory,
                baseVersion.Name);
            var removeNames = existingLoaderNames
                .Concat(actualLoaders.Select(profile => profile.Name))
                .Where(name => !string.Equals(
                    name,
                    result.VersionName,
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            // Saves/mods live inside each version folder. Copy them into the
            // new profile before the old loader folders are deleted so
            // switching loaders never loses user data.
            var targetGameDirectory = _minecraft.GetGameDirectory(
                session.Settings.GameDirectory,
                result.VersionName);

            var mergeSourceVersion = effectiveSourceVersion;
            await Task.Run(() => VersionIsolationService.MergeVersionData(
                    session.Settings.GameDirectory,
                    mergeSourceVersion,
                    targetGameDirectory))
                .ConfigureAwait(true);

            foreach (var removeName in removeNames)
            {
                var nameToMerge = removeName;
                var mergeTarget = targetGameDirectory;
                await Task.Run(() => VersionIsolationService.MergeVersionData(
                        session.Settings.GameDirectory,
                        nameToMerge,
                        mergeTarget))
                    .ConfigureAwait(true);

                var oldVersion = session.Versions.FirstOrDefault(version =>
                    string.Equals(version.Name, removeName, StringComparison.OrdinalIgnoreCase));
                if (oldVersion is not null)
                    session.RemoveVersion(oldVersion);
                else
                    session.Settings.RemoveVersionSettings(removeName);

                await Task.Run(
                        () => _minecraft.DeleteVersion(session.Settings.GameDirectory, removeName))
                    .ConfigureAwait(true);
            }

            _settingsService.SaveSilently(session.Settings);

            session.SetStatus(L.Format(
                "Launch.LoaderInstalled",
                kind.ToDisplayName(),
                kind.FormatLoaderVersion(loaderVersion ?? string.Empty),
                result.VersionName));
            return result.VersionName;
        }
        catch (Exception ex)
        {
            session.SetError(
                kind == ModLoaderKind.Vanilla
                    ? L.Format("Launch.DownloadFailed", baseVersion.Name, ex.Message)
                    : L.Format("Launch.LoaderInstallFailed", kind.ToDisplayName(), ex.Message));
            return null;
        }
        finally
        {
            session.EndBusy();
        }
    }

    /// <summary>
    /// Deletes a loader profile while keeping the vanilla base version. The
    /// loader's launch settings are copied back to the base when possible.
    /// </summary>
    public async Task<bool> RemoveModLoaderAsync(LauncherSession session, GameVersion version)
    {
        if (session.IsBusy || !version.IsModded)
            return false;

        var gameDirectory = session.Settings.GameDirectory;
        var baseVersionName = version.BaseVersionName;
        if (string.IsNullOrWhiteSpace(baseVersionName)
            || string.Equals(baseVersionName, version.Name, StringComparison.OrdinalIgnoreCase))
        {
            session.SetError(L.Tr("Launch.LoaderBaseUnknown"));
            return false;
        }

        session.BeginBusy(L.Format("Launch.RemovingLoader", version.LoaderLabel));

        try
        {
            var loaderSettings = session.Settings.GetVersionSettings(version.Name).Clone();
            var baseSettings = session.Settings.GetVersionSettings(baseVersionName);
            baseSettings.CopyFrom(loaderSettings);

            // The loader folder may contain saves/mods. Move them to the base
            // version folder before deleting it.
            var mergeSource = version.Name;
            var mergeTarget = _minecraft.GetGameDirectory(
                gameDirectory,
                baseVersionName);
            await Task.Run(() => VersionIsolationService.MergeVersionData(
                    gameDirectory,
                    mergeSource,
                    mergeTarget))
                .ConfigureAwait(true);

            await Task.Run(() => _minecraft.DeleteVersion(gameDirectory, version.Name))
                .ConfigureAwait(true);
            session.RemoveVersion(version);
            _settingsService.SaveSilently(session.Settings);

            session.SetStatus(L.Format(
                "Launch.LoaderRemoved",
                version.LoaderLabel,
                baseVersionName));
            return true;
        }
        catch (Exception ex)
        {
            session.SetError(L.Format("Launch.RemoveLoaderFailed", ex.Message));
            return false;
        }
        finally
        {
            session.EndBusy();
        }
    }

    private static Progress<InstallerProgressChangedEventArgs> CreateFileProgress(
        LauncherSession session,
        string displayName)
        => new(args =>
        {
            session.IsProgressIndeterminate = false;
            var total = Math.Max(1, args.TotalTasks);
            var ratio = Math.Clamp((double)args.ProgressedTasks / total, 0, 1);

            // A new installation phase starts with queued files. Reset the bar
            // here so a finished installer download does not leave it at 100%.
            if (args.EventType == InstallerEventType.Queued && args.ProgressedTasks == 0)
                session.Progress = 0;
            else
                session.Progress = Math.Max(session.Progress, ratio);

            session.SetStatus(L.Format(
                "Launch.DownloadingFile",
                displayName,
                args.ProgressedTasks,
                args.TotalTasks,
                args.Name));
        });

    private static Progress<ByteProgress> CreateByteProgress(LauncherSession session)
    {
        var speedTracker = new DownloadSpeedTracker();
        return new(progress =>
        {
            if (progress.TotalBytes <= 0)
                return;

            session.IsProgressIndeterminate = false;
            var ratio = progress.ToRatio();
            if (ratio is >= 0 and <= 1)
                session.Progress = Math.Max(session.Progress, ratio);

            var sample = speedTracker.Update(progress);
            if (sample is { } value)
                session.UpdateDownloadMetrics(
                    value.ProgressedBytes,
                    value.TotalBytes,
                    value.BytesPerSecond);
        });
    }

    private async Task<Process> BuildLaunchProcessAsync(
        LauncherSession session,
        GameVersion version,
        MSession login,
        GameVersionSettings settings)
    {
        try
        {
            return await _minecraft.BuildProcessAsync(
                    session.Settings.GameDirectory,
                    version.Name,
                    login,
                    settings,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (IsMissingFileException(ex))
        {
            // Building the process extracts natives. A partially installed
            // version can fail here before the classpath can be inspected.
            await RepairLaunchFilesAsync(session, version).ConfigureAwait(true);
            return await _minecraft.BuildProcessAsync(
                    session.Settings.GameDirectory,
                    version.Name,
                    login,
                    settings,
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
    }

    private async Task RepairLaunchFilesAsync(
        LauncherSession session,
        GameVersion version)
    {
        session.IsProgressIndeterminate = true;
        session.SetStatus(L.Tr("Launch.RepairingLaunchFiles"));
        await _minecraft.InstallAsync(
                session.Settings.GameDirectory,
                version.Name,
                CreateFileProgress(session, version.Name),
                CreateByteProgress(session),
                CancellationToken.None)
            .ConfigureAwait(true);
    }

    private static bool IsMissingFileException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FileNotFoundException or DirectoryNotFoundException)
                return true;
        }

        return false;
    }

    public async Task<Process?> LaunchAsync(LauncherSession session, GameVersion version)
    {
        if (session.IsBusy || session.IsGameRunning)
            return null;

        var settings = session.Settings.GetVersionSettings(version.Name);
        MSession login;

        try
        {
            login = ResolveLogin(session, settings);
            ValidateVersionSettings(settings);
        }
        catch (Exception ex)
        {
            session.SetError(L.Format("Launch.LaunchError", version.Name, ex.Message));
            return null;
        }

        session.BeginGameStarting();
        session.BeginBusy(L.Format("Launch.StartingGame", version.Name));

        try
        {
            if (!version.IsInstalled)
                await InstallCoreAsync(session, version, CancellationToken.None);

            session.IsProgressIndeterminate = true;
            session.SetStatus(L.Tr("Launch.BuildingProcess"));

            var gameDirectory = _minecraft.GetGameDirectory(
                session.Settings.GameDirectory,
                version.Name);

            var process = await BuildLaunchProcessAsync(
                session,
                version,
                login,
                settings);

            var missingFiles = MinecraftService.FindMissingLaunchFiles(process);
            if (missingFiles.Count > 0)
            {
                // Older Forge installer profiles reference runtime libraries
                // that are not part of install_profile.json. Repair the final
                // profile automatically instead of starting a JVM that exits
                // immediately with "Could not find or load main class".
                await RepairLaunchFilesAsync(session, version);

                process = await BuildLaunchProcessAsync(
                    session,
                    version,
                    login,
                    settings);

                missingFiles = MinecraftService.FindMissingLaunchFiles(process);
                if (missingFiles.Count > 0)
                {
                    var preview = string.Join(L.Tr("Common.ListSeparator"), missingFiles.Take(3));
                    throw new InvalidOperationException(L.Format(
                        "Launch.MissingFiles",
                        preview + (missingFiles.Count > 3 ? L.Tr("Launch.MissingFilesMore") : string.Empty)));
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.PreLaunchCommand))
            {
                session.SetStatus(L.Tr("Launch.RunningPreLaunch"));
                await RunPreLaunchCommandAsync(
                    settings.PreLaunchCommand,
                    gameDirectory,
                    _minecraft.GetVersionDirectory(session.Settings.GameDirectory, version.Name),
                    version);
            }

            if (!process.Start())
                throw new InvalidOperationException(L.Tr("Launch.StartJavaFailed"));

            session.MarkInstalled(version.Name);
            _settingsService.SaveSilently(session.Settings);
            session.AttachGameProcess(version.Name, process);
            if (session.IsGameRunning)
                session.SetStatus(L.Format("Launch.GameRunning", version.Name));
            return process;
        }
        catch (Exception ex)
        {
            session.CancelGameStarting();
            session.SetError(L.Format("Launch.LaunchError", version.Name, ex.Message));
            return null;
        }
        finally
        {
            session.EndBusy();
        }
    }

    /// <summary>
    /// Builds the exact launch command and writes it as a Windows batch file.
    /// The exported script contains the current login token.
    /// </summary>
    public async Task<bool> ExportLaunchScriptAsync(
        LauncherSession session,
        GameVersion version,
        string targetPath)
    {
        if (session.IsBusy)
            return false;

        var settings = session.Settings.GetVersionSettings(version.Name);
        MSession login;

        try
        {
            if (!version.IsInstalled)
                throw new InvalidOperationException(L.Tr("Launch.InstallFirstForScript"));
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new InvalidOperationException(L.Tr("Launch.NoScriptPath"));

            login = ResolveLogin(session, settings);
            ValidateVersionSettings(settings);
        }
        catch (Exception ex)
        {
            session.SetError(L.Format("Launch.ExportScriptFailed", ex.Message));
            return false;
        }

        session.BeginBusy(L.Tr("Launch.GeneratingScript"));

        try
        {
            var process = await _minecraft.BuildProcessAsync(
                session.Settings.GameDirectory,
                version.Name,
                login,
                settings,
                CancellationToken.None);

            var content = BuildBatchScript(
                process,
                settings,
                version,
                _minecraft.GetGameDirectory(
                    session.Settings.GameDirectory,
                    version.Name),
                _minecraft.GetVersionDirectory(session.Settings.GameDirectory, version.Name));

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(targetPath, content, new UTF8Encoding(false));
            session.SetStatus(L.Format("Launch.ScriptExported", targetPath));
            return true;
        }
        catch (Exception ex)
        {
            session.SetError(L.Format("Launch.ExportScriptFailed", ex.Message));
            return false;
        }
        finally
        {
            session.EndBusy();
        }
    }

    private static MSession ResolveLogin(LauncherSession session, GameVersionSettings settings)
    {
        switch (settings.LoginMode)
        {
            case VersionLoginMode.CurrentAccount:
                return session.ActiveSession
                       ?? throw new InvalidOperationException(L.Tr("Launch.NeedAccount"));

            case VersionLoginMode.Microsoft:
                if (session.Account?.Kind != AccountKind.Microsoft || session.ActiveSession is null)
                    throw new InvalidOperationException(L.Tr("Launch.NeedMicrosoftAccount"));
                return session.ActiveSession;

            case VersionLoginMode.Offline:
                var username = string.IsNullOrWhiteSpace(settings.OfflineUsername)
                    ? session.Settings.OfflineUsername
                    : settings.OfflineUsername.Trim();

                if (username.Length is < 3 or > 16)
                    throw new InvalidOperationException(L.Tr("Launch.InvalidOfflineName"));

                return AuthenticationService.CreateOfflineSession(username);

            default:
                throw new InvalidOperationException(L.Tr("Launch.UnknownLoginMode"));
        }
    }

    private static void ValidateVersionSettings(GameVersionSettings settings)
    {
        if (settings.MinimumRamMb > settings.MaximumRamMb)
            throw new InvalidOperationException(L.Tr("Launch.RamInvalid"));

        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && !File.Exists(settings.JavaPath))
            throw new InvalidOperationException(L.Format("Launch.JavaNotFound", settings.JavaPath));

        if (settings.AutoJoinServer && string.IsNullOrWhiteSpace(settings.ServerAddress))
            throw new InvalidOperationException(L.Tr("Launch.ServerAddressRequired"));

        if (settings.AutoJoinServer && settings.ServerPort is < 1 or > 65535)
            throw new InvalidOperationException(L.Tr("Launch.ServerPortInvalid"));
    }

    private static async Task RunPreLaunchCommandAsync(
        string command,
        string gameDirectory,
        string versionDirectory,
        GameVersion version)
    {
        var expandedCommand = command
            .Replace("{gameDir}", gameDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{versionDir}", versionDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", version.Name, StringComparison.OrdinalIgnoreCase);

        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : "/bin/sh",
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (isWindows)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
        }

        startInfo.ArgumentList.Add(expandedCommand);
        startInfo.Environment["PMCL_GAME_DIR"] = gameDirectory;
        startInfo.Environment["PMCL_VERSION"] = version.Name;
        startInfo.Environment["PMCL_VERSION_DIR"] = versionDirectory;

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(L.Tr("Launch.PreLaunchStartFailed"));

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(PreLaunchTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore process cleanup errors.
            }

            throw new TimeoutException(
                L.Format("Launch.PreLaunchTimeout", PreLaunchTimeout.TotalMinutes));
        }

        var output = await outputTask.ConfigureAwait(true);
        var error = await errorTask.ConfigureAwait(true);

        if (process.ExitCode == 0)
            return;

        var detail = string.IsNullOrWhiteSpace(error) ? output : error;
        detail = detail.Trim();
        if (detail.Length > 500)
            detail = detail[..500] + "...";

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? L.Format("Launch.PreLaunchExitCode", process.ExitCode)
                : L.Format("Launch.PreLaunchExitCodeDetail", process.ExitCode, detail));
    }

    private static string BuildBatchScript(
        Process process,
        GameVersionSettings settings,
        GameVersion version,
        string gameDirectory,
        string versionDirectory)
    {
        var builder = new StringBuilder();

        builder.AppendLine("@echo off");
        builder.AppendLine("setlocal");
        builder.AppendLine("rem Generated by PMCL. This script contains the current login token; keep it private.");
        builder.AppendLine($"cd /d {QuoteBatchArgument(gameDirectory)}");
        builder.AppendLine($"set \"PMCL_GAME_DIR={EscapeBatchValue(gameDirectory)}\"");
        builder.AppendLine($"set \"PMCL_VERSION={EscapeBatchValue(version.Name)}\"");
        builder.AppendLine($"set \"PMCL_VERSION_DIR={EscapeBatchValue(versionDirectory)}\"");

        if (!string.IsNullOrWhiteSpace(settings.PreLaunchCommand))
        {
            builder.AppendLine("rem Pre-launch command");
            builder.AppendLine(settings.PreLaunchCommand
                .Replace("{gameDir}", gameDirectory, StringComparison.OrdinalIgnoreCase)
                .Replace("{versionDir}", versionDirectory, StringComparison.OrdinalIgnoreCase)
                .Replace("{version}", version.Name, StringComparison.OrdinalIgnoreCase));
            builder.AppendLine("if errorlevel 1 (");
            builder.AppendLine("  echo Pre-launch command failed.");
            builder.AppendLine("  pause");
            builder.AppendLine("  exit /b 1");
            builder.AppendLine(")");
        }

        var javaPath = process.StartInfo.FileName;
        if (string.IsNullOrWhiteSpace(javaPath))
            throw new InvalidOperationException(L.Tr("Launch.JavaPathMissing"));

        builder.Append(QuoteBatchArgument(javaPath));
        foreach (var argument in GetProcessArguments(process))
            builder.Append(' ').Append(QuoteBatchArgument(argument));
        builder.AppendLine();
        builder.AppendLine("if errorlevel 1 pause");
        builder.AppendLine("endlocal");

        return builder.ToString();
    }

    private static IEnumerable<string> GetProcessArguments(Process process)
    {
        if (process.StartInfo.ArgumentList.Count > 0)
            return process.StartInfo.ArgumentList;

        return CommandLineSplitter.Split(process.StartInfo.Arguments);
    }

    private static string QuoteBatchArgument(string value)
    {
        var escaped = EscapeBatchValue(value);
        if (escaped.Length == 0)
            return "\"\"";

        var needsQuotes = escaped.Any(character =>
            char.IsWhiteSpace(character)
            || character is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')');

        if (!needsQuotes)
            return escaped;

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;

        foreach (var character in escaped)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }

            builder.Append(character);
        }

        if (backslashes > 0)
            builder.Append('\\', backslashes * 2);

        builder.Append('"');
        return builder.ToString();
    }

    private static string EscapeBatchValue(string value) => value.Replace("%", "%%");

    private async Task InstallCoreAsync(
        LauncherSession session,
        GameVersion version,
        CancellationToken cancellationToken)
    {
        session.IsProgressIndeterminate = true;

        var fileProgress = CreateFileProgress(session, version.Name);
        var byteProgress = CreateByteProgress(session);

        await _minecraft.InstallAsync(
            session.Settings.GameDirectory,
            version.Name,
            fileProgress,
            byteProgress,
            cancellationToken).ConfigureAwait(true);

        _settingsService.SaveSilently(session.Settings);
    }
}

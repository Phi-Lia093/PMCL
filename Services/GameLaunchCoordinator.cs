using System.Diagnostics;
using System.Text;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installers;
using PMCL.Models;
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

    public GameLaunchCoordinator(MinecraftService minecraft, SettingsService settingsService)
    {
        _minecraft = minecraft;
        _settingsService = settingsService;
    }

    public async Task InstallAsync(LauncherSession session, GameVersion version)
    {
        if (session.IsBusy)
            return;

        session.BeginBusy($"正在准备下载 Minecraft {version.Name}...");

        try
        {
            await InstallCoreAsync(session, version, CancellationToken.None);
            session.MarkInstalled(version.Name);
            session.SetStatus($"Minecraft {version.Name} 下载完成");
        }
        catch (Exception ex)
        {
            session.SetError($"下载 {version.Name} 失败：{ex.Message}");
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

        session.BeginBusy($"正在补全 Minecraft {version.Name} 的文件...");

        try
        {
            await InstallCoreAsync(session, version, CancellationToken.None);
            session.MarkInstalled(version.Name);
            session.SetStatus($"Minecraft {version.Name} 文件补全完成");
        }
        catch (Exception ex)
        {
            session.SetError($"补全 {version.Name} 失败：{ex.Message}");
        }
        finally
        {
            session.EndBusy();
        }
    }

    public async Task LaunchAsync(LauncherSession session, GameVersion version)
    {
        if (session.IsBusy)
            return;

        var settings = session.Settings.GetVersionSettings(version.Name);
        MSession login;

        try
        {
            login = ResolveLogin(session, settings);
            ValidateVersionSettings(settings);
        }
        catch (Exception ex)
        {
            session.SetError($"无法启动 {version.Name}：{ex.Message}");
            return;
        }

        session.BeginBusy($"正在启动 Minecraft {version.Name}...");

        try
        {
            if (!version.IsInstalled)
                await InstallCoreAsync(session, version, CancellationToken.None);

            session.IsProgressIndeterminate = true;
            session.SetStatus("正在构建游戏进程...");

            var process = await _minecraft.BuildProcessAsync(
                session.Settings.GameDirectory,
                version.Name,
                login,
                settings,
                CancellationToken.None);

            if (!string.IsNullOrWhiteSpace(settings.PreLaunchCommand))
            {
                session.SetStatus("正在执行启动前命令...");
                await RunPreLaunchCommandAsync(
                    settings.PreLaunchCommand,
                    session.Settings.GameDirectory,
                    version);
            }

            if (!process.Start())
                throw new InvalidOperationException("无法启动 Java 进程。");

            session.MarkInstalled(version.Name);
            _settingsService.SaveSilently(session.Settings);
            session.SetStatus($"Minecraft {version.Name} 已启动，祝你好运！");
        }
        catch (Exception ex)
        {
            session.SetError($"启动 {version.Name} 失败：{ex.Message}");
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
                throw new InvalidOperationException("请先安装该版本，再导出启动脚本。");
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new InvalidOperationException("未选择脚本保存位置。");

            login = ResolveLogin(session, settings);
            ValidateVersionSettings(settings);
        }
        catch (Exception ex)
        {
            session.SetError($"导出启动脚本失败：{ex.Message}");
            return false;
        }

        session.BeginBusy("正在生成启动脚本...");

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
                session.Settings.GameDirectory);

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(targetPath, content, new UTF8Encoding(false));
            session.SetStatus($"启动脚本已导出：{targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            session.SetError($"导出启动脚本失败：{ex.Message}");
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
                       ?? throw new InvalidOperationException("请先在账户页登录 Microsoft 或离线账户。");

            case VersionLoginMode.Microsoft:
                if (session.Account?.Kind != AccountKind.Microsoft || session.ActiveSession is null)
                    throw new InvalidOperationException("该版本要求 Microsoft 账户，请先在账户页登录。");
                return session.ActiveSession;

            case VersionLoginMode.Offline:
                var username = string.IsNullOrWhiteSpace(settings.OfflineUsername)
                    ? session.Settings.OfflineUsername
                    : settings.OfflineUsername.Trim();

                if (username.Length is < 3 or > 16)
                    throw new InvalidOperationException("离线用户名长度必须为 3 到 16 个字符。");

                return AuthenticationService.CreateOfflineSession(username);

            default:
                throw new InvalidOperationException("未知的登录方式。");
        }
    }

    private static void ValidateVersionSettings(GameVersionSettings settings)
    {
        if (settings.MinimumRamMb > settings.MaximumRamMb)
            throw new InvalidOperationException("最大内存不能小于最小内存。");

        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && !File.Exists(settings.JavaPath))
            throw new InvalidOperationException($"找不到 Java：{settings.JavaPath}");

        if (settings.AutoJoinServer && string.IsNullOrWhiteSpace(settings.ServerAddress))
            throw new InvalidOperationException("已启用自动进入服务器，但服务器地址为空。");

        if (settings.AutoJoinServer && settings.ServerPort is < 1 or > 65535)
            throw new InvalidOperationException("服务器端口必须在 1 到 65535 之间。");
    }

    private static async Task RunPreLaunchCommandAsync(
        string command,
        string gameDirectory,
        GameVersion version)
    {
        var versionDirectory = Path.Combine(gameDirectory, "versions", version.Name);
        var expandedCommand = command
            .Replace("{gameDir}", gameDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{versionDir}", versionDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", version.Name, StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            WorkingDirectory = gameDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(expandedCommand);
        startInfo.Environment["PMCL_GAME_DIR"] = gameDirectory;
        startInfo.Environment["PMCL_VERSION"] = version.Name;
        startInfo.Environment["PMCL_VERSION_DIR"] = versionDirectory;

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动启动前命令。");

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
                $"启动前命令超过 {PreLaunchTimeout.TotalMinutes:0} 分钟仍未结束，已终止。");
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
                ? $"启动前命令失败（退出码 {process.ExitCode}）。"
                : $"启动前命令失败（退出码 {process.ExitCode}）：{detail}");
    }

    private static string BuildBatchScript(
        Process process,
        GameVersionSettings settings,
        GameVersion version,
        string gameDirectory)
    {
        var versionDirectory = Path.Combine(gameDirectory, "versions", version.Name);
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
            throw new InvalidOperationException("CmlLib 未返回 Java 可执行文件路径。");

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

        var fileProgress = new Progress<InstallerProgressChangedEventArgs>(args =>
        {
            session.IsProgressIndeterminate = false;
            var total = Math.Max(1, args.TotalTasks);
            session.Progress = Math.Clamp((double)args.ProgressedTasks / total, 0, 1);
            session.SetStatus(
                $"正在下载 {version.Name}（{args.ProgressedTasks}/{args.TotalTasks}） {args.Name}");
        });

        var byteProgress = new Progress<ByteProgress>(progress =>
        {
            var ratio = progress.ToRatio();
            if (ratio is > 0 and <= 1)
                session.StatusMessage = $"正在下载 {version.Name} ... {ratio:P0}";
        });

        await _minecraft.InstallAsync(
            session.Settings.GameDirectory,
            version.Name,
            fileProgress,
            byteProgress,
            cancellationToken).ConfigureAwait(true);

        _settingsService.SaveSilently(session.Settings);
    }
}

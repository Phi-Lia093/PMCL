using CmlLib.Core;
using CmlLib.Core.Installers;
using PMCL.Models;
using PMCL.ViewModels;

namespace PMCL.Services;

/// <summary>
/// Orchestrates download progress and game launching on top of <see cref="MinecraftService"/>.
/// </summary>
public sealed class GameLaunchCoordinator
{
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

    public async Task LaunchAsync(LauncherSession session, GameVersion version)
    {
        if (session.IsBusy)
            return;

        if (session.ActiveSession is null || session.Account is null)
        {
            session.SetError("请先登录 Microsoft 或离线账户。");
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
                session.ActiveSession,
                session.Settings,
                CancellationToken.None);

            if (!process.Start())
                throw new InvalidOperationException("无法启动 Java 进程。");

            session.MarkInstalled(version.Name);
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

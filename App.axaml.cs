using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using PMCL.Localization;
using PMCL.Services;
using PMCL.ViewModels;
using PMCL.Views;

namespace PMCL;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayCloseGameItem;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new AppPaths();
            var settingsService = new SettingsService(paths);
            var settings = settingsService.Load();
            LocalizationService.Instance.Initialize(settings.Language);

            var session = new LauncherSession(settings);
            var authentication = new AuthenticationService(paths);
            var minecraft = new MinecraftService(() => settings.DownloadSource);
            var javaRuntimes = new JavaRuntimeService(paths);

            MainWindow? mainWindow = null;
            var picker = new StoragePickerService(() => mainWindow);
            var viewModel = new MainViewModel(
                session,
                authentication,
                settingsService,
                minecraft,
                picker,
                paths,
                javaRuntimes);

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            mainWindow = window;
            _mainWindow = window;

            desktop.MainWindow = window;
            CreateTrayIcon(session);

            desktop.ShutdownRequested += (_, _) =>
            {
                DisposeTrayIcon();
                viewModel.FlushPendingSettings();
                settingsService.SaveSilently(session.Settings);
            };

            window.Opened += async (_, _) =>
            {
                try
                {
                    await viewModel.InitializeAsync();
                }
                catch (Exception ex)
                {
                    session.SetError(L.Format("App.InitializeFailed", ex.Message));
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Adds the notification-area icon that keeps the launcher reachable while
    /// it is minimized to the tray. Platforms without tray support simply skip it.
    /// </summary>
    private void CreateTrayIcon(LauncherSession session)
    {
        if (_trayIcon is not null)
            return;

        try
        {
            var showItem = new NativeMenuItem(L.Tr("Tray.Show"));
            showItem.Click += (_, _) => Dispatcher.UIThread.Post(ShowMainWindow);

            var hideItem = new NativeMenuItem(L.Tr("Tray.Hide"));
            hideItem.Click += (_, _) => Dispatcher.UIThread.Post(() => _mainWindow?.HideToTray());

            _trayCloseGameItem = new NativeMenuItem(L.Tr("Tray.CloseGame"))
            {
                IsEnabled = false
            };
            _trayCloseGameItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                ShowMainWindow();
                session.ShowGameClosePromptCommand.Execute(null);
            });

            var exitItem = new NativeMenuItem(L.Tr("Tray.Exit"));
            exitItem.Click += (_, _) => Dispatcher.UIThread.Post(ExitApplication);

            var menu = new NativeMenu();
            menu.Add(showItem);
            menu.Add(hideItem);
            menu.Add(_trayCloseGameItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            var trayIcon = new TrayIcon
            {
                ToolTipText = "PMCL - Philia093's MineCraft Launcher",
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PMCL/Assets/avalonia-logo.ico"))),
                Menu = menu,
                IsVisible = true
            };

            trayIcon.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowMainWindow);

            session.GameStarted += (_, args) => Dispatcher.UIThread.Post(() =>
            {
                trayIcon.ToolTipText = L.Format("Tray.Running", args.VersionName);
                if (_trayCloseGameItem is not null)
                    _trayCloseGameItem.IsEnabled = true;
            });

            session.GameExited += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                trayIcon.ToolTipText = "PMCL - Philia093's MineCraft Launcher";
                if (_trayCloseGameItem is not null)
                    _trayCloseGameItem.IsEnabled = false;
            });

            TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
            _trayIcon = trayIcon;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(L.Format("App.TrayInitFailed", ex));
            _trayIcon = null;
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
            return;

        _mainWindow.ShowFromTray();
        _mainWindow.Activate();
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.ExitApplication();
            return;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
            return;

        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
        _trayCloseGameItem = null;
        TrayIcon.SetIcons(this, new TrayIcons());
    }
}

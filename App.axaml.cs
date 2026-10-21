using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PMCL.Services;
using PMCL.ViewModels;
using PMCL.Views;

namespace PMCL;

public partial class App : Application
{
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

            var session = new LauncherSession(settings);
            var authentication = new AuthenticationService(paths);
            var minecraft = new MinecraftService();

            MainWindow? mainWindow = null;
            var picker = new StoragePickerService(() => mainWindow);
            var viewModel = new MainViewModel(session, authentication, settingsService, minecraft, picker, paths);

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            mainWindow = window;

            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
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
                    session.SetError($"启动初始化失败：{ex.Message}");
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
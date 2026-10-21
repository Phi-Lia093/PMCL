using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;

namespace PMCL.ViewModels;

/// <summary>
/// A version row with its own install/launch commands.
/// </summary>
public sealed class VersionItemViewModel : ViewModelBase
{
    private readonly Func<GameVersion, Task> _installAsync;
    private readonly Func<GameVersion, Task> _launchAsync;

    public VersionItemViewModel(
        GameVersion version,
        Func<GameVersion, Task> installAsync,
        Func<GameVersion, Task> launchAsync)
    {
        Version = version;
        _installAsync = installAsync;
        _launchAsync = launchAsync;

        InstallCommand = new AsyncRelayCommand(() => _installAsync(Version));
        LaunchCommand = new AsyncRelayCommand(() => _launchAsync(Version));

        Version.PropertyChanged += OnVersionPropertyChanged;
    }

    public GameVersion Version { get; }

    public IAsyncRelayCommand InstallCommand { get; }

    public IAsyncRelayCommand LaunchCommand { get; }

    public string StatusLabel => Version.IsInstalled ? "已安装" : "未安装";

    public string InstallLabel => Version.IsInstalled ? "重新下载" : "下载";

    private void OnVersionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameVersion.IsInstalled) or null or "")
        {
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(InstallLabel));
        }
    }
}

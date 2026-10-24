using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;

using PMCL.Localization;

namespace PMCL.ViewModels;

/// <summary>
/// A version row with its own download/repair command.
/// </summary>
public sealed class VersionItemViewModel : ViewModelBase
{
    private readonly Func<GameVersion, Task> _installAsync;

    public VersionItemViewModel(
        GameVersion version,
        Func<GameVersion, Task> installAsync)
    {
        Version = version;
        _installAsync = installAsync;

        InstallCommand = new AsyncRelayCommand(() => _installAsync(Version));

        Version.PropertyChanged += OnVersionPropertyChanged;
    }

    public GameVersion Version { get; }

    public IAsyncRelayCommand InstallCommand { get; }

    public string StatusLabel => Version.IsInstalled
        ? L.Tr("Version.Installed")
        : L.Tr("Version.NotInstalled");

    public string InstallLabel => Version.IsModded
        ? L.Tr("Version.ManageLoader")
        : Version.IsInstalled
            ? L.Tr("Version.Redownload")
            : L.Tr("Version.Download");

    public string LoaderBadge => Version.LoaderBadge;

    public bool HasLoader => Version.IsModded;

    private void OnVersionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameVersion.IsInstalled) or null or "")
        {
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(InstallLabel));
        }
    }
}

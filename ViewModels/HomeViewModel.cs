using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Landing page: selected version, account state and the primary play actions.
/// </summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly GameLaunchCoordinator _coordinator;
    private readonly Func<Task> _refreshVersionsAsync;
    private readonly Action _openAccount;

    public HomeViewModel(
        LauncherSession session,
        GameLaunchCoordinator coordinator,
        Func<Task> refreshVersionsAsync,
        Action openAccount)
    {
        Session = session;
        _coordinator = coordinator;
        _refreshVersionsAsync = refreshVersionsAsync;
        _openAccount = openAccount;
    }

    public LauncherSession Session { get; }

    [RelayCommand]
    private Task InstallAsync()
    {
        var version = Session.SelectedVersion;
        return version is null
            ? Task.CompletedTask
            : _coordinator.InstallAsync(Session, version);
    }

    [RelayCommand]
    private Task LaunchAsync()
    {
        var version = Session.SelectedVersion;
        return version is null
            ? Task.CompletedTask
            : _coordinator.LaunchAsync(Session, version);
    }

    [RelayCommand]
    private Task RefreshVersionsAsync() => _refreshVersionsAsync();

    [RelayCommand]
    private void OpenAccount() => _openAccount();

    public void RequestInstall(GameVersion version) => _ = _coordinator.InstallAsync(Session, version);
}

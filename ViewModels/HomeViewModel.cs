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
    private readonly Action<GameVersion> _openVersionSettings;

    public HomeViewModel(
        LauncherSession session,
        GameLaunchCoordinator coordinator,
        Func<Task> refreshVersionsAsync,
        Action openAccount,
        Action<GameVersion> openVersionSettings)
    {
        Session = session;
        _coordinator = coordinator;
        _refreshVersionsAsync = refreshVersionsAsync;
        _openAccount = openAccount;
        _openVersionSettings = openVersionSettings;
    }

    public LauncherSession Session { get; }

    [RelayCommand]
    private Task LaunchAsync()
    {
        var version = Session.SelectedVersion;
        return version is { IsInstalled: true }
            ? _coordinator.LaunchAsync(Session, version)
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenVersionSettings()
    {
        if (Session.SelectedVersion is { IsInstalled: true } version)
            _openVersionSettings(version);
    }

    [RelayCommand]
    private Task RefreshVersionsAsync() => _refreshVersionsAsync();

    [RelayCommand]
    private void OpenAccount() => _openAccount();
}

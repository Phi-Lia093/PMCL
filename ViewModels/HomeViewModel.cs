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
    private async Task LaunchAsync()
    {
        var version = Session.SelectedVersion;
        if (version is { IsInstalled: true })
            await _coordinator.LaunchAsync(Session, version);
    }

    [RelayCommand]
    private void ShowGameSession() => Session.ShowGameClosePromptCommand.Execute(null);

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

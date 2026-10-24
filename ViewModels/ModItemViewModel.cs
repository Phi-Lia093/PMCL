using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// One row in the per-version mod list. Enable/disable and delete operate on
/// the jar file through callbacks owned by <see cref="VersionSettingsViewModel"/>.
/// </summary>
public sealed partial class ModItemViewModel : ViewModelBase
{
    private readonly Func<ModItemViewModel, Task> _toggleAsync;
    private readonly Func<ModItemViewModel, Task> _deleteAsync;

    public ModItemViewModel(
        LocalModInfo info,
        Func<ModItemViewModel, Task> toggleAsync,
        Func<ModItemViewModel, Task> deleteAsync)
    {
        Info = info;
        _toggleAsync = toggleAsync;
        _deleteAsync = deleteAsync;

        ToggleCommand = new AsyncRelayCommand(() => _toggleAsync(this));
        ConfirmDeleteCommand = new AsyncRelayCommand(() => _deleteAsync(this));
    }

    public LocalModInfo Info { get; private set; }

    public string DisplayName => Info.DisplayName;

    public string FileName => Info.FileName;

    public string SizeText => Info.SizeText;

    public string StatusText => Info.StatusText;

    public bool IsEnabled => Info.IsEnabled;

    public string ToggleLabel => Info.IsEnabled
        ? L.Tr("Mod.Disable")
        : L.Tr("Mod.Enable");

    public string ToggleHint => Info.IsEnabled
        ? L.Tr("Mod.DisableHint")
        : L.Tr("Mod.EnableHint");

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand ConfirmDeleteCommand { get; }

    [ObservableProperty]
    public partial bool IsDeleteConfirmVisible { get; set; }

    [RelayCommand]
    private void RequestDelete() => IsDeleteConfirmVisible = true;

    [RelayCommand]
    private void CancelDelete() => IsDeleteConfirmVisible = false;

    public void Update(LocalModInfo info)
    {
        Info = info;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ToggleHint));
    }
}

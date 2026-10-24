using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// One archive row in a content pack group (resource pack, shader pack or data
/// pack). Enable/disable and delete operate on the file through callbacks owned
/// by <see cref="ContentPackGroupViewModel"/>.
/// </summary>
public sealed partial class ContentPackItemViewModel : ViewModelBase
{
    private static readonly string[] DerivedPropertyNames =
    [
        nameof(DisplayName),
        nameof(FileName),
        nameof(SizeText),
        nameof(StatusText),
        nameof(IsEnabled),
        nameof(ToggleLabel),
        nameof(ToggleHint),
        nameof(IconInitial)
    ];

    private readonly Func<ContentPackItemViewModel, Task> _toggleAsync;
    private readonly Func<ContentPackItemViewModel, Task> _deleteAsync;

    public ContentPackItemViewModel(
        LocalContentPack info,
        Func<ContentPackItemViewModel, Task> toggleAsync,
        Func<ContentPackItemViewModel, Task> deleteAsync)
    {
        Info = info;
        _toggleAsync = toggleAsync;
        _deleteAsync = deleteAsync;

        ToggleCommand = new AsyncRelayCommand(() => _toggleAsync(this));
        ConfirmDeleteCommand = new AsyncRelayCommand(() => _deleteAsync(this));
    }

    public LocalContentPack Info { get; private set; }

    public string DisplayName => Info.DisplayName;

    public string FileName => Info.FileName;

    public string SizeText => Info.SizeText;

    public string StatusText => Info.StatusText;

    public bool IsEnabled => Info.IsEnabled;

    public bool HasContainer => Info.HasContainer;

    public string ContainerLabel => Info.ContainerLabel;

    public string ToggleLabel => IsEnabled ? L.Tr("Content.Disable") : L.Tr("Content.Enable");

    public string ToggleHint => IsEnabled ? L.Tr("Content.DisableHint") : L.Tr("Content.EnableHint");

    public string IconInitial
    {
        get
        {
            var title = Info.DisplayName.Trim();
            return title.Length == 0 ? "?" : char.ToUpperInvariant(title[0]).ToString();
        }
    }

    public IAsyncRelayCommand ToggleCommand { get; }

    public IAsyncRelayCommand ConfirmDeleteCommand { get; }

    [ObservableProperty]
    public partial bool IsDeleteConfirmVisible { get; set; }

    [RelayCommand]
    private void RequestDelete() => IsDeleteConfirmVisible = true;

    [RelayCommand]
    private void CancelDelete() => IsDeleteConfirmVisible = false;

    public void Update(LocalContentPack info)
    {
        Info = info;
        foreach (var name in DerivedPropertyNames)
            OnPropertyChanged(name);
    }
}

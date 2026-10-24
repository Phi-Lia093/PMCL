using CommunityToolkit.Mvvm.ComponentModel;
using PMCL.Localization;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// One selectable mod loader card in the shared loader picker.
/// </summary>
public sealed partial class ModLoaderChoiceViewModel : ViewModelBase
{
    public ModLoaderChoiceViewModel(ModLoaderCatalogEntry catalog)
    {
        Catalog = catalog;
        SelectedVersionIndex = catalog.Versions.Count > 0 ? 0 : -1;
    }

    public ModLoaderCatalogEntry Catalog { get; }

    public ModLoaderKind Kind => Catalog.Kind;

    public string DisplayName => Catalog.DisplayName;

    public string Description => Catalog.Description;

    public IReadOnlyList<ModLoaderVersionOption> Versions => Catalog.Versions;

    public bool IsAvailable => Catalog.IsAvailable;

    public double CardOpacity => IsAvailable ? 1 : 0.45;

    public string StatusText => IsAvailable
        ? L.Format("Loader.VersionsAvailable", Versions.Count)
        : Catalog.Error ?? L.Tr("Loader.Unavailable");

    [ObservableProperty]
    public partial int SelectedVersionIndex { get; set; }

    public ModLoaderVersionOption? SelectedVersion => SelectedVersionIndex >= 0
                                                       && SelectedVersionIndex < Versions.Count
        ? Versions[SelectedVersionIndex]
        : null;

    public string SelectedVersionText => SelectedVersion?.DisplayName ?? L.Tr("Version.Select");

    public void SelectVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;

        for (var i = 0; i < Versions.Count; i++)
        {
            if (string.Equals(Versions[i].Version, version, StringComparison.OrdinalIgnoreCase))
            {
                SelectedVersionIndex = i;
                return;
            }
        }
    }

    partial void OnSelectedVersionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(SelectedVersionText));
    }
}

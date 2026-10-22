using CommunityToolkit.Mvvm.ComponentModel;
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
        ? $"{Versions.Count} 个版本可用"
        : Catalog.Error ?? "暂不可用";

    [ObservableProperty]
    public partial int SelectedVersionIndex { get; set; }

    public ModLoaderVersionOption? SelectedVersion => SelectedVersionIndex >= 0
                                                       && SelectedVersionIndex < Versions.Count
        ? Versions[SelectedVersionIndex]
        : null;

    public string SelectedVersionText => SelectedVersion?.DisplayName ?? "请选择版本";

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

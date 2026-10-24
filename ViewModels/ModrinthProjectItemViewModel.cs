using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// One Modrinth search result. The parent page performs the actual download so
/// it can refresh the local mod list and surface dependency warnings.
/// </summary>
public sealed partial class ModrinthProjectItemViewModel : ViewModelBase
{
    public ModrinthProjectItemViewModel(
        ModrinthSearchHit project,
        Func<ModrinthProjectItemViewModel, Task> installAsync)
    {
        Project = project;
        InstallCommand = new AsyncRelayCommand(() => installAsync(this));
    }

    public ModrinthSearchHit Project { get; }

    public string Title => string.IsNullOrWhiteSpace(Project.Title) ? Project.Slug : Project.Title;

    public string Description => string.IsNullOrWhiteSpace(Project.Description)
        ? L.Tr("Modrinth.NoDescription")
        : Project.Description;

    public string AuthorText => Project.AuthorText;

    public string DownloadsText => Project.DownloadsText;

    public string VersionSummary => Project.VersionSummary;

    public string CategoriesText => Project.Categories.Count == 0
        ? L.Tr("Modrinth.Uncategorized")
        : string.Join(" · ", Project.Categories.Take(3));

    public string IconInitial
    {
        get
        {
            var title = Title.Trim();
            return title.Length == 0 ? "?" : char.ToUpperInvariant(title[0]).ToString();
        }
    }

    public IAsyncRelayCommand InstallCommand { get; }

    [ObservableProperty]
    public partial Bitmap? Icon { get; set; }

    public bool HasIcon => Icon is not null;

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    [ObservableProperty]
    public partial bool IsInstalling { get; set; }

    public string InstallLabel => IsInstalling
        ? L.Tr("Loader.Installing")
        : L.Tr("Modrinth.Install");

    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(InstallLabel));
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Version browser with search, type filter and per-row actions.
/// </summary>
public sealed partial class VersionsViewModel : ViewModelBase
{
    private readonly GameLaunchCoordinator _coordinator;
    private readonly Func<Task> _refreshVersionsAsync;

    public VersionsViewModel(
        LauncherSession session,
        GameLaunchCoordinator coordinator,
        Func<Task> refreshVersionsAsync)
    {
        Session = session;
        _coordinator = coordinator;
        _refreshVersionsAsync = refreshVersionsAsync;

        Session.Versions.CollectionChanged += OnVersionsChanged;
        RebuildItems();
    }

    public LauncherSession Session { get; }

    public ObservableCollection<VersionItemViewModel> Items { get; } = new();

    public IReadOnlyList<string> TypeFilters { get; } = new[] { "全部类型", "正式版", "快照", "旧版本" };

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedTypeIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowInstalledOnly { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "尚未加载版本列表";

    [ObservableProperty]
    public partial VersionItemViewModel? SelectedItem { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _refreshVersionsAsync();
        RebuildItems();
    }

    [RelayCommand]
    private Task InstallAsync(VersionItemViewModel? item)
        => item is null ? Task.CompletedTask : _coordinator.InstallAsync(Session, item.Version);

    [RelayCommand]
    private async Task LaunchAsync(VersionItemViewModel? item)
    {
        if (item is null)
            return;

        Session.SelectedVersion = item.Version;
        await _coordinator.LaunchAsync(Session, item.Version);
    }

    [RelayCommand]
    private void SelectVersion(VersionItemViewModel? item)
    {
        if (item is not null)
            Session.SelectedVersion = item.Version;
    }

    partial void OnSearchTextChanged(string value) => RebuildItems();

    partial void OnSelectedTypeIndexChanged(int value) => RebuildItems();

    partial void OnShowInstalledOnlyChanged(bool value) => RebuildItems();

    partial void OnSelectedItemChanged(VersionItemViewModel? value)
    {
        if (value is not null)
            Session.SelectedVersion = value.Version;
    }

    private void OnVersionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildItems();

    /// <summary>
    /// Refreshes the visible rows after the shared version list was replaced.
    /// </summary>
    public void RebuildFromSession() => RebuildItems();

    private void RebuildItems()
    {
        IEnumerable<GameVersion> filtered = Session.Versions;

        filtered = SelectedTypeIndex switch
        {
            1 => filtered.Where(version => version.Type.Equals("release", StringComparison.OrdinalIgnoreCase)),
            2 => filtered.Where(version => version.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase)),
            3 => filtered.Where(version =>
                version.Type.StartsWith("old_", StringComparison.OrdinalIgnoreCase)),
            _ => filtered
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var keyword = SearchText.Trim();
            filtered = filtered.Where(version =>
                version.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (ShowInstalledOnly)
            filtered = filtered.Where(version => version.IsInstalled);

        Items.Clear();
        SelectedItem = null;
        foreach (var version in filtered)
        {
            Items.Add(new VersionItemViewModel(
                version,
                v => _coordinator.InstallAsync(Session, v),
                v =>
                {
                    Session.SelectedVersion = v;
                    return _coordinator.LaunchAsync(Session, v);
                }));
        }

        Summary = Session.Versions.Count == 0
            ? "尚未加载版本列表"
            : $"显示 {Items.Count} / {Session.Versions.Count} 个版本";
    }
}

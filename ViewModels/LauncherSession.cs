using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CmlLib.Core.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Shared UI state: current account, selected version, version catalog and global progress.
/// </summary>
public sealed partial class LauncherSession : ObservableObject
{
    public LauncherSession(LauncherSettings settings)
    {
        Settings = settings;
        InstalledVersions.CollectionChanged += OnInstalledVersionsChanged;
        RefreshDownloadSourceText();
    }

    public LauncherSettings Settings { get; }

    public ObservableCollection<GameVersion> Versions { get; } = new();

    /// <summary>
    /// Installed versions only; this is what the home page dropdown exposes.
    /// </summary>
    public ObservableCollection<GameVersion> InstalledVersions { get; } = new();

    public ObservableCollection<SavedAccount> SavedAccounts { get; } = new();

    /// <summary>
    /// The authenticated session passed to Minecraft. Not bound to the UI.
    /// </summary>
    public MSession? ActiveSession { get; private set; }

    [ObservableProperty]
    public partial AccountProfile? Account { get; set; }

    [ObservableProperty]
    public partial string? ActiveAccountId { get; set; }

    [ObservableProperty]
    public partial GameVersion? SelectedVersion { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingVersions { get; set; }

    [ObservableProperty]
    public partial bool VersionsLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "准备就绪";

    [ObservableProperty]
    public partial string? LastError { get; set; }

    [ObservableProperty]
    public partial string ProgressPercentText { get; set; } = "0%";

    [ObservableProperty]
    public partial string DownloadSpeedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DownloadBytesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DownloadSourceText { get; set; } =
        "下载源：" + DownloadSourceKind.Official.ToDisplayName();

    public bool HasDownloadMetrics =>
        !string.IsNullOrWhiteSpace(DownloadSpeedText)
        || !string.IsNullOrWhiteSpace(DownloadBytesText);

    public bool IsNotBusy => !IsBusy;

    public bool HasAccount => Account is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public string AccountName => Account?.Username ?? "未登录";

    public string AccountKindLabel => Account?.KindLabel ?? "登录后即可开始游戏";

    public string AccountInitial => Account?.Initial ?? "?";

    public string AccountDetailText => Account?.Detail ?? "选择或添加一个账户以开始游戏。";

    public bool HasSelectedVersion => SelectedVersion is not null;

    public bool HasInstalledVersions => InstalledVersions.Count > 0;

    public bool HasInstalledSelection => SelectedVersion is { IsInstalled: true };

    public bool CanStartGame => HasInstalledSelection && !IsBusy;

    public bool CanOpenVersionSettings => HasInstalledSelection && !IsBusy;

    public string SelectedVersionName => SelectedVersion?.Name ?? "请选择版本";

    public GameVersionSettings? SelectedVersionSettings => SelectedVersion is null
        ? null
        : Settings.GetVersionSettings(SelectedVersion.Name);

    public void SetAccount(
        MSession session,
        AccountKind kind,
        string? accountId = null,
        string? gamertag = null)
    {
        ActiveSession = session;
        ActiveAccountId = accountId;
        Account = new AccountProfile(
            session.Username ?? "Player",
            session.UUID ?? string.Empty,
            kind,
            accountId,
            gamertag);
        Settings.LastAccountKind = kind.ToString();
        Settings.LastAccountId = accountId ?? string.Empty;
        if (kind == AccountKind.Offline)
            Settings.OfflineUsername = session.Username ?? Settings.OfflineUsername;
    }

    public void ClearAccount()
    {
        ActiveSession = null;
        ActiveAccountId = null;
        Account = null;
        Settings.LastAccountId = string.Empty;
        Settings.LastAccountKind = string.Empty;
    }

    public void BeginBusy(string status)
    {
        LastError = null;
        Progress = 0;
        IsProgressIndeterminate = true;
        ResetDownloadMetrics();
        RefreshDownloadSourceText();
        IsBusy = true;
        StatusMessage = status;
    }

    public void EndBusy()
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
        Progress = 0;
        ResetDownloadMetrics();
    }

    public void ResetDownloadMetrics()
    {
        DownloadSpeedText = string.Empty;
        DownloadBytesText = string.Empty;
    }

    public void UpdateDownloadMetrics(long progressedBytes, long totalBytes, double bytesPerSecond)
    {
        DownloadBytesText = totalBytes > 0
            ? $"已下载 {DownloadSpeedTracker.FormatBytes(progressedBytes)} / {DownloadSpeedTracker.FormatBytes(totalBytes)}"
            : $"已下载 {DownloadSpeedTracker.FormatBytes(progressedBytes)}";

        DownloadSpeedText = DownloadSpeedTracker.FormatSpeed(bytesPerSecond);
    }

    public void RefreshDownloadSourceText()
        => DownloadSourceText = "下载源：" + Settings.DownloadSource.ToDisplayName();

    public void SetStatus(string message)
    {
        LastError = null;
        StatusMessage = message;
    }

    public void SetError(string message)
    {
        LastError = message;
        StatusMessage = message;
    }

    [RelayCommand]
    private void ClearError()
    {
        LastError = null;
        StatusMessage = "准备就绪";
    }

    public void ApplyVersions(IEnumerable<GameVersion> versions)
    {
        var previousName = SelectedVersion?.Name;
        var ordered = versions
            .OrderByDescending(version => version.IsRelease)
            .ThenByDescending(version => version.ReleaseTime)
            .ToList();

        Versions.Clear();
        InstalledVersions.Clear();

        foreach (var version in ordered)
        {
            Versions.Add(version);
            if (version.IsInstalled)
                InstalledVersions.Add(version);
        }

        var preferredName = !string.IsNullOrWhiteSpace(previousName)
            ? previousName
            : Settings.LastVersion;

        SelectedVersion =
            InstalledVersions.FirstOrDefault(version => version.Name == preferredName)
            ?? InstalledVersions.FirstOrDefault();
    }

    public void MarkInstalled(string versionName)
    {
        GameVersion? installedVersion = null;

        foreach (var version in Versions.Where(version => version.Name == versionName))
        {
            version.IsInstalled = true;
            installedVersion = version;
        }

        RebuildInstalledVersions();

        if (SelectedVersion is null || !SelectedVersion.IsInstalled)
            SelectedVersion = installedVersion;
    }

    /// <summary>
    /// Guarantees that the home page only points at an installed version.
    /// </summary>
    public void EnsureInstalledSelection()
    {
        if (SelectedVersion is { IsInstalled: true })
            return;

        SelectedVersion =
            InstalledVersions.FirstOrDefault(version => version.Name == Settings.LastVersion)
            ?? InstalledVersions.FirstOrDefault();
    }

    public GameVersion? FindVersion(string versionName)
        => Versions.FirstOrDefault(version =>
            string.Equals(version.Name, versionName, StringComparison.OrdinalIgnoreCase));

    public void SelectVersion(string versionName)
    {
        var version = FindVersion(versionName);
        if (version is { IsInstalled: true })
            SelectedVersion = version;
    }

    public void RenameVersion(string oldName, string newName)
    {
        var version = Versions.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, oldName, StringComparison.Ordinal));

        if (version is null)
            return;

        version.Name = newName;
        Settings.MoveVersionSettings(oldName, newName);

        if (string.Equals(Settings.LastVersion, oldName, StringComparison.Ordinal))
            Settings.LastVersion = newName;

        RebuildInstalledVersions();
        OnPropertyChanged(nameof(SelectedVersionName));
        OnPropertyChanged(nameof(SelectedVersionSettings));
    }

    public void RemoveVersion(GameVersion version)
    {
        Settings.RemoveVersionSettings(version.Name);
        Versions.Remove(version);
        RebuildInstalledVersions();

        if (ReferenceEquals(SelectedVersion, version) || SelectedVersion is not { IsInstalled: true })
            SelectedVersion = InstalledVersions.FirstOrDefault();

        if (string.Equals(Settings.LastVersion, version.Name, StringComparison.Ordinal))
            Settings.LastVersion = SelectedVersion?.Name ?? string.Empty;

        OnPropertyChanged(nameof(SelectedVersionName));
        OnPropertyChanged(nameof(SelectedVersionSettings));
        NotifySelectionStateChanged();
    }

    private void RebuildInstalledVersions()
    {
        InstalledVersions.Clear();
        foreach (var version in Versions.Where(version => version.IsInstalled))
            InstalledVersions.Add(version);
    }

    private void OnInstalledVersionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasInstalledVersions));
        NotifySelectionStateChanged();
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(HasInstalledSelection));
        OnPropertyChanged(nameof(CanStartGame));
        OnPropertyChanged(nameof(CanOpenVersionSettings));
    }

    partial void OnAccountChanged(AccountProfile? value)
    {
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountKindLabel));
        OnPropertyChanged(nameof(AccountInitial));
        OnPropertyChanged(nameof(AccountDetailText));
    }

    partial void OnSelectedVersionChanged(GameVersion? value)
    {
        if (value is { IsInstalled: true })
            Settings.LastVersion = value.Name;

        OnPropertyChanged(nameof(HasSelectedVersion));
        OnPropertyChanged(nameof(SelectedVersionName));
        OnPropertyChanged(nameof(SelectedVersionSettings));
        NotifySelectionStateChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        NotifySelectionStateChanged();
    }

    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnProgressChanged(double value)
        => ProgressPercentText = $"{Math.Clamp(value, 0, 1):P0}";

    partial void OnDownloadSpeedTextChanged(string value)
        => OnPropertyChanged(nameof(HasDownloadMetrics));

    partial void OnDownloadBytesTextChanged(string value)
        => OnPropertyChanged(nameof(HasDownloadMetrics));
}

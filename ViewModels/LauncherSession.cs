using System.Collections.ObjectModel;
using CmlLib.Core.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Models;

namespace PMCL.ViewModels;

/// <summary>
/// Shared UI state: current account, selected version, version catalog and global progress.
/// </summary>
public sealed partial class LauncherSession : ObservableObject
{
    public LauncherSession(LauncherSettings settings)
    {
        Settings = settings;
    }

    public LauncherSettings Settings { get; }

    public ObservableCollection<GameVersion> Versions { get; } = new();

    public ObservableCollection<GameVersion> ReleaseVersions { get; } = new();

    public ObservableCollection<SavedMicrosoftAccount> SavedAccounts { get; } = new();

    /// <summary>
    /// The authenticated session passed to Minecraft. Not bound to the UI.
    /// </summary>
    public MSession? ActiveSession { get; private set; }

    [ObservableProperty]
    public partial AccountProfile? Account { get; set; }

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
    public partial string? DetectedJavaPath { get; set; }

    public bool IsNotBusy => !IsBusy;

    public bool HasAccount => Account is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public string AccountName => Account?.Username ?? "未登录";

    public string AccountKindLabel => Account?.KindLabel ?? "登录后即可开始游戏";

    public string AccountInitial => Account?.Initial ?? "?";

    public bool HasSelectedVersion => SelectedVersion is not null;

    public string SelectedVersionName => SelectedVersion?.Name ?? "请选择版本";

    public string JavaSummary => !string.IsNullOrWhiteSpace(Settings.JavaPath)
        ? Settings.JavaPath!
        : DetectedJavaPath ?? "自动检测（未找到 Java）";

    public void SetAccount(MSession session, AccountKind kind)
    {
        ActiveSession = session;
        Account = new AccountProfile(
            session.Username ?? "Player",
            session.UUID ?? string.Empty,
            kind);
        Settings.LastAccountKind = kind.ToString();
        if (kind == AccountKind.Offline)
            Settings.OfflineUsername = session.Username ?? Settings.OfflineUsername;
    }

    public void ClearAccount()
    {
        ActiveSession = null;
        Account = null;
    }

    public void BeginBusy(string status)
    {
        LastError = null;
        Progress = 0;
        IsProgressIndeterminate = true;
        IsBusy = true;
        StatusMessage = status;
    }

    public void EndBusy()
    {
        IsBusy = false;
        IsProgressIndeterminate = false;
        Progress = 0;
    }

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
        ReleaseVersions.Clear();

        foreach (var version in ordered)
        {
            Versions.Add(version);
            if (version.IsRelease)
                ReleaseVersions.Add(version);
        }

        var preferredName = !string.IsNullOrWhiteSpace(previousName)
            ? previousName
            : Settings.LastVersion;

        SelectedVersion =
            Versions.FirstOrDefault(version => version.Name == preferredName)
            ?? ReleaseVersions.FirstOrDefault()
            ?? Versions.FirstOrDefault();
    }

    public void MarkInstalled(string versionName)
    {
        foreach (var version in Versions.Where(version => version.Name == versionName))
            version.IsInstalled = true;
    }

    partial void OnAccountChanged(AccountProfile? value)
    {
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountKindLabel));
        OnPropertyChanged(nameof(AccountInitial));
    }

    partial void OnSelectedVersionChanged(GameVersion? value)
    {
        if (value is not null)
            Settings.LastVersion = value.Name;

        OnPropertyChanged(nameof(HasSelectedVersion));
        OnPropertyChanged(nameof(SelectedVersionName));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    partial void OnLastErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnDetectedJavaPathChanged(string? value) => OnPropertyChanged(nameof(JavaSummary));

    public void NotifyJavaSummaryChanged() => OnPropertyChanged(nameof(JavaSummary));
}

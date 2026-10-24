using System;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMCL.Localization;
using PMCL.Models;
using PMCL.Services;

namespace PMCL.ViewModels;

/// <summary>
/// Project information page with author, contact and license links.
/// </summary>
public sealed partial class AboutViewModel : ViewModelBase
{
    public const string ProjectName = "PMCL";
    public const string Author = "PhiLia093";
    public const string GitHubUrl = "https://github.com/Phi-Lia093";
    public const string RepositoryUrl = "https://github.com/Phi-Lia093/PMCL";
    public const string LicenseUrl = "https://www.gnu.org/licenses/gpl-3.0.html";
    public const string EmailAddress = "phi_lia093@126.com";

    private readonly LauncherSession _session;

    public AboutViewModel(LauncherSession session)
    {
        _session = session;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = version is null ? "0.1.0" : version.ToString(3);
    }

    public string VersionText { get; }

    public string AppVersionText => $"{ProjectName} v{VersionText}";

    public string CopyrightText => "Copyright © 2026 PhiLia093";

    public string GitHubDisplay => GitHubUrl;

    public string EmailDisplay => EmailAddress;

    public string RepositoryDisplay => RepositoryUrl;

    [RelayCommand]
    private void OpenGitHub() => OpenExternal(GitHubUrl);

    [RelayCommand]
    private void OpenRepository() => OpenExternal(RepositoryUrl);

    [RelayCommand]
    private void OpenLicense() => OpenExternal(LicenseUrl);

    [RelayCommand]
    private void OpenEmail()
    {
        var subject = Uri.EscapeDataString(L.Tr("About.EmailSubject"));
        OpenExternal($"mailto:{EmailAddress}?subject={subject}");
    }

    private void OpenExternal(string url)
    {
        if (!SystemBrowserService.TryOpenExternal(url, out var error))
            _session.SetError(L.Format("Browser.OpenFailed", error));
    }
}

using System.Diagnostics;
using PMCL.Localization;

namespace PMCL.Services;

/// <summary>
/// Opens Microsoft's OAuth page in the user's default browser. Using the system
/// browser avoids shipping a WebView2 runtime and works on every desktop OS.
/// </summary>
public static class SystemBrowserService
{
    public static bool TryOpen(string url, out string? error)
    {
        if (!TryOpenExternal(url, out error))
        {
            error = L.Tr("Browser.InvalidLoginUrl");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Opens a normal external link or a mailto URI. This is used by the About
    /// page, which is not restricted to OAuth sign-in links.
    /// </summary>
    public static bool TryOpenExternal(string url, out string? error)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp
                    && uri.Scheme != Uri.UriSchemeHttps
                    && uri.Scheme != Uri.UriSchemeMailto))
            {
                error = L.Tr("Browser.InvalidUrl");
                return false;
            }

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

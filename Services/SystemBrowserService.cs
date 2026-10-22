using System.Diagnostics;

namespace PMCL.Services;

/// <summary>
/// Opens Microsoft's OAuth page in the user's default browser. Using the system
/// browser avoids shipping a WebView2 runtime and works on every desktop OS.
/// </summary>
public static class SystemBrowserService
{
    public static bool TryOpen(string url, out string? error)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "登录链接无效。";
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

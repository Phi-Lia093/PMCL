using System.Net;
using System.Net.Http.Headers;
using PMCL.Models;

namespace PMCL.Services;

/// <summary>
/// Builds the HTTP clients used by CmlLib and maps official Mojang / loader
/// hosts to BMCLAPI mirrors when the user selects the mirror source. Every
/// mirrored request falls back to the untouched official URL.
/// </summary>
public static class DownloadSourceRouter
{
    public const string BmclApiBase = "https://bmclapi2.bangbang93.com";
    public const string BmclMavenBase = "https://mirror.nju.edu.cn/bmclapi";
    public const string BmclMavenFallback = "https://mirrors.cernet.edu.cn/bmclapi";
    public const string FabricMetaOfficial = "https://meta.fabricmc.net";
    public const string ForgeMavenBase = "https://maven.minecraftforge.net";
    public const string NeoForgeMavenBase = "https://maven.neoforged.net";

    public static HttpClient CreateHttpClient(
        Func<DownloadSourceKind> sourceProvider,
        TimeSpan timeout)
    {
        var handler = new DownloadSourceHandler(
            sourceProvider,
            new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip
                    | DecompressionMethods.Deflate
                    | DecompressionMethods.Brotli
            });

        var client = new HttpClient(handler)
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PMCL", "0.1.0"));
        return client;
    }

    /// <summary>
    /// Returns mirrored alternatives for an official URL. The original URL is
    /// not included in the result.
    /// </summary>
    public static IReadOnlyList<Uri> GetMirrorUris(Uri officialUri)
    {
        if (!officialUri.IsAbsoluteUri)
            return Array.Empty<Uri>();

        var host = officialUri.Host;
        var path = officialUri.PathAndQuery;

        if (MatchesHost(host, "maven.minecraftforge.net")
            || MatchesHost(host, "maven.neoforged.net"))
        {
            path = StripMavenRepositoryPrefix(path);
            return new[]
            {
                new Uri(BmclMavenBase + path),
                new Uri(BmclMavenFallback + path)
            };
        }

        if (MatchesHost(host, "maven.fabricmc.net"))
        {
            return new[]
            {
                new Uri(BmclMavenBase + path),
                new Uri(BmclMavenFallback + path)
            };
        }

        if (MatchesHost(host, "meta.fabricmc.net"))
            return new[] { new Uri(BmclApiBase + "/fabric-meta" + path) };

        if (MatchesHost(host, "resources.download.minecraft.net"))
            return new[] { new Uri(BmclApiBase + "/assets" + path) };

        if (MatchesHost(host, "libraries.minecraft.net"))
            return new[] { new Uri(BmclApiBase + "/maven" + path) };

        if (MatchesHost(host, "launchermeta.mojang.com")
            || MatchesHost(host, "piston-meta.mojang.com")
            || MatchesHost(host, "piston-data.mojang.com"))
        {
            return new[] { new Uri(BmclApiBase + path) };
        }

        return Array.Empty<Uri>();
    }

    public static bool IsBmclHost(Uri uri)
        => MatchesHost(uri.Host, "bmclapi2.bangbang93.com")
           || MatchesHost(uri.Host, "mirror.nju.edu.cn")
           || MatchesHost(uri.Host, "mirrors.cernet.edu.cn");

    /// <summary>
    /// Returns the canonical official URL for a known mirror URL, or null when
    /// the mirror URL cannot be mapped back safely.
    /// </summary>
    public static Uri? TryGetOfficialUri(Uri mirrorUri)
    {
        if (!mirrorUri.IsAbsoluteUri)
            return null;

        var path = mirrorUri.AbsolutePath;
        if (MatchesHost(mirrorUri.Host, "bmclapi2.bangbang93.com"))
        {
            if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                return new Uri("https://resources.download.minecraft.net" + path["/assets".Length..]);
            if (path.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase))
                return new Uri("https://libraries.minecraft.net" + path["/maven".Length..]);
            if (path.StartsWith("/fabric-meta/", StringComparison.OrdinalIgnoreCase))
                return new Uri(FabricMetaOfficial + path["/fabric-meta".Length..]);
        }

        return null;
    }

    private static string StripMavenRepositoryPrefix(string path)
    {
        if (path.StartsWith("/releases/", StringComparison.OrdinalIgnoreCase))
            return path["/releases".Length..];
        if (path.StartsWith("/snapshots/", StringComparison.OrdinalIgnoreCase))
            return path["/snapshots".Length..];
        return path;
    }

    private static bool MatchesHost(string host, string expected)
        => string.Equals(host, expected, StringComparison.OrdinalIgnoreCase);

    private sealed class DownloadSourceHandler : DelegatingHandler
    {
        private readonly Func<DownloadSourceKind> _sourceProvider;

        public DownloadSourceHandler(
            Func<DownloadSourceKind> sourceProvider,
            HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
            _sourceProvider = sourceProvider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { IsAbsoluteUri: true } originalUri
                || request.Content is not null
                || _sourceProvider() != DownloadSourceKind.BmclApi)
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var mirrorUris = GetMirrorUris(originalUri);
            if (mirrorUris.Count == 0)
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            foreach (var mirrorUri in mirrorUris)
            {
                using var mirrorRequest = CloneGetRequest(request, mirrorUri);
                try
                {
                    var response = await base.SendAsync(mirrorRequest, cancellationToken)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                        return response;

                    response.Dispose();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Per-request timeout: continue with the official fallback.
                }
                catch (HttpRequestException)
                {
                    // Mirror unavailable: continue with the official fallback.
                }
            }

            using var officialRequest = CloneGetRequest(request, originalUri);
            return await base.SendAsync(officialRequest, cancellationToken).ConfigureAwait(false);
        }

        private static HttpRequestMessage CloneGetRequest(
            HttpRequestMessage source,
            Uri requestUri)
        {
            var clone = new HttpRequestMessage(source.Method, requestUri);
            foreach (var header in source.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Version = source.Version;
            clone.VersionPolicy = source.VersionPolicy;
            return clone;
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using Avalonia.Media.Imaging;

namespace PMCL.Services;

/// <summary>
/// Downloads Modrinth project icons once and keeps the decoded bitmap in memory
/// so rebuilding the mod list (for example while typing a filter) never has to
/// hit the network again. Failures are cached as <c>null</c> and simply leave
/// the fallback icon in place.
/// </summary>
public sealed class ModIconCache : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly ConcurrentDictionary<string, Bitmap?> _icons =
        new(StringComparer.OrdinalIgnoreCase);

    public ModIconCache()
        : this(CreateClient(), ownsClient: true)
    {
    }

    public ModIconCache(HttpClient client, bool ownsClient = false)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    public async Task<Bitmap?> GetAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var key = url.Trim();
        if (_icons.TryGetValue(key, out var cached))
            return cached;

        Bitmap? bitmap = null;
        try
        {
            var bytes = await _client.GetByteArrayAsync(key, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or OperationCanceledException
                                              or IOException
                                              or ArgumentException
                                              or FormatException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;

            bitmap = null;
        }

        _icons[key] = bitmap;
        return bitmap;
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PMCL/0.1 (+https://github.com/Phi-Lia093/PMCL)");
        return client;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();

        foreach (var icon in _icons.Values)
            icon?.Dispose();

        _icons.Clear();
    }
}

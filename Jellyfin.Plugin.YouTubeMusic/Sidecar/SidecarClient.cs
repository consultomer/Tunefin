using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeMusic.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeMusic.Sidecar;

/// <summary>
/// Thin HTTP client for the ytm-sidecar service.
/// </summary>
public sealed class SidecarClient
{
    /// <summary>Name of the configured <see cref="HttpClient"/>.</summary>
    public const string HttpClientName = "YouTubeMusicSidecar";

    private const int MaxKnownTracks = 5000;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SidecarClient> _logger;

    // Remember metadata of tracks we've shown, so a favorite can be downloaded with the
    // exact album/artist even though Jellyfin's channel item model doesn't persist "Album".
    private readonly ConcurrentDictionary<string, SidecarSearchResult> _knownTracks = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SidecarClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public SidecarClient(IHttpClientFactory httpClientFactory, ILogger<SidecarClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets the base URL, without trailing slash.</summary>
    public static string BaseUrl => Config.SidecarUrl.TrimEnd('/');

    /// <summary>Gets the sidecar account key for a Jellyfin user (their cookies file name).</summary>
    /// <param name="userId">User id.</param>
    /// <returns>Account key.</returns>
    public static string AccountKey(Guid userId) => userId.ToString("N");

    /// <summary>Gets the stream URL for a video, played with a user's YouTube account.</summary>
    /// <param name="videoId">Video id.</param>
    /// <param name="userId">Jellyfin user whose YouTube cookies are used.</param>
    /// <returns>Absolute URL.</returns>
    public static string StreamUrl(string videoId, Guid? userId) =>
        $"{BaseUrl}/stream/{Uri.EscapeDataString(videoId)}" + (userId is Guid u ? "?account=" + AccountKey(u) : string.Empty);

    /// <summary>Gets headers that must accompany every sidecar request (also used for media sources).</summary>
    /// <returns>Header dictionary.</returns>
    public static Dictionary<string, string> RequiredHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Config.SidecarApiKey))
        {
            headers["X-Api-Key"] = Config.SidecarApiKey;
        }

        return headers;
    }

    /// <summary>Looks up remembered metadata for a video.</summary>
    /// <param name="videoId">Video id.</param>
    /// <returns>Search result, if seen.</returns>
    public SidecarSearchResult? GetKnownTrack(string videoId) =>
        _knownTracks.TryGetValue(videoId, out var r) ? r : null;

    /// <summary>Searches YouTube Music songs.</summary>
    /// <param name="query">Query.</param>
    /// <param name="limit">Max results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Results.</returns>
    public async Task<IReadOnlyList<SidecarSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}&limit={limit}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await CreateClient().SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var results = await response.Content
            .ReadFromJsonAsync<List<SidecarSearchResult>>(timeout.Token)
            .ConfigureAwait(false) ?? new List<SidecarSearchResult>();

        if (_knownTracks.Count > MaxKnownTracks)
        {
            _knownTracks.Clear();
        }

        foreach (var r in results)
        {
            _knownTracks[r.VideoId] = r;
        }

        return results;
    }

    /// <summary>Resolves container/codec info for a video (also warms the sidecar's URL cache).</summary>
    /// <param name="videoId">Video id.</param>
    /// <param name="userId">Jellyfin user whose YouTube cookies are used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resolve info, or null on failure.</returns>
    public async Task<SidecarResolveResult?> ResolveAsync(string videoId, Guid? userId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveTimeout);
            using var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/resolve/{Uri.EscapeDataString(videoId)}" + (userId is Guid u ? "?account=" + AccountKey(u) : string.Empty));
            using var response = await CreateClient().SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sidecar could not resolve {VideoId}: HTTP {Status}", videoId, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<SidecarResolveResult>(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Sidecar resolve failed for {VideoId}", videoId);
            return null;
        }
    }

    /// <summary>Asks the sidecar to download a track into the music library.</summary>
    /// <param name="body">Request body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw HTTP response (caller disposes).</returns>
    public async Task<HttpResponseMessage> DownloadAsync(SidecarDownloadRequest body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/download");
        request.Content = JsonContent.Create(body);
        return await CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the video ids in a user's private library.</summary>
    /// <param name="userFolder">User folder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Video ids.</returns>
    public async Task<HashSet<string>> GetLibraryAsync(string userFolder, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{BaseUrl}/library/{Uri.EscapeDataString(userFolder)}");
        using var response = await CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var lib = await response.Content.ReadFromJsonAsync<SidecarLibrary>(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(lib?.VideoIds ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    /// <summary>Removes a song from a user's private library.</summary>
    /// <param name="userFolder">User folder.</param>
    /// <param name="videoId">Video id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task RemoveFromLibraryAsync(string userFolder, string videoId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"{BaseUrl}/library/{Uri.EscapeDataString(userFolder)}/{Uri.EscapeDataString(videoId)}");
        using var response = await CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads a user's YouTube Music liked songs with their cookies.</summary>
    /// <param name="cookies">cookies.txt content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Liked songs (newest first).</returns>
    /// <exception cref="InvalidOperationException">Cookies rejected.</exception>
    public async Task<IReadOnlyList<SidecarSearchResult>> GetLikedSongsAsync(string cookies, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/likes");
        request.Content = JsonContent.Create(new { cookies });
        using var response = await CreateClient().SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var message = System.Text.RegularExpressions.Regex.Match(body, "\"error\":\"([^\"]*)\"").Groups[1].Value;
            throw new InvalidOperationException(string.IsNullOrEmpty(message) ? $"HTTP {(int)response.StatusCode}" : message);
        }

        var songs = await response.Content.ReadFromJsonAsync<List<SidecarSearchResult>>(cancellationToken).ConfigureAwait(false) ?? new();
        foreach (var s in songs)
        {
            _knownTracks[s.VideoId] = s;
        }

        return songs;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        foreach (var (key, value) in RequiredHeaders())
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient(HttpClientName);
}

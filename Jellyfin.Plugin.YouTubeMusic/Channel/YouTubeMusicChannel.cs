using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeMusic.Sidecar;
using Jellyfin.Plugin.YouTubeMusic.Users;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeMusic.Channel;

/// <summary>
/// "YouTube Music" channel: saved searches as folders, songs streamed through the sidecar.
/// </summary>
public class YouTubeMusicChannel : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia, IHasCacheKey
{
    /// <summary>Channel display name; also used to recognise our items.</summary>
    public const string ChannelName = "YouTube Music";

    /// <summary>Prefix of every media item id in this channel.</summary>
    public const string ItemIdPrefix = "ytm_";

    /// <summary>Provider id key stored on channel items.</summary>
    public const string ProviderKey = "YouTubeMusic";

    private const string SearchFolderPrefix = "search:";

    private readonly SidecarClient _sidecar;
    private readonly ILogger<YouTubeMusicChannel> _logger;
    private readonly IHttpContextAccessor _http;

    // Searches typed in clients (most recent last); each becomes a channel folder.
    private const int MaxRecentSearches = 30;
    private readonly object _recentLock = new();
    private readonly List<string> _recentSearches = new();
    private readonly ConcurrentDictionary<string, List<string>> _searchOrder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeMusicChannel"/> class.
    /// </summary>
    /// <param name="sidecar">Sidecar client.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="http">HTTP context accessor (to know which user is playing).</param>
    public YouTubeMusicChannel(SidecarClient sidecar, ILogger<YouTubeMusicChannel> logger, IHttpContextAccessor http)
    {
        _sidecar = sidecar;
        _logger = logger;
        _http = http;
    }

    /// <inheritdoc />
    public string Name => ChannelName;

    /// <inheritdoc />
    public string Description => "Search and stream YouTube Music. Favorite a song to download it to your library.";

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => "https://music.youtube.com";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Extracts the YouTube video id from a channel item id (<c>ytm_XXXXXXXXXXX</c>).</summary>
    /// <param name="itemId">Channel item id / external id.</param>
    /// <returns>The video id, or null if this isn't one of ours.</returns>
    public static string? TryGetVideoId(string? itemId)
    {
        if (string.IsNullOrEmpty(itemId) || !itemId.StartsWith(ItemIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = itemId[ItemIdPrefix.Length..];
        return id.Length == 11 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }

    /// <inheritdoc />
    public string GetCacheKey(string? userId)
    {
        lock (_recentLock)
        {
            // Connected and not-connected users must not share cached results.
            return (HasYouTube(userId) ? "yt|" : "none|") + string.Join('|', Config.SavedSearches) + "|" + string.Join('|', _recentSearches) + "|" + Config.SearchResultLimit;
        }
    }

    /// <summary>Registers a search typed in a client; returns the channel folder id for it.</summary>
    /// <param name="term">Search text.</param>
    /// <returns>Folder id (as stored in the item's ExternalId).</returns>
    public string RegisterSearch(string term)
    {
        term = term.Trim();
        lock (_recentLock)
        {
            _recentSearches.RemoveAll(s => s.Equals(term, StringComparison.OrdinalIgnoreCase));
            _recentSearches.Add(term);
            if (_recentSearches.Count > MaxRecentSearches)
            {
                _recentSearches.RemoveAt(0);
            }
        }

        return FolderId(term);
    }

    /// <summary>Gets YouTube's relevance order (video ids) for a search.</summary>
    /// <param name="term">Search text.</param>
    /// <returns>Video ids, best match first.</returns>
    public List<string> GetSearchOrder(string term) =>
        _searchOrder.TryGetValue(term.Trim(), out var order) ? order : new List<string>();

    /// <summary>Looks up search metadata for a channel item DTO.</summary>
    /// <param name="dto">Item.</param>
    /// <returns>Metadata, if known.</returns>
    public SidecarSearchResult? GetKnownTrack(MediaBrowser.Model.Dto.BaseItemDto dto) =>
        dto.ProviderIds is not null && dto.ProviderIds.TryGetValue(ProviderKey, out var vid) ? _sidecar.GetKnownTrack(vid) : null;

    /// <summary>Runs (and caches) a search so its folder can show the top result's artwork.</summary>
    /// <param name="term">Search text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public Task PrefetchAsync(string term, CancellationToken cancellationToken) => SearchAsync(term, cancellationToken);

    private static string FolderId(string term) => SearchFolderPrefix + term.Trim().ToLowerInvariant();

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Song },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Audio },
            MaxPageSize = 50,
            SupportsContentDownloading = false,
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => HasYouTube(userId);

    /// <summary>Only users who connected their own YouTube account (cookies) get YouTube Music.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>True if the user has YouTube cookies stored.</returns>
    public static bool HasYouTube(string? userId) =>
        Guid.TryParse(userId, out var id) && LikesSyncService.HasCookies(id);

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        if (!query.UserId.Equals(Guid.Empty) && !LikesSyncService.HasCookies(query.UserId))
        {
            return new ChannelItemResult();
        }

        if (string.IsNullOrEmpty(query.FolderId))
        {
            return GetRootFolders();
        }

        if (query.FolderId.StartsWith(SearchFolderPrefix, StringComparison.Ordinal))
        {
            var term = query.FolderId[SearchFolderPrefix.Length..];
            var items = await SearchAsync(term, cancellationToken).ConfigureAwait(false);
            return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
        }

        return new ChannelItemResult();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        var first = Config.SavedSearches.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        return first is null
            ? Enumerable.Empty<ChannelItemInfo>()
            : await SearchAsync(first, cancellationToken).ConfigureAwait(false);
    }

    private Guid? CurrentUserId() =>
        Guid.TryParse(_http.HttpContext?.User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : null;

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var videoId = TryGetVideoId(id) ?? throw new ArgumentException($"Not a YouTube Music item id: {id}", nameof(id));

        // Ask the sidecar what container we'll get so clients can direct-play without probing.
        // Play with the requesting user's own YouTube account.
        var userId = CurrentUserId();
        var resolved = await _sidecar.ResolveAsync(videoId, userId, cancellationToken).ConfigureAwait(false);
        var (container, codec) = MapFormat(resolved);
        var known = _sidecar.GetKnownTrack(videoId);

        var source = new MediaSourceInfo
        {
            Id = id,
            Name = known?.Title ?? videoId,
            Path = SidecarClient.StreamUrl(videoId, userId),
            Protocol = MediaProtocol.Http,
            IsRemote = true,
            Type = MediaSourceType.Default,
            Container = container,
            RunTimeTicks = known?.DurationSeconds is double s ? TimeSpan.FromSeconds(s).Ticks : null,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            // Only skip ffprobe when we actually know the format.
            SupportsProbing = resolved is null,
            RequiredHttpHeaders = SidecarClient.RequiredHeaders(),
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 0,
                    Codec = codec,
                    Channels = 2,
                    SampleRate = codec == "opus" ? 48000 : 44100,
                    IsDefault = true,
                },
            },
        };

        return new[] { source };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        var stream = typeof(YouTubeMusicChannel).Assembly
            .GetManifestResourceStream($"{typeof(Plugin).Namespace}.Resources.channel.png");

        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = stream is not null,
            Stream = stream,
            Format = ImageFormat.Png,
        });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop };
    }

    private static (string Container, string Codec) MapFormat(SidecarResolveResult? resolved)
    {
        if (resolved is null)
        {
            return ("m4a", "aac");
        }

        var codec = resolved.Acodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ? "aac"
            : resolved.Acodec.StartsWith("opus", StringComparison.OrdinalIgnoreCase) ? "opus"
            : resolved.Acodec;

        return resolved.Ext switch
        {
            "m4a" or "mp4" => ("m4a", codec),
            "webm" => ("webm", codec),
            var other => (other, codec),
        };
    }

    private ChannelItemResult GetRootFolders()
    {
        List<string> recent;
        lock (_recentLock)
        {
            recent = Enumerable.Reverse(_recentSearches).ToList(); // newest first
        }

        var folders = Config.SavedSearches
            .Concat(recent)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => new ChannelItemInfo
            {
                Id = FolderId(s),
                Name = s,
                ImageUrl = GetSearchOrder(s).FirstOrDefault() is string top ? _sidecar.GetKnownTrack(top)?.ThumbnailUrl : null,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
            })
            .ToList();

        return new ChannelItemResult { Items = folders, TotalRecordCount = folders.Count };
    }

    private async Task<List<ChannelItemInfo>> SearchAsync(string term, CancellationToken cancellationToken)
    {
        try
        {
            var results = await _sidecar
                .SearchAsync(term, Math.Clamp(Config.SearchResultLimit, 1, 50), cancellationToken)
                .ConfigureAwait(false);
            _searchOrder[term.Trim()] = results.Select(r => r.VideoId).ToList();
            // IndexNumber = relevance rank, so clients sorting by track number keep YouTube's order.
            return results.Select((r, i) => ToChannelItem(r, i + 1)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "YouTube Music search for '{Term}' failed; is the sidecar reachable at {Url}?", term, SidecarClient.BaseUrl);
            return new List<ChannelItemInfo>();
        }
    }

    private static ChannelItemInfo ToChannelItem(SidecarSearchResult r, int rank)
    {
        var artists = r.Artist
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var item = new ChannelItemInfo
        {
            Id = ItemIdPrefix + r.VideoId,
            Name = r.Title,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Song,
            MediaType = ChannelMediaType.Audio,
            Artists = artists,
            AlbumArtists = artists.Take(1).ToList(),
            RunTimeTicks = r.DurationSeconds is double s ? TimeSpan.FromSeconds(s).Ticks : null,
            ImageUrl = r.ThumbnailUrl,
            Overview = r.Album is null ? r.Artist : $"{r.Artist} — {r.Album}",
            ProviderIds = new Dictionary<string, string> { [ProviderKey] = r.VideoId },
            HomePageUrl = $"https://music.youtube.com/watch?v={r.VideoId}",
            IndexNumber = rank,
        };

        return item;
    }
}

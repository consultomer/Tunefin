using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.YouTubeMusic.Channel;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.YouTubeMusic.Search;

/// <summary>
/// Adds the top YouTube Music matches to normal song searches (<c>GET /Items?searchTerm=…&amp;includeItemTypes=Audio</c>),
/// which is what Jellyfin Web and Finamp call when you type in their search box.
/// The results are real channel items, so they can be played and favorited like any other song.
/// </summary>
public sealed class SearchInjectionFilter : IAsyncActionFilter
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private readonly IChannelManager _channelManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;
    private readonly YouTubeMusicChannel _channel;
    private readonly ILogger<SearchInjectionFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchInjectionFilter"/> class.
    /// </summary>
    /// <param name="channelManager">Channel manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="dtoService">DTO service.</param>
    /// <param name="channel">Our channel.</param>
    /// <param name="logger">Logger.</param>
    public SearchInjectionFilter(
        IChannelManager channelManager,
        IUserManager userManager,
        IDtoService dtoService,
        YouTubeMusicChannel channel,
        ILogger<SearchInjectionFilter> logger)
    {
        _channelManager = channelManager;
        _userManager = userManager;
        _dtoService = dtoService;
        _channel = channel;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (IsGetSingleItem(context))
        {
            // Keep a "YouTube: term" card looking like an album when a client (Finamp) re-fetches it.
            var single = await next().ConfigureAwait(false);
            if (single.Result is ObjectResult { Value: BaseItemDto item }
                && item.Type == BaseItemKind.ChannelFolderItem
                && item.ChannelName == YouTubeMusicChannel.ChannelName)
            {
                item.Type = BaseItemKind.MusicAlbum;
                item.Name = $"YouTube: {item.Name}";
                item.AlbumArtist = "YouTube Music";
            }

            return;
        }

        var (term, mode) = GetSearchTerm(context);
        if (term is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Start the YouTube lookup in parallel with Jellyfin's own (local) search.
        // Users who haven't connected YouTube get Jellyfin's normal search only.
        var userId = GetUserId(context);
        if (userId is not Guid connected || !Users.LikesSyncService.HasCookies(connected))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var youtubeTask = userId is Guid uid
            ? (mode == SearchMode.Songs
                ? FindYouTubeItemsAsync(term, uid, context.HttpContext.RequestAborted)
                : FindYouTubeAlbumCardAsync(term, uid, context.HttpContext.RequestAborted))
            : null;
        _logger.LogInformation("YouTube Music: {Mode} search '{Term}' from {Client}", mode, term, context.HttpContext.User.FindFirst("Jellyfin-Client")?.Value ?? "unknown client");

        var executed = await next().ConfigureAwait(false);
        if (youtubeTask is null
            || executed.Exception is not null
            || executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> local } objectResult)
        {
            return;
        }

        try
        {
            var youtube = await youtubeTask.ConfigureAwait(false);
            if (youtube.Count == 0)
            {
                return;
            }

            // Skip YouTube hits you already have locally (e.g. songs you liked before).
            var localIds = local.Items.Select(i => i.Id).ToHashSet();
            var localNames = local.Items.Select(i => Normalize(i.Name)).ToHashSet(StringComparer.Ordinal);
            var extra = mode == SearchMode.Songs
                ? youtube.Where(d => !localIds.Contains(d.Id) && !localNames.Contains(Normalize(d.Name))).ToList()
                : youtube.Where(d => !localIds.Contains(d.Id)).ToList();
            if (extra.Count == 0)
            {
                return;
            }

            // Local results first, then the YouTube suggestions.
            var merged = local.Items.Concat(extra).ToArray();
            objectResult.Value = new QueryResult<BaseItemDto>(local.StartIndex, local.TotalRecordCount + extra.Count, merged);
        }
        catch (Exception ex)
        {
            // Never break the normal search because YouTube is slow or down.
            _logger.LogWarning(ex, "YouTube Music search for '{Term}' failed; returning local results only", term);
        }
    }

    private static bool IsGetSingleItem(ActionExecutingContext context) =>
        Plugin.Instance?.Configuration.EnableSearchInjection == true
        && context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "UserLibrary", ActionName: "GetItem" or "GetItemLegacy" };

    private static string Normalize(string? s) => (s ?? string.Empty).Trim().ToUpperInvariant();

    private enum SearchMode
    {
        /// <summary>Song search: add YouTube songs.</summary>
        Songs,

        /// <summary>Album search (Finamp's Albums tab): add one "YouTube: term" album card.</summary>
        Albums,
    }

    private static (string? Term, SearchMode Mode) GetSearchTerm(ActionExecutingContext context)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableSearchInjection)
        {
            return (null, default);
        }

        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "Items" } action
            || action.ActionName is not ("GetItems" or "GetItemsByUserIdLegacy"))
        {
            return (null, default);
        }

        var q = context.HttpContext.Request.Query;
        var term = First(q, "searchTerm")?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < 2)
        {
            return (null, default);
        }

        // Only the first page, and not when filtering favorites.
        var start = First(q, "startIndex");
        if ((!string.IsNullOrEmpty(start) && start != "0")
            || string.Equals(First(q, "isFavorite"), "true", StringComparison.OrdinalIgnoreCase)
            || Values(q, "filters").Any(f => f.Equals("IsFavorite", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, default);
        }

        var types = Values(q, "includeItemTypes").ToList();
        if (types.Any(t => t.Equals("Audio", StringComparison.OrdinalIgnoreCase)))
        {
            return (term, SearchMode.Songs);
        }

        if (types.Count == 1 && types[0].Equals("MusicAlbum", StringComparison.OrdinalIgnoreCase))
        {
            return (term, SearchMode.Albums);
        }

        return (null, default);
    }

    private static string? First(IQueryCollection q, string key) =>
        q.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value.FirstOrDefault();

    private static IEnumerable<string> Values(IQueryCollection q, string key) =>
        q.Where(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value)
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static Guid? GetUserId(ActionExecutingContext context)
    {
        var claim = context.HttpContext.User.FindFirst(UserIdClaim)?.Value;
        if (Guid.TryParse(claim, out var id))
        {
            return id;
        }

        var q = context.HttpContext.Request.Query;
        var fromQuery = First(q, "userId") ?? context.RouteData.Values["userId"]?.ToString();
        return Guid.TryParse(fromQuery, out id) ? id : null;
    }

    /// <summary>Finamp Albums tab: one album card ("YouTube: term") that opens the search folder.</summary>
    private async Task<IReadOnlyList<BaseItemDto>> FindYouTubeAlbumCardAsync(string term, Guid userId, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(Timeout);
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Array.Empty<BaseItemDto>();
        }

        // Run the search first so the folder gets the top result's artwork.
        await _channel.PrefetchAsync(term, cts.Token).ConfigureAwait(false);
        var found = await FindFolderAsync(term, user, cts.Token).ConfigureAwait(false);
        if (found is null)
        {
            return Array.Empty<BaseItemDto>();
        }

        var dto = _dtoService.GetBaseItemDto(found.Value.Folder, new DtoOptions(true), user);

        // Present it as an album so Finamp shows it in the Albums tab and opens it as one;
        // Finamp then lists its tracks with ParentId=<folder>, which Jellyfin serves from the channel.
        dto.Type = BaseItemKind.MusicAlbum;
        dto.IsFolder = true;
        dto.Name = $"YouTube: {term}";
        dto.AlbumArtist = "YouTube Music";
        dto.AlbumArtists = new[] { new NameGuidPair { Name = "YouTube Music", Id = Guid.Empty } };
        dto.ChildCount = _channel.GetSearchOrder(term).Count;
        return new[] { dto };
    }

    private async Task<(BaseItem Folder, Guid ChannelId)?> FindFolderAsync(string term, User user, CancellationToken ct)
    {
        var folderExternalId = _channel.RegisterSearch(term);

        var channels = await _channelManager.GetChannelsInternalAsync(new ChannelQuery { UserId = user.Id }).ConfigureAwait(false);
        var channelItem = channels.Items.FirstOrDefault(c => c.Name == YouTubeMusicChannel.ChannelName);
        if (channelItem is null)
        {
            _logger.LogWarning("YouTube Music channel not registered yet (run 'Refresh Channels')");
            return null;
        }

        var root = await _channelManager.GetChannelItemsInternal(
            new InternalItemsQuery(user) { ChannelIds = new[] { channelItem.Id } },
            new Progress<double>(),
            ct).ConfigureAwait(false);

        var folder = root.Items.FirstOrDefault(i => i.ExternalId == folderExternalId);
        return folder is null ? null : (folder, channelItem.Id);
    }

    private async Task<IReadOnlyList<BaseItemDto>> FindYouTubeItemsAsync(string term, Guid userId, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        cts.CancelAfter(Timeout);
        var ct = cts.Token;

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return Array.Empty<BaseItemDto>();
        }

        var found = await FindFolderAsync(term, user, ct).ConfigureAwait(false);
        if (found is null)
        {
            return Array.Empty<BaseItemDto>();
        }

        var (folder, channelId) = found.Value;
        var songs = await _channelManager.GetChannelItemsInternal(
            new InternalItemsQuery(user) { ChannelIds = new[] { channelId }, ParentId = folder.Id },
            new Progress<double>(),
            ct).ConfigureAwait(false);

        // Keep YouTube's relevance order (channel results come back sorted by name).
        var order = _channel.GetSearchOrder(term);
        var items = songs.Items
            .OrderBy(i => order.IndexOf(YouTubeMusicChannel.TryGetVideoId(i.ExternalId) ?? string.Empty) is var idx && idx >= 0 ? idx : int.MaxValue)
            .Take(Math.Clamp(Plugin.Instance?.Configuration.SearchInjectionLimit ?? 5, 1, 20))
            .ToList();

        var dtos = _dtoService.GetBaseItemDtos(items, new DtoOptions(true), user, null, false);
        foreach (var dto in dtos)
        {
            // Make it obvious in the client that this is a YouTube result, not a local file.
            var known = _channel.GetKnownTrack(dto);
            dto.Album = known?.Album is { Length: > 0 } album ? $"{album} · YouTube" : "YouTube Music";
        }

        return dtos;
    }
}

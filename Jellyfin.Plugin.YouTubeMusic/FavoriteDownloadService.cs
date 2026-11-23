using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeMusic.Channel;
using Jellyfin.Plugin.YouTubeMusic.Sidecar;
using Jellyfin.Plugin.YouTubeMusic.Users;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeMusic;

/// <summary>
/// ♥ on a YouTube song  → download into that user's private library (shared store, no duplicates).
/// New file appears     → marked as favorite for its owner, so it shows up in their Favorites.
/// ♡ (un-heart) on it   → removed from that user's library (the file is freed when nobody else has it).
/// </summary>
public sealed class FavoriteDownloadService : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly SidecarClient _sidecar;
    private readonly UserLibraryService _libraries;
    private readonly LibraryScanScheduler _scan;
    private readonly ILogger<FavoriteDownloadService> _logger;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    // Set while we mark files as favorite ourselves, so those events aren't treated as user actions.
    private static readonly AsyncLocal<bool> Internal = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoriteDownloadService"/> class.
    /// </summary>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="sidecar">Sidecar client.</param>
    /// <param name="libraries">User libraries.</param>
    /// <param name="scan">Scan scheduler.</param>
    /// <param name="logger">Logger.</param>
    public FavoriteDownloadService(
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        SidecarClient sidecar,
        UserLibraryService libraries,
        LibraryScanScheduler scan,
        ILogger<FavoriteDownloadService> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _sidecar = sidecar;
        _libraries = libraries;
        _scan = scan;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemAdded; // re-liked songs reuse the old item (same path)
        _logger.LogInformation("YouTube Music favorite-to-download listener started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemAdded;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is not Audio || _libraries.FindOwner(e.Item.Path) is not { } found)
            {
                return;
            }

            // A song landed in someone's private "Liked" library: show it as a favorite for them.
            var data = _userDataManager.GetUserData(found.Owner, e.Item);
            if (data is { IsFavorite: false })
            {
                data.IsFavorite = true;
                Internal.Value = true;
                try
                {
                    _userDataManager.SaveUserData(found.Owner, e.Item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
                }
                finally
                {
                    Internal.Value = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking new YouTube Music file as favorite");
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            if (Internal.Value
                || Plugin.Instance?.Configuration is not { EnableAutoDownloadOnFavorite: true }
                || e.SaveReason is not (UserDataSaveReason.UpdateUserRating or UserDataSaveReason.UpdateUserData)
                || e.UserData is null
                || e.Item is null)
            {
                return;
            }

            var user = _userManager.GetUserById(e.UserId);
            if (user is null)
            {
                return;
            }

            // Case 1: a YouTube channel song.
            var videoId = GetChannelVideoId(e.Item);
            if (videoId is not null)
            {
                if (e.UserData.IsFavorite)
                {
                    Download(user, e.Item, videoId);
                }
                else
                {
                    Remove(user, videoId);
                }

                return;
            }

            // Case 2: un-hearting the downloaded copy in your own private library removes it.
            if (!e.UserData.IsFavorite && _libraries.FindOwner(e.Item.Path) is { } found && found.Owner.Id.Equals(user.Id))
            {
                // Also clear the heart on the YouTube search result, or the next import would bring it back.
                ClearChannelFavorite(user, found.VideoId);
                Remove(user, found.VideoId);
            }
        }
        catch (Exception ex)
        {
            // Never let a plugin exception bubble into Jellyfin's user-data pipeline.
            _logger.LogError(ex, "Error handling UserDataSaved event");
        }
    }

    private void Download(Jellyfin.Database.Implementations.Entities.User user, BaseItem item, string videoId)
    {
        if (!LikesSyncService.HasCookies(user.Id))
        {
            _logger.LogInformation("{User} liked a YouTube song but hasn't connected a YouTube account; not downloading", user.Username);
            return;
        }

        var key = user.Id.ToString("N") + videoId;
        if (!_inFlight.TryAdd(key, 0))
        {
            return; // double tap
        }

        var known = _sidecar.GetKnownTrack(videoId);
        var audio = item as Audio;
        var request = new SidecarDownloadRequest
        {
            VideoId = videoId,
            Title = known?.Title ?? item.Name ?? videoId,
            Artist = known?.Artist ?? (audio?.Artists is { Count: > 0 } a ? string.Join(", ", a) : "Unknown Artist"),
            Album = known?.Album ?? (string.IsNullOrWhiteSpace(item.Album) ? null : item.Album),
            User = UserLibraryService.FolderName(user.Username),
            Account = SidecarClient.AccountKey(user.Id),
        };

        _ = Task.Run(
            async () =>
            {
                try
                {
                    _logger.LogInformation("{User} liked {Artist} - {Title} ({VideoId}); adding to their library", user.Username, request.Artist, request.Title, videoId);
                    await _libraries.EnsureLibraryAsync(user).ConfigureAwait(false);

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    using var response = await _sidecar.DownloadAsync(request, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("Sidecar failed to download {VideoId}: HTTP {Status} {Body}", videoId, (int)response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                        return;
                    }

                    var result = await response.Content.ReadFromJsonAsync<SidecarDownloadResult>(timeout.Token).ConfigureAwait(false);
                    _logger.LogInformation("{VideoId} is in {User}'s library: {Path}{Existing}", videoId, user.Username, result?.FilePath, result?.AlreadyExisted == true ? " (already there)" : string.Empty);
                    if (result?.AlreadyExisted != true)
                    {
                        _scan.Request();
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Download of {VideoId} for {User} failed; is the sidecar reachable at {Url}?", videoId, user.Username, SidecarClient.BaseUrl);
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                }
            },
            _stopping.Token);
    }

    private void Remove(Jellyfin.Database.Implementations.Entities.User user, string videoId)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _sidecar.RemoveFromLibraryAsync(UserLibraryService.FolderName(user.Username), videoId, _stopping.Token).ConfigureAwait(false);
                    _logger.LogInformation("{User} unliked {VideoId}; removed from their library", user.Username, videoId);
                    _scan.Request();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not remove {VideoId} from {User}'s library", videoId, user.Username);
                }
            },
            _stopping.Token);
    }

    private void ClearChannelFavorite(Jellyfin.Database.Implementations.Entities.User user, string videoId)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio },
            IsFavorite = true,
            Recursive = true,
        });

        foreach (var item in items.Where(i => GetChannelVideoId(i) == videoId))
        {
            var data = _userDataManager.GetUserData(user, item);
            if (data is { IsFavorite: true })
            {
                data.IsFavorite = false;
                Internal.Value = true;
                try
                {
                    _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
                }
                finally
                {
                    Internal.Value = false;
                }
            }
        }
    }

    private static string? GetChannelVideoId(BaseItem item)
    {
        if (item.ChannelId.Equals(Guid.Empty))
        {
            return null;
        }

        return YouTubeMusicChannel.TryGetVideoId(item.ExternalId)
            ?? (item.ProviderIds.TryGetValue(YouTubeMusicChannel.ProviderKey, out var pid) ? YouTubeMusicChannel.TryGetVideoId(YouTubeMusicChannel.ItemIdPrefix + pid) : null);
    }
}

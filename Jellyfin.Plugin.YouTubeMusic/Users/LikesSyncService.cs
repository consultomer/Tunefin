using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.YouTubeMusic.Channel;
using Jellyfin.Plugin.YouTubeMusic.Sidecar;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeMusic.Users;

/// <summary>Sync status shown to the user / admin.</summary>
public sealed class LikesSyncStatus
{
    /// <summary>Gets or sets a value indicating whether cookies are stored.</summary>
    public bool HasCookies { get; set; }

    /// <summary>Gets or sets a value indicating whether a sync is running.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets the last sync time (UTC).</summary>
    public DateTime? LastSync { get; set; }

    /// <summary>Gets or sets the last result message.</summary>
    public string? LastResult { get; set; }

    /// <summary>Gets or sets the number of liked songs on YouTube Music.</summary>
    public int LikedOnYouTube { get; set; }

    /// <summary>Gets or sets the number of songs in the user's private library.</summary>
    public int InLibrary { get; set; }
}

/// <summary>
/// Per-user: stores YouTube cookies, imports YouTube Music "Liked songs", and backfills Jellyfin
/// favorites, into the user's private library (no duplicate files: the sidecar hardlinks a shared store).
/// </summary>
public sealed class LikesSyncService
{
    private readonly SidecarClient _sidecar;
    private readonly UserLibraryService _libraries;
    private readonly LibraryScanScheduler _scan;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LikesSyncService> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LikesSyncService"/> class.
    /// </summary>
    /// <param name="sidecar">Sidecar client.</param>
    /// <param name="libraries">User libraries.</param>
    /// <param name="scan">Scan scheduler.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public LikesSyncService(
        SidecarClient sidecar,
        UserLibraryService libraries,
        LibraryScanScheduler scan,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<LikesSyncService> logger)
    {
        _sidecar = sidecar;
        _libraries = libraries;
        _scan = scan;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static string DataDir
    {
        get
        {
            var dir = Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "users");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string CookiePath(Guid userId) => Path.Combine(DataDir, $"{userId:N}.cookies.txt");

    private static string StatusPath(Guid userId) => Path.Combine(DataDir, $"{userId:N}.status.json");

    /// <summary>Gets a value indicating whether the user has stored cookies.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>True if stored.</returns>
    public static bool HasCookies(Guid userId) => File.Exists(CookiePath(userId));

    /// <summary>Gets the status for a user.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>Status.</returns>
    public LikesSyncStatus GetStatus(Guid userId)
    {
        LikesSyncStatus status;
        try
        {
            status = JsonSerializer.Deserialize<LikesSyncStatus>(File.ReadAllText(StatusPath(userId))) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            status = new LikesSyncStatus();
        }

        status.HasCookies = HasCookies(userId);
        status.Running = _running.ContainsKey(userId);
        return status;
    }

    /// <summary>Validates cookies against YouTube and stores them for the user.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="cookies">cookies.txt content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of liked songs found.</returns>
    public async Task<int> SaveCookiesAsync(Guid userId, string cookies, CancellationToken cancellationToken)
    {
        var liked = await _sidecar.GetLikedSongsAsync(cookies, cancellationToken).ConfigureAwait(false);
        var path = CookiePath(userId);
        await File.WriteAllTextAsync(path, cookies, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var status = GetStatus(userId);
        status.LikedOnYouTube = liked.Count;
        status.LastResult = $"Cookies saved - {liked.Count} liked songs found on YouTube Music.";
        SaveStatus(userId, status);
        return liked.Count;
    }

    /// <summary>Deletes the user's stored cookies.</summary>
    /// <param name="userId">User id.</param>
    public void DeleteCookies(Guid userId)
    {
        File.Delete(CookiePath(userId));
        var status = GetStatus(userId);
        status.LastResult = "YouTube disconnected - search, playback and likes import are off until you connect again. Downloaded songs stay in your library.";
        SaveStatus(userId, status);
    }

    /// <summary>Starts a background sync for a user (no-op if one is running).</summary>
    /// <param name="userId">User id.</param>
    /// <returns>True if started.</returns>
    public bool StartSync(Guid userId)
    {
        if (_running.ContainsKey(userId))
        {
            return false;
        }

        _ = Task.Run(() => SyncUserAsync(userId, CancellationToken.None));
        return true;
    }

    /// <summary>Syncs one user: Jellyfin favorites + YouTube Music likes into the private library.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task SyncUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        // One sync per user at a time (startup task, 6-hourly task and "Import now" can overlap).
        if (!_running.TryAdd(userId, Task.CompletedTask))
        {
            return;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            _running.TryRemove(userId, out _);
            return;
        }

        var status = GetStatus(userId);
        if (!status.HasCookies)
        {
            // No YouTube account connected: nothing can be downloaded for this user.
            _running.TryRemove(userId, out _);
            return;
        }

        status.Running = true;
        try
        {
            var folder = UserLibraryService.FolderName(user.Username);
            var have = await _sidecar.GetLibraryAsync(folder, cancellationToken).ConfigureAwait(false);
            var wanted = new List<SidecarDownloadRequest>();

            // 1. YouTube channel songs this user hearted in Jellyfin (also migrates older likes).
            foreach (var fav in GetChannelFavorites(user))
            {
                if (!have.Contains(fav.VideoId))
                {
                    wanted.Add(fav);
                }
            }

            // 2. The user's YouTube Music "Liked songs".
            var skippedLocal = 0;
            if (HasCookies(userId))
            {
                var cookies = await File.ReadAllTextAsync(CookiePath(userId), cancellationToken).ConfigureAwait(false);
                var liked = await _sidecar.GetLikedSongsAsync(cookies, cancellationToken).ConfigureAwait(false);
                status.LikedOnYouTube = liked.Count;
                foreach (var song in liked)
                {
                    if (have.Contains(song.VideoId) || wanted.Any(w => w.VideoId == song.VideoId))
                    {
                        continue;
                    }

                    // Already in one of the user's normal libraries (e.g. ripped CD)? Don't duplicate it.
                    if (HasLocalCopy(user, song.Title, song.Artist))
                    {
                        skippedLocal++;
                        continue;
                    }

                    wanted.Add(new SidecarDownloadRequest { VideoId = song.VideoId, Title = song.Title, Artist = song.Artist, Album = song.Album });
                }
            }

            if (wanted.Count > 0)
            {
                await _libraries.EnsureLibraryAsync(user).ConfigureAwait(false);
            }

            int added = 0, failed = 0;
            using var gate = new SemaphoreSlim(Math.Clamp(Plugin.Instance?.Configuration.ImportConcurrency ?? 10, 1, 32));
            await Task.WhenAll(wanted.Select(async req =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    req.User = folder;
                    req.Account = SidecarClient.AccountKey(userId);
                    using var response = await _sidecar.DownloadAsync(req, cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref added);
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogWarning("Import of {VideoId} for {User} failed: HTTP {Status}", req.VideoId, user.Username, (int)response.StatusCode);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning(ex, "Import of {VideoId} for {User} failed", req.VideoId, user.Username);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            if (added > 0)
            {
                _scan.Request();
            }

            status.InLibrary = have.Count + added;
            status.LastSync = DateTime.UtcNow;
            status.LastResult = $"Added {added} song(s)"
                + (failed > 0 ? $", {failed} failed" : string.Empty)
                + (skippedLocal > 0 ? $", {skippedLocal} skipped (already in your library)" : string.Empty)
                + ".";
            _logger.LogInformation("YouTube Music likes sync for {User}: {Result}", user.Username, status.LastResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status.LastSync = DateTime.UtcNow;
            status.LastResult = "Sync failed: " + ex.Message;
            _logger.LogError(ex, "YouTube Music likes sync for {User} failed", user.Username);
        }
        finally
        {
            status.Running = false;
            SaveStatus(userId, status);
            _running.TryRemove(userId, out _);
        }
    }

    private IEnumerable<SidecarDownloadRequest> GetChannelFavorites(User user)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            IsFavorite = true,
            Recursive = true,
        });

        foreach (var item in items)
        {
            var videoId = item.ChannelId.Equals(Guid.Empty) ? null : YouTubeMusicChannel.TryGetVideoId(item.ExternalId);
            if (videoId is null)
            {
                continue;
            }

            var known = _sidecar.GetKnownTrack(videoId);
            var audio = item as Audio;
            yield return new SidecarDownloadRequest
            {
                VideoId = videoId,
                Title = known?.Title ?? item.Name ?? videoId,
                Artist = known?.Artist ?? (audio?.Artists is { Count: > 0 } a ? string.Join(", ", a) : "Unknown Artist"),
                Album = known?.Album,
            };
        }
    }

    private bool HasLocalCopy(User user, string title, string artist)
    {
        var root = Plugin.Instance?.Configuration.LibraryRootPath ?? "/media/YouTube";
        var artists = artist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToUpperInvariant()).ToHashSet();

        var matches = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Recursive = true,
            SearchTerm = title,
            Limit = 50,
        });

        return matches.Any(i =>
            !string.IsNullOrEmpty(i.Path)
            && !i.Path.StartsWith(root, StringComparison.Ordinal)
            && string.Equals(i.Name?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)
            && i is Audio a
            && a.Artists.Concat(a.AlbumArtists).Any(x => artists.Contains(x.ToUpperInvariant())));
    }

    private static void SaveStatus(Guid userId, LikesSyncStatus status)
    {
        try
        {
            File.WriteAllText(StatusPath(userId), JsonSerializer.Serialize(status));
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

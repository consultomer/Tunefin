using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.YouTubeMusic.Users;

/// <summary>
/// Scheduled task: keeps library privacy rules applied and imports every user's
/// favorites / YouTube Music likes into their private library.
/// </summary>
public sealed class ImportLikesTask : IScheduledTask
{
    private readonly LikesSyncService _sync;
    private readonly UserLibraryService _libraries;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportLikesTask"/> class.
    /// </summary>
    /// <param name="sync">Sync service.</param>
    /// <param name="libraries">User libraries.</param>
    /// <param name="userManager">User manager.</param>
    public ImportLikesTask(LikesSyncService sync, UserLibraryService libraries, IUserManager userManager)
    {
        _sync = sync;
        _libraries = libraries;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public string Name => "Import YouTube Music likes";

    /// <inheritdoc />
    public string Key => "YouTubeMusicImportLikes";

    /// <inheritdoc />
    public string Description => "Downloads each user's liked songs (Jellyfin favorites and, if they added cookies, their YouTube Music likes) into their private library, and keeps those libraries private.";

    /// <inheritdoc />
    public string Category => "YouTube Music";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _libraries.ApplyAccessAsync().ConfigureAwait(false);
        var users = _userManager.GetUsers().ToList();
        for (var i = 0; i < users.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _sync.SyncUserAsync(users[i].Id, cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / users.Count);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks },
        };
    }
}

using System;
using System.Threading;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeMusic.Users;

/// <summary>Debounced library scan: many downloads in a row trigger a single scan.</summary>
public sealed class LibraryScanScheduler : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(15);
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryScanScheduler> _logger;
    private readonly Timer _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScanScheduler"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryScanScheduler(ILibraryManager libraryManager, ILogger<LibraryScanScheduler> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Requests a scan (debounced).</summary>
    public void Request()
    {
        if (Plugin.Instance?.Configuration.TriggerLibraryScanAfterDownload == true)
        {
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer.Dispose();

    private void Run()
    {
        try
        {
            _logger.LogInformation("Queuing library scan after YouTube Music changes");
            _libraryManager.QueueLibraryScan();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue library scan");
        }
    }
}

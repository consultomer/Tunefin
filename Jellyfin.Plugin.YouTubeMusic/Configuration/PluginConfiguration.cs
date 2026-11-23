using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTubeMusic.Configuration;

/// <summary>
/// Plugin configuration (persisted as XML by Jellyfin).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the ytm-sidecar service, as reachable from the Jellyfin server.
    /// </summary>
    public string SidecarUrl { get; set; } = "http://ytm-sidecar:8787";

    /// <summary>
    /// Gets or sets an optional shared secret sent to the sidecar as <c>X-Api-Key</c>.
    /// </summary>
    public string SidecarApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether favoriting a YouTube Music item downloads it.
    /// </summary>
    public bool EnableAutoDownloadOnFavorite { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a library scan is queued after a download completes.
    /// </summary>
    public bool TriggerLibraryScanAfterDownload { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of results returned per search.
    /// </summary>
    public int SearchResultLimit { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether YouTube matches are added to normal song searches
    /// (the search box in Jellyfin Web / Finamp).
    /// </summary>
    public bool EnableSearchInjection { get; set; } = true;

    /// <summary>
    /// Gets or sets how many YouTube matches are added to a normal song search.
    /// </summary>
    public int SearchInjectionLimit { get; set; } = 5;

    /// <summary>
    /// Gets or sets how many songs a likes import downloads at the same time.
    /// The sidecar's MAX_CONCURRENT_DOWNLOADS must be at least this high to benefit.
    /// </summary>
    public int ImportConcurrency { get; set; } = 10;

    /// <summary>
    /// Gets or sets the folder (as seen by Jellyfin) holding one private library per user.
    /// Must be the same host folder the sidecar mounts as YOUTUBE_DIR, and not inside another library.
    /// </summary>
    public string LibraryRootPath { get; set; } = "/media/YouTube";

    /// <summary>Gets or sets the private library name; {0} = user name.</summary>
    public string LibraryNameFormat { get; set; } = "Liked – {0}";

    /// <summary>
    /// Gets or sets users that had "access all libraries" and were switched to an explicit list
    /// (to hide other users' private libraries). New libraries are added for them automatically.
    /// </summary>
    public Guid[] ManagedAllFolderUsers { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets the saved searches shown as folders at the root of the channel.
    /// Clients that cannot type into a channel (most of them) browse these instead.
    /// </summary>
    public string[] SavedSearches { get; set; } = Array.Empty<string>();
}

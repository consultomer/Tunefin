using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeMusic.Users;

/// <summary>
/// One private Jellyfin music library per user ("Liked – name"), backed by
/// <c>{LibraryRootPath}/{name}/</c> which the sidecar fills with hardlinks to a shared store.
/// Keeps every other user's library access so they can't see it.
/// </summary>
public sealed partial class UserLibraryService
{
    private const int MaxSegmentBytes = 150;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<UserLibraryService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="UserLibraryService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="logger">Logger.</param>
    public UserLibraryService(ILibraryManager libraryManager, IUserManager userManager, ILogger<UserLibraryService> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    /// <summary>Folder name for a user (same rules as the sidecar's sanitizeSegment).</summary>
    /// <param name="username">User name.</param>
    /// <returns>Safe folder name.</returns>
    public static string FolderName(string username)
    {
        var s = Regex.Replace(username.Normalize(NormalizationForm.FormC), @"[\u0000-\u001f\u007f]", string.Empty);
        s = Regex.Replace(s, @"[<>:""/\\|?*]", "_");
        s = Regex.Replace(s, @"\s+", " ").Trim().Trim('.', ' ');
        if (Regex.IsMatch(s, "^(con|prn|aux|nul|com\\d|lpt\\d)$", RegexOptions.IgnoreCase))
        {
            s = "_" + s;
        }

        while (Encoding.UTF8.GetByteCount(s) > MaxSegmentBytes)
        {
            s = s[..^1].TrimEnd();
        }

        return s.Length > 0 ? s : "user";
    }

    /// <summary>Gets the library folder (as seen by Jellyfin) for a user.</summary>
    /// <param name="user">User.</param>
    /// <returns>Absolute path.</returns>
    public static string LibraryPath(User user) => Path.Combine(Config.LibraryRootPath, FolderName(user.Username));

    /// <summary>Finds which user owns a file in a private library, if any.</summary>
    /// <param name="filePath">Media file path.</param>
    /// <returns>(owner, videoId) or null.</returns>
    public (User Owner, string VideoId)? FindOwner(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(filePath);
        if (dir is null || !SamePath(Path.GetDirectoryName(dir), Config.LibraryRootPath))
        {
            return null;
        }

        var owner = _userManager.GetUsers().FirstOrDefault(u => SamePath(LibraryPath(u), dir));
        if (owner is null)
        {
            return null;
        }

        var videoId = ReadManifest(dir).FirstOrDefault(kv => kv.Value == Path.GetFileName(filePath)).Key;
        return videoId is null ? null : (owner, videoId);
    }

    /// <summary>Reads a user folder's manifest (videoId → file name).</summary>
    /// <param name="userDir">User folder.</param>
    /// <returns>Manifest.</returns>
    public static Dictionary<string, string> ReadManifest(string userDir)
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(userDir, ".ytm-library.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Creates the user's private library if needed, then re-applies access rules.</summary>
    /// <param name="user">User.</param>
    /// <returns>Task.</returns>
    public async Task EnsureLibraryAsync(User user)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = LibraryPath(user);
            Directory.CreateDirectory(path);
            var exists = _libraryManager.GetVirtualFolders().Any(f => f.Locations.Any(l => SamePath(l, path)));
            if (!exists)
            {
                var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, Config.LibraryNameFormat, user.Username);
                _logger.LogInformation("Creating private library '{Name}' at {Path}", name, path);
                var options = new LibraryOptions
                {
                    PathInfos = new[] { new MediaPathInfo(path) },
                    EnableRealtimeMonitor = true,
                };
                await _libraryManager.AddVirtualFolder(name, CollectionTypeOptions.music, options, true).ConfigureAwait(false);
            }

            await ApplyAccessCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Makes sure nobody can see another user's private library.</summary>
    /// <returns>Task.</returns>
    public async Task ApplyAccessAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ApplyAccessCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ApplyAccessCoreAsync()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var folders = _libraryManager.GetVirtualFolders();
        var users = _userManager.GetUsers().ToList();

        var ownerOf = new Dictionary<Guid, Guid>();
        foreach (var f in folders)
        {
            if (!Guid.TryParse(f.ItemId, out var libId))
            {
                continue;
            }

            var owner = users.FirstOrDefault(u => f.Locations.Any(l => SamePath(l, LibraryPath(u))));
            if (owner is not null)
            {
                ownerOf[libId] = owner.Id;
            }
        }

        var allIds = folders.Select(f => Guid.TryParse(f.ItemId, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var managed = plugin.Configuration.ManagedAllFolderUsers.ToHashSet();
        var managedChanged = false;

        foreach (var user in users)
        {
            var policy = _userManager.GetUserDto(user, null).Policy;
            var othersPrivate = ownerOf.Where(kv => kv.Value != user.Id).Select(kv => kv.Key).ToHashSet();
            var own = ownerOf.Where(kv => kv.Value == user.Id).Select(kv => kv.Key);

            Guid[] desired;
            var switched = false;
            if (policy.EnableAllFolders)
            {
                if (othersPrivate.Count == 0)
                {
                    continue; // nothing to hide yet
                }

                // "Access all libraries" would expose other people's likes: switch this user to an
                // explicit list, and remember to keep adding new libraries for them automatically.
                policy.EnableAllFolders = false;
                switched = true;
                desired = allIds.Where(id => !othersPrivate.Contains(id)).ToArray();
                managedChanged |= managed.Add(user.Id);
            }
            else if (managed.Contains(user.Id))
            {
                desired = allIds.Where(id => !othersPrivate.Contains(id)).ToArray();
            }
            else
            {
                desired = policy.EnabledFolders.Where(id => !othersPrivate.Contains(id)).Union(own).ToArray();
            }

            if (!switched && desired.ToHashSet().SetEquals(policy.EnabledFolders))
            {
                continue;
            }

            policy.EnabledFolders = desired;
            _logger.LogInformation("Updating library access for {User}: {Count} libraries (private libraries of other users hidden)", user.Username, desired.Length);
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }

        if (managedChanged)
        {
            plugin.Configuration.ManagedAllFolderUsers = managed.ToArray();
            plugin.SaveConfiguration();
        }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal);
}

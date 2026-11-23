using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeMusic.Users;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.YouTubeMusic.Api;

/// <summary>Body for saving cookies.</summary>
public sealed class CookiesBody
{
    /// <summary>Gets or sets the cookies.txt content.</summary>
    public string Cookies { get; set; } = string.Empty;
}

/// <summary>
/// Self-service for every Jellyfin user: connect their YouTube Music account (cookies) and
/// import their liked songs into their own private library. Page: <c>/YouTubeMusic/Account</c>.
/// </summary>
[ApiController]
[Route("YouTubeMusic")]
public class AccountController : ControllerBase
{
    private readonly LikesSyncService _sync;
    private readonly UserLibraryService _libraries;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccountController"/> class.
    /// </summary>
    /// <param name="sync">Sync service.</param>
    /// <param name="libraries">User libraries.</param>
    /// <param name="userManager">User manager.</param>
    public AccountController(LikesSyncService sync, UserLibraryService libraries, IUserManager userManager)
    {
        _sync = sync;
        _libraries = libraries;
        _userManager = userManager;
    }

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : null;

    /// <summary>Short address for the self-service page: <c>https://your-server/YouTube</c>.</summary>
    /// <returns>Redirect.</returns>
    [HttpGet("/YouTube")]
    [AllowAnonymous]
    public ActionResult ShortLink() => Redirect($"{Request.PathBase}/YouTubeMusic/Account");

    /// <summary>The self-service page (it authenticates with the Jellyfin Web login in the same browser).</summary>
    /// <returns>HTML.</returns>
    [HttpGet("Account")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult AccountPage()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Configuration.account.html");
        return stream is null ? NotFound() : File(stream, "text/html; charset=utf-8");
    }

    /// <summary>Gets the current user's status.</summary>
    /// <returns>Status.</returns>
    [HttpGet("Me")]
    [Authorize]
    public ActionResult<object> Me()
    {
        if (CurrentUserId is not Guid id || _userManager.GetUserById(id) is not { } user)
        {
            return Unauthorized();
        }

        return Ok(new { user.Username, Status = _sync.GetStatus(id) });
    }

    /// <summary>Saves (validates) the current user's YouTube cookies and starts an import.</summary>
    /// <param name="body">Cookies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Liked song count.</returns>
    [HttpPost("Me/Cookies")]
    [Authorize]
    public async Task<ActionResult<object>> SaveCookies([FromBody] CookiesBody body, CancellationToken cancellationToken)
    {
        if (CurrentUserId is not Guid id)
        {
            return Unauthorized();
        }

        try
        {
            var count = await _sync.SaveCookiesAsync(id, body.Cookies, cancellationToken).ConfigureAwait(false);
            _sync.StartSync(id);
            return Ok(new { LikedOnYouTube = count });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { Error = ex.Message });
        }
    }

    /// <summary>Removes the current user's cookies.</summary>
    /// <returns>No content.</returns>
    [HttpDelete("Me/Cookies")]
    [Authorize]
    public ActionResult DeleteCookies()
    {
        if (CurrentUserId is not Guid id)
        {
            return Unauthorized();
        }

        _sync.DeleteCookies(id);
        return NoContent();
    }

    /// <summary>Starts an import for the current user.</summary>
    /// <returns>Accepted.</returns>
    [HttpPost("Me/Sync")]
    [Authorize]
    public ActionResult Sync()
    {
        if (CurrentUserId is not Guid id)
        {
            return Unauthorized();
        }

        _sync.StartSync(id);
        return Accepted();
    }

    /// <summary>Admin: status of every user.</summary>
    /// <returns>Statuses.</returns>
    [HttpGet("Users")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> AllUsers()
    {
        return Ok(_userManager.GetUsers().Select(u => new
        {
            u.Id,
            u.Username,
            Folder = UserLibraryService.FolderName(u.Username),
            Status = _sync.GetStatus(u.Id),
        }));
    }

    /// <summary>Admin: start an import for a user.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>Accepted.</returns>
    [HttpPost("Users/{userId}/Sync")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult SyncUser([FromRoute] Guid userId)
    {
        _sync.StartSync(userId);
        return Accepted();
    }

    /// <summary>Admin: re-apply library privacy now.</summary>
    /// <returns>No content.</returns>
    [HttpPost("Privacy/Apply")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> ApplyPrivacy()
    {
        await _libraries.ApplyAccessAsync().ConfigureAwait(false);
        return NoContent();
    }
}

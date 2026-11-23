using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeMusic.Sidecar;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.YouTubeMusic.Api;

/// <summary>
/// Admin endpoints used by the configuration page (the browser can't reach the sidecar directly).
/// </summary>
[ApiController]
[Route("YouTubeMusic")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class YouTubeMusicController : ControllerBase
{
    private readonly SidecarClient _sidecar;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeMusicController"/> class.
    /// </summary>
    /// <param name="sidecar">Sidecar client.</param>
    public YouTubeMusicController(SidecarClient sidecar)
    {
        _sidecar = sidecar;
    }

    /// <summary>Runs a search through the sidecar (connection test).</summary>
    /// <param name="q">Query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results.</returns>
    [HttpGet("Search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<SidecarSearchResult>>> Search([FromQuery] string q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest("q is required");
        }

        try
        {
            var limit = Math.Clamp(Plugin.Instance?.Configuration.SearchResultLimit ?? 25, 1, 50);
            return Ok(await _sidecar.SearchAsync(q, limit, cancellationToken).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, $"Sidecar at {SidecarClient.BaseUrl} failed: {ex.Message}");
        }
    }

    /// <summary>Adds a saved search (shows up as a folder in the channel).</summary>
    /// <param name="query">Search text.</param>
    /// <returns>The saved searches.</returns>
    [HttpPost("SavedSearches")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<string[]> AddSavedSearch([FromQuery] string query)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || string.IsNullOrWhiteSpace(query))
        {
            return BadRequest();
        }

        var list = plugin.Configuration.SavedSearches.ToList();
        if (!list.Contains(query.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            list.Add(query.Trim());
            plugin.Configuration.SavedSearches = list.ToArray();
            plugin.SaveConfiguration();
        }

        return Ok(plugin.Configuration.SavedSearches);
    }
}

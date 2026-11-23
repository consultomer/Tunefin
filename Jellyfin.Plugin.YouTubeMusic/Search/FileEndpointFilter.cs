using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeMusic.Channel;
using Jellyfin.Plugin.YouTubeMusic.Sidecar;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeMusic.Search;

/// <summary>
/// Finamp direct-plays by downloading <c>GET /Items/{id}/File</c>. YouTube channel items have no
/// file on disk, so Jellyfin would fail; instead proxy the sidecar's audio stream (with Range support).
/// </summary>
public sealed class FileEndpointFilter : IAsyncActionFilter
{
    private static readonly string[] ForwardedHeaders = { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges" };

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FileEndpointFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileEndpointFilter"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public FileEndpointFilter(ILibraryManager libraryManager, IHttpClientFactory httpClientFactory, ILogger<FileEndpointFilter> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var videoId = GetYouTubeVideoId(context);
        if (videoId is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Short-circuit: never run Jellyfin's GetFile for a channel item.
        await ProxyAsync(context.HttpContext, videoId).ConfigureAwait(false);
        context.Result = new EmptyResult();
    }

    private string? GetYouTubeVideoId(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "Library", ActionName: "GetFile" }
            || !Guid.TryParse(context.RouteData.Values["itemId"]?.ToString(), out var itemId))
        {
            return null;
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || item.ChannelId.Equals(Guid.Empty) || !string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        return YouTubeMusicChannel.TryGetVideoId(item.ExternalId);
    }

    private async Task ProxyAsync(HttpContext http, string videoId)
    {
        var isHead = HttpMethods.IsHead(http.Request.Method);
        using var request = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, SidecarClient.StreamUrl(videoId, Guid.TryParse(http.User.FindFirst("Jellyfin-UserId")?.Value, out var uid) ? uid : null));
        foreach (var (key, value) in SidecarClient.RequiredHeaders())
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (http.Request.Headers.TryGetValue("Range", out var range))
        {
            request.Headers.TryAddWithoutValidation("Range", range.ToString());
        }

        try
        {
            var client = _httpClientFactory.CreateClient(SidecarClient.HttpClientName);
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted)
                .ConfigureAwait(false);

            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in ForwardedHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sidecar refused stream for {VideoId}: HTTP {Status}", videoId, (int)response.StatusCode);
            }

            if (!isHead)
            {
                await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // client seeked / stopped
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Could not reach the sidecar to stream {VideoId}", videoId);
            if (!http.Response.HasStarted)
            {
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }
}

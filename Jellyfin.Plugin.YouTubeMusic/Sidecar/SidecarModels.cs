using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.YouTubeMusic.Sidecar;

/// <summary>A song returned by <c>GET /search</c>.</summary>
public sealed class SidecarSearchResult
{
    /// <summary>Gets or sets the YouTube video id.</summary>
    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist(s), comma separated.</summary>
    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album name.</summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    [JsonPropertyName("durationSeconds")]
    public double? DurationSeconds { get; set; }

    /// <summary>Gets or sets the thumbnail url.</summary>
    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }
}

/// <summary>Response of <c>GET /resolve/:videoId</c>.</summary>
public sealed class SidecarResolveResult
{
    /// <summary>Gets or sets the container extension (m4a, webm...).</summary>
    [JsonPropertyName("ext")]
    public string Ext { get; set; } = string.Empty;

    /// <summary>Gets or sets the audio codec as reported by yt-dlp (mp4a.40.2, opus...).</summary>
    [JsonPropertyName("acodec")]
    public string Acodec { get; set; } = string.Empty;
}

/// <summary>Body of <c>POST /download</c>.</summary>
public sealed class SidecarDownloadRequest
{
    /// <summary>Gets or sets the YouTube video id.</summary>
    [JsonPropertyName("videoId")]
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist.</summary>
    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album.</summary>
    [JsonPropertyName("album")]
    public string? Album { get; set; }

    /// <summary>Gets or sets the user folder (private library). Null = legacy shared layout.</summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }

    /// <summary>Gets or sets the account key (Jellyfin user id, "N") whose YouTube cookies are used.</summary>
    [JsonPropertyName("account")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Account { get; set; }
}

/// <summary>Response of <c>GET /library/:user</c>.</summary>
public sealed class SidecarLibrary
{
    /// <summary>Gets or sets the video ids in the user's library.</summary>
    [JsonPropertyName("videoIds")]
    public string[] VideoIds { get; set; } = System.Array.Empty<string>();
}

/// <summary>Response of <c>POST /download</c>.</summary>
public sealed class SidecarDownloadResult
{
    /// <summary>Gets or sets the status ("downloaded").</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the file path, as seen by the sidecar container.</summary>
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the file was already present.</summary>
    [JsonPropertyName("alreadyExisted")]
    public bool AlreadyExisted { get; set; }
}

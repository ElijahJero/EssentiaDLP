using System.Text.Json;

namespace EssentiaDLP.Models;

public sealed class AppSettings
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int MaxSongs { get; set; } = 100;
    public int MaxSongsPerMinute { get; set; } = 10;
    public string Cron { get; set; } = "";
}

public sealed class AppSettingsUpdate
{
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
    public int? MaxSongs { get; set; }
    public int? MaxSongsPerMinute { get; set; }
    public string? Cron { get; set; }
}

public sealed class FailedItem
{
    public string Id { get; set; } = "";
    public int? TrackId { get; set; }
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string Query { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SingleRunRequest
{
    public string Query { get; set; } = "";
}

public sealed class RecoverUrlRequest
{
    public string Url { get; set; } = "";
}

public sealed class DownloadInfo
{
    public string Path { get; set; } = "";
    public string? Title { get; set; }
    public string? Artist { get; set; }
}

public sealed class AnalyzerResult
{
    public JsonElement Analysis { get; set; }
    public JsonElement Galaxy { get; set; }
}

public sealed class JobStatus
{
    public bool Running { get; set; }
    public string? Mode { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string[] Logs { get; set; } = [];
    public object? LastResult { get; set; }
    public string? Error { get; set; }
}

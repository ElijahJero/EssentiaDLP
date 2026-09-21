using System.Text.Json;
using EssentiaDLP.Models;

namespace EssentiaDLP.Services;

public sealed class AnalysisRunner
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SettingsStore _settings;
    private readonly FailedDownloadStore _failed;
    private readonly GalaxyClient _galaxy;
    private readonly Downloader _downloader;
    private readonly AnalyzerClient _analyzer;
    private readonly JobLog _log;
    private readonly ILogger<AnalysisRunner> _logger;

    public AnalysisRunner(
        SettingsStore settings,
        FailedDownloadStore failed,
        GalaxyClient galaxy,
        Downloader downloader,
        AnalyzerClient analyzer,
        JobLog log,
        ILogger<AnalysisRunner> logger)
    {
        _settings = settings;
        _failed = failed;
        _galaxy = galaxy;
        _downloader = downloader;
        _analyzer = analyzer;
        _log = log;
        _logger = logger;
    }

    public bool IsRunning => _log.Snapshot().Running;

    public bool TryStartGalaxy()
    {
        if (!_lock.Wait(0))
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunGalaxyAsync(CancellationToken.None);
            }
            finally
            {
                _lock.Release();
            }
        });
        return true;
    }

    public bool TryStartSingle(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;
        if (!_lock.Wait(0))
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunSingleAsync(query.Trim(), CancellationToken.None);
            }
            finally
            {
                _lock.Release();
            }
        });
        return true;
    }

    public bool TryRecoverUrl(string id, string url)
    {
        if (!_lock.Wait(0))
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverAsync(id, url: url, uploadPath: null, CancellationToken.None);
            }
            finally
            {
                _lock.Release();
            }
        });
        return true;
    }

    public bool TryRecoverUpload(string id, string sourcePath)
    {
        if (!_lock.Wait(0))
            return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverAsync(id, url: null, uploadPath: sourcePath, CancellationToken.None);
            }
            finally
            {
                _lock.Release();
            }
        });
        return true;
    }

    private async Task RunGalaxyAsync(CancellationToken ct)
    {
        _log.Begin("galaxy");
        var settings = _settings.Get();
        var uploaded = 0;
        var attempted = 0;
        string? error = null;
        try
        {
            var take = settings.MaxSongs;
            var seen = new HashSet<int>();
            var started = new Queue<DateTime>();
            const int batch = 25;
            while (true)
            {
                var remaining = take <= 0 ? (int?)null : take - attempted;
                if (remaining is 0)
                {
                    _log.Line($"Reached max of {take} songs.");
                    break;
                }
                var fetchN = remaining is null ? batch : Math.Min(batch, remaining.Value);
                List<JsonElement> pending;
                try
                {
                    pending = await _galaxy.PendingAsync(settings.Url, settings.ApiKey, fetchN, ct);
                }
                catch (GalaxyException ex)
                {
                    _log.Line($"Galaxy error: {ex.Message}");
                    error = ex.Message;
                    break;
                }
                var items = pending.Where(p => p.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) && seen.Add(id)).ToList();
                if (items.Count == 0)
                {
                    _log.Line(attempted == 0
                        ? "No tracks waiting for an Essentia profile."
                        : "No more tracks waiting for an Essentia profile.");
                    break;
                }
                foreach (var item in items)
                {
                    if (take > 0 && attempted >= take)
                        break;
                    var trackId = item.GetProperty("id").GetInt32();
                    var artist = Str(item, "artist") ?? "";
                    var title = Str(item, "title") ?? "";
                    var query = string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)
                        ? $"{artist} {title}".Trim()
                        : $"{artist} - {title}";
                    WaitForRateLimit(started, settings.MaxSongsPerMinute);
                    _log.Line($"— {artist} — {title} (#{trackId})");
                    attempted++;
                    try
                    {
                        var result = await DownloadAnalyzeAsync(query, artist, title, ct);
                        await _galaxy.PutProfileAsync(settings.Url, settings.ApiKey, trackId, result.Galaxy, ct);
                        uploaded++;
                        _log.Line($"Uploaded profile for #{trackId} ({uploaded} uploaded, {attempted} tried)");
                        LogProfile(result.Analysis);
                    }
                    catch (GalaxyException ex)
                    {
                        _log.Line($"Galaxy error: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        _log.Line($"Error: {ex.Message}");
                        _failed.Upsert(new FailedItem
                        {
                            Id = trackId.ToString(),
                            TrackId = trackId,
                            Artist = NullIfEmpty(artist),
                            Title = NullIfEmpty(title),
                            Query = query,
                            Error = ex.Message,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Line($"Error: {ex.Message}");
            _logger.LogError(ex, "Galaxy run failed");
        }
        finally
        {
            _log.Complete(new { uploaded, attempted }, error);
        }
    }

    private async Task RunSingleAsync(string query, CancellationToken ct)
    {
        _log.Begin("single");
        string? error = null;
        object? result = null;
        var (artist, title) = Downloader.ArtistTitleFromQuery(query);
        try
        {
            var analysis = await DownloadAnalyzeAsync(query, artist, title, ct);
            result = JsonSerializer.Deserialize<object>(analysis.Analysis.GetRawText());
            _log.Line("Analysis complete.");
            LogProfile(analysis.Analysis);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Line($"Error: {ex.Message}");
            _failed.Upsert(new FailedItem
            {
                Id = Guid.NewGuid().ToString("n")[..12],
                Artist = artist,
                Title = title,
                Query = query,
                Error = ex.Message,
            });
        }
        finally
        {
            _log.Complete(result, error);
        }
    }

    private async Task RecoverAsync(string id, string? url, string? uploadPath, CancellationToken ct)
    {
        _log.Begin("recover");
        string? error = null;
        object? resultObj = null;
        var item = _failed.Get(id);
        if (item is null)
        {
            _log.Line($"Unknown failed item {id}.");
            _log.Complete(null, "not found");
            return;
        }
        try
        {
            var work = NewWorkDir();
            DownloadInfo info;
            try
            {
                if (!string.IsNullOrWhiteSpace(uploadPath))
                {
                    info = await _downloader.ImportAsync(uploadPath, work, item.Title, item.Artist, _log.Line, ct);
                }
                else
                {
                    var target = url?.Trim() ?? "";
                    if (!Downloader.LooksLikeUrl(target))
                        throw new InvalidOperationException("Paste a direct audio/video URL (YouTube, SoundCloud, Bandcamp, Spotify).");
                    info = await _downloader.DownloadAsync(target, work, _log.Line, ct);
                }
                _log.Line("Analyzing with Essentia...");
                var analysis = await _analyzer.AnalyzeAsync(info.Path, item.Title ?? info.Title, item.Artist ?? info.Artist, ct);
                if (item.TrackId is int trackId)
                {
                    var settings = _settings.Get();
                    await _galaxy.PutProfileAsync(settings.Url, settings.ApiKey, trackId, analysis.Galaxy, ct);
                    _log.Line($"Uploaded profile for #{trackId}.");
                }
                resultObj = JsonSerializer.Deserialize<object>(analysis.Analysis.GetRawText());
                LogProfile(analysis.Analysis);
                _failed.Remove(id);
                _log.Line("Removed from failed list.");
            }
            finally
            {
                DeleteTree(work);
                if (uploadPath is not null)
                    TryDelete(uploadPath);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Line($"Error: {ex.Message}");
            item.Error = ex.Message;
            _failed.Upsert(item);
        }
        finally
        {
            _log.Complete(resultObj, error);
        }
    }

    private async Task<AnalyzerResult> DownloadAnalyzeAsync(string query, string? artist, string? title, CancellationToken ct)
    {
        var work = NewWorkDir();
        try
        {
            var info = await _downloader.DownloadAsync(query, work, _log.Line, ct);
            _log.Line("Analyzing with Essentia...");
            return await _analyzer.AnalyzeAsync(info.Path, title ?? info.Title, artist ?? info.Artist, ct);
        }
        finally
        {
            DeleteTree(work);
        }
    }

    private void WaitForRateLimit(Queue<DateTime> started, int maxPerMinute)
    {
        if (maxPerMinute <= 0)
            return;
        var window = TimeSpan.FromMinutes(1);
        var now = DateTime.UtcNow;
        while (started.Count > 0 && now - started.Peek() >= window)
            started.Dequeue();
        if (started.Count >= maxPerMinute)
        {
            var sleepFor = window - (now - started.Peek());
            if (sleepFor > TimeSpan.Zero)
            {
                _log.Line($"Rate limit {maxPerMinute}/min — waiting {sleepFor.TotalSeconds:0}s");
                Thread.Sleep(sleepFor);
            }
            now = DateTime.UtcNow;
            while (started.Count > 0 && now - started.Peek() >= window)
                started.Dequeue();
        }
        started.Enqueue(DateTime.UtcNow);
    }

    private void LogProfile(JsonElement analysis)
    {
        if (analysis.ValueKind == JsonValueKind.Object && analysis.TryGetProperty("profile", out var profile))
            _log.Line(profile.GetString() ?? "");
    }

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "essentia", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

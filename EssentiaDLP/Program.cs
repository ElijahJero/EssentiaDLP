using EssentiaDLP.Models;
using EssentiaDLP.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 120 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 120 * 1024 * 1024;
});

builder.Services.AddHttpClient(nameof(GalaxyClient), c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<JsonFileStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<FailedDownloadStore>();
builder.Services.AddSingleton<JobLog>();
builder.Services.AddSingleton<Downloader>();
builder.Services.AddSingleton<AnalyzerClient>();
builder.Services.AddSingleton<GalaxyClient>();
builder.Services.AddSingleton<AnalysisRunner>();
builder.Services.AddHostedService<CronScheduler>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (JobLog log) => Results.Json(log.Snapshot()));

app.MapGet("/api/settings", (SettingsStore store) => Results.Json(store.Get()));

app.MapPost("/api/settings", (AppSettingsUpdate body, SettingsStore store) =>
{
    var cronError = CronScheduler.Validate(body.Cron);
    if (cronError is not null)
        return Results.BadRequest(new { error = "Invalid cron: " + cronError });
    return Results.Json(store.Update(body));
});

app.MapPost("/api/run/galaxy", (AnalysisRunner runner) =>
    runner.TryStartGalaxy()
        ? Results.Accepted()
        : Results.Json(new { error = "Analysis is already running." }, statusCode: 409));

app.MapPost("/api/run/single", (SingleRunRequest body, AnalysisRunner runner) =>
{
    if (string.IsNullOrWhiteSpace(body.Query))
        return Results.BadRequest(new { error = "Query or URL is required." });
    return runner.TryStartSingle(body.Query)
        ? Results.Accepted()
        : Results.Json(new { error = "Analysis is already running." }, statusCode: 409);
});

app.MapGet("/api/failed", (FailedDownloadStore store) => Results.Json(store.List()));

app.MapDelete("/api/failed/{id}", (string id, FailedDownloadStore store) =>
    store.Remove(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/failed/{id}/url", (string id, RecoverUrlRequest body, AnalysisRunner runner, FailedDownloadStore store) =>
{
    if (store.Get(id) is null)
        return Results.NotFound();
    if (string.IsNullOrWhiteSpace(body.Url))
        return Results.BadRequest(new { error = "URL is required." });
    return runner.TryRecoverUrl(id, body.Url)
        ? Results.Accepted()
        : Results.Json(new { error = "Analysis is already running." }, statusCode: 409);
});

app.MapPost("/api/failed/{id}/upload", async (string id, HttpRequest request, AnalysisRunner runner, FailedDownloadStore store) =>
{
    if (store.Get(id) is null)
        return Results.NotFound();
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart form upload." });
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "Choose an audio file." });
    var ext = Path.GetExtension(file.FileName);
    if (string.IsNullOrEmpty(ext))
        ext = ".mp3";
    var dest = Path.Combine(Path.GetTempPath(), "essentia-upload-" + Guid.NewGuid().ToString("n") + ext);
    await using (var fs = File.Create(dest))
        await file.CopyToAsync(fs);
    if (runner.TryRecoverUpload(id, dest))
        return Results.Accepted();
    try { File.Delete(dest); } catch { }
    return Results.Json(new { error = "Analysis is already running." }, statusCode: 409);
}).DisableAntiforgery();

app.Run();

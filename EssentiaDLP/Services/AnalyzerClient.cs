using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EssentiaDLP.Models;

namespace EssentiaDLP.Services;

public sealed class AnalyzerClient : IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<AnalyzerClient> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _disposed;

    public AnalyzerClient(IConfiguration config, ILogger<AnalyzerClient> log)
    {
        _config = config;
        _log = log;
    }

    public async Task<AnalyzerResult> AnalyzeAsync(string path, string? title, string? artist, CancellationToken ct)
    {
        Exception? last = null;
        for (var i = 0; i < 2; i++)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return await CallAsync(path, title, artist, ct);
            }
            catch (AnalyzerCrashedException ex)
            {
                last = ex;
                CloseProcess();
            }
            finally
            {
                _gate.Release();
            }
        }
        throw new InvalidOperationException($"Essentia analyzer crashed ({last?.Message}). Skip or retry this track.");
    }

    private async Task<AnalyzerResult> CallAsync(string path, string? title, string? artist, CancellationToken ct)
    {
        EnsureProcess();
        var payload = JsonSerializer.Serialize(new { path, title, artist });
        await _stdin!.WriteLineAsync(payload.AsMemory(), ct);
        await _stdin.FlushAsync(ct);
        string? line;
        while (true)
        {
            line = await _stdout!.ReadLineAsync(ct);
            if (line is null)
            {
                var code = _proc?.HasExited == true ? _proc.ExitCode : (int?)null;
                throw new AnalyzerCrashedException($"exit {code}");
            }
            line = line.Trim();
            if (line.StartsWith('{'))
                break;
        }
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            return new AnalyzerResult
            {
                Analysis = root.GetProperty("result").Clone(),
                Galaxy = root.TryGetProperty("galaxy", out var g) ? g.Clone() : default,
            };
        }
        var error = root.TryGetProperty("error", out var err) ? err.GetString() : "unknown analyzer error";
        throw new InvalidOperationException(error);
    }

    private void EnsureProcess()
    {
        if (_proc is { HasExited: false })
            return;
        CloseProcess();
        var python = _config["Python"];
        if (string.IsNullOrWhiteSpace(python))
            python = OperatingSystem.IsWindows() ? "python" : "python3";
        var script = Path.GetFullPath(_config["AnalyzerScript"] ?? "analyzer/tag_library.py");
        if (!File.Exists(script))
            throw new FileNotFoundException($"Analyzer script not found: {script}");
        var models = Path.GetFullPath(_config["ModelsDirectory"] ?? "analyzer/models");
        var psi = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--worker");
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["ESSENTIA_MODELS_DIR"] = models;
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Python analyzer.");
        _stdin = _proc.StandardInput;
        _stdout = _proc.StandardOutput;
        _ = DrainStderr(_proc);
        _log.LogInformation("Started Essentia worker {Pid}", _proc.Id);
    }

    private async Task DrainStderr(Process proc)
    {
        try
        {
            while (await proc.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _log.LogWarning("analyzer: {Line}", line);
            }
        }
        catch { }
    }

    private void CloseProcess()
    {
        try { _stdin?.Close(); } catch { }
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        _stdin = null;
        _stdout = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseProcess();
        _gate.Dispose();
    }
}

public sealed class AnalyzerCrashedException : Exception
{
    public AnalyzerCrashedException(string message) : base(message) { }
}

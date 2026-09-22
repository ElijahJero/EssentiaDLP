using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using EssentiaDLP.Models;

namespace EssentiaDLP.Services;

public sealed class DownloadFailedException : Exception
{
    public DownloadFailedException(string message) : base(message) { }
}

public sealed class Downloader
{
    public const int MaxAudioSeconds = 30 * 60;
    private const int SearchResults = 10;
    private const string Format = "bestaudio[abr<=160]/bestaudio/bestaudio*/best";
    private const string FormatFallback = "bestaudio/best";
    private const string MatchFilter = "!is_live & !is_upcoming";
    private const string OutputTemplate = "%(title)s.%(ext)s";

    private static readonly string[] AudioGlobs = ["*.mp3", "*.m4a", "*.opus", "*.ogg", "*.webm", "*.wav", "*.flac", "*.aac"];
    private static readonly Regex LiveLine = new(@"(?i)(downloading (a )?live(stream)?|livestream from start|\bis a live stream\b)", RegexOptions.Compiled);
    private static readonly Regex Decoration = new(@"[✦★☆■□▪◆◇▶►※＊]+", RegexOptions.Compiled);
    private static readonly Regex Bracket = new(@"[\[【\(（][^\[【\(（\]】\)）]*[\]】\)）]", RegexOptions.Compiled);
    private static readonly Regex SoundCloudUrl = new(@"https?://(?:www\.)?soundcloud\.com/[^\s\]\)>'""\,]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Latin = new(@"[^\x00-\x7F]+", RegexOptions.Compiled);
    private static readonly Regex NonLatin = new(@"[\x00-\x7F]+", RegexOptions.Compiled);
    private static readonly Regex UrlRe = new(
        @"(?i)^(https?://|www\.)|(youtube\.com|youtu\.be|music\.youtube\.com|open\.spotify\.com|spotify\.com|soundcloud\.com|bandcamp\.com)/",
        RegexOptions.Compiled);
    private static readonly Regex SpotifyRe = new(@"(?i)(open\.)?spotify\.com|spotify:", RegexOptions.Compiled);
    private static readonly Regex UnsafeName = new(@"[<>:""/\\|?*]", RegexOptions.Compiled);

    public static bool LooksLikeUrl(string text) => UrlRe.IsMatch(text.Trim());

    public async Task<DownloadInfo> DownloadAsync(string query, string outDir, Action<string> onLine, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        query = query.Trim();
        var errors = new List<string>();
        var scUrls = new List<string>();

        void Capture(string line)
        {
            onLine(line);
            foreach (var url in CollectSoundCloud(line))
            {
                if (!scUrls.Contains(url))
                    scUrls.Add(url);
            }
        }

        if (SpotifyRe.IsMatch(query))
        {
            onLine("Spotify URL detected — using spotDL.");
            try
            {
                return await DownloadWithSpotdlAsync(query, outDir, Capture, ct);
            }
            catch (Exception ex)
            {
                errors.Add($"spotDL failed ({ex.Message})");
                onLine($"spotDL failed: {ex.Message}");
            }
            foreach (var url in scUrls)
            {
                try
                {
                    onLine($"Retrying SoundCloud URL with yt-dlp: {url}");
                    return await DownloadWithYtdlpAsync(url, outDir, onLine, ct, format: FormatFallback);
                }
                catch (Exception retry)
                {
                    onLine($"SoundCloud retry failed: {retry.Message}");
                    errors.Add($"yt-dlp {url} failed ({retry.Message})");
                }
            }
            throw new DownloadFailedException(string.Join("; ", errors));
        }

        try
        {
            return await DownloadWithYtdlpAsync(query, outDir, onLine, ct);
        }
        catch (Exception ex)
        {
            onLine($"yt-dlp failed: {ex.Message}");
            errors.Add($"yt-dlp failed ({ex.Message})");
        }

        if (!LooksLikeUrl(query))
        {
            var seen = new HashSet<string> { ResolveTarget(query) };
            foreach (var (label, target) in SearchTargets(query))
            {
                if (!seen.Add(target))
                    continue;
                try
                {
                    onLine($"Retrying yt-dlp ({label})...");
                    var fmt = target.StartsWith("scsearch", StringComparison.Ordinal) ? FormatFallback : Format;
                    return await DownloadWithYtdlpAsync(query, outDir, onLine, ct, target, fmt);
                }
                catch (Exception retry)
                {
                    onLine($"yt-dlp ({label}) failed: {retry.Message}");
                    errors.Add($"yt-dlp ({label}) failed ({retry.Message})");
                }
            }
        }

        var spotQueries = new List<string> { query };
        if (!LooksLikeUrl(query))
            spotQueries.AddRange(SearchQueryVariants(query).Skip(1).Take(2));
        foreach (var search in spotQueries)
        {
            try
            {
                return await DownloadWithSpotdlAsync(search, outDir, Capture, ct);
            }
            catch (Exception fallback)
            {
                onLine($"spotDL failed ({search}): {fallback.Message}");
                errors.Add($"spotDL failed ({fallback.Message})");
                foreach (var url in CollectSoundCloud(fallback.Message))
                {
                    if (!scUrls.Contains(url))
                        scUrls.Add(url);
                }
            }
        }

        foreach (var url in scUrls)
        {
            try
            {
                onLine($"Retrying SoundCloud URL with yt-dlp: {url}");
                return await DownloadWithYtdlpAsync(url, outDir, onLine, ct, format: FormatFallback);
            }
            catch (Exception retry)
            {
                onLine($"SoundCloud retry failed: {retry.Message}");
                errors.Add($"yt-dlp {url} failed ({retry.Message})");
            }
        }

        throw new DownloadFailedException(errors.Count > 0 ? string.Join("; ", errors) : "Download failed");
    }

    public async Task<DownloadInfo> ImportAsync(string srcPath, string outDir, string? title, string? artist, Action<string> onLine, CancellationToken ct)
    {
        if (!File.Exists(srcPath))
            throw new FileNotFoundException($"File not found: {srcPath}");
        Directory.CreateDirectory(outDir);
        var stem = string.Join(" - ", new[] { artist, title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(stem))
            stem = Path.GetFileNameWithoutExtension(srcPath);
        var dest = Path.Combine(outDir, SafeFilename(stem) + ".mp3");
        if (string.Equals(Path.GetExtension(srcPath), ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(srcPath, dest, overwrite: true);
                onLine($"Copied: {Path.GetFileName(dest)}");
            }
            else
            {
                dest = srcPath;
                onLine($"Using: {Path.GetFileName(dest)}");
            }
        }
        else
        {
            await FfmpegToMp3Async(srcPath, dest, onLine, ct);
        }
        dest = await CapDurationAsync(dest, onLine, ct);
        return MetadataFromPath(dest, title, artist);
    }

    public static (string? Artist, string? Title) ArtistTitleFromQuery(string query)
    {
        query = query.Trim();
        var idx = query.IndexOf(" - ", StringComparison.Ordinal);
        if (idx >= 0)
            return (NullIfEmpty(query[..idx].Trim()), NullIfEmpty(query[(idx + 3)..].Trim()));
        return (null, NullIfEmpty(query));
    }

    private async Task<DownloadInfo> DownloadWithYtdlpAsync(
        string query,
        string outDir,
        Action<string> onLine,
        CancellationToken ct,
        string? target = null,
        string? format = null,
        bool downloadSections = true)
    {
        CheckDownloadDeps();
        target ??= ResolveTarget(query);
        var fmt = format ?? Format;
        var ffmpeg = FindFfmpeg();
        var before = ListAudio(outDir);
        var searching = target.StartsWith("ytsearch", StringComparison.Ordinal) || target.StartsWith("scsearch", StringComparison.Ordinal);

        var cmd = new List<string>
        {
            "--ignore-config", "--no-wait-for-video", "--ignore-errors",
            "--match-filter", MatchFilter,
            "--windows-filenames", "--restrict-filenames",
            "--ffmpeg-location", ffmpeg,
        };
        if (downloadSections)
            cmd.AddRange(["--download-sections", $"*0-{MaxAudioSeconds}"]);
        cmd.AddRange(JsRuntimeArgs());
        cmd.AddRange([
            "--extractor-args", "youtube:player_client=web_embedded,web,default,-android_vr",
            "-f", fmt, "-x", "--audio-format", "mp3", "--audio-quality", "160K",
            "--postprocessor-args", $"ffmpeg:-t {MaxAudioSeconds}",
            "-o", Path.Combine(outDir, OutputTemplate),
            "--embed-metadata", "--newline",
            "--print", "video:TITLE:%(title)s",
            "--print", "video:ARTIST:%(artist,uploader)s",
            "--print", "after_move:SAVED:%(filepath)s",
            target,
        ]);

        if (searching)
        {
            var insert = cmd.IndexOf("--ignore-errors") + 1;
            cmd.InsertRange(insert, ["--no-abort-on-error", "--playlist-end", SearchResults.ToString()]);
        }
        else
        {
            cmd.Insert(cmd.IndexOf("--ignore-config") + 1, "--no-playlist");
            var insert = cmd.IndexOf("--match-filter") + 2;
            cmd.InsertRange(insert, ["--max-downloads", "1"]);
        }

        onLine($"Running yt-dlp ({(searching ? "search" : "url")})...");
        var (code, lines) = await RunLoggedAsync(FindYtDlp(), cmd, onLine, ct, killWhen: searching ? "SAVED:" : null);

        string? title = null, artist = null, saved = null;
        var liveRejected = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("TITLE:", StringComparison.Ordinal))
                title = NullIfEmpty(line[6..].Trim()) ?? title;
            else if (line.StartsWith("ARTIST:", StringComparison.Ordinal))
            {
                artist = NullIfEmpty(line[7..].Trim());
                if (artist is "NA")
                    artist = null;
            }
            else if (line.StartsWith("SAVED:", StringComparison.Ordinal))
                saved = line[6..].Trim();
            else if (LiveLine.IsMatch(line) && !line.Contains("does not pass filter", StringComparison.OrdinalIgnoreCase))
                liveRejected = true;
            else if (line.Contains("does not pass filter", StringComparison.OrdinalIgnoreCase))
                liveRejected = true;
        }

        if (saved is null || !File.Exists(saved))
            saved = NewestAudio(outDir, before);
        if (saved is not null && File.Exists(saved))
        {
            saved = await EnsureMp3Async(saved, onLine, ct);
            return MetadataFromPath(saved, title, artist);
        }
        if (downloadSections && lines.Any(l => l.Contains("ffmpeg exited with code", StringComparison.OrdinalIgnoreCase)))
        {
            onLine("ffmpeg could not trim the download — retrying without a section cut.");
            return await DownloadWithYtdlpAsync(query, outDir, onLine, ct, target, format, downloadSections: false);
        }
        if (liveRejected && !searching)
            throw new InvalidOperationException("yt-dlp skipped a livestream");
        if (code is not 0 and not 101 && saved is null)
            throw new InvalidOperationException($"yt-dlp exited with code {code}");
        throw new FileNotFoundException("Download finished but no mp3 file was found.");
    }

    private async Task<DownloadInfo> DownloadWithSpotdlAsync(string query, string outDir, Action<string> onLine, CancellationToken ct)
    {
        var ffmpeg = FindFfmpeg();
        var before = Directory.Exists(outDir)
            ? Directory.GetFiles(outDir, "*.mp3").Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = Path.Combine(outDir, "{artists} - {title}.{output-ext}");
        onLine($"Searching with spotDL: {query}");
        var args = new List<string>
        {
            "download", query.Trim(),
            "--ffmpeg", ffmpeg,
            "--format", "mp3",
            "--bitrate", "160k",
            "--output", output,
            "--threads", "1",
            "--overwrite", "force",
            "--lyrics", "genius",
            "--log-level", "INFO",
            "--print-errors",
        };
        // spotdl requires a lyrics provider flag form; empty providers via --lyrics none if supported
        args.Remove("--lyrics");
        args.Remove("genius");
        var (code, _) = await RunLoggedAsync(FindSpotdl(), args, onLine, ct);
        var saved = NewestAudio(outDir, before);
        if (saved is null)
            throw new FileNotFoundException(code != 0
                ? $"spotDL exited with code {code}"
                : "spotDL finished but no mp3 file was found.");
        saved = await EnsureMp3Async(saved, onLine, ct);
        onLine($"Saved: {Path.GetFileName(saved)}");
        var (artist, title) = ArtistTitleFromQuery(Path.GetFileNameWithoutExtension(saved));
        return MetadataFromPath(saved, title, artist);
    }

    private static async Task<string> EnsureMp3Async(string path, Action<string> onLine, CancellationToken ct)
    {
        if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var dest = Path.ChangeExtension(path, ".mp3")!;
            await FfmpegToMp3Async(path, dest, onLine, ct);
            try
            {
                if (File.Exists(dest) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
            }
            catch (IOException) { }
            path = dest;
        }
        return await CapDurationAsync(path, onLine, ct);
    }

    private static async Task<string> CapDurationAsync(string path, Action<string> onLine, CancellationToken ct)
    {
        var duration = await ProbeDurationAsync(path, ct);
        if (duration is null || duration <= MaxAudioSeconds + 0.5)
            return path;
        onLine($"Source is {duration / 60:0} min — truncating to 30:00 before analysis…");
        var tmp = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".truncated" + (Path.GetExtension(path) is { Length: > 0 } e ? e : ".mp3"));
        var copy = await RunAsync(FindFfmpeg(), ["-y", "-i", path, "-t", MaxAudioSeconds.ToString(), "-vn", "-c", "copy", tmp], ct);
        if (copy != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
        {
            TryDelete(tmp);
            tmp = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".truncated.mp3");
            await FfmpegToMp3Async(path, tmp, onLine, ct);
        }
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
            File.Copy(tmp, path, overwrite: true);
            TryDelete(tmp);
        }
        return path;
    }

    private static async Task FfmpegToMp3Async(string src, string dest, Action<string> onLine, CancellationToken ct)
    {
        onLine($"Converting {Path.GetFileName(src)} to mp3…");
        var code = await RunAsync(FindFfmpeg(), [
            "-y", "-i", src, "-t", MaxAudioSeconds.ToString(), "-vn",
            "-codec:a", "libmp3lame", "-b:a", "160k", dest
        ], ct);
        if (code != 0 || !File.Exists(dest))
            throw new InvalidOperationException($"Could not convert {Path.GetFileName(src)} (ffmpeg {code})");
        onLine($"Saved: {Path.GetFileName(dest)}");
    }

    private static async Task<double?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        try
        {
            var ffprobe = Which("ffprobe") ?? Which("ffprobe.exe");
            if (ffprobe is null)
                return null;
            var (code, stdout) = await RunCaptureAsync(ffprobe, [
                "-v", "error", "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1", path
            ], ct);
            if (code == 0 && double.TryParse(stdout.Trim(), out var secs) && secs > 0)
                return secs;
        }
        catch { }
        return null;
    }

    private static string ResolveTarget(string query)
    {
        query = query.Trim();
        if (LooksLikeUrl(query))
            return query.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + query : query;
        return $"ytsearch{SearchResults}:{query}";
    }

    private static List<string> SearchQueryVariants(string query, int limit = 6)
    {
        var variants = new List<string>();
        void Add(string text)
        {
            var cleaned = Regex.Replace(text, @"\s+", " ").Trim(' ', '-');
            if (cleaned.Length > 0 && variants.All(v => !v.Equals(cleaned, StringComparison.OrdinalIgnoreCase)))
                variants.Add(cleaned);
        }
        query = query.Trim();
        Add(query);
        Add(Decoration.Replace(query, " "));
        Add(Bracket.Replace(Decoration.Replace(query, " "), " "));
        var split = query.IndexOf(" - ", StringComparison.Ordinal);
        if (split >= 0)
        {
            var artist = query[..split];
            var title = query[(split + 3)..];
            var latinArtist = Latin.Replace(artist, " ").Trim();
            var cjkArtist = NonLatin.Replace(artist, " ").Trim();
            var plainTitle = Bracket.Replace(Decoration.Replace(title, " "), " ");
            var lead = Regex.Match(plainTitle.Trim(), @"([\w.]+(?:\s+[\w.]+){0,4})");
            if (latinArtist.Length > 0)
            {
                Add($"{latinArtist} - {plainTitle}");
                if (lead.Success) Add($"{latinArtist} - {lead.Groups[1].Value}");
            }
            if (cjkArtist.Length > 0)
            {
                Add($"{cjkArtist} - {plainTitle}");
                if (lead.Success) Add($"{cjkArtist} - {lead.Groups[1].Value}");
            }
            Add($"{artist} - {plainTitle}");
            if (lead.Success) Add($"{artist} - {lead.Groups[1].Value}");
        }
        return variants.Take(limit).ToList();
    }

    private static List<(string Label, string Target)> SearchTargets(string query)
    {
        var targets = new List<(string, string)>();
        var seen = new HashSet<string>();
        void Add(string label, string target)
        {
            if (seen.Add(target))
                targets.Add((label, target));
        }
        var variants = SearchQueryVariants(query, 4);
        foreach (var variant in variants.Skip(1).Take(2))
            Add($"YouTube: {variant}", $"ytsearch{SearchResults}:{variant}");
        foreach (var variant in variants.Take(2))
            Add($"SoundCloud: {variant}", $"scsearch5:{variant}");
        return targets;
    }

    private static List<string> CollectSoundCloud(params string?[] texts)
    {
        var found = new List<string>();
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match match in SoundCloudUrl.Matches(text))
            {
                var url = match.Value.TrimEnd('.', ',', ';', ':', ')');
                if (!found.Contains(url))
                    found.Add(url);
            }
        }
        return found;
    }

    private static DownloadInfo MetadataFromPath(string path, string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var idx = stem.IndexOf(" - ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                artist ??= NullIfEmpty(stem[..idx].Trim());
                title ??= NullIfEmpty(stem[(idx + 3)..].Trim());
            }
        }
        return new DownloadInfo
        {
            Path = path,
            Title = title ?? Path.GetFileNameWithoutExtension(path),
            Artist = artist,
        };
    }

    private static HashSet<string> ListAudio(string outDir)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(outDir))
            return found;
        foreach (var pattern in AudioGlobs)
        {
            foreach (var file in Directory.GetFiles(outDir, pattern))
                found.Add(Path.GetFullPath(file));
        }
        return found;
    }

    private static string? NewestAudio(string outDir, HashSet<string> before)
    {
        var created = ListAudio(outDir);
        created.ExceptWith(before);
        if (created.Count == 0)
            return null;
        return created.MaxBy(p => new FileInfo(p).LastWriteTimeUtc);
    }

    private static void CheckDownloadDeps()
    {
        FindFfmpeg();
        if (JsRuntimeArgs().Count == 0)
            throw new FileNotFoundException("No JavaScript runtime found. Install Deno 2.3+ (or Node 22+) for YouTube downloads.");
    }

    private static List<string> FindYtDlp()
    {
        var yt = Which("yt-dlp") ?? Which("yt-dlp.exe");
        if (yt is not null)
            return [yt];
        var py = FindPython();
        return [py, "-m", "yt_dlp"];
    }

    private static List<string> FindSpotdl()
    {
        var spot = Which("spotdl") ?? Which("spotdl.exe");
        if (spot is not null)
            return [spot];
        var py = FindPython();
        return [py, "-m", "spotdl"];
    }

    private static string FindFfmpeg() =>
        Which("ffmpeg") ?? Which("ffmpeg.exe")
        ?? throw new FileNotFoundException("ffmpeg not found. Install ffmpeg and add it to PATH.");

    private static List<string>? _jsRuntimeArgs;

    private static List<string> JsRuntimeArgs()
    {
        if (_jsRuntimeArgs is not null)
            return _jsRuntimeArgs;

        var deno = Which("deno") ?? Which("deno.exe");
        if (deno is not null)
            return _jsRuntimeArgs = JsRuntime("deno", deno);

        var node = Which("node") ?? Which("node.exe") ?? Which("nodejs");
        if (node is not null && RuntimeMajor(node) >= 22)
            return _jsRuntimeArgs = JsRuntime("node", node);

        var quickjs = Which("qjs") ?? Which("qjs.exe");
        if (quickjs is not null)
            return _jsRuntimeArgs = JsRuntime("quickjs", quickjs);

        return _jsRuntimeArgs = [];
    }

    private static List<string> JsRuntime(string runtime, string path) =>
        ["--js-runtimes", $"{runtime}:{path}", "--remote-components", "ejs:github"];

    private static int RuntimeMajor(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var proc = Process.Start(psi);
            if (proc is null)
                return 0;
            var text = proc.StandardOutput.ReadToEnd().Trim().TrimStart('v', 'V');
            proc.WaitForExit(5000);
            var dot = text.IndexOf('.');
            var major = dot > 0 ? text[..dot] : text;
            return int.TryParse(major, out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FindPython() =>
        Which("python3") ?? Which("python") ?? Which("python.exe")
        ?? throw new FileNotFoundException("Python was not found.");

    private static string? Which(string name)
    {
        if (File.Exists(name))
            return Path.GetFullPath(name);
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string SafeFilename(string name)
    {
        var cleaned = UnsafeName.Replace(name, "_").Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "audio" : cleaned;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static async Task<(int Code, List<string> Lines)> RunLoggedAsync(
        List<string> exeAndArgs, List<string> args, Action<string> onLine, CancellationToken ct, string? killWhen = null)
    {
        var file = exeAndArgs[0];
        var allArgs = exeAndArgs.Skip(1).Concat(args).ToList();
        var lines = new List<string>();
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in allArgs)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var collector = new StringBuilder();
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lines.Add(line);
                onLine(line);
                if (killWhen is not null && line.StartsWith(killWhen, StringComparison.Ordinal))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return;
                }
            }
        }
        await Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError));
        await proc.WaitForExitAsync(ct);
        _ = collector;
        return (proc.ExitCode, lines);
    }

    private static async Task<int> RunAsync(string file, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private static async Task<(int Code, string Stdout)> RunCaptureAsync(string file, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stdout);
    }
}

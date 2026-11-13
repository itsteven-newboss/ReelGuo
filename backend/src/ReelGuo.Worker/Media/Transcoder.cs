using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;

namespace ReelGuo.Worker.Media;

public sealed class FfmpegOptions
{
    public const string Section = "Ffmpeg";

    /// <summary>Folder containing ffmpeg and ffprobe. Relative paths resolve from the content root; empty uses PATH.</summary>
    public string BinDirectory { get; set; } = "";

    /// <summary>Scratch space for downloads and encodes. Empty uses the system temp folder.</summary>
    public string WorkDirectory { get; set; } = "";
}

public sealed record TranscodeResult(
    string HlsPrefix, int Version, double DurationSeconds, int Width, int Height, string PosterKey, string RenditionsJson);

/// <summary>Turns one source file into versioned HLS plus a poster, and uploads both to the media bucket.</summary>
public sealed class Transcoder(StorageBuckets storage, FfmpegOptions options, ILogger<Transcoder> logger)
{
    private string Tool(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        return string.IsNullOrWhiteSpace(options.BinDirectory) ? exe : Path.Combine(options.BinDirectory, exe);
    }

    public async Task<TranscodeResult> RunAsync(
        string sourceKey, long seriesId, int episodeNumber, int version,
        Func<int, Task> reportProgress, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(options.WorkDirectory) ? Path.GetTempPath() : options.WorkDirectory;
        var work = Path.Combine(root, "reelguo-transcode", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, "source" + Path.GetExtension(sourceKey));
            if (!await storage.Raw.ExistsAsync(sourceKey, cancellationToken))
                throw new MediaRejectedException("找不到上传的视频文件");
            await storage.Raw.DownloadToFileAsync(sourceKey, input, cancellationToken);
            await reportProgress(2);

            var probeJson = await RunAsync(Tool("ffprobe"),
                ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", input], work, null, cancellationToken);
            var probe = MediaProbe.Parse(probeJson.Stdout);
            var plan = EncodePlan.For(probe);
            logger.LogInformation("Encoding {Source}: {Width}x{Height} {Duration:0.0}s {Fps}fps hdr={Hdr} audio={Audio} -> {Rungs} renditions",
                sourceKey, probe.Width, probe.Height, probe.DurationSeconds, plan.Fps, probe.IsHdr, probe.HasAudio, plan.Renditions.Count);

            var output = Path.Combine(work, "hls");
            Directory.CreateDirectory(output);
            for (var i = 0; i < plan.Renditions.Count; i++) Directory.CreateDirectory(Path.Combine(output, $"r{i}"));

            var lastReported = 2;
            await RunAsync(Tool("ffmpeg"), plan.FfmpegArguments(input), output, async line =>
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                if (!long.TryParse(line.AsSpan("out_time_us=".Length), out var us) || us <= 0) return;
                var pct = (int)Math.Clamp(5 + us / 1e6 / plan.DurationSeconds * 85, 5, 90);
                if (pct >= lastReported + 5) { lastReported = pct; await reportProgress(pct); }
            }, cancellationToken);

            var poster = Path.Combine(work, "poster.jpg");
            await RunAsync(Tool("ffmpeg"), plan.PosterArguments(input, poster), work, null, cancellationToken);
            await reportProgress(92);

            // Upload: segments and init files first, playlists last, so a playlist never points at a missing file.
            var prefix = MediaPaths.EpisodePrefix(seriesId, episodeNumber, version);
            var files = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Select(f => (Local: f, Relative: Path.GetRelativePath(output, f).Replace('\\', '/')))
                .OrderBy(f => f.Relative.EndsWith(".m3u8", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(f => f.Relative, StringComparer.Ordinal)
                .ToList();
            if (!files.Any(f => f.Relative == "master.m3u8"))
                throw new InvalidOperationException("ffmpeg did not write master.m3u8");
            foreach (var (local, relative) in files)
            {
                var key = prefix + relative;
                await storage.Media.PutFileAsync(key, local, StorageKeys.ContentTypeFor(key), cancellationToken);
            }

            var posterKey = MediaPaths.PosterKey(seriesId, episodeNumber, version);
            await storage.Media.PutFileAsync(posterKey, poster, "image/jpeg", cancellationToken);
            await reportProgress(99);

            var renditions = JsonSerializer.Serialize(plan.Renditions.Select(r => new { width = r.Width, height = r.Height, kbps = r.VideoKbps }));
            return new TranscodeResult(prefix, version, probe.DurationSeconds, probe.Width, probe.Height, posterKey, renditions);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not clean up {WorkDir}", work); }
        }
    }

    private sealed record ProcessOutput(string Stdout, string StderrTail);

    /// <summary>Runs a tool; stdout lines go to <paramref name="onStdoutLine"/> (or are collected), stderr keeps the last lines for errors.</summary>
    private static async Task<ProcessOutput> RunAsync(
        string fileName, IReadOnlyList<string> args, string workingDirectory, Func<string, Task>? onStdoutLine, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Cannot start {fileName}. Is ffmpeg installed? ({ex.Message})", ex);
        }

        var stdout = new StringBuilder();
        var stderr = new Queue<string>();
        var readOut = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (onStdoutLine is null) stdout.AppendLine(line);
                else await onStdoutLine(line);
            }
        }, cancellationToken);
        var readErr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                stderr.Enqueue(line);
                if (stderr.Count > 30) stderr.Dequeue();
            }
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(readOut, readErr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var tail = string.Join('\n', stderr);
            throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(fileName)} exited with {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {tail}");
        }
        return new ProcessOutput(stdout.ToString(), string.Join('\n', stderr));
    }
}

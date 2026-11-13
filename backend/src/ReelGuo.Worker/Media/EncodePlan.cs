using System.Globalization;

namespace ReelGuo.Worker.Media;

public sealed record Rendition(int Width, int Height, int VideoKbps);

/// <summary>Everything ffmpeg needs for one episode: the ladder plus the normalisation decisions.</summary>
public sealed record EncodePlan(IReadOnlyList<Rendition> Renditions, int Fps, bool ToneMap, bool AddSilentAudio, double DurationSeconds)
{
    /// <summary>Rungs by the short side of the frame (works for portrait and landscape).</summary>
    public static readonly (int ShortSide, int Kbps)[] Ladder = [(360, 600), (540, 1200), (720, 2200), (1080, 4500)];

    public const int SegmentSeconds = 4;
    public const int FirstSegmentSeconds = 2;
    public const int GopSeconds = 2;
    public const int AudioKbps = 128;
    public const int MaxFps = 30;

    public static EncodePlan For(ProbeResult probe)
    {
        var shortSide = Math.Min(probe.Width, probe.Height);
        var rungs = Ladder.Where(r => r.ShortSide <= shortSide).ToList();
        // Never upscale: a source smaller than the lowest rung is encoded once at its own size.
        if (rungs.Count == 0) rungs.Add((Even(shortSide), Ladder[0].Kbps));

        var renditions = rungs.Select(r => SizeFor(probe.Width, probe.Height, r.ShortSide, r.Kbps)).ToList();
        var fps = probe.FramesPerSecond <= 0 ? MaxFps : (int)Math.Clamp(Math.Round(probe.FramesPerSecond), 15, MaxFps);
        return new EncodePlan(renditions, fps, probe.IsHdr, !probe.HasAudio, probe.DurationSeconds);
    }

    private static Rendition SizeFor(int width, int height, int shortSide, int kbps)
    {
        var portrait = height >= width;
        var longSide = Even((int)Math.Round(shortSide * (portrait ? height / (double)width : width / (double)height)));
        return portrait ? new Rendition(Even(shortSide), longSide, kbps) : new Rendition(longSide, Even(shortSide), kbps);
    }

    private static int Even(int v) => Math.Max(2, v - v % 2);

    /// <summary>Filters applied once before the ladder split: constant frame rate, optional HDR to SDR.</summary>
    public string PreFilter()
    {
        var parts = new List<string> { $"fps={Fps}" };
        if (ToneMap)
            parts.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        return string.Join(',', parts);
    }

    /// <summary>
    /// One ffmpeg run producing every rendition as HLS fMP4 with a master playlist:
    /// master.m3u8, r{n}/index.m3u8, r{n}/init_{n}.mp4, r{n}/seg_0000.m4s ...
    /// Keyframes every 2 s on every rendition so segments line up for ABR switching.
    /// Output paths are relative: ffmpeg must run with the output folder as its working directory,
    /// otherwise (a) Windows builds write backslashes into master.m3u8 and (b) init segments land
    /// outside the variant folders their playlists point into.
    /// </summary>
    public IReadOnlyList<string> FfmpegArguments(string input)
    {
        var n = Renditions.Count;
        var inv = CultureInfo.InvariantCulture;
        var gop = Fps * GopSeconds;

        var video = new List<string> { $"[0:v]{PreFilter()},split={n}{string.Concat(Enumerable.Range(0, n).Select(i => $"[s{i}]"))}" };
        for (var i = 0; i < n; i++)
            video.Add($"[s{i}]scale={Renditions[i].Width}:{Renditions[i].Height}:flags=lanczos,format=yuv420p[v{i}]");

        var audioSource = AddSilentAudio ? "[1:a]" : "[0:a:0]";
        var audio = $"{audioSource}atrim=duration={DurationSeconds.ToString("0.###", inv)},loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000,asplit={n}{string.Concat(Enumerable.Range(0, n).Select(i => $"[a{i}]"))}";

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        if (AddSilentAudio) args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
        args.AddRange(["-filter_complex", string.Join(';', video.Append(audio))]);

        for (var i = 0; i < n; i++) args.AddRange(["-map", $"[v{i}]", "-map", $"[a{i}]"]);

        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high"]);
        for (var i = 0; i < n; i++)
        {
            var kbps = Renditions[i].VideoKbps;
            args.AddRange([$"-b:v:{i}", $"{kbps}k", $"-maxrate:v:{i}", $"{kbps * 3 / 2}k", $"-bufsize:v:{i}", $"{kbps * 2}k"]);
        }
        args.AddRange([
            "-g", gop.ToString(inv), "-keyint_min", gop.ToString(inv), "-sc_threshold", "0",
            "-force_key_frames", $"expr:gte(t,n_forced*{GopSeconds})",
            "-c:a", "aac", "-b:a", $"{AudioKbps}k", "-ac", "2",
            "-f", "hls",
            "-hls_time", SegmentSeconds.ToString(inv),
            "-hls_init_time", FirstSegmentSeconds.ToString(inv),
            "-hls_playlist_type", "vod",
            "-hls_segment_type", "fmp4",
            "-hls_flags", "independent_segments",
            "-hls_fmp4_init_filename", "init.mp4",
            "-hls_segment_filename", "r%v/seg_%04d.m4s",
            "-master_pl_name", "master.m3u8",
            "-var_stream_map", string.Join(' ', Enumerable.Range(0, n).Select(i => $"v:{i},a:{i}")),
            "-progress", "pipe:1", "-nostats",
            "r%v/index.m3u8",
        ]);
        return args;
    }

    /// <summary>Poster frame: one JPEG, 720 px wide (or the source width if smaller), same colour handling as the video.</summary>
    public IReadOnlyList<string> PosterArguments(string input, string output)
    {
        var at = Math.Min(1.0, DurationSeconds / 3).ToString("0.###", CultureInfo.InvariantCulture);
        var filter = (ToneMap ? PreFilter() + "," : "") + "scale='min(720,iw)':-2,format=yuvj420p";
        return ["-hide_banner", "-nostdin", "-y", "-ss", at, "-i", input, "-frames:v", "1", "-vf", filter, "-q:v", "3", output];
    }
}

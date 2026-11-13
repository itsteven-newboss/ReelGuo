using System.Globalization;
using System.Text.Json;

namespace ReelGuo.Worker.Media;

/// <summary>What the transcoder needs to know about a source file. Width/height are display size (rotation applied).</summary>
public sealed record ProbeResult(
    double DurationSeconds,
    int Width,
    int Height,
    double FramesPerSecond,
    bool HasAudio,
    bool IsHdr,
    string VideoCodec);

/// <summary>A source the pipeline will never accept; retrying cannot help.</summary>
public sealed class MediaRejectedException(string message) : Exception(message);

public static class MediaProbe
{
    /// <summary>Parses <c>ffprobe -print_format json -show_streams -show_format</c> output.</summary>
    public static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = root.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : [];

        var video = streams.FirstOrDefault(x => Str(x, "codec_type") == "video" && !IsAttachedPicture(x));
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new MediaRejectedException("文件中没有视频画面");

        var width = Int(video, "width");
        var height = Int(video, "height");
        if (width < 16 || height < 16) throw new MediaRejectedException("视频分辨率无效");

        var rotation = Math.Abs(Rotation(video)) % 360;
        if (rotation is 90 or 270) (width, height) = (height, width);

        var duration = Double(root.TryGetProperty("format", out var format) ? format : default, "duration");
        if (duration <= 0) duration = Double(video, "duration");
        if (duration < 1) throw new MediaRejectedException("视频时长不足 1 秒");

        var fps = Rate(Str(video, "avg_frame_rate"));
        if (fps <= 0) fps = Rate(Str(video, "r_frame_rate"));

        var transfer = Str(video, "color_transfer");
        return new ProbeResult(
            duration, width, height, fps,
            HasAudio: streams.Any(x => Str(x, "codec_type") == "audio"),
            IsHdr: transfer is "smpte2084" or "arib-std-b67",
            VideoCodec: Str(video, "codec_name") ?? "unknown");
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    private static int Rotation(JsonElement video)
    {
        if (video.TryGetProperty("side_data_list", out var list))
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(r.GetDouble());
        if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate)
            && int.TryParse(rotate.GetString(), out var deg))
            return deg;
        return 0;
    }

    private static double Rate(string? fraction)
    {
        if (string.IsNullOrEmpty(fraction)) return 0;
        var parts = fraction.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(fraction, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double Double(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }
}

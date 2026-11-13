using Microsoft.Extensions.Time.Testing;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;
using ReelGuo.Worker.Media;

namespace ReelGuo.Api.Tests;

public sealed class MediaUrlSignerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static (MediaUrlSigner Signer, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(Now);
        return (new MediaUrlSigner(new MediaOptions { SigningKey = TestKeys.SigningKey, PublicBaseUrl = "https://cdn.test/", PlaybackUrlMinutes = 60 }, time), time);
    }

    private static (long Exp, string Sig, string Path) Split(string url)
    {
        var parts = url["https://cdn.test/m/".Length..].Split('/', 3);
        return (long.Parse(parts[0]), parts[1], parts[2]);
    }

    [Fact]
    public void Playback_url_validates_for_every_file_in_the_episode_folder()
    {
        var (signer, _) = Create();
        var (url, expires) = signer.PlaybackUrl("s/12/e/3/v2/");
        var (exp, sig, path) = Split(url);

        Assert.Equal("s/12/e/3/v2/master.m3u8", path);
        Assert.Equal(Now.AddMinutes(60), expires);
        Assert.True(signer.IsValid(exp, sig, path));
        Assert.True(signer.IsValid(exp, sig, "s/12/e/3/v2/r0/seg_0003.m4s"));
    }

    [Theory]
    [InlineData("s/12/e/4/v2/master.m3u8")]   // another episode
    [InlineData("s/13/e/3/v2/master.m3u8")]   // another series
    [InlineData("s/12/e/3/v1/master.m3u8")]   // another version
    [InlineData("p/covers/x.jpg")]            // outside episode folders
    [InlineData("master.m3u8")]
    public void Signature_does_not_cover_other_paths(string path)
    {
        var (signer, _) = Create();
        var (exp, sig, _) = Split(signer.PlaybackUrl("s/12/e/3/v2/").Url);
        Assert.False(signer.IsValid(exp, sig, path));
    }

    [Fact]
    public void Expired_or_tampered_urls_are_rejected()
    {
        var (signer, time) = Create();
        var (exp, sig, path) = Split(signer.PlaybackUrl("s/12/e/3/v2/").Url);

        Assert.False(signer.IsValid(exp + 1, sig, path));
        Assert.False(signer.IsValid(exp, sig[..^1] + (sig[^1] == 'A' ? 'B' : 'A'), path));
        time.Advance(TimeSpan.FromMinutes(61));
        Assert.False(signer.IsValid(exp, sig, path));
    }

    [Fact]
    public void Known_signature_vector_matches_the_edge_worker()
    {
        // The same vector is asserted in edge/src/sign.test.ts so both implementations stay compatible.
        var (signer, _) = Create();
        Assert.Equal("syg9SyrK7lpbfqYO02OCB_7N2KfqhU-X7HwmRLfv0Fw", signer.Sign(1800000000, "s/12/e/3/v2/"));
    }
}

public sealed class StorageKeyTests
{
    [Theory]
    [InlineData("s/1/e/1/v1/master.m3u8", true)]
    [InlineData("incoming/ep01.mp4", true)]
    [InlineData("../secret", false)]
    [InlineData("a/../b", false)]
    [InlineData("/abs", false)]
    [InlineData("a//b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a/b?c", false)]
    [InlineData("", false)]
    public void Only_safe_relative_keys_are_valid(string key, bool valid) => Assert.Equal(valid, StorageKeys.IsValid(key));

    [Theory]
    [InlineData("第1集 final.mp4", "1_final.mp4")]
    [InlineData("C:\\videos\\ep 02.MOV", "ep_02.MOV")]
    [InlineData("..", "file")]
    public void File_names_are_sanitised(string input, string expected) => Assert.Equal(expected, StorageKeys.SanitizeFileName(input));

    [Fact]
    public async Task Filesystem_storage_round_trips_and_refuses_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "reelguo-tests", Guid.NewGuid().ToString("N"));
        var storage = new FileSystemObjectStorage(root);
        var ct = TestContext.Current.CancellationToken;
        await storage.PutAsync("a/b.txt", new MemoryStream("hello"u8.ToArray()), "text/plain", ct);

        Assert.True(await storage.ExistsAsync("a/b.txt", ct));
        Assert.Equal(["a/b.txt"], await storage.ListAsync("a/", ct));
        await using (var obj = await storage.OpenReadAsync("a/b.txt", ct))
            Assert.Equal(5, obj!.Length);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ExistsAsync("../escape", ct));
        Directory.Delete(root, true);
    }
}

public sealed class CsvTests
{
    [Fact]
    public void Parses_quotes_commas_newlines_and_bom()
    {
        var rows = Csv.Parse("\uFEFFa,b,c\r\n\"x, y\",\"say \"\"hi\"\"\",\"line1\nline2\"\n\n1,,3");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["x, y", "say \"hi\"", "line1\nline2"], rows[1]);
        Assert.Equal(["1", "", "3"], rows[2]);
    }

    [Fact]
    public void Header_rows_become_dictionaries()
    {
        var (headers, rows) = Csv.ParseWithHeader("External_ID , episode\nS1,1\nS1");
        Assert.Equal(["external_id", "episode"], headers);
        Assert.Equal("S1", rows[0]["external_id"]);
        Assert.Equal("", rows[1]["episode"]);
    }
}

public sealed class PublishRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly Category Open = new() { Slug = "ai-duanju", Name = "AI成人短剧", Status = CategoryStatus.Active };

    private static Series Complete() => new()
    {
        Title = "t",
        RightsHolder = "Studio",
        ContractRef = "C-1",
        LicenceStart = Today.AddDays(-1),
        Territories = ["*"],
        AiGenerated = true,
        HasRealPerson = false,
    };

    [Fact]
    public void Complete_series_passes() => Assert.Empty(PublishRules.Check(Complete(), Open, 1, Today));

    [Fact]
    public void Missing_rights_data_and_episodes_are_all_reported()
    {
        var problems = PublishRules.Check(new Series { Title = "t" }, Open, 0, Today);
        Assert.Equal(7, problems.Count);
    }

    [Fact]
    public void Real_person_requires_consent_reference()
    {
        var s = Complete();
        s.HasRealPerson = true;
        Assert.Contains("出现真实人物时必须填写授权同意书编号", PublishRules.Check(s, Open, 1, Today));
        s.ConsentRef = "CONSENT-9";
        Assert.Empty(PublishRules.Check(s, Open, 1, Today));
    }

    [Fact]
    public void Coming_soon_channels_cannot_be_published_to()
    {
        var faceSwap = new Category { Slug = "ai-huanlian", Name = "AI换脸", Status = CategoryStatus.ComingSoon };
        Assert.Contains(PublishRules.Check(Complete(), faceSwap, 1, Today), p => p.Contains("未开放"));
    }

    [Fact]
    public void Expired_licence_blocks_publishing()
    {
        var s = Complete();
        s.LicenceEnd = Today.AddDays(-1);
        Assert.Contains("授权已过期", PublishRules.Check(s, Open, 1, Today));
    }

    [Theory]
    [InlineData(new[] { "us", "JP", "us" }, new[] { "JP", "US" })]
    [InlineData(new[] { "US", "*" }, new[] { "*" })]
    public void Territories_are_normalised(string[] input, string[] expected) =>
        Assert.Equal(expected, PublishRules.NormalizeTerritories(input));

    [Theory]
    [InlineData("ZZ")]
    [InlineData("XX")]
    [InlineData("CHN")]
    public void Invalid_territories_are_rejected(string code) => Assert.Null(PublishRules.NormalizeTerritories([code]));
}

public sealed class MediaProbeTests
{
    private const string Portrait = """
        {"streams":[
          {"codec_type":"video","codec_name":"h264","width":1080,"height":1920,"avg_frame_rate":"30000/1001","color_transfer":"bt709"},
          {"codec_type":"audio","codec_name":"aac"}],
         "format":{"duration":"95.5"}}
        """;

    [Fact]
    public void Reads_size_rate_audio_and_duration()
    {
        var p = MediaProbe.Parse(Portrait);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        Assert.Equal(29.97, p.FramesPerSecond, 2);
        Assert.True(p.HasAudio);
        Assert.False(p.IsHdr);
        Assert.Equal(95.5, p.DurationSeconds);
    }

    [Fact]
    public void Rotation_swaps_display_size_and_hdr_is_detected()
    {
        var p = MediaProbe.Parse("""
            {"streams":[{"codec_type":"video","codec_name":"hevc","width":1920,"height":1080,"r_frame_rate":"60/1",
              "color_transfer":"arib-std-b67","side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]}],
             "format":{"duration":"12"}}
            """);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        Assert.True(p.IsHdr);
        Assert.False(p.HasAudio);
    }

    [Fact]
    public void Cover_art_is_not_mistaken_for_video()
    {
        var ex = Assert.Throws<MediaRejectedException>(() => MediaProbe.Parse("""
            {"streams":[{"codec_type":"audio"},{"codec_type":"video","width":500,"height":500,"disposition":{"attached_pic":1}}],"format":{"duration":"30"}}
            """));
        Assert.Contains("没有视频", ex.Message);
    }

    [Fact]
    public void Too_short_files_are_rejected() =>
        Assert.Throws<MediaRejectedException>(() => MediaProbe.Parse("""
            {"streams":[{"codec_type":"video","width":720,"height":1280}],"format":{"duration":"0.4"}}
            """));
}

public sealed class EncodePlanTests
{
    private static ProbeResult Probe(int w, int h, double fps = 30, bool audio = true, bool hdr = false) =>
        new(60, w, h, fps, audio, hdr, "h264");

    [Fact]
    public void Full_hd_portrait_gets_the_whole_ladder()
    {
        var plan = EncodePlan.For(Probe(1080, 1920));
        Assert.Equal([(360, 640), (540, 960), (720, 1280), (1080, 1920)], plan.Renditions.Select(r => (r.Width, r.Height)));
    }

    [Fact]
    public void Landscape_sources_keep_their_shape()
    {
        var plan = EncodePlan.For(Probe(1920, 1080));
        Assert.Equal((640, 360), (plan.Renditions[0].Width, plan.Renditions[0].Height));
        Assert.Equal((1920, 1080), (plan.Renditions[^1].Width, plan.Renditions[^1].Height));
    }

    [Fact]
    public void Never_upscales()
    {
        Assert.Equal(3, EncodePlan.For(Probe(720, 1280)).Renditions.Count);
        var tiny = EncodePlan.For(Probe(301, 533));
        Assert.Single(tiny.Renditions);
        Assert.Equal(300, tiny.Renditions[0].Width);
    }

    [Theory]
    [InlineData(59.94, 30)]
    [InlineData(25, 25)]
    [InlineData(0, 30)]
    [InlineData(8, 15)]
    public void Frame_rate_is_capped_and_made_constant(double source, int expected) =>
        Assert.Equal(expected, EncodePlan.For(Probe(1080, 1920, fps: source)).Fps);

    [Fact]
    public void Silent_sources_get_a_silent_track_and_hdr_gets_tone_mapping()
    {
        var plan = EncodePlan.For(Probe(1080, 1920, audio: false, hdr: true));
        var args = string.Join(' ', plan.FfmpegArguments("in.mp4"));
        Assert.Contains("r%v/index.m3u8", args);
        Assert.Contains("anullsrc", args);
        Assert.Contains("tonemap=tonemap=hable", args);
        Assert.Contains("v:0,a:0 v:1,a:1 v:2,a:2 v:3,a:3", args);
        Assert.Contains("-hls_segment_type fmp4", args);
    }
}

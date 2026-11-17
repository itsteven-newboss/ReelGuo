using System.Net;
using ReelGuo.Api.Admin;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Admin.AdminPreviewEndpoints;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Which video the admin preview plays, for each state an episode can be in.</summary>
public sealed class PreviewUnitTests
{
    private static Episode Ep(EpisodeStatus status, string? source = null, string? hls = null, string? video = null) =>
        new() { Status = status, SourceKey = source, HlsPrefix = hls, VideoKey = video };

    [Fact]
    public void Processed_video_wins_over_the_original()
    {
        Assert.Equal(PreviewKind.HlsOutput, Pick(Ep(EpisodeStatus.Ready, "uploads/1/a/x.mp4", hls: "s/1/e/1/v1/"), cosEnabled: false));
        Assert.Equal(PreviewKind.CosOutput, Pick(Ep(EpisodeStatus.Ready, "videos/r/t/x.mp4", video: "videos/r/t/x.mp4"), cosEnabled: true));
        // Replacing a ready episode keeps the old output playable while the new upload is processed.
        Assert.Equal(PreviewKind.HlsOutput, Pick(Ep(EpisodeStatus.Processing, "uploads/1/b/y.mp4", hls: "s/1/e/1/v1/"), cosEnabled: false));
    }

    [Fact]
    public void Original_plays_once_the_upload_finished()
    {
        Assert.Equal(PreviewKind.LocalOriginal, Pick(Ep(EpisodeStatus.Processing, "uploads/1/a/x.mp4"), cosEnabled: false));
        Assert.Equal(PreviewKind.LocalOriginal, Pick(Ep(EpisodeStatus.Failed, "uploads/1/a/x.mov"), cosEnabled: false));
        Assert.Equal(PreviewKind.CosOriginal, Pick(Ep(EpisodeStatus.Failed, "videos/r/t/x.mp4"), cosEnabled: true));
    }

    [Fact]
    public void Nothing_to_play_before_the_upload_or_without_cos()
    {
        Assert.Equal(PreviewKind.None, Pick(Ep(EpisodeStatus.Uploading), cosEnabled: false));
        Assert.Equal(PreviewKind.None, Pick(Ep(EpisodeStatus.Uploading, "uploads/1/a/x.mp4"), cosEnabled: false));
        // A COS episode on a server whose COS settings were removed: there is no way to sign a link.
        Assert.Equal(PreviewKind.None, Pick(Ep(EpisodeStatus.Ready, "videos/r/t/x.mp4", video: "videos/r/t/x.mp4"), cosEnabled: false));
    }

    [Fact]
    public async Task Preview_routes_need_an_admin()
    {
        await using var api = new ApiFactory();
        var anonymous = api.CreateClient();
        foreach (var url in new[] { "/api/admin/episodes/1/preview", "/api/admin/episodes/1/preview/source", "/api/admin/episodes/1/preview/hls/1/master.m3u8" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url, Ct)).StatusCode);
    }
}

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Telemetry;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Which traces the local store keeps (LocalTelemetry.cs), without a database.</summary>
public sealed class TelemetryUnitTests : IDisposable
{
    private readonly ActivitySource _source = new($"telemetry-test-{Guid.NewGuid():N}");
    private readonly ActivityListener _listener;

    public TelemetryUnitTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s == _source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private static (LocalTraceProcessor Processor, TelemetrySink Sink) Store(double sampleRate = 0)
    {
        var status = new LocalTelemetryStatus();
        var sink = new TelemetrySink(status);
        var processor = new LocalTraceProcessor("test", sink, status, Options.Create(new LocalTelemetryOptions { SampleRate = sampleRate }));
        return (processor, sink);
    }

    private static List<TelemetrySpan> Written(TelemetrySink sink)
    {
        var spans = new List<TelemetrySpan>();
        while (sink.Spans.Reader.TryRead(out var s)) spans.Add(s);
        return spans;
    }

    /// <summary>A request with one database call; the call fails when asked.</summary>
    private void Request(LocalTraceProcessor processor, string name, bool fail = false, double seconds = 0.01, ActivityKind kind = ActivityKind.Server)
    {
        var root = _source.StartActivity(name, kind)!;
        var child = _source.StartActivity("postgresql", ActivityKind.Client)!;
        if (fail) child.SetStatus(ActivityStatusCode.Error, "relation \"series\" does not exist");
        child.Stop();
        processor.OnEnd(child);
        root.SetEndTime(root.StartTimeUtc.AddSeconds(seconds));
        root.Stop();
        processor.OnEnd(root);
    }

    [Fact]
    public void A_fast_successful_request_is_dropped_and_a_failed_one_is_kept_whole()
    {
        var (processor, sink) = Store();
        Request(processor, "GET /api/v1/feed");
        Assert.Empty(Written(sink));

        Request(processor, "GET /api/v1/series/{id}", fail: true);
        var spans = Written(sink);
        Assert.Equal(2, spans.Count);
        Assert.Single(spans, s => s.IsRoot && s.Name == "GET /api/v1/series/{id}" && s.Kind == "server");
        var failed = Assert.Single(spans, s => s.IsError);
        Assert.Equal("relation \"series\" does not exist", failed.StatusMessage);
        Assert.Equal(spans.Single(s => s.IsRoot).SpanId, failed.ParentSpanId);
        Assert.All(spans, s => Assert.Equal(spans[0].TraceId, s.TraceId));
    }

    [Fact]
    public void Slow_requests_are_kept_and_a_background_call_only_when_it_fails_or_is_slow()
    {
        var (processor, sink) = Store(sampleRate: 1);
        Request(processor, "GET /api/v1/ranks", seconds: 2);
        Assert.Equal(2, Written(sink).Count);

        // Polling with no request around it: not sampled, even at a 100% sample rate.
        var poll = _source.StartActivity("postgresql", ActivityKind.Client)!;
        poll.Stop();
        processor.OnEnd(poll);
        Assert.Empty(Written(sink));

        var broken = _source.StartActivity("postgresql", ActivityKind.Client)!;
        broken.SetStatus(ActivityStatusCode.Error, "connection refused");
        broken.Stop();
        processor.OnEnd(broken);
        Assert.Single(Written(sink));
    }

    [Fact]
    public void An_error_logged_inside_a_trace_keeps_it()
    {
        var (processor, sink) = Store();
        var job = _source.StartActivity("media.transcode", ActivityKind.Internal)!;
        processor.MarkKeep(job.TraceId);
        job.Stop();
        processor.OnEnd(job);
        Assert.Single(Written(sink));
    }

    [Fact]
    public void Attributes_and_exceptions_are_recorded_and_long_values_cut()
    {
        var activity = _source.StartActivity("GET /x", ActivityKind.Server)!;
        activity.SetTag("http.route", "/x");
        activity.SetTag("long", new string('a', 2000));
        activity.AddException(new InvalidOperationException("磁盘已满"));
        activity.SetStatus(ActivityStatusCode.Error);
        activity.Stop();
        var span = LocalTraceProcessor.ToSpan("reelguo-api", activity);
        Assert.True(span.IsError);
        Assert.Equal("磁盘已满", span.StatusMessage);
        Assert.Contains("\"http.route\":\"/x\"", span.Attributes);
        Assert.Contains("System.InvalidOperationException", span.Attributes);
        Assert.DoesNotContain(new string('a', 600), span.Attributes);
    }

    [Fact]
    public void Sampling_is_deterministic_per_trace_and_close_to_the_rate()
    {
        var id = ActivityTraceId.CreateRandom();
        Assert.False(LocalTraceProcessor.InSample(id, 0));
        Assert.True(LocalTraceProcessor.InSample(id, 1));
        Assert.Equal(LocalTraceProcessor.InSample(id, 0.5), LocalTraceProcessor.InSample(id, 0.5));
        var kept = Enumerable.Range(0, 20_000).Count(_ => LocalTraceProcessor.InSample(ActivityTraceId.CreateRandom(), 0.02));
        Assert.InRange(kept, 250, 550);
    }
}

/// <summary>The store against the database, and the admin pages that show it.</summary>
public sealed class TelemetryTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Failed_work_and_error_logs_are_stored_and_shown_to_admins()
    {
        await using var api = db.CreateApi(new Dictionary<string, string?> { ["Telemetry:Local:SampleRate"] = "1" });
        var admin = await AdminClient.SignInAsync(api);
        var writer = api.Services.GetRequiredService<TelemetryWriter>();

        // A normal request (kept here because the sample rate is 100%), and a job that fails after logging an error.
        (await api.CreateClient().GetAsync("/api/v1/config", Ct)).EnsureSuccessStatusCode();
        string jobTrace;
        using (var job = ReelGuoTelemetry.Source.StartActivity("telemetry.test-job", ActivityKind.Internal))
        {
            Assert.NotNull(job);
            jobTrace = job.TraceId.ToHexString();
            api.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ReelGuo.Tests")
                .LogError(new InvalidOperationException("磁盘已满"), "测试任务失败 {Episode}", 3);
            job.SetStatus(ActivityStatusCode.Error, "测试任务失败");
        }

        // The request's span ends just after its response: wait until it is written.
        string? configTrace = null;
        for (var i = 0; i < 50 && configTrace is null; i++)
        {
            await writer.FlushAsync(Ct);
            var all = await admin.GetAsync("/api/admin/telemetry/traces?show=all&q=%2Fapi%2Fv1%2Fconfig", Ct).OkJsonAsync();
            configTrace = all.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("trace_id").GetString()).FirstOrDefault();
            if (configTrace is null) await Task.Delay(100, Ct);
        }
        Assert.NotNull(configTrace);

        var overview = await admin.GetAsync("/api/admin/telemetry", Ct).OkJsonAsync();
        Assert.True(overview.GetProperty("store").GetBoolean());
        Assert.False(overview.GetProperty("export").GetBoolean());
        Assert.True(overview.GetProperty("failed_traces24h").GetInt32() >= 1);
        Assert.True(overview.GetProperty("errors24h").GetInt32() >= 1);

        var problems = await admin.GetAsync("/api/admin/telemetry/traces", Ct).OkJsonAsync();
        var failed = Assert.Single(problems.GetProperty("items").EnumerateArray(), t => t.GetProperty("trace_id").GetString() == jobTrace);
        Assert.True(failed.GetProperty("is_error").GetBoolean());
        Assert.Equal("telemetry.test-job", failed.GetProperty("name").GetString());
        Assert.DoesNotContain(problems.GetProperty("items").EnumerateArray(), t => t.GetProperty("trace_id").GetString() == configTrace);

        var config = await admin.GetAsync($"/api/admin/telemetry/traces/{configTrace}", Ct).OkJsonAsync();
        var root = Assert.Single(config.GetProperty("spans").EnumerateArray(), s => s.GetProperty("parent_span_id").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert.Contains("/api/v1/config", root.GetProperty("name").GetString());
        Assert.Equal("/api/v1/config", root.GetProperty("attributes").GetProperty("http.route").GetString());

        var detail = await admin.GetAsync($"/api/admin/telemetry/traces/{jobTrace}", Ct).OkJsonAsync();
        var span = Assert.Single(detail.GetProperty("spans").EnumerateArray());
        Assert.Equal("测试任务失败", span.GetProperty("status_message").GetString());
        var log = Assert.Single(detail.GetProperty("logs").EnumerateArray());
        Assert.Equal("error", log.GetProperty("level").GetString());
        Assert.Equal("测试任务失败 3", log.GetProperty("message").GetString());
        Assert.Contains("磁盘已满", log.GetProperty("exception").GetString());

        var logs = await admin.GetAsync("/api/admin/telemetry/logs?level=error&q=%E7%A3%81%E7%9B%98", Ct).OkJsonAsync();
        Assert.Contains(logs.GetProperty("items").EnumerateArray(), l => l.GetProperty("trace_id").GetString() == jobTrace);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/telemetry/traces/{new string('0', 32)}", Ct)).StatusCode);

        var status = await admin.GetAsync("/api/admin/system/status", Ct).OkJsonAsync();
        Assert.True(status.GetProperty("telemetry_store").GetBoolean());
        Assert.DoesNotContain(status.GetProperty("alerts").EnumerateArray(), a => a.GetProperty("message").GetString()!.Contains("OTEL_EXPORTER_OTLP_ENDPOINT"));
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using ReelGuo.Data.Entities;

namespace ReelGuo.Data.Telemetry;

/// <summary>
/// Tracing without any outside service: the interesting part of the telemetry is stored in the app's own database
/// (telemetry_spans, telemetry_logs) and shown on the admin page 运行追踪. Kept are whole traces of requests and jobs
/// that failed, logged an error or were slow, a small sample of the rest, and every warning and error log line.
/// Everything older than <see cref="LocalTelemetryOptions.RetentionDays"/> is deleted. An OTLP export
/// (OTEL_EXPORTER_OTLP_ENDPOINT) can run alongside.
/// </summary>
public sealed class LocalTelemetryOptions
{
    public const string Section = "Telemetry:Local";

    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 7;
    /// <summary>A request or job at least this long is kept whole.</summary>
    public int SlowMs { get; set; } = 1000;
    /// <summary>Share of normal requests and jobs kept anyway (0..1), as a baseline to compare with.</summary>
    public double SampleRate { get; set; } = 0.02;
    public int FlushSeconds { get; set; } = 2;
    /// <summary>Spans kept per trace; a long job's database polling beyond this is left out.</summary>
    public int MaxSpansPerTrace { get; set; } = 300;
}

/// <summary>What the admin page shows about the store itself.</summary>
public sealed class LocalTelemetryStatus
{
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);
    public DateTimeOffset? LastWrite { get; internal set; }
    public string? LastError { get; internal set; }
    internal void Drop(long n = 1) => Interlocked.Add(ref _dropped, n);
}

/// <summary>Spans and log lines waiting to be written. Bounded: under a flood the oldest are dropped, never the app.</summary>
public sealed class TelemetrySink(LocalTelemetryStatus status)
{
    internal readonly Channel<TelemetrySpan> Spans = Channel.CreateBounded<TelemetrySpan>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, _ => status.Drop());
    internal readonly Channel<TelemetryLog> Logs = Channel.CreateBounded<TelemetryLog>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, _ => status.Drop());
}

/// <summary>
/// Collects each trace's spans in memory and decides once its first span in this process ends (the request or the
/// job): kept whole if anything in it failed, an error was logged in it, it was slow, or it falls in the sample.
/// </summary>
public sealed class LocalTraceProcessor(string service, TelemetrySink sink, LocalTelemetryStatus status, IOptions<LocalTelemetryOptions> options)
    : BaseProcessor<Activity>
{
    private sealed class Pending
    {
        public readonly List<TelemetrySpan> Spans = [];
        public bool Keep;
        public readonly long Started = Environment.TickCount64;
    }

    private readonly ConcurrentDictionary<ActivityTraceId, Pending> _pending = new();
    private long _ended;

    /// <summary>An error was logged inside this trace: keep it whatever happens.</summary>
    public void MarkKeep(ActivityTraceId traceId) => _pending.AddOrUpdate(traceId, _ => new Pending { Keep = true }, (_, p) => { p.Keep = true; return p; });

    public override void OnEnd(Activity activity)
    {
        if (!activity.Recorded) return;
        var o = options.Value;
        var span = ToSpan(service, activity);
        var pending = _pending.GetOrAdd(activity.TraceId, _ => new Pending());
        lock (pending)
        {
            if (pending.Spans.Count < o.MaxSpansPerTrace) pending.Spans.Add(span);
            else status.Drop();
            pending.Keep |= span.IsError;
        }

        if (span.IsRoot && _pending.TryRemove(activity.TraceId, out var done))
        {
            // Requests and jobs are sampled; a stray database or HTTP call with no request around it (background
            // polling) is only worth keeping when it failed or was slow.
            var unit = activity.Kind is ActivityKind.Server or ActivityKind.Consumer or ActivityKind.Internal;
            var keep = done.Keep || span.DurationMs >= o.SlowMs || (unit && InSample(activity.TraceId, o.SampleRate));
            if (keep) Write(done);
        }

        if (Interlocked.Increment(ref _ended) % 1000 == 0) Sweep();
    }

    private void Write(Pending pending)
    {
        lock (pending)
        {
            foreach (var s in pending.Spans) sink.Spans.Writer.TryWrite(s);
        }
    }

    /// <summary>Traces whose first span never ended here (it is in another process): decided after 10 minutes.</summary>
    private void Sweep()
    {
        var cutoff = Environment.TickCount64 - 10 * 60_000;
        foreach (var (id, p) in _pending)
        {
            if (p.Started > cutoff || !_pending.TryRemove(id, out var gone)) continue;
            if (gone.Keep) Write(gone);
        }
    }

    /// <summary>Deterministic per trace, so the API and the worker agree on the same trace.</summary>
    public static bool InSample(ActivityTraceId traceId, double rate)
    {
        if (rate <= 0) return false;
        if (rate >= 1) return true;
        Span<byte> bytes = stackalloc byte[16];
        traceId.CopyTo(bytes);
        var value = BitConverter.ToUInt64(bytes[8..]);
        return value < rate * ulong.MaxValue;
    }

    private const int MaxAttributes = 40;
    private const int MaxValue = 500;

    public static TelemetrySpan ToSpan(string service, Activity a)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in a.TagObjects)
        {
            if (value is null || attributes.Count >= MaxAttributes) continue;
            attributes[key] = Cut(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", MaxValue);
        }
        string? exceptionMessage = null;
        foreach (var e in a.Events)
        {
            if (e.Name != "exception") continue;
            foreach (var (key, value) in e.Tags)
            {
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                attributes[key] = Cut(text, key == "exception.stacktrace" ? 4000 : MaxValue);
                if (key == "exception.message") exceptionMessage = text;
            }
        }
        var isError = a.Status == ActivityStatusCode.Error;
        return new TelemetrySpan
        {
            TraceId = a.TraceId.ToHexString(),
            SpanId = a.SpanId.ToHexString(),
            ParentSpanId = a.ParentSpanId == default ? null : a.ParentSpanId.ToHexString(),
            IsRoot = a.Parent is null,
            Service = service,
            Name = Cut(a.DisplayName, 300),
            Kind = a.Kind.ToString().ToLowerInvariant(),
            StartedAt = new DateTimeOffset(a.StartTimeUtc, TimeSpan.Zero),
            DurationMs = Math.Round(a.Duration.TotalMilliseconds, 2),
            IsError = isError,
            StatusMessage = isError ? Cut(a.StatusDescription ?? exceptionMessage ?? "", 2000) : null,
            Attributes = JsonSerializer.Serialize(attributes),
        };
    }

    internal static string Cut(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Warning, error and critical log lines; an error also keeps the trace it happened in.</summary>
public sealed class LocalLogProcessor(string service, TelemetrySink sink, LocalTraceProcessor traces) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        // The writer's own database work never records itself.
        if (Sdk.SuppressInstrumentation) return;
        var level = (int)record.LogLevel;
        if (level < (int)LogLevel.Warning || level > (int)LogLevel.Critical) return;
        var hasTrace = record.TraceId != default;
        sink.Logs.Writer.TryWrite(new TelemetryLog
        {
            At = new DateTimeOffset(DateTime.SpecifyKind(record.Timestamp, DateTimeKind.Utc)),
            Service = service,
            Level = level,
            Category = LocalTraceProcessor.Cut(record.CategoryName ?? "", 300),
            Message = LocalTraceProcessor.Cut(record.FormattedMessage ?? record.Body ?? "", 4000),
            Exception = record.Exception is { } ex ? LocalTraceProcessor.Cut(ex.ToString(), 16000) : null,
            TraceId = hasTrace ? record.TraceId.ToHexString() : null,
            SpanId = hasTrace && record.SpanId != default ? record.SpanId.ToHexString() : null,
        });
        if (level >= 4 && hasTrace) traces.MarkKeep(record.TraceId);
    }
}

/// <summary>Writes the sink to the database every few seconds and deletes what is older than the retention.</summary>
public sealed class TelemetryWriter(TelemetrySink sink, LocalTelemetryStatus status, IServiceScopeFactory scopes,
    IOptions<LocalTelemetryOptions> options, TimeProvider time) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextCleanup;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.FlushSeconds)), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await FlushAsync(stoppingToken);
        }
        // Last lines written on the way out (best effort, a few seconds at most).
        using var last = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(last.Token);
    }

    /// <summary>Writes everything queued now. Also used by tests.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        using var quiet = SuppressInstrumentationScope.Begin();
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var spans = Drain(sink.Spans.Reader, 5000);
            var logs = Drain(sink.Logs.Reader, 5000);
            var now = time.GetUtcNow();
            if (spans.Count == 0 && logs.Count == 0 && now < _nextCleanup) return;

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
            if (spans.Count > 0 || logs.Count > 0)
            {
                db.TelemetrySpans.AddRange(spans);
                db.TelemetryLogs.AddRange(logs);
                await db.SaveChangesAsync(ct);
                status.LastWrite = now;
            }
            if (now >= _nextCleanup)
            {
                var cutoff = now.AddDays(-Math.Max(1, options.Value.RetentionDays));
                await db.TelemetrySpans.Where(s => s.StartedAt < cutoff).ExecuteDeleteAsync(ct);
                await db.TelemetryLogs.Where(l => l.At < cutoff).ExecuteDeleteAsync(ct);
                _nextCleanup = now.AddHours(1);
            }
            status.LastError = null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never let monitoring take the app down; the page shows the reason.
            status.LastError = LocalTraceProcessor.Cut(e.GetBaseException().Message, 500);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<T> Drain<T>(ChannelReader<T> reader, int max)
    {
        var items = new List<T>();
        while (items.Count < max && reader.TryRead(out var item)) items.Add(item);
        return items;
    }
}

public static class LocalTelemetrySetup
{
    public static bool Enabled(IConfiguration configuration) =>
        configuration.GetSection(LocalTelemetryOptions.Section).GetValue(nameof(LocalTelemetryOptions.Enabled), true);

    /// <summary>The store's services; the processors are added to the tracing and logging pipelines by the caller.</summary>
    internal static void AddLocalTelemetryServices(this IServiceCollection services, string service)
    {
        services.AddOptions<LocalTelemetryOptions>()
            .Configure<IConfiguration>((o, config) => config.GetSection(LocalTelemetryOptions.Section).Bind(o));
        services.AddSingleton<LocalTelemetryStatus>();
        services.AddSingleton<TelemetrySink>();
        services.AddSingleton(sp => new LocalTraceProcessor(service, sp.GetRequiredService<TelemetrySink>(),
            sp.GetRequiredService<LocalTelemetryStatus>(), sp.GetRequiredService<IOptions<LocalTelemetryOptions>>()));
        services.AddSingleton(sp => new LocalLogProcessor(service, sp.GetRequiredService<TelemetrySink>(), sp.GetRequiredService<LocalTraceProcessor>()));
        services.AddSingleton<TelemetryWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryWriter>());
    }
}

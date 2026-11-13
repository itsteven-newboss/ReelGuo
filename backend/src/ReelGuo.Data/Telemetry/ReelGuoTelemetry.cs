using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ReelGuo.Data.Telemetry;

/// <summary>
/// Traces, metrics and logs for the API and the worker, through OpenTelemetry. Nothing leaves the process unless
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (plus <c>OTEL_EXPORTER_OTLP_HEADERS</c> for the vendor's key), so any
/// OTLP backend works (Grafana Cloud, Honeycomb, Datadog, a self-hosted collector).
/// </summary>
public static class ReelGuoTelemetry
{
    public const string Name = "ReelGuo";

    public static string Version { get; } =
        typeof(ReelGuoTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public static readonly ActivitySource Source = new(Name, Version);
    public static readonly Meter Meter = new(Name, Version);

    // Business signals worth alerting on (their rates, not their totals).
    public static readonly Counter<long> MediaJobs = Meter.CreateCounter<long>("reelguo.media_jobs", description: "Media jobs finished, by outcome (succeeded, retry, failed).");
    public static readonly Histogram<double> TranscodeSeconds = Meter.CreateHistogram<double>("reelguo.transcode.duration", "s", "Time to encode one episode.");
    public static readonly Counter<long> PlayEvents = Meter.CreateCounter<long>("reelguo.play_events", description: "Play events received, by kind.");
    public static readonly Counter<long> PlaybackErrors = Meter.CreateCounter<long>("reelguo.playback_errors", description: "Plays that ended in a fatal player error, by error code.");
    public static readonly Counter<long> Comments = Meter.CreateCounter<long>("reelguo.comments", description: "Comments posted, by initial status.");
    public static readonly Counter<long> Reports = Meter.CreateCounter<long>("reelguo.reports", description: "Reports filed, by urgency.");
    public static readonly Counter<long> Orders = Meter.CreateCounter<long>("reelguo.orders", description: "Orders by outcome (created, paid, failed, refunded, charged_back) and kind.");
    public static readonly Counter<long> Revenue = Meter.CreateCounter<long>("reelguo.revenue", "{cent}", "Paid order value in the currency's smallest unit, by currency; reversals count negative.");
    public static readonly Counter<long> Unlocks = Meter.CreateCounter<long>("reelguo.unlocks", description: "Episodes unlocked with coins.");
    public static readonly Counter<long> CoinsSpent = Meter.CreateCounter<long>("reelguo.coins_spent", "{coin}", "Coins spent on unlocks.");
    public static readonly Counter<long> PaymentNotices = Meter.CreateCounter<long>("reelguo.payment_notices", description: "Payment provider notices by provider and outcome (applied, duplicate, ignored, rejected).");

    public static bool ExportEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    /// <summary>Common setup; the API adds ASP.NET Core instrumentation through the callbacks.</summary>
    public static IOpenTelemetryBuilder AddReelGuoTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<TracerProviderBuilder>? tracing = null,
        Action<MeterProviderBuilder>? metrics = null)
    {
        var builder = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: Version, serviceInstanceId: $"{Environment.MachineName}:{Environment.ProcessId}"))
            .WithTracing(t =>
            {
                t.AddSource(Name).AddHttpClientInstrumentation().AddNpgsql();
                tracing?.Invoke(t);
            })
            .WithMetrics(m =>
            {
                m.AddMeter(Name, "Npgsql").AddRuntimeInstrumentation().AddHttpClientInstrumentation();
                metrics?.Invoke(m);
            })
            .WithLogging(_ => { }, o =>
            {
                o.IncludeFormattedMessage = true;
                o.IncludeScopes = true;
            });
        if (ExportEnabled(configuration)) builder.UseOtlpExporter();
        return builder;
    }
}

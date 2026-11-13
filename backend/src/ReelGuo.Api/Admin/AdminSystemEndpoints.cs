using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Billing;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Media;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Storage;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Api.Admin;

public sealed record WorkerStatusDto(
    string WorkerId, string Version, DateTimeOffset StartedAt, DateTimeOffset SeenAt, bool Alive, long JobsSucceeded, long JobsFailed, long? CurrentJobId);

/// <summary>
/// Payments health. <c>provider</c> is the provider for new orders (null when purchases are off or it isn't available);
/// <c>stale_pending_orders</c> are checkouts open for over an hour; <c>rejected_notices24h</c> are provider notices that
/// failed verification or didn't match their order; the chargeback rate is over the last 30 days.
/// </summary>
public sealed record PaymentsStatusDto(
    bool Enabled, string? Provider, bool Sandbox, int StalePendingOrders, int RejectedNotices24h, int PaidOrders30d, int Chargebacks30d, double ChargebackRate30d);

/// <summary>level: critical (act now) or warning (look today).</summary>
public sealed record StatusAlertDto(string Level, string Message);

public sealed record SystemStatusDto(
    string ApiVersion, string Environment, string Instance, DateTimeOffset StartedAt, DateTimeOffset ServerTime,
    bool DatabaseOk, double DatabaseLatencyMs, long DatabaseBytes,
    bool StorageOk, string StorageProvider,
    List<WorkerStatusDto> Workers,
    int JobsQueued, int JobsRunning, int JobsFailed24h, double? OldestQueuedSeconds,
    DateTimeOffset? RankingsComputedAt,
    int PendingComments, int OpenReports, int UrgentReports,
    int PlaysLastHour, int PlaybackErrorsLastHour,
    int RevokedSeries, int BlockedCountries, int StrictAgeCountries,
    bool TelemetryExport,
    PaymentsStatusDto Payments,
    List<StatusAlertDto> Alerts);

/// <summary>系统状态: one page with everything the on-call person checks first.</summary>
public static class AdminSystemEndpoints
{
    /// <summary>A worker that hasn't written a heartbeat for this long is treated as down.</summary>
    public static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(90);

    private static readonly DateTimeOffset ProcessStarted = DateTimeOffset.UtcNow;

    public static RouteGroupBuilder MapAdminSystem(this RouteGroupBuilder admin)
    {
        admin.MapGet("/system/status", Status).WithTags("Admin: system").WithSummary("Health of the database, storage, workers, queues and moderation.");
        return admin;
    }

    /// <summary>Public, for uptime monitors: 200 while at least one media worker is alive, 503 otherwise.</summary>
    public static IEndpointRouteBuilder MapWorkerHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/worker", async (ReelGuoDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var since = time.GetUtcNow() - WorkerTimeout;
            var alive = await db.WorkerHeartbeats.CountAsync(h => h.SeenAt >= since, ct);
            return alive > 0 ? Results.Text($"Healthy ({alive} worker(s))") : Results.Text("Unhealthy (no live worker)", statusCode: StatusCodes.Status503ServiceUnavailable);
        }).ExcludeFromDescription();
        return app;
    }

    private static async Task<Ok<SystemStatusDto>> Status(
        ReelGuoDbContext db, StorageBuckets storage, IConfiguration configuration, IHostEnvironment environment, IBlockedCountryStore blocked,
        IRevokedSeriesStore revoked, PaymentGateway gateway, IOptions<BillingOptions> billing, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var alerts = new List<StatusAlertDto>();
        void Critical(string m) => alerts.Add(new("critical", m));
        void Warning(string m) => alerts.Add(new("warning", m));

        var watch = Stopwatch.StartNew();
        long dbBytes = 0;
        var dbOk = true;
        try
        {
            dbBytes = await db.Database.SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            dbOk = false;
            Critical("数据库无法连接");
        }
        var dbLatency = watch.Elapsed.TotalMilliseconds;

        var storageOk = true;
        try
        {
            await storage.Media.ExistsAsync("p/health-probe", ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            storageOk = false;
            Critical("媒体存储无法访问，新视频无法处理、图片可能无法显示");
        }

        if (!dbOk)
        {
            return TypedResults.Ok(new SystemStatusDto(ReelGuoTelemetry.Version, environment.EnvironmentName, Environment.MachineName, ProcessStarted, now,
                false, dbLatency, 0, storageOk, configuration["Storage:Provider"] ?? "FileSystem", [], 0, 0, 0, null, null, 0, 0, 0, 0, 0, 0, 0, 0,
                ReelGuoTelemetry.ExportEnabled(configuration), new PaymentsStatusDto(billing.Value.Enabled, null, false, 0, 0, 0, 0, 0), alerts));
        }

        var workers = (await db.WorkerHeartbeats.AsNoTracking().OrderByDescending(h => h.SeenAt).ToListAsync(ct))
            .Select(h => new WorkerStatusDto(h.WorkerId, h.Version, h.StartedAt, h.SeenAt, now - h.SeenAt < WorkerTimeout, h.JobsSucceeded, h.JobsFailed, h.CurrentJobId))
            .ToList();

        var queued = await db.MediaJobs.CountAsync(j => j.State == MediaJobState.Queued, ct);
        var running = await db.MediaJobs.CountAsync(j => j.State == MediaJobState.Running, ct);
        var dayAgo = now.AddDays(-1);
        var failed = await db.MediaJobs.CountAsync(j => j.State == MediaJobState.Failed && j.UpdatedAt >= dayAgo, ct);
        var oldest = await db.MediaJobs.Where(j => j.State == MediaJobState.Queued && j.RunAfter <= now).OrderBy(j => j.RunAfter)
            .Select(j => (DateTimeOffset?)j.RunAfter).FirstOrDefaultAsync(ct);
        var oldestSeconds = oldest is { } o ? (now - o).TotalSeconds : (double?)null;
        var rankings = await db.Ranks.MaxAsync(r => (DateTimeOffset?)r.ComputedAt, ct);

        var pending = await db.Comments.CountAsync(c => c.Status == CommentStatus.Pending, ct);
        var open = await db.Reports.CountAsync(r => r.Status == ReportStatus.Open, ct);
        var urgent = await db.Reports.CountAsync(r => r.Status == ReportStatus.Open && r.Urgent, ct);
        var hourAgo = now.AddHours(-1);
        var plays = await db.PlayEvents.CountAsync(e => e.Kind == PlayEventKind.Start && e.At >= hourAgo, ct);
        var playbackErrors = await db.PlaybackSamples.CountAsync(s => s.Error != null && s.At >= hourAgo, ct);
        var strict = await db.AgeRules.CountAsync(r => r.Mode == AgeCheckMode.Strict, ct);
        var telemetry = ReelGuoTelemetry.ExportEnabled(configuration);

        if (!workers.Any(w => w.Alive)) Critical("没有在线的转码 Worker：新上传的视频不会被处理");
        if (urgent > 0) Critical($"有 {urgent} 条紧急举报待处理（未成年人 / 未经同意的真人形象）");
        if (oldestSeconds > 600) Warning($"转码队列积压：最早的任务已等待 {oldestSeconds / 60:0} 分钟");
        if (failed > 0) Warning($"最近 24 小时有 {failed} 个转码任务失败，请在内容管理中查看");
        if (rankings is null || now - rankings > TimeSpan.FromMinutes(30)) Warning("榜单超过 30 分钟未更新（Worker 的榜单任务可能停止）");
        if (plays >= 20 && playbackErrors * 20 > plays) Warning($"最近 1 小时播放失败偏多：{playbackErrors} 次失败 / {plays} 次播放");
        if (pending > 200) Warning($"待审核评论积压 {pending} 条");
        if (!telemetry && !environment.IsDevelopment()) Warning("未配置监控数据导出（OTEL_EXPORTER_OTLP_ENDPOINT），出问题时无法追踪");

        var provider = gateway.Active;
        var stalePending = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending && o.CreatedAt < now.AddHours(-1), ct);
        var rejected = await db.PaymentEvents.CountAsync(e => e.Outcome == PaymentEventOutcome.Rejected && e.ReceivedAt >= dayAgo, ct);
        var monthAgo = now.AddDays(-30);
        var paid30 = await db.Orders.CountAsync(o => o.PaidAt >= monthAgo, ct);
        var chargebacks30 = await db.Orders.CountAsync(o => o.Status == OrderStatus.ChargedBack && o.ReversedAt >= monthAgo, ct);
        var chargebackRate = paid30 > 0 ? (double)chargebacks30 / paid30 : 0;
        var payments = new PaymentsStatusDto(billing.Value.Enabled, provider?.Name, provider?.IsSandbox ?? false, stalePending, rejected, paid30, chargebacks30, chargebackRate);

        if (provider?.IsSandbox == true && !environment.IsDevelopment()) Critical("支付处于模拟模式：任何人都可以免费充值。上线前请接入真实支付渠道");
        if (billing.Value.Enabled && provider is null) Warning($"付费已开启，但支付渠道「{billing.Value.Provider}」不可用：观众无法充值，付费剧集无法解锁");
        if (rejected > 0) Warning($"最近 24 小时有 {rejected} 条支付通知未通过校验或与订单不符，请在订单页核对");
        // Card networks start monitoring merchants at about 0.9 % chargebacks; stay well below.
        if (paid30 >= 20 && chargebackRate >= 0.005) Warning($"近 30 天拒付率 {chargebackRate:P1}（{chargebacks30}/{paid30}），接近卡组织监控线");

        return TypedResults.Ok(new SystemStatusDto(
            ReelGuoTelemetry.Version, environment.EnvironmentName, Environment.MachineName, ProcessStarted, now,
            true, dbLatency, dbBytes, storageOk, configuration["Storage:Provider"] ?? "FileSystem", workers,
            queued, running, failed, oldestSeconds, rankings, pending, open, urgent, plays, playbackErrors,
            (await revoked.GetAsync(ct)).Count, (await blocked.GetAsync(ct)).Count, strict, telemetry, payments, alerts));
    }
}

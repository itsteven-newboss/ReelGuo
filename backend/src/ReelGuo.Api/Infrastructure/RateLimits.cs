using System.Threading.RateLimiting;

namespace ReelGuo.Api.Infrastructure;

public static class RateLimits
{
    /// <summary>Admin sign-in: 10 attempts per minute per IP.</summary>
    public const string AdminLogin = "admin-login";

    /// <summary>Playback events: 120 batches per minute per IP.</summary>
    public const string Events = "events";

    /// <summary>Viewer sign-in and sign-up: 10 per minute per IP.</summary>
    public const string Auth = "auth";

    /// <summary>Token refresh: 60 per minute per IP.</summary>
    public const string Refresh = "refresh";

    /// <summary>Posting comments: 30 per minute per IP (per-account limits are checked separately).</summary>
    public const string Comments = "comments";

    /// <summary>Reports (anonymous allowed): 20 per hour per IP.</summary>
    public const string Reports = "reports";

    /// <summary>Episode unlocks: 60 per minute per IP.</summary>
    public const string Unlock = "unlock";

    /// <summary>Starting purchases: 20 per 10 minutes per IP.</summary>
    public const string Orders = "orders";

    /// <summary>Payment provider notices: 600 per minute per IP (providers send bursts after an outage).</summary>
    public const string Webhooks = "webhooks";

    public static IServiceCollection AddReelGuoRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return new ValueTask(ctx.HttpContext.Response.WriteAsJsonAsync(ApiErrorResponse.Of("too_many_attempts", "请求过于频繁，请稍后再试")));
            };
            o.AddPolicy(AdminLogin, ctx => Window(ctx, 10));
            o.AddPolicy(Events, ctx => Window(ctx, 120));
            o.AddPolicy(Auth, ctx => Window(ctx, 10));
            o.AddPolicy(Refresh, ctx => Window(ctx, 60));
            o.AddPolicy(Comments, ctx => Window(ctx, 30));
            o.AddPolicy(Reports, ctx => Window(ctx, 20, TimeSpan.FromHours(1)));
            o.AddPolicy(Unlock, ctx => Window(ctx, 60));
            o.AddPolicy(Orders, ctx => Window(ctx, 20, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Webhooks, ctx => Window(ctx, 600));
        });

    private static RateLimitPartition<string> Window(HttpContext ctx, int permits, TimeSpan? window = null) =>
        RateLimitPartition.GetFixedWindowLimiter(
            Network.ClientIp(ctx),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window ?? TimeSpan.FromMinutes(1), QueueLimit = 0 });
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ReelGuo.Api.Admin;
using ReelGuo.Api.Billing;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Media;
using ReelGuo.Api.Public;
using ReelGuo.Api.Social;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Content;
using ReelGuo.Data.Jobs;
using ReelGuo.Data.Media;
using ReelGuo.Data.Storage;
using ReelGuo.Data.Telemetry;
using ReelGuo.Api.Seo;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
// One JSON object per log line outside development, for log collectors.
if (!builder.Environment.IsDevelopment()) builder.Logging.AddJsonConsole(o => { o.UseUtcTimestamp = true; o.IncludeScopes = true; });
builder.Services.AddReelGuoTelemetry(builder.Configuration, "reelguo-api",
    tracing: t => t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")),
    metrics: m => m.AddAspNetCoreInstrumentation());

// JSON is snake_case to match the field names the HGhub-style client expects (episode_count, video_url, ...).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    // Numbers are numbers: no "123" strings accepted, so the OpenAPI types stay exact.
    o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});

builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "ReelGuo API";
    doc.Info.Version = "v1";
    return Task.CompletedTask;
}));

builder.Services.AddDbContext<ReelGuoDbContext>(o =>
{
    var cs = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("ConnectionStrings:Default is not set.");
    ReelGuoDbContext.Configure(o, cs);
});

builder.Services.AddHealthChecks().AddDbContextCheck<ReelGuoDbContext>("database", tags: ["ready"]);

builder.Services.Configure<GeoOptions>(builder.Configuration.GetSection(GeoOptions.Section));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.Section));
// Search-engine pages: the web app's HTML with each page's content (Caddy sends page paths here).
builder.Services.Configure<SeoOptions>(builder.Configuration.GetSection(SeoOptions.Section));
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("seo-template", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<PageTemplate>();
builder.Services.AddScoped<SeoEndpoints.Ctx>();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICountryResolver, CountryResolver>();
builder.Services.AddSingleton<IBlockedCountryStore, BlockedCountryStore>();
builder.Services.AddSingleton<IRevokedSeriesStore, RevokedSeriesStore>();
builder.Services.AddSingleton<IAgeRuleStore, AgeRuleStore>();

builder.Services.AddReelGuoStorage(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.Configure<MediaOptions>(builder.Configuration.GetSection(MediaOptions.Section));
builder.Services.AddSingleton(sp => new MediaUrlSigner(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MediaOptions>>().Value, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<MediaJobQueue>();
builder.Services.AddScoped<RankingCalculator>();
builder.Services.AddSingleton(sp => new PostRenderer(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MediaOptions>>().Value.PublicBaseUrl));
builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.Section));

builder.Services.AddReelGuoNetwork(builder.Configuration);
builder.Services.AddAdminAuth();
builder.Services.AddUserAuth(builder.Configuration);
builder.Services.Configure<ModerationOptions>(builder.Configuration.GetSection(ModerationOptions.Section));
builder.Services.AddScoped<Moderation>();
builder.Services.AddBilling(builder.Configuration, builder.Environment);
builder.Services.AddReelGuoRateLimits();

var app = builder.Build();

// First: the real client address and scheme behind trusted proxies (rate limits, logs and cookies depend on them).
app.UseReelGuoNetwork();
// Baseline headers on every API and media response (the web apps get their CSP from Caddy).
app.Use((ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    return next(ctx);
});
app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    // Malformed requests (bad query/route values, unreadable JSON) are the caller's fault: 400, not 500.
    var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    if (error is BadHttpRequestException bad)
    {
        ctx.Response.StatusCode = bad.StatusCode;
        await ctx.Response.WriteAsJsonAsync(ApiErrorResponse.Of("bad_request", "请求参数有误"));
        return;
    }
    // The trace id lets support find this request in the logs and traces.
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;
    await ctx.Response.WriteAsJsonAsync(ApiErrorResponse.Of("internal_error", "服务器开小差了，请稍后重试", traceId: traceId));
}));

app.UseMiddleware<GeoBlockingMiddleware>();
app.UseAuthentication();
app.UseMiddleware<AgeGateMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();               // /openapi/v1.json
    app.MapScalarApiReference();    // /scalar/v1
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapWorkerHealth();

app.MapPublicApi();
app.MapAdminApi();
app.MapMediaEdge();
app.MapEdgeApi();
app.MapPayments();
app.MapSeo();  // last: it also answers unknown page paths

if (!BuildTime.IsGeneratingOpenApi)
{
    if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>().Database.MigrateAsync();
    }
    await AdminAuth.EnsureBootstrapAdminAsync(app.Services);
}

app.Run();

/// <summary>Exposed for WebApplicationFactory in tests.</summary>
public partial class Program;

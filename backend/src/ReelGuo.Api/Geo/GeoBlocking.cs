using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Geo;

namespace ReelGuo.Api.Geo;

public sealed class GeoOptions
{
    public const string Section = "Geo";

    /// <summary>Master switch for country blocking.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Header the CDN uses to report the visitor's country.</summary>
    public string CountryHeader { get; set; } = "CF-IPCountry";

    /// <summary>
    /// Only turn on when every request reaches the API through the CDN; otherwise anyone can send a fake header.
    /// When off, every visitor's country is XX (unknown).
    /// </summary>
    public bool TrustCountryHeader { get; set; }

    /// <summary>Honour X-Debug-Country for local testing. Ignored outside the Development environment.</summary>
    public bool AllowDebugHeader { get; set; }

    /// <summary>How long the block list is cached in memory. Admin changes on this instance apply immediately.</summary>
    public int CacheSeconds { get; set; } = 60;
}

public interface ICountryResolver
{
    /// <summary>Upper-case country code for the request, or XX when it cannot be determined.</summary>
    string Resolve(HttpContext context);
}

public sealed class CountryResolver(IOptions<GeoOptions> options, IHostEnvironment environment) : ICountryResolver
{
    public const string DebugHeader = "X-Debug-Country";

    public string Resolve(HttpContext context)
    {
        var o = options.Value;
        var headers = context.Request.Headers;

        if (o.AllowDebugHeader && environment.IsDevelopment() && headers.TryGetValue(DebugHeader, out var debug))
            return CountryCodes.Normalize(debug.ToString()) ?? CountryCodes.Unknown;

        // With proxies configured, only a request that came through one may state its country.
        if (o.TrustCountryHeader && (!Network.ProxiesConfigured(context) || Network.ViaTrustedProxy(context))
            && headers.TryGetValue(o.CountryHeader, out var reported))
            return CountryCodes.Normalize(reported.ToString()) ?? CountryCodes.Unknown;

        return CountryCodes.Unknown;
    }
}

public interface IBlockedCountryStore
{
    ValueTask<IReadOnlySet<string>> GetAsync(CancellationToken cancellationToken);

    /// <summary>Forces a reload on the next request; called after admin edits.</summary>
    void Invalidate();
}

/// <summary>In-memory cache of the blocked_countries table. On a failed refresh it keeps serving the last known list.</summary>
public sealed class BlockedCountryStore(
    IServiceScopeFactory scopes,
    IOptions<GeoOptions> options,
    TimeProvider time,
    ILogger<BlockedCountryStore> logger) : IBlockedCountryStore
{
    private sealed record Snapshot(FrozenSet<string> Codes, DateTimeOffset LoadedAt);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Snapshot? _snapshot;

    public async ValueTask<IReadOnlySet<string>> GetAsync(CancellationToken cancellationToken)
    {
        if (IsFresh(_snapshot)) return _snapshot!.Codes;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = _snapshot;
            if (IsFresh(current)) return current!.Codes;

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
                var codes = await db.BlockedCountries.AsNoTracking().Select(b => b.CountryCode).ToListAsync(cancellationToken);
                var next = new Snapshot(codes.Select(c => c.Trim()).ToFrozenSet(StringComparer.Ordinal), time.GetUtcNow());
                _snapshot = next;
                return next.Codes;
            }
            catch (Exception ex) when (current is not null && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not refresh the blocked-country list; keeping the last known list.");
                return current.Codes;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate()
    {
        var current = _snapshot;
        if (current is not null) _snapshot = current with { LoadedAt = DateTimeOffset.MinValue };
    }

    private bool IsFresh(Snapshot? s) =>
        s is not null && time.GetUtcNow() - s.LoadedAt < TimeSpan.FromSeconds(Math.Max(1, options.Value.CacheSeconds));
}

/// <summary>
/// Returns 451 for public API calls (/api/v1/*) and media (/media/*) from blocked countries.
/// Health checks and the admin API are not blocked, so the team can manage the list from anywhere.
/// </summary>
public sealed class GeoBlockingMiddleware(
    RequestDelegate next,
    ICountryResolver resolver,
    IBlockedCountryStore store,
    IOptions<GeoOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (options.Value.Enabled && (path.StartsWithSegments("/api/v1") || path.StartsWithSegments("/media")))
        {
            var country = resolver.Resolve(context);
            var blocked = await store.GetAsync(context.RequestAborted);
            if (blocked.Contains(country))
            {
                context.Response.StatusCode = StatusCodes.Status451UnavailableForLegalReasons;
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsJsonAsync(ApiErrorResponse.Of("region_blocked", "当前地区暂不提供服务"));
                return;
            }
        }

        await next(context);
    }
}

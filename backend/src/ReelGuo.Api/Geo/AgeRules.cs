using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Geo;

/// <summary>Per-country age-verification rules (admin: 年龄验证). Countries without a rule use the 18+ confirmation.</summary>
public interface IAgeRuleStore
{
    ValueTask<AgeCheckMode> ModeForAsync(string country, CancellationToken cancellationToken);

    void Invalidate();
}

public sealed class AgeRuleStore(IServiceScopeFactory scopes, TimeProvider time, ILogger<AgeRuleStore> logger) : IAgeRuleStore
{
    private readonly DbSnapshot<FrozenDictionary<string, AgeCheckMode>> _snapshot = new(scopes, time, logger, TimeSpan.FromSeconds(60),
        async (db, _, ct) => (await db.AgeRules.AsNoTracking().ToListAsync(ct))
            .ToFrozenDictionary(r => r.CountryCode.Trim(), r => r.Mode, StringComparer.Ordinal));

    public async ValueTask<AgeCheckMode> ModeForAsync(string country, CancellationToken cancellationToken) =>
        (await _snapshot.GetAsync(cancellationToken)).GetValueOrDefault(country, AgeCheckMode.Declaration);

    public void Invalidate() => _snapshot.Invalidate();
}

/// <summary>
/// In countries with a strict rule, content endpoints answer 403 <c>age_verification_required</c> unless the visitor is
/// signed in to an account whose age has been verified. Boot, sign-in, the account itself, reports, play events and the
/// native app's update checks stay open, so a visitor can learn why and sign in. Media files are protected by their short-lived signed links,
/// which only /play hands out (and /play is covered here).
/// </summary>
public sealed class AgeGateMiddleware(RequestDelegate next, ICountryResolver resolver, IAgeRuleStore rules)
{
    private static readonly string[] OpenPaths = ["/api/v1/config", "/api/v1/auth", "/api/v1/reports", "/api/v1/events", "/api/v1/app"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api/v1")
            || OpenPaths.Any(p => path.StartsWithSegments(p))
            || path.Equals("/api/v1/me", StringComparison.OrdinalIgnoreCase)
            || await rules.ModeForAsync(resolver.Resolve(context), context.RequestAborted) != AgeCheckMode.Strict
            || await IsVerifiedAsync(context))
        {
            await next(context);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(ApiErrorResponse.Of("age_verification_required", "当前地区需完成年龄验证后才能观看"));
    }

    private static async Task<bool> IsVerifiedAsync(HttpContext context)
    {
        if (await UserAuth.OptionalUserIdAsync(context) is not { } uid) return false;
        var db = context.RequestServices.GetRequiredService<ReelGuoDbContext>();
        return await db.Users.AnyAsync(u => u.Id == uid && u.AgeVerifiedAt != null && u.Status == UserStatus.Active, context.RequestAborted);
    }
}

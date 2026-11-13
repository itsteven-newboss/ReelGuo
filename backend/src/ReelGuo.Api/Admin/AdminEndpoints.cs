using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Geo;

namespace ReelGuo.Api.Admin;

public sealed record AdminSessionDto(long Id, string Username, string Nickname, string Actor);

public sealed record AdminLoginRequest(string Username, string Password);

public sealed record BlockedCountryDto(string CountryCode, string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UpdatedBy)
{
    public static BlockedCountryDto From(BlockedCountry b) => new(b.CountryCode.Trim(), b.Note, b.CreatedAt, b.UpdatedAt, b.UpdatedBy);
}

public sealed record UpsertBlockedCountryRequest(string? Note);

public sealed record AuditLogEntryDto(long Id, DateTimeOffset At, string Actor, string Action, string TargetType, string TargetId, string? DetailsJson);

public static class AdminEndpoints
{
    public const int MaxNoteLength = 500;

    public static RouteGroupBuilder MapAdminApi(this IEndpointRouteBuilder app)
    {
        var unauthorized = new ProducesResponseTypeMetadata(StatusCodes.Status401Unauthorized, typeof(ApiErrorResponse), ["application/json"]);

        // Sign-in endpoints: anonymous, CSRF-checked, rate-limited.
        var auth = app.MapGroup("/api/admin/auth").WithTags("Admin: auth").AddEndpointFilter<AdminCsrfFilter>();
        auth.MapPost("/login", Login).RequireRateLimiting(RateLimits.AdminLogin)
            .WithSummary("Signs in an admin and sets the session cookie.");
        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(AdminAuth.Scheme);
            return TypedResults.NoContent();
        }).WithSummary("Ends the admin session.");

        // Everything else requires a signed-in admin.
        var admin = app.MapGroup("/api/admin")
            .RequireAuthorization(AdminAuth.Policy)
            .AddEndpointFilter<AdminCsrfFilter>()
            .WithMetadata(unauthorized);

        admin.MapGet("/session", Session).WithTags("Admin: auth").WithSummary("The signed-in admin.");

        var geo = admin.MapGroup("/geo").WithTags("Admin: country blocking");
        geo.MapGet("/country-codes", () => TypedResults.Ok(CountryCodes.All))
            .WithSummary("All codes the block list accepts: ISO 3166-1 alpha-2 plus XX (unknown), T1 (Tor), XK (Kosovo).");
        geo.MapGet("/blocked-countries", ListBlocked).WithSummary("Current block list.");
        geo.MapPut("/blocked-countries/{countryCode}", UpsertBlocked).WithSummary("Block a country, or update its note.");
        geo.MapDelete("/blocked-countries/{countryCode}", RemoveBlocked).WithSummary("Unblock a country.");

        admin.MapGet("/audit-log", ListAudit).WithTags("Admin").WithSummary("Most recent admin actions, newest first.");

        admin.MapAdminUsers();
        admin.MapAdminCatalog();
        admin.MapAdminPosts();
        admin.MapAdminModeration();
        admin.MapAdminAgeRules();
        admin.MapAdminSystem();
        admin.MapAdminAnalytics();
        admin.MapAdminBilling();
        admin.MapAdminAppReleases();
        admin.MapAdminPortal();
        return admin;
    }

    private static async Task<Results<Ok<AdminSessionDto>, JsonHttpResult<ApiErrorResponse>>> Login(
        AdminLoginRequest request,
        HttpContext http,
        ReelGuoDbContext db,
        IPasswordHasher<User> hasher,
        TimeProvider time,
        CancellationToken ct)
    {
        var normalized = (request.Username ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        var valid = user is { Role: UserRole.Admin, Status: UserStatus.Active }
            && hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "") != PasswordVerificationResult.Failed;
        if (!valid)
        {
            // Same response for unknown user, wrong password and disabled account.
            return TypedResults.Json(ApiErrorResponse.Of("invalid_credentials", "账号或密码错误"), statusCode: StatusCodes.Status401Unauthorized);
        }

        user!.LastLoginAt = time.GetUtcNow();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password!) == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, request.Password!);
        db.AddAudit(time, $"admin:{user.Username}", "admin.login", "user", user.Username);
        await db.SaveChangesAsync(ct);

        await http.SignInAsync(AdminAuth.Scheme, AdminAuth.PrincipalFor(user));
        return TypedResults.Ok(new AdminSessionDto(user.Id, user.Username, user.Nickname, $"admin:{user.Username}"));
    }

    private static Ok<AdminSessionDto> Session(HttpContext ctx) => TypedResults.Ok(new AdminSessionDto(
        AdminActor.UserId(ctx) ?? 0,
        ctx.User.Identity?.Name ?? "",
        ctx.User.FindFirst("nickname")?.Value ?? "",
        AdminActor.From(ctx)));

    private static async Task<Ok<List<BlockedCountryDto>>> ListBlocked(ReelGuoDbContext db, CancellationToken ct)
    {
        var rows = await db.BlockedCountries.AsNoTracking().OrderBy(b => b.CountryCode).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(BlockedCountryDto.From).ToList());
    }

    private static async Task<Results<Ok<BlockedCountryDto>, BadRequest<ApiErrorResponse>>> UpsertBlocked(
        string countryCode,
        UpsertBlockedCountryRequest request,
        HttpContext http,
        ReelGuoDbContext db,
        IBlockedCountryStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        var code = CountryCodes.Normalize(countryCode);
        if (code is null)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_country_code", $"无效的国家/地区代码：{countryCode}"));

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
            return TypedResults.BadRequest(ApiErrorResponse.Of("note_too_long", $"备注不能超过 {MaxNoteLength} 个字符"));

        var actor = AdminActor.From(http);
        var now = time.GetUtcNow();
        var row = await db.BlockedCountries.FindAsync([code], ct);
        string action;
        object details;
        if (row is null)
        {
            row = new BlockedCountry { CountryCode = code, Note = note, CreatedAt = now, UpdatedAt = now, UpdatedBy = actor };
            db.BlockedCountries.Add(row);
            action = "geo.block";
            details = new { note };
        }
        else
        {
            details = new { previous_note = row.Note, note };
            row.Note = note;
            row.UpdatedAt = now;
            row.UpdatedBy = actor;
            action = "geo.block.update";
        }

        db.AddAudit(now, actor, action, "blocked_country", code, details);
        await db.SaveChangesAsync(ct);
        store.Invalidate();
        return TypedResults.Ok(BlockedCountryDto.From(row));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> RemoveBlocked(
        string countryCode,
        HttpContext http,
        ReelGuoDbContext db,
        IBlockedCountryStore store,
        TimeProvider time,
        CancellationToken ct)
    {
        var code = CountryCodes.Normalize(countryCode);
        var row = code is null ? null : await db.BlockedCountries.FindAsync([code], ct);
        if (row is null)
            return TypedResults.NotFound(ApiErrorResponse.Of("not_found", $"{countryCode} 不在屏蔽列表中"));

        var now = time.GetUtcNow();
        db.BlockedCountries.Remove(row);
        db.AddAudit(now, AdminActor.From(http), "geo.unblock", "blocked_country", row.CountryCode.Trim(), new { previous_note = row.Note });
        await db.SaveChangesAsync(ct);
        store.Invalidate();
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<AuditLogEntryDto>>> ListAudit(
        ReelGuoDbContext db,
        [FromQuery(Name = "target_type")] string? targetType,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var query = db.AuditLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(targetType)) query = query.Where(a => a.TargetType == targetType);
        var rows = await query.OrderByDescending(a => a.Id).Take(take)
            .Select(a => new AuditLogEntryDto(a.Id, a.At, a.Actor, a.Action, a.TargetType, a.TargetId, a.DetailsJson))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

}

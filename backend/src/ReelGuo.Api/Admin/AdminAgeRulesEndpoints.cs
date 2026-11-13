using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Geo;

namespace ReelGuo.Api.Admin;

public sealed record AgeRuleDto(string CountryCode, AgeCheckMode Mode, string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string UpdatedBy)
{
    public static AgeRuleDto From(AgeRule r) => new(r.CountryCode.Trim(), r.Mode, r.Note, r.CreatedAt, r.UpdatedAt, r.UpdatedBy);
}

public sealed record UpsertAgeRuleRequest(AgeCheckMode Mode, string? Note);

public sealed record SetAgeVerifiedRequest(bool Verified, string? Note);

/// <summary>年龄验证: which countries require verified age (set after legal review), and manual verification of members.</summary>
public static class AdminAgeRulesEndpoints
{
    private const int MaxNoteLength = 500;

    public static RouteGroupBuilder MapAdminAgeRules(this RouteGroupBuilder admin)
    {
        var rules = admin.MapGroup("/geo/age-rules").WithTags("Admin: age verification");
        rules.MapGet("/", List).WithSummary("Countries with an age-verification rule. Others use the 18+ confirmation.");
        rules.MapPut("/{countryCode}", Upsert).WithSummary("Sets a country's rule (strict = verified age required).");
        rules.MapDelete("/{countryCode}", Remove).WithSummary("Removes a rule; the country goes back to the 18+ confirmation.");

        admin.MapPost("/members/{id:long}/age-verification", SetVerified).WithTags("Admin: members")
            .WithSummary("Marks a member's age as verified (or withdraws it), e.g. after checking ID by hand.");
        return admin;
    }

    private static async Task<Ok<List<AgeRuleDto>>> List(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.AgeRules.AsNoTracking().OrderBy(r => r.CountryCode).ToListAsync(ct)).Select(AgeRuleDto.From).ToList());

    private static async Task<Results<Ok<AgeRuleDto>, BadRequest<ApiErrorResponse>>> Upsert(
        string countryCode, UpsertAgeRuleRequest request, HttpContext http, ReelGuoDbContext db, IAgeRuleStore store, TimeProvider time, CancellationToken ct)
    {
        var code = CountryCodes.Normalize(countryCode);
        if (code is null) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_country_code", $"无效的国家/地区代码：{countryCode}"));
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength }) return TypedResults.BadRequest(ApiErrorResponse.Of("note_too_long", $"备注不能超过 {MaxNoteLength} 个字符"));

        var actor = AdminActor.From(http);
        var now = time.GetUtcNow();
        var row = await db.AgeRules.FindAsync([code], ct);
        object details;
        if (row is null)
        {
            row = new AgeRule { CountryCode = code, Mode = request.Mode, Note = note, CreatedAt = now, UpdatedAt = now, UpdatedBy = actor };
            db.AgeRules.Add(row);
            details = new { mode = request.Mode, note };
        }
        else
        {
            details = new { previous_mode = row.Mode, mode = request.Mode, previous_note = row.Note, note };
            row.Mode = request.Mode;
            row.Note = note;
            row.UpdatedAt = now;
            row.UpdatedBy = actor;
        }
        db.AddAudit(now, actor, "age.rule.set", "age_rule", code, details);
        await db.SaveChangesAsync(ct);
        store.Invalidate();
        return TypedResults.Ok(AgeRuleDto.From(row));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> Remove(
        string countryCode, HttpContext http, ReelGuoDbContext db, IAgeRuleStore store, TimeProvider time, CancellationToken ct)
    {
        var code = CountryCodes.Normalize(countryCode);
        var row = code is null ? null : await db.AgeRules.FindAsync([code], ct);
        if (row is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", $"{countryCode} 没有年龄验证规则"));
        db.AgeRules.Remove(row);
        db.AddAudit(time, AdminActor.From(http), "age.rule.remove", "age_rule", row.CountryCode.Trim(), new { previous_mode = row.Mode, previous_note = row.Note });
        await db.SaveChangesAsync(ct);
        store.Invalidate();
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>>> SetVerified(
        long id, SetAgeVerifiedRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.User, ct);
        if (user is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "会员不存在"));
        var now = time.GetUtcNow();
        user.AgeVerifiedAt = request.Verified ? user.AgeVerifiedAt ?? now : null;
        user.UpdatedAt = now;
        db.AddAudit(now, AdminActor.From(http), request.Verified ? "member.age_verify" : "member.age_unverify", "user", user.Username,
            new { id, note = (request.Note ?? "").Trim() });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}

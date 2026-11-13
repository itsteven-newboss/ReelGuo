using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Public;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Media;

namespace ReelGuo.Api.Users;

public sealed record RegisterRequest(string Username, string Password, string? Nickname, bool AcceptTerms);

public sealed record LoginRequest(string Username, string Password);

public sealed record MeUserDto(long Id, string Username, string Nickname, string AvatarColor, string? Sign, DateTimeOffset CreatedAt, bool AgeVerified);

public sealed record AuthResponse(string AccessToken, int ExpiresIn, MeUserDto User);

public sealed record MeCountsDto(int Likes, int Collects, int Following, int History);

/// <summary>Same shape as the reference client's User.interactions, plus post interactions.</summary>
public sealed record MeInteractionsDto(List<long> LikeVideoIds, List<long> FavoriteVideoIds, List<long> FollowUserIds, List<long> LikePostIds, List<long> FavoritePostIds);

public sealed record MeDto(MeUserDto User, MeCountsDto Counts, MeInteractionsDto Interactions);

public sealed record UpdateProfileRequest(string? Nickname, string? Sign);

/// <summary>Device watch-progress entry; <c>at</c> is Unix milliseconds (as the app stores it).</summary>
public sealed record HistoryItemDto(long Id, int Ep, double T, double Dur, long At);

public sealed record HistoryPutRequest(List<HistoryItemDto> Items);

public sealed record MergeRequest(List<long>? Likes, List<long>? Favs, List<HistoryItemDto>? History);

public static partial class AccountEndpoints
{
    private static readonly string[] AvatarColors = ["#7c5cff", "#ff6f8c", "#2bb5a0", "#f0a43a", "#4a90e2", "#c45ad6", "#5ab552", "#e2675a"];
    public const int MaxHistory = 80;

    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder v1)
    {
        var auth = v1.MapGroup("/auth").WithTags("Account").AddEndpointFilter<AppHeaderFilter>();
        auth.MapPost("/register", Register).RequireRateLimiting(RateLimits.Auth).WithSummary("Creates a viewer account (18+ confirmation required) and signs in.");
        auth.MapPost("/login", Login).RequireRateLimiting(RateLimits.Auth).WithSummary("Signs in; sets the refresh cookie.");
        auth.MapPost("/refresh", Refresh).RequireRateLimiting(RateLimits.Refresh).WithSummary("Rotates the refresh cookie and returns a new access token.");
        auth.MapPost("/logout", Logout).WithSummary("Signs out this device (revokes its refresh tokens).");

        var me = v1.MapGroup("/me").WithTags("Account").RequireAuthorization(UserAuth.Policy);
        me.MapGet("/", Me).WithSummary("The signed-in viewer with counts and interaction ids.");
        me.MapPut("/", UpdateProfile).WithSummary("Updates nickname and signature.");
        me.MapGet("/history", GetHistory).WithSummary("Watch progress, newest first.");
        me.MapPut("/history", PutHistory).WithSummary("Upserts watch progress; the newer entry wins.");
        me.MapDelete("/history", async (HttpContext http, ReelGuoDbContext db, CancellationToken ct) =>
        {
            var uid = UserAuth.UserId(http);
            await db.WatchHistory.Where(h => h.UserId == uid).ExecuteDeleteAsync(ct);
            return TypedResults.NoContent();
        }).WithSummary("Clears the viewer's watch history (清空观看历史).");
        me.MapPost("/merge", Merge).WithSummary("Adds the device's likes, favourites and history to the account (first sign-in on a device).");
        me.MapGet("/favorites", Favorites).WithSummary("Favourited series the viewer may watch.");
        return v1;
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,20}$")]
    private static partial Regex UsernamePattern();

    internal static MeUserDto ToDto(User u) => new(u.Id, u.Username, u.Nickname, u.AvatarColor, u.Sign, u.CreatedAt, u.AgeVerifiedAt != null);

    private static async Task<Results<Ok<AuthResponse>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Register(
        RegisterRequest request, HttpContext http, ReelGuoDbContext db, IPasswordHasher<User> hasher, UserTokenService tokens, TimeProvider time, CancellationToken ct)
    {
        var username = (request.Username ?? "").Trim();
        if (!UsernamePattern().IsMatch(username))
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_username", "账号需为 3-20 位字母、数字或下划线"));
        if ((request.Password ?? "").Length is < 8 or > 128)
            return TypedResults.BadRequest(ApiErrorResponse.Of("weak_password", "密码需为 8-128 位"));
        if (!request.AcceptTerms)
            return TypedResults.BadRequest(ApiErrorResponse.Of("terms_required", "请确认已年满 18 周岁并同意用户协议"));
        var nickname = string.IsNullOrWhiteSpace(request.Nickname) ? username : request.Nickname.Trim();
        if (nickname.Length > 20) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_nickname", "昵称不能超过 20 个字符"));

        var normalized = username.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct))
            return TypedResults.Conflict(ApiErrorResponse.Of("username_taken", "账号已被注册"));

        var now = time.GetUtcNow();
        var user = new User
        {
            Username = username,
            NormalizedUsername = normalized,
            Nickname = nickname,
            PasswordHash = "",
            AvatarColor = AvatarColors[Random.Shared.Next(AvatarColors.Length)],
            Role = UserRole.User,
            Status = UserStatus.Active,
            TermsAcceptedAt = now,
            LastLoginAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password!);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        var (access, expiresIn) = tokens.CreateAccessToken(user);
        var (refresh, raw) = tokens.NewRefreshToken(user.Id, Guid.NewGuid(), http.Request.Headers.UserAgent.ToString());
        await db.SaveChangesAsync(ct);
        UserTokenService.SetCookie(http, raw, refresh.ExpiresAt);
        return TypedResults.Ok(new AuthResponse(access, expiresIn, ToDto(user)));
    }

    private static async Task<Results<Ok<AuthResponse>, JsonHttpResult<ApiErrorResponse>>> Login(
        LoginRequest request, HttpContext http, ReelGuoDbContext db, IPasswordHasher<User> hasher, UserTokenService tokens, TimeProvider time, CancellationToken ct)
    {
        var normalized = (request.Username ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        var verdict = user is { Role: UserRole.User, Status: UserStatus.Active }
            ? hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "")
            : PasswordVerificationResult.Failed;
        if (verdict == PasswordVerificationResult.Failed)
            return TypedResults.Json(ApiErrorResponse.Of("invalid_credentials", "账号或密码错误"), statusCode: StatusCodes.Status401Unauthorized);

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded) user!.PasswordHash = hasher.HashPassword(user, request.Password!);
        user!.LastLoginAt = time.GetUtcNow();
        var (access, expiresIn) = tokens.CreateAccessToken(user);
        var (refresh, raw) = tokens.NewRefreshToken(user.Id, Guid.NewGuid(), http.Request.Headers.UserAgent.ToString());
        await db.SaveChangesAsync(ct);
        UserTokenService.SetCookie(http, raw, refresh.ExpiresAt);
        return TypedResults.Ok(new AuthResponse(access, expiresIn, ToDto(user)));
    }

    private static async Task<Results<Ok<AuthResponse>, JsonHttpResult<ApiErrorResponse>>> Refresh(
        HttpContext http, UserTokenService tokens, CancellationToken ct)
    {
        var raw = http.Request.Cookies[UserAuth.RefreshCookie];
        var rotated = string.IsNullOrEmpty(raw) ? null : await tokens.RotateAsync(raw, http.Request.Headers.UserAgent.ToString(), ct);
        if (rotated is not { } r)
        {
            UserTokenService.ClearCookie(http);
            return TypedResults.Json(ApiErrorResponse.Of("session_expired", "登录已过期，请重新登录"), statusCode: StatusCodes.Status401Unauthorized);
        }
        UserTokenService.SetCookie(http, r.Raw, r.ExpiresAt);
        var (access, expiresIn) = tokens.CreateAccessToken(r.User);
        return TypedResults.Ok(new AuthResponse(access, expiresIn, ToDto(r.User)));
    }

    private static async Task<NoContent> Logout(HttpContext http, UserTokenService tokens, CancellationToken ct)
    {
        var raw = http.Request.Cookies[UserAuth.RefreshCookie];
        if (!string.IsNullOrEmpty(raw)) await tokens.RevokeByRawAsync(raw, ct);
        UserTokenService.ClearCookie(http);
        return TypedResults.NoContent();
    }

    internal static async Task<MeDto> LoadMeAsync(ReelGuoDbContext db, long userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        var likes = await db.SeriesLikes.Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).Select(x => x.SeriesId).ToListAsync(ct);
        var favs = await db.SeriesCollects.Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).Select(x => x.SeriesId).ToListAsync(ct);
        var follows = await db.Follows.Where(x => x.FollowerId == userId).Select(x => x.FolloweeId).ToListAsync(ct);
        var postLikes = await db.PostLikes.Where(x => x.UserId == userId).Select(x => x.PostId).ToListAsync(ct);
        var postFavs = await db.PostCollects.Where(x => x.UserId == userId).Select(x => x.PostId).ToListAsync(ct);
        var history = await db.WatchHistory.CountAsync(x => x.UserId == userId, ct);
        return new MeDto(ToDto(user), new MeCountsDto(likes.Count, favs.Count, follows.Count, history),
            new MeInteractionsDto(likes, favs, follows, postLikes, postFavs));
    }

    private static async Task<Ok<MeDto>> Me(HttpContext http, ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await LoadMeAsync(db, UserAuth.UserId(http), ct));

    private static async Task<Results<Ok<MeUserDto>, BadRequest<ApiErrorResponse>>> UpdateProfile(
        UpdateProfileRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var user = await db.Users.FirstAsync(u => u.Id == UserAuth.UserId(http), ct);
        if (request.Nickname is { } nick)
        {
            nick = nick.Trim();
            if (nick.Length is 0 or > 20) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_nickname", "昵称需为 1-20 个字符"));
            user.Nickname = nick;
        }
        if (request.Sign is { } sign)
        {
            sign = sign.Trim();
            if (sign.Length > 60) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_sign", "签名不能超过 60 个字符"));
            user.Sign = sign.Length == 0 ? null : sign;
        }
        user.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(user));
    }

    private static async Task<Ok<List<HistoryItemDto>>> GetHistory(HttpContext http, ReelGuoDbContext db, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var rows = await db.WatchHistory.AsNoTracking().Where(h => h.UserId == uid).OrderByDescending(h => h.UpdatedAt).Take(MaxHistory).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(h => new HistoryItemDto(h.SeriesId, h.EpisodeNumber, h.PositionSeconds, h.DurationSeconds, h.UpdatedAt.ToUnixTimeMilliseconds())).ToList());
    }

    private static async Task<Results<NoContent, BadRequest<ApiErrorResponse>>> PutHistory(
        HistoryPutRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (request.Items is not { Count: <= 100 }) return TypedResults.BadRequest(ApiErrorResponse.Of("too_many_items", "每次最多 100 条"));
        await UpsertHistoryAsync(db, UserAuth.UserId(http), request.Items, time, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Newest entry per series wins; unknown series and future timestamps are ignored.</summary>
    private static async Task UpsertHistoryAsync(ReelGuoDbContext db, long uid, IEnumerable<HistoryItemDto> items, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var latest = items
            .Where(i => i.Ep > 0 && i.T >= 0 && double.IsFinite(i.T) && double.IsFinite(i.Dur))
            .GroupBy(i => i.Id).Select(g => g.MaxBy(i => i.At)!).ToList();
        if (latest.Count == 0) return;

        var ids = latest.Select(i => i.Id).ToList();
        var existingSeries = (await db.Series.Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var current = await db.WatchHistory.Where(h => h.UserId == uid && ids.Contains(h.SeriesId)).ToDictionaryAsync(h => h.SeriesId, ct);

        foreach (var item in latest.Where(i => existingSeries.Contains(i.Id)))
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(item.At);
            if (at > now.AddMinutes(5)) at = now;
            if (current.TryGetValue(item.Id, out var row))
            {
                if (row.UpdatedAt >= at) continue;
                row.EpisodeNumber = item.Ep;
                row.PositionSeconds = item.T;
                row.DurationSeconds = item.Dur;
                row.UpdatedAt = at;
            }
            else
            {
                db.WatchHistory.Add(new WatchHistoryEntry
                {
                    UserId = uid, SeriesId = item.Id, EpisodeNumber = item.Ep, PositionSeconds = item.T, DurationSeconds = item.Dur, UpdatedAt = at,
                });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Ok<MeDto>> Merge(MergeRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        var likes = (request.Likes ?? []).Distinct().Take(500).ToArray();
        var favs = (request.Favs ?? []).Distinct().Take(500).ToArray();

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Insert only series that exist; then recount the affected counters.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO series_likes (user_id, series_id, created_at)
                SELECT {uid}, s.id, {now} FROM series s WHERE s.id = ANY({likes}) ON CONFLICT DO NOTHING
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO series_collects (user_id, series_id, created_at)
                SELECT {uid}, s.id, {now} FROM series s WHERE s.id = ANY({favs}) ON CONFLICT DO NOTHING
                """, ct);
            var touched = likes.Concat(favs).Distinct().ToArray();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE series s SET
                    like_count = (SELECT count(*) FROM series_likes l WHERE l.series_id = s.id),
                    collect_count = (SELECT count(*) FROM series_collects c WHERE c.series_id = s.id)
                 WHERE s.id = ANY({touched})
                """, ct);
            await tx.CommitAsync(ct);
        }

        if (request.History is { Count: > 0 } history)
            await UpsertHistoryAsync(db, uid, history.Take(100), time, ct);
        return TypedResults.Ok(await LoadMeAsync(db, uid, ct));
    }

    private static async Task<Ok<VideoCardPage>> Favorites(
        HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, MediaUrlSigner signer, TimeProvider time,
        [FromQuery] int? page, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var query = db.Series.AsNoTracking().VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time))
            .Where(s => db.SeriesCollects.Any(c => c.UserId == uid && c.SeriesId == s.Id))
            .OrderByDescending(s => db.SeriesCollects.Where(c => c.UserId == uid && c.SeriesId == s.Id).Select(c => c.CreatedAt).First());
        return TypedResults.Ok(await PublicEndpoints.PageAsync(query, signer, page, 20, ct));
    }
}

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Users;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Base64 HMAC key (32+ bytes) for access tokens. Required.</summary>
    public string JwtKey { get; set; } = "";
    public string Issuer { get; set; } = "reelguo";
    public int AccessMinutes { get; set; } = 15;
    public int RefreshDays { get; set; } = 30;
}

/// <summary>
/// Viewer accounts. Short-lived JWT access tokens (sent as a Bearer header, kept in memory by the app)
/// plus a rotating refresh token in an httpOnly cookie scoped to /api/v1/auth.
/// </summary>
public static class UserAuth
{
    public const string Scheme = "user";
    public const string Policy = "user";
    public const string Role = "User";

    /// <summary>Required on the cookie-authenticated /auth endpoints (CSRF protection, like the admin header).</summary>
    public const string AppHeader = "X-ReelGuo-App";
    public const string RefreshCookie = "rg_refresh";
    public const string RefreshCookiePath = "/api/v1/auth";

    public static IServiceCollection AddUserAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddAuthentication().AddJwtBearer(Scheme, _ => { });
        services.AddOptions<JwtBearerOptions>(Scheme).Configure<IOptions<AuthOptions>>((o, auth) =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = auth.Value.Issuer,
                ValidateAudience = false,
                IssuerSigningKey = SigningKey(auth.Value),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "name",
                RoleClaimType = "role",
            };
            o.Events = new JwtBearerEvents
            {
                // A disabled account stops working immediately, not when its token expires.
                OnTokenValidated = async ctx =>
                {
                    var db = ctx.HttpContext.RequestServices.GetRequiredService<ReelGuoDbContext>();
                    var active = long.TryParse(ctx.Principal?.FindFirstValue("sub"), out var id) && await db.Users.AnyAsync(
                        u => u.Id == id && u.Role == UserRole.User && u.Status == UserStatus.Active, ctx.HttpContext.RequestAborted);
                    if (!active) ctx.Fail("account unavailable");
                },
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(ApiErrorResponse.Of("unauthorized", "请先登录"));
                },
                OnForbidden = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return ctx.Response.WriteAsJsonAsync(ApiErrorResponse.Of("forbidden", "没有权限"));
                },
            };
        });

        services.AddAuthorizationBuilder().AddPolicy(Policy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireRole(Role));
        services.AddScoped<UserTokenService>();
        return services;
    }

    internal static SymmetricSecurityKey SigningKey(AuthOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.JwtKey)) throw new InvalidOperationException("Auth:JwtKey is not set.");
        var bytes = Convert.FromBase64String(o.JwtKey);
        if (bytes.Length < 32) throw new InvalidOperationException("Auth:JwtKey must be at least 32 bytes (base64).");
        return new SymmetricSecurityKey(bytes);
    }

    /// <summary>The signed-in viewer on endpoints that require the user policy.</summary>
    public static long UserId(HttpContext http) => long.Parse(http.User.FindFirstValue("sub")!);

    /// <summary>The viewer if a valid token was sent, on endpoints that also work signed out.</summary>
    public static async Task<long?> OptionalUserIdAsync(HttpContext http)
    {
        if (!http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var result = await http.AuthenticateAsync(Scheme);
        return result.Succeeded && long.TryParse(result.Principal.FindFirstValue("sub"), out var id) ? id : null;
    }
}

/// <summary>Rejects cookie-authenticated requests without the app header (CSRF).</summary>
public sealed class AppHeaderFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Request.Headers.ContainsKey(UserAuth.AppHeader)
            ? await next(context)
            : ApiResults.Error(StatusCodes.Status403Forbidden, "app_header_missing", $"缺少请求头 {UserAuth.AppHeader}");
}

public sealed class UserTokenService(ReelGuoDbContext db, IOptions<AuthOptions> options, TimeProvider time)
{
    private AuthOptions O => options.Value;

    public (string Token, int ExpiresIn) CreateAccessToken(User user)
    {
        var expires = time.GetUtcNow().AddMinutes(Math.Max(1, O.AccessMinutes));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = O.Issuer,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim("name", user.Username),
                new Claim("role", UserAuth.Role),
            ]),
            IssuedAt = time.GetUtcNow().UtcDateTime,
            NotBefore = time.GetUtcNow().UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(UserAuth.SigningKey(O), SecurityAlgorithms.HmacSha256),
        });
        return (token, (int)(expires - time.GetUtcNow()).TotalSeconds);
    }

    private static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>Creates a refresh token (a new family on sign-in). Caller saves changes.</summary>
    public (RefreshToken Entity, string Raw) NewRefreshToken(long userId, Guid familyId, string? userAgent)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Max(1, O.RefreshDays)),
            UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent,
        };
        db.RefreshTokens.Add(entity);
        return (entity, raw);
    }

    /// <summary>
    /// Exchanges a refresh token for a new one. Returns null when it is unknown, expired, or the account is
    /// unavailable. A token that was already rotated means it leaked: the whole family is revoked.
    /// </summary>
    public async Task<(User User, string Raw, DateTimeOffset ExpiresAt)?> RotateAsync(string raw, string? userAgent, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var token = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == Hash(raw), ct);
        if (token is null) return null;
        if (token.RevokedAt is not null)
        {
            await RevokeFamilyAsync(token.FamilyId, ct);
            return null;
        }
        if (token.ExpiresAt <= now || token.User.Status != UserStatus.Active || token.User.Role != UserRole.User) return null;

        var (next, nextRaw) = NewRefreshToken(token.UserId, token.FamilyId, userAgent);
        token.RevokedAt = now;
        token.ReplacedById = next.Id;
        await db.SaveChangesAsync(ct);
        return (token.User, nextRaw, next.ExpiresAt);
    }

    public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, time.GetUtcNow()), ct);

    public async Task RevokeByRawAsync(string raw, CancellationToken ct)
    {
        var family = await db.RefreshTokens.Where(t => t.TokenHash == Hash(raw)).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (family is { } f) await RevokeFamilyAsync(f, ct);
    }

    public Task RevokeAllForUserAsync(long userId, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, time.GetUtcNow()), ct);

    public static void SetCookie(HttpContext http, string raw, DateTimeOffset expires) =>
        http.Response.Cookies.Append(UserAuth.RefreshCookie, raw, new CookieOptions
        {
            HttpOnly = true,
            Secure = Network.SecureCookies(http),
            SameSite = SameSiteMode.Strict,
            Path = UserAuth.RefreshCookiePath,
            Expires = expires,
            IsEssential = true,
        });

    public static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(UserAuth.RefreshCookie, new CookieOptions { Path = UserAuth.RefreshCookiePath, SameSite = SameSiteMode.Strict });
}

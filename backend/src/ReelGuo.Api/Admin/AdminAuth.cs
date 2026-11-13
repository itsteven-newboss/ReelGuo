using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Admin;

public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>
    /// Creates the first admin account on startup when no admin exists yet. Set once for a new
    /// environment, sign in, then remove the password from configuration.
    /// </summary>
    public string? BootstrapUsername { get; set; }
    public string? BootstrapPassword { get; set; }
}

public static class AdminAuth
{
    public const string Scheme = "admin";
    public const string Policy = "admin";

    /// <summary>
    /// Every state-changing admin request must carry this header. Browsers cannot add custom headers
    /// to cross-site requests without CORS (which the API does not enable), so this blocks CSRF.
    /// </summary>
    public const string CsrfHeader = "X-ReelGuo-Admin";

    public const int MinPasswordLength = 10;

    public static IServiceCollection AddAdminAuth(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "rg_admin";
            o.Cookie.Path = "/api/admin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => WriteError(ctx.Response, StatusCodes.Status401Unauthorized, "unauthorized", "请先登录后台");
            o.Events.OnRedirectToAccessDenied = ctx => WriteError(ctx.Response, StatusCodes.Status403Forbidden, "forbidden", "没有后台权限");
            o.Events.OnValidatePrincipal = EnsureStillActiveAdmin;
        });

        // Secure outside development: behind a TLS-terminating proxy the request itself looks like plain HTTP.
        services.AddOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(Scheme)
            .Configure<IHostEnvironment>((o, env) => o.Cookie.SecurePolicy = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);

        services.AddAuthorizationBuilder().AddPolicy(Policy, p => p
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireRole(nameof(UserRole.Admin)));

        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        return services;
    }

    public static ClaimsPrincipal PrincipalFor(User user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim("nickname", user.Nickname),
        new Claim(ClaimTypes.Role, nameof(UserRole.Admin)),
    ], Scheme));

    /// <summary>Disabling an admin (or removing the role) ends their existing sessions on the next request.</summary>
    private static async Task EnsureStillActiveAdmin(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var db = ctx.HttpContext.RequestServices.GetRequiredService<ReelGuoDbContext>();
        var ok = long.TryParse(id, out var userId) && await db.Users.AnyAsync(u =>
            u.Id == userId && u.Role == UserRole.Admin && u.Status == UserStatus.Active, ctx.HttpContext.RequestAborted);
        if (!ok)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(Scheme);
        }
    }

    internal static Task WriteError(HttpResponse response, int status, string code, string message)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(ApiErrorResponse.Of(code, message));
    }

    /// <summary>Creates the bootstrap admin when configured and no admin exists.</summary>
    public static async Task EnsureBootstrapAdminAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var options = services.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.BootstrapUsername) || string.IsNullOrWhiteSpace(options.BootstrapPassword)) return;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        if (await db.Users.AnyAsync(u => u.Role == UserRole.Admin, cancellationToken)) return;

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var username = options.BootstrapUsername.Trim();
        var user = new User
        {
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            Nickname = username,
            PasswordHash = "",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = time,
            UpdatedAt = time,
        };
        user.PasswordHash = hasher.HashPassword(user, options.BootstrapPassword);
        db.Users.Add(user);
        db.AuditLog.Add(new AuditLogEntry
        {
            At = time, Actor = "system", Action = "admin.bootstrap", TargetType = "user", TargetId = username,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Rejects state-changing admin requests that lack the CSRF header.</summary>
public sealed class AdminCsrfFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !request.Headers.ContainsKey(AdminAuth.CsrfHeader))
        {
            return ApiResults.Error(StatusCodes.Status403Forbidden, "csrf_header_missing", $"缺少请求头 {AdminAuth.CsrfHeader}");
        }
        return await next(context);
    }
}

public static class AdminActor
{
    /// <summary>The audit-log actor for the signed-in admin, e.g. "admin:zhangsan".</summary>
    public static string From(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true ? $"admin:{context.User.Identity.Name}" : "anonymous";

    public static long? UserId(HttpContext context) =>
        long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

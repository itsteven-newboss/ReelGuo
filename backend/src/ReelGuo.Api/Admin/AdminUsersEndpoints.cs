using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Admin;

public sealed record AdminUserDto(long Id, string Username, string Nickname, UserStatus Status, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt);

public sealed record CreateAdminRequest(string Username, string Nickname, string Password);

public sealed record SetAdminStatusRequest(bool Active);

public sealed record SetPasswordRequest(string Password);

public static partial class AdminUsersEndpoints
{
    public static RouteGroupBuilder MapAdminUsers(this RouteGroupBuilder admin)
    {
        var users = admin.MapGroup("/users").WithTags("Admin: accounts");
        users.MapGet("/", List).WithSummary("All admin accounts.");
        users.MapPost("/", Create).WithSummary("Creates an admin account.");
        users.MapPost("/{id:long}/status", SetStatus).WithSummary("Enables or disables an admin account. Disabling ends its sessions.");
        users.MapPost("/{id:long}/password", SetPassword).WithSummary("Sets a new password for an admin account.");
        return admin;
    }

    private static async Task<Ok<List<AdminUserDto>>> List(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Admin)
            .OrderBy(u => u.Id)
            .Select(u => new AdminUserDto(u.Id, u.Username, u.Nickname, u.Status, u.LastLoginAt, u.CreatedAt))
            .ToListAsync(ct));

    private static async Task<Results<Ok<AdminUserDto>, BadRequest<ApiErrorResponse>, Conflict<ApiErrorResponse>>> Create(
        CreateAdminRequest request, HttpContext http, ReelGuoDbContext db, IPasswordHasher<User> hasher, TimeProvider time, CancellationToken ct)
    {
        var username = (request.Username ?? "").Trim();
        if (!UsernamePattern().IsMatch(username))
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_username", "用户名需为 3-32 位字母、数字、下划线、点或短横线"));
        if ((request.Password ?? "").Length < AdminAuth.MinPasswordLength)
            return TypedResults.BadRequest(ApiErrorResponse.Of("weak_password", $"密码至少 {AdminAuth.MinPasswordLength} 位"));
        var nickname = string.IsNullOrWhiteSpace(request.Nickname) ? username : request.Nickname.Trim();
        if (nickname.Length > 64)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_nickname", "显示名称不能超过 64 个字符"));

        var normalized = username.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalized, ct))
            return TypedResults.Conflict(ApiErrorResponse.Of("username_taken", "用户名已存在"));

        var now = time.GetUtcNow();
        var user = new User
        {
            Username = username,
            NormalizedUsername = normalized,
            Nickname = nickname,
            PasswordHash = "",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password!);
        db.Users.Add(user);
        db.AddAudit(now, AdminActor.From(http), "admin.create", "user", username, new { nickname });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new AdminUserDto(user.Id, user.Username, user.Nickname, user.Status, null, user.CreatedAt));
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> SetStatus(
        long id, SetAdminStatusRequest request, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.Admin, ct);
        if (user is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "账号不存在"));
        if (!request.Active)
        {
            if (AdminActor.UserId(http) == id)
                return TypedResults.BadRequest(ApiErrorResponse.Of("cannot_disable_self", "不能停用自己的账号"));
            var otherActive = await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.Status == UserStatus.Active && u.Id != id, ct);
            if (otherActive == 0)
                return TypedResults.BadRequest(ApiErrorResponse.Of("last_admin", "至少需要保留一个启用的管理员"));
        }

        user.Status = request.Active ? UserStatus.Active : UserStatus.Disabled;
        user.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), request.Active ? "admin.enable" : "admin.disable", "user", user.Username);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> SetPassword(
        long id, SetPasswordRequest request, HttpContext http, ReelGuoDbContext db, IPasswordHasher<User> hasher, TimeProvider time, CancellationToken ct)
    {
        if ((request.Password ?? "").Length < AdminAuth.MinPasswordLength)
            return TypedResults.BadRequest(ApiErrorResponse.Of("weak_password", $"密码至少 {AdminAuth.MinPasswordLength} 位"));
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.Role == UserRole.Admin, ct);
        if (user is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "账号不存在"));

        user.PasswordHash = hasher.HashPassword(user, request.Password!);
        user.UpdatedAt = time.GetUtcNow();
        db.AddAudit(time, AdminActor.From(http), "admin.password", "user", user.Username);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UsernamePattern();
}

using System.Text.Json;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Infrastructure;

public static class Audit
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Adds an append-only audit entry; saved with the caller's SaveChanges so it commits with the change itself.</summary>
    public static void AddAudit(this ReelGuoDbContext db, TimeProvider time, string actor, string action, string targetType, string targetId, object? details = null) =>
        db.AddAudit(time.GetUtcNow(), actor, action, targetType, targetId, details);

    public static void AddAudit(this ReelGuoDbContext db, DateTimeOffset at, string actor, string action, string targetType, string targetId, object? details = null) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            At = at,
            Actor = actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, Json),
        });
}

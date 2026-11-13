using Microsoft.EntityFrameworkCore;
using ReelGuo.Data.Entities;

namespace ReelGuo.Data.Catalog;

public static class SeriesStats
{
    /// <summary>Recomputes the ready-episode count and latest-episode time after episodes change.</summary>
    public static async Task RefreshAsync(ReelGuoDbContext db, long seriesId, CancellationToken cancellationToken)
    {
        var ready = await db.Episodes
            .Where(e => e.SeriesId == seriesId && e.Status == EpisodeStatus.Ready)
            .Select(e => e.UpdatedAt)
            .ToListAsync(cancellationToken);
        DateTimeOffset? last = ready.Count == 0 ? null : ready.Max();
        await db.Series.Where(s => s.Id == seriesId).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.EpisodeCount, ready.Count)
            .SetProperty(s => s.LastEpisodeAt, last), cancellationToken);
    }

    /// <summary>Finds the creator profile with this display name, creating one (with no login) if needed.</summary>
    public static async Task<User> FindOrCreateAuthorAsync(ReelGuoDbContext db, string name, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Role == UserRole.Creator && u.Nickname == trimmed, cancellationToken);
        if (existing is not null) return existing;

        var handle = $"creator_{Guid.NewGuid():N}"[..20];
        var author = new User
        {
            Username = handle,
            NormalizedUsername = handle,
            Nickname = trimmed.Length > 64 ? trimmed[..64] : trimmed,
            PasswordHash = "!", // creator profiles cannot sign in
            Role = UserRole.Creator,
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Users.Add(author);
        return author;
    }
}

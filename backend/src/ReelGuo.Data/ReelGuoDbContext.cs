using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ReelGuo.Data.Entities;

namespace ReelGuo.Data;

public sealed class ReelGuoDbContext(DbContextOptions<ReelGuoDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<BlockedCountry> BlockedCountries => Set<BlockedCountry>();
    public DbSet<AgeRule> AgeRules => Set<AgeRule>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<PlaybackSample> PlaybackSamples => Set<PlaybackSample>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<MediaJob> MediaJobs => Set<MediaJob>();
    public DbSet<PlayEvent> PlayEvents => Set<PlayEvent>();
    public DbSet<RankEntry> Ranks => Set<RankEntry>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SeriesLike> SeriesLikes => Set<SeriesLike>();
    public DbSet<SeriesCollect> SeriesCollects => Set<SeriesCollect>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<PostLike> PostLikes => Set<PostLike>();
    public DbSet<PostCollect> PostCollects => Set<PostCollect>();
    public DbSet<WatchHistoryEntry> WatchHistory => Set<WatchHistoryEntry>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CommentLike> CommentLikes => Set<CommentLike>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ModerationTerm> ModerationTerms => Set<ModerationTerm>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();
    public DbSet<EpisodeUnlock> EpisodeUnlocks => Set<EpisodeUnlock>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<AppRelease> AppReleases => Set<AppRelease>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<TopicSeries> TopicSeries => Set<TopicSeries>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<SeriesRating> SeriesRatings => Set<SeriesRating>();

    /// <summary>Shared Npgsql + snake_case setup for the API, the worker, tests and the EF tools.</summary>
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Store enums as readable text rather than integers.
        builder.Properties<CategoryStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<SeriesStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<EpisodeStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<UserStatus>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<MediaJobKind>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<MediaJobState>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<PlayEventKind>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<RankKind>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<PostStatus>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<CommentTarget>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<AgeCheckMode>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<PlaybackDevice>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<CommentStatus>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<ReportTarget>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<ReportReason>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<ReportStatus>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<ProductKind>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<OrderStatus>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<WalletEntryKind>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<PaymentEventOutcome>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Trigram index for substring search on titles (works for Chinese too).
        model.HasPostgresExtension("pg_trgm");

        model.Entity<User>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(32);
            e.Property(x => x.NormalizedUsername).HasMaxLength(32);
            e.HasIndex(x => x.NormalizedUsername).IsUnique();
            e.Property(x => x.Nickname).HasMaxLength(64);
            e.Property(x => x.AvatarColor).HasMaxLength(16);
            e.Property(x => x.Sign).HasMaxLength(200);
        });

        model.Entity<Category>(e =>
        {
            e.Property(x => x.Slug).HasMaxLength(64);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(200);
            e.HasData(SeedData.Categories);
        });

        model.Entity<Series>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.ExternalId).HasMaxLength(128);
            e.HasIndex(x => x.ExternalId).IsUnique();
            e.HasIndex(x => new { x.CategoryId, x.Status, x.PublishedAt });
            e.HasIndex(x => new { x.Status, x.HotScore });
            e.HasIndex(x => x.Territories).HasMethod("gin");
            e.HasIndex(x => x.Title).HasMethod("gin").HasOperators("gin_trgm_ops").HasDatabaseName("ix_series_title_trgm");
            e.Property(x => x.RightsHolder).HasMaxLength(200);
            e.Property(x => x.ContractRef).HasMaxLength(200);
            e.Property(x => x.ConsentRef).HasMaxLength(200);
            e.HasOne(x => x.Category).WithMany(c => c.Series).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.SeriesId, x.EpisodeNumber }).IsUnique();
            e.Property(x => x.RenditionsJson).HasColumnType("jsonb");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.ProcessingError).HasMaxLength(2000);
            e.HasOne(x => x.Series).WithMany(s => s.Episodes).HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MediaJob>(e =>
        {
            e.HasIndex(x => new { x.State, x.RunAfter });
            e.HasIndex(x => x.EpisodeId);
            e.Property(x => x.LockedBy).HasMaxLength(128);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PlayEvent>(e =>
        {
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.SeriesId, x.At });
        });

        model.Entity<RankEntry>(e =>
        {
            e.ToTable("ranks");
            e.HasKey(x => new { x.Kind, x.Position });
            e.HasOne(x => x.Series).WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Post>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Summary).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.PublishedAt });
            e.HasOne(x => x.Series).WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.UserAgent).HasMaxLength(256);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SeriesLike>(e =>
        {
            e.HasKey(x => new { x.UserId, x.SeriesId });
            e.HasIndex(x => x.SeriesId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Series>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<SeriesCollect>(e =>
        {
            e.HasKey(x => new { x.UserId, x.SeriesId });
            e.HasIndex(x => x.SeriesId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Series>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Follow>(e =>
        {
            e.HasKey(x => new { x.FollowerId, x.FolloweeId });
            e.HasIndex(x => x.FolloweeId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.FollowerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.FolloweeId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PostLike>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PostId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PostCollect>(e =>
        {
            e.HasKey(x => new { x.UserId, x.PostId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<WatchHistoryEntry>(e =>
        {
            e.ToTable("watch_history");
            e.HasKey(x => new { x.UserId, x.SeriesId });
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Series>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Comment>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(500);
            e.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.RejectReason).HasMaxLength(200);
            e.Property(x => x.ModeratedBy).HasMaxLength(128);
            e.HasIndex(x => new { x.TargetType, x.TargetId, x.EpisodeNumber, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.ParentId, x.Status });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Comment>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<CommentLike>(e =>
        {
            e.HasKey(x => new { x.UserId, x.CommentId });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Comment>().WithMany().HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Report>(e =>
        {
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.ResolutionNote).HasMaxLength(500);
            e.Property(x => x.ResolvedBy).HasMaxLength(128);
            e.HasIndex(x => new { x.Status, x.Urgent, x.CreatedAt });
            e.HasIndex(x => new { x.TargetType, x.TargetId });
        });
        model.Entity<ModerationTerm>(e =>
        {
            e.Property(x => x.Term).HasMaxLength(64);
            e.HasIndex(x => x.Term).IsUnique();
            e.Property(x => x.CreatedBy).HasMaxLength(128);
        });

        model.Entity<BlockedCountry>(e =>
        {
            e.HasKey(x => x.CountryCode);
            e.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
            e.HasData(SeedData.BlockedCountries);
        });

        model.Entity<AgeRule>(e =>
        {
            e.HasKey(x => x.CountryCode);
            e.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
        });

        model.Entity<WorkerHeartbeat>(e =>
        {
            e.HasKey(x => x.WorkerId);
            e.Property(x => x.WorkerId).HasMaxLength(128);
            e.Property(x => x.Version).HasMaxLength(64);
        });

        model.Entity<PlaybackSample>(e =>
        {
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.Browser).HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(64);
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.SeriesId, x.At });
        });

        model.Entity<Product>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Badge).HasMaxLength(8);
            e.HasData(SeedData.Products);
        });

        model.Entity<Order>(e =>
        {
            e.Property(x => x.OrderNo).HasMaxLength(32);
            e.HasIndex(x => x.OrderNo).IsUnique();
            e.Property(x => x.ProductName).HasMaxLength(32);
            e.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderRef).HasMaxLength(128);
            e.HasIndex(x => new { x.Provider, x.ProviderRef });
            e.Property(x => x.Country).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.StatusReason).HasMaxLength(200);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => x.PaidAt);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Wallet>(e =>
        {
            e.HasKey(x => x.UserId);
            // Bonus coins have no cash value and are never owed; purchased coins may go negative after a chargeback.
            e.ToTable(t => t.HasCheckConstraint("ck_wallets_bonus_coins", "bonus_coins >= 0"));
            e.HasOne<User>().WithOne().HasForeignKey<Wallet>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<WalletEntry>(e =>
        {
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasIndex(x => new { x.Kind, x.At });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<EpisodeUnlock>(e =>
        {
            e.HasKey(x => new { x.UserId, x.SeriesId, x.EpisodeNumber });
            e.HasIndex(x => new { x.SeriesId, x.At });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Series>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PaymentEvent>(e =>
        {
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.EventId).HasMaxLength(128);
            e.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.OrderNo).HasMaxLength(32);
            e.HasIndex(x => x.OrderNo);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Detail).HasMaxLength(500);
            e.HasIndex(x => new { x.Outcome, x.ReceivedAt });
        });

        model.Entity<AppRelease>(e =>
        {
            e.Property(x => x.Platform).HasMaxLength(32);
            e.Property(x => x.Version).HasMaxLength(32);
            e.HasIndex(x => new { x.Platform, x.Version }).IsUnique();
            e.Property(x => x.Url).HasMaxLength(1000);
            e.Property(x => x.Signature).HasMaxLength(2000);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.UpdatedBy).HasMaxLength(128);
        });

        model.Entity<Topic>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(40);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => new { x.Published, x.SortOrder });
        });
        model.Entity<TopicSeries>(e =>
        {
            e.HasKey(x => new { x.TopicId, x.SeriesId });
            e.HasIndex(x => x.SeriesId);
            e.HasOne<Topic>().WithMany(t => t.Items).HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Series).WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<Announcement>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(200);
            e.Property(x => x.Url).HasMaxLength(500);
        });
        model.Entity<SeriesRating>(e =>
        {
            e.HasKey(x => new { x.UserId, x.SeriesId });
            e.HasIndex(x => x.SeriesId);
            e.ToTable(t => t.HasCheckConstraint("ck_series_ratings_score", "score BETWEEN 1 AND 10"));
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Series>().WithMany().HasForeignKey(x => x.SeriesId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.TargetType).HasMaxLength(64);
            e.Property(x => x.TargetId).HasMaxLength(128);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TargetType, x.At });
        });
    }
}

/// <summary>Lets `dotnet ef` build the model without starting the API.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReelGuoDbContext>
{
    public ReelGuoDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ReelGuoDbContext>();
        ReelGuoDbContext.Configure(options,
            Environment.GetEnvironmentVariable("REELGUO_DB")
            ?? "Host=localhost;Port=5432;Database=reelguo;Username=reelguo;Password=reelguo");
        return new ReelGuoDbContext(options.Options);
    }
}

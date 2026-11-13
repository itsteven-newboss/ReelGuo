using Microsoft.EntityFrameworkCore;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Api.Billing;

public sealed class BillingOptions
{
    public const string Section = "Billing";

    /// <summary>Master switch. Off: every episode is free and the store is hidden.</summary>
    public bool Enabled { get; set; }

    /// <summary>Payment provider for new orders ("sandbox", or a real adapter's name). Empty: nothing can be bought.</summary>
    public string Provider { get; set; } = "";

    /// <summary>ISO 4217 currency of the store; products in other currencies are not offered.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>What buyers see on their card statement. Shown in the store so they recognise the charge.</summary>
    public string BillingDescriptor { get; set; } = "";

    /// <summary>
    /// The app's public address (e.g. https://app.example.com) for the links that hosted checkout pages return to.
    /// Empty: relative links, which work only when checkout and app share an origin (the sandbox).
    /// </summary>
    public string AppBaseUrl { get; set; } = "";

    /// <summary>HMAC secret for the sandbox provider's checkout links and notices.</summary>
    public string SandboxSecret { get; set; } = "";

    /// <summary>
    /// The sandbox lets anyone "pay" without paying, so it runs only in Development unless this is set
    /// (for a staging deployment that must never take real traffic).
    /// </summary>
    public bool AllowSandboxOutsideDevelopment { get; set; }
}

public enum UnlockStatus
{
    /// <summary>Coins were spent; the episode is the viewer's for good.</summary>
    Unlocked,
    /// <summary>The viewer had already bought it; nothing was spent.</summary>
    Owned,
    /// <summary>VIP covers it; nothing was spent.</summary>
    Vip,
    /// <summary>The episode is free.</summary>
    Free,
    Insufficient,
}

public sealed record UnlockOutcome(UnlockStatus Status, int Spent, Wallet? Wallet);

/// <summary>
/// Coins and VIP. Every change locks the viewer's wallet row (SELECT … FOR UPDATE) inside a transaction and adds a
/// ledger entry in the same transaction, so concurrent unlocks and payment notices can't overspend or double-credit.
/// Lock order is always order row first, then wallet row, so payment notices and unlocks never deadlock.
/// </summary>
public sealed class WalletService(ReelGuoDbContext db, TimeProvider time)
{
    public static bool VipActive(DateTimeOffset? vipUntil, DateTimeOffset now) => vipUntil > now;

    /// <summary>Spends bonus coins first, then purchased coins. Null when the balance is short.</summary>
    public static (int Bonus, int Coins)? Split(int coins, int bonusCoins, int price)
    {
        if (price <= 0) return (0, 0);
        if (coins + bonusCoins < price) return null;
        var fromBonus = Math.Min(Math.Max(0, bonusCoins), price);
        return (fromBonus, price - fromBonus);
    }

    /// <summary>
    /// Deltas that take back a coin pack: its bonus coins (as far as they are still there, the rest from purchased
    /// coins) and its purchased coins. Purchased coins may go negative.
    /// </summary>
    public static (int BonusDelta, int CoinsDelta) Reversal(int bonusBalance, int coinsGranted, int bonusGranted)
    {
        var fromBonus = Math.Min(Math.Max(0, bonusBalance), bonusGranted);
        return (-fromBonus, -(coinsGranted + (bonusGranted - fromBonus)));
    }

    public Task<Wallet?> FindAsync(long userId, CancellationToken ct) =>
        db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId, ct);

    public async Task<bool> CanWatchAsync(long userId, long seriesId, int episode, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Wallets.AnyAsync(w => w.UserId == userId && w.VipUntil > now, ct)
            || await db.EpisodeUnlocks.AnyAsync(u => u.UserId == userId && u.SeriesId == seriesId && u.EpisodeNumber == episode, ct);
    }

    /// <summary>Creates the wallet if needed and locks it until the current transaction ends.</summary>
    public async Task<Wallet> LockAsync(long userId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Wallet changes need a transaction.");
        // A tracked copy would shadow the locked row's current values.
        foreach (var entry in db.ChangeTracker.Entries<Wallet>().Where(e => e.Entity.UserId == userId).ToList())
            entry.State = EntityState.Detached;

        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO wallets (user_id, coins, bonus_coins, auto_unlock, updated_at) VALUES ({userId}, 0, 0, false, {now}) ON CONFLICT (user_id) DO NOTHING", ct);
        return (await db.Wallets.FromSql($"SELECT * FROM wallets WHERE user_id = {userId} FOR UPDATE").ToListAsync(ct)).Single();
    }

    /// <summary>Changes a locked wallet and records the change. The caller saves and commits.</summary>
    public WalletEntry Apply(
        Wallet wallet, WalletEntryKind kind, int coinsDelta, int bonusDelta, int vipDays, string description,
        long? orderId = null, long? seriesId = null, int? episode = null, string? actor = null, string? note = null)
    {
        var now = time.GetUtcNow();
        wallet.Coins += coinsDelta;
        wallet.BonusCoins += bonusDelta;
        if (vipDays > 0)
            wallet.VipUntil = (VipActive(wallet.VipUntil, now) ? wallet.VipUntil!.Value : now).AddDays(vipDays);
        else if (vipDays < 0 && wallet.VipUntil is { } until)
            wallet.VipUntil = until.AddDays(vipDays) > now ? until.AddDays(vipDays) : now;
        wallet.UpdatedAt = now;

        var entry = new WalletEntry
        {
            UserId = wallet.UserId,
            Kind = kind,
            CoinsDelta = coinsDelta,
            BonusDelta = bonusDelta,
            CoinsAfter = wallet.Coins,
            BonusAfter = wallet.BonusCoins,
            VipDays = vipDays,
            VipUntilAfter = wallet.VipUntil,
            OrderId = orderId,
            SeriesId = seriesId,
            EpisodeNumber = episode,
            Description = description.Length > 200 ? description[..200] : description,
            Actor = actor,
            Note = note,
            At = now,
        };
        db.WalletEntries.Add(entry);
        return entry;
    }

    /// <summary>Buys one episode with coins. Idempotent: an episode already owned (or covered by VIP) costs nothing.</summary>
    public async Task<UnlockOutcome> UnlockAsync(long userId, long seriesId, int episode, int price, string description, CancellationToken ct)
    {
        if (price <= 0) return new UnlockOutcome(UnlockStatus.Free, 0, await FindAsync(userId, ct));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wallet = await LockAsync(userId, ct);
        var now = time.GetUtcNow();
        if (VipActive(wallet.VipUntil, now))
        {
            await tx.CommitAsync(ct);
            return new UnlockOutcome(UnlockStatus.Vip, 0, wallet);
        }
        if (await db.EpisodeUnlocks.AnyAsync(u => u.UserId == userId && u.SeriesId == seriesId && u.EpisodeNumber == episode, ct))
        {
            await tx.CommitAsync(ct);
            return new UnlockOutcome(UnlockStatus.Owned, 0, wallet);
        }
        if (Split(wallet.Coins, wallet.BonusCoins, price) is not { } split)
        {
            await tx.RollbackAsync(ct);
            return new UnlockOutcome(UnlockStatus.Insufficient, 0, wallet);
        }

        Apply(wallet, WalletEntryKind.Unlock, -split.Coins, -split.Bonus, 0, description, seriesId: seriesId, episode: episode);
        db.EpisodeUnlocks.Add(new EpisodeUnlock { UserId = userId, SeriesId = seriesId, EpisodeNumber = episode, Coins = price, At = now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        ReelGuoTelemetry.Unlocks.Add(1);
        ReelGuoTelemetry.CoinsSpent.Add(price);
        return new UnlockOutcome(UnlockStatus.Unlocked, price, wallet);
    }
}

public static class BillingSetup
{
    public static IServiceCollection AddBilling(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<BillingOptions>(configuration.GetSection(BillingOptions.Section));
        services.AddScoped<WalletService>();
        services.AddScoped<PaymentProcessor>();
        services.AddSingleton<PaymentGateway>();

        // Real providers are registered here once one is chosen (each implements IPaymentProvider).
        var options = configuration.GetSection(BillingOptions.Section).Get<BillingOptions>() ?? new BillingOptions();
        if (!string.IsNullOrEmpty(options.SandboxSecret) && (environment.IsDevelopment() || options.AllowSandboxOutsideDevelopment))
            services.AddSingleton<IPaymentProvider, SandboxPaymentProvider>();
        return services;
    }
}

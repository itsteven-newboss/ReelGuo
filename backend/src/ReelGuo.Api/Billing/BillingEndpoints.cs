using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Api.Public;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Api.Billing;

/// <summary>The viewer's coins and VIP. <c>balance</c> = coins + bonus_coins (what unlocks can spend).</summary>
public sealed record WalletDto(int Coins, int BonusCoins, int Balance, DateTimeOffset? VipUntil, bool VipActive, bool AutoUnlock);

public sealed record WalletSettingsRequest(bool AutoUnlock);

/// <summary>One line of 消费记录. Deltas are signed; <c>vip_days</c> is negative when a VIP purchase was reversed.</summary>
public sealed record WalletEntryDto(
    long Id, WalletEntryKind Kind, int Coins, int BonusCoins, int VipDays, string Description, string? OrderNo, long? SeriesId, int? Ep, DateTimeOffset At);

public sealed record WalletEntryPage(List<WalletEntryDto> Items, PaginationDto Pagination);

/// <summary>Which paid episodes of a series the viewer may watch: bought ones, or all of them while VIP.</summary>
public sealed record SeriesAccessDto(long SeriesId, List<int> UnlockedEpisodes, bool VipActive);

/// <summary><c>result</c>: unlocked (coins spent), owned / vip / free (nothing spent).</summary>
public sealed record UnlockResponse(int Ep, UnlockStatus Result, int Spent, WalletDto Wallet);

public sealed record ProductDto(int Id, ProductKind Kind, string Name, int PriceCents, string Currency, int Coins, int BonusCoins, int VipDays, string? Badge);

/// <summary>
/// The store. <c>payment_method</c> is the provider's display name; <c>billing_descriptor</c> is what appears on the
/// buyer's card statement; <c>sandbox</c> means no real money moves.
/// </summary>
public sealed record StoreDto(List<ProductDto> Products, string Currency, string PaymentMethod, string? BillingDescriptor, bool Sandbox);

public sealed record CreateOrderRequest(int ProductId);

public sealed record OrderDto(
    string OrderNo, OrderStatus Status, ProductKind Kind, string ProductName, int PriceCents, string Currency,
    int Coins, int BonusCoins, int VipDays, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);

/// <summary>Send the buyer to <c>checkout_url</c> (the provider's payment page); it returns to /pay/result?order=….</summary>
public sealed record CheckoutDto(OrderDto Order, string CheckoutUrl);

public static class BillingEndpoints
{
    public static RouteGroupBuilder MapBilling(this RouteGroupBuilder v1)
    {
        v1.MapGet("/store", Store).WithTags("Billing")
            .WithSummary("Coin packs and VIP passes on sale. 404 store_unavailable when purchases are off.");

        var wallet = v1.MapGroup("/me/wallet").WithTags("Billing").RequireAuthorization(UserAuth.Policy);
        wallet.MapGet("/", GetWallet).WithSummary("The viewer's coins and VIP.");
        wallet.MapPut("/settings", UpdateSettings).WithSummary("Turns automatic unlocking of the next paid episode on or off.");
        wallet.MapGet("/entries", Entries).WithSummary("消费记录: every change to the wallet, newest first.");

        v1.MapGet("/series/{id:long}/access", Access).WithTags("Billing").RequireAuthorization(UserAuth.Policy)
            .WithSummary("Paid episodes of a series the viewer may watch.");
        v1.MapPost("/series/{id:long}/episodes/{ep:int}/unlock", Unlock).WithTags("Billing").RequireAuthorization(UserAuth.Policy)
            .RequireRateLimiting(RateLimits.Unlock)
            .Produces<ApiErrorResponse>(StatusCodes.Status402PaymentRequired)
            .WithSummary("Buys an episode with coins (bonus coins first). Idempotent. 402 insufficient_coins when the balance is short.");

        var orders = v1.MapGroup("/orders").WithTags("Billing").RequireAuthorization(UserAuth.Policy);
        orders.MapPost("/", CreateOrder).RequireRateLimiting(RateLimits.Orders)
            .Produces<ApiErrorResponse>(StatusCodes.Status502BadGateway)
            .WithSummary("Starts a purchase and returns the provider's checkout URL.");
        orders.MapGet("/{orderNo}", GetOrder).WithSummary("One of the viewer's orders (polled by the payment result page).");
        return v1;
    }

    internal static WalletDto ToDto(Wallet? w, DateTimeOffset now) => w is null
        ? new WalletDto(0, 0, 0, null, false, false)
        : new WalletDto(w.Coins, w.BonusCoins, w.Coins + w.BonusCoins, w.VipUntil, WalletService.VipActive(w.VipUntil, now), w.AutoUnlock);

    private static OrderDto ToDto(Order o) => new(o.OrderNo, o.Status, o.Kind, o.ProductName, o.PriceCents, o.Currency.Trim(),
        o.Coins, o.BonusCoins, o.VipDays, o.CreatedAt, o.PaidAt);

    private static async Task<Results<Ok<StoreDto>, NotFound<ApiErrorResponse>>> Store(
        ReelGuoDbContext db, PaymentGateway gateway, IOptions<BillingOptions> options, CancellationToken ct)
    {
        if (gateway.Active is not { } provider) return TypedResults.NotFound(ApiErrorResponse.Of("store_unavailable", "充值暂未开放"));
        var currency = options.Value.Currency.ToUpperInvariant();
        var products = await db.Products.AsNoTracking()
            .Where(p => p.Active && p.Currency == currency)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new ProductDto(p.Id, p.Kind, p.Name, p.PriceCents, p.Currency, p.Coins, p.BonusCoins, p.VipDays, p.Badge))
            .ToListAsync(ct);
        var descriptor = string.IsNullOrWhiteSpace(options.Value.BillingDescriptor) ? null : options.Value.BillingDescriptor.Trim();
        return TypedResults.Ok(new StoreDto(products, currency, provider.DisplayName, descriptor, provider.IsSandbox));
    }

    private static async Task<Ok<WalletDto>> GetWallet(HttpContext http, WalletService wallets, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(ToDto(await wallets.FindAsync(UserAuth.UserId(http), ct), time.GetUtcNow()));

    private static async Task<Ok<WalletDto>> UpdateSettings(
        WalletSettingsRequest request, HttpContext http, ReelGuoDbContext db, WalletService wallets, TimeProvider time, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO wallets (user_id, coins, bonus_coins, auto_unlock, updated_at) VALUES ({uid}, 0, 0, {request.AutoUnlock}, {now})
            ON CONFLICT (user_id) DO UPDATE SET auto_unlock = excluded.auto_unlock, updated_at = excluded.updated_at
            """, ct);
        return TypedResults.Ok(ToDto(await wallets.FindAsync(uid, ct), now));
    }

    private static async Task<Ok<WalletEntryPage>> Entries(
        HttpContext http, ReelGuoDbContext db, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var p = Math.Max(1, page ?? 1);
        var s = Math.Clamp(size ?? 20, 1, 50);
        var query = db.WalletEntries.AsNoTracking().Where(e => e.UserId == uid);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip((p - 1) * s).Take(s)
            .Select(e => new WalletEntryDto(e.Id, e.Kind, e.CoinsDelta, e.BonusDelta, e.VipDays, e.Description,
                db.Orders.Where(o => o.Id == e.OrderId).Select(o => o.OrderNo).FirstOrDefault(), e.SeriesId, e.EpisodeNumber, e.At))
            .ToListAsync(ct);
        return TypedResults.Ok(new WalletEntryPage(items, new PaginationDto(p, Math.Max(1, (int)Math.Ceiling(total / (double)s)), s, total)));
    }

    private static async Task<Ok<SeriesAccessDto>> Access(long id, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var now = time.GetUtcNow();
        var vip = await db.Wallets.AnyAsync(w => w.UserId == uid && w.VipUntil > now, ct);
        var episodes = await db.EpisodeUnlocks.AsNoTracking().Where(u => u.UserId == uid && u.SeriesId == id)
            .OrderBy(u => u.EpisodeNumber).Select(u => u.EpisodeNumber).ToListAsync(ct);
        return TypedResults.Ok(new SeriesAccessDto(id, episodes, vip));
    }

    private static async Task<Results<Ok<UnlockResponse>, NotFound<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> Unlock(
        long id, int ep, HttpContext http, ReelGuoDbContext db, ICountryResolver resolver, WalletService wallets,
        IOptions<BillingOptions> options, TimeProvider time, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        // Only what the viewer could actually watch here can be bought.
        var row = await db.Episodes.AsNoTracking()
            .Where(e => e.SeriesId == id && e.EpisodeNumber == ep && e.Status == EpisodeStatus.Ready && e.HlsPrefix != null)
            .Where(e => db.Series.VisibleTo(resolver.Resolve(http), PublicEndpoints.Today(time)).Any(s => s.Id == e.SeriesId))
            .Select(e => new { e.IsFree, e.Series.EpisodePrice, e.Series.FreeEpisodes, e.Series.Title })
            .FirstOrDefaultAsync(ct);
        if (row is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "该集暂时无法购买"));

        var price = Pricing.EpisodePrice(options.Value.Enabled, row.EpisodePrice, row.FreeEpisodes, ep, row.IsFree);
        var title = row.Title.Length > 60 ? row.Title[..60] + "…" : row.Title;
        var outcome = await wallets.UnlockAsync(uid, id, ep, price, $"解锁《{title}》第 {ep} 集", ct);
        if (outcome.Status == UnlockStatus.Insufficient)
            return TypedResults.Json(ApiErrorResponse.Of("insufficient_coins", "金币不足，请先充值"), statusCode: StatusCodes.Status402PaymentRequired);
        return TypedResults.Ok(new UnlockResponse(ep, outcome.Status, outcome.Spent, ToDto(outcome.Wallet, time.GetUtcNow())));
    }

    private static async Task<Results<Ok<CheckoutDto>, NotFound<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> CreateOrder(
        CreateOrderRequest request, HttpContext http, ReelGuoDbContext db, PaymentGateway gateway, ICountryResolver resolver,
        IOptions<BillingOptions> options, TimeProvider time, ILogger<PaymentGateway> logger, CancellationToken ct)
    {
        if (gateway.Active is not { } provider) return TypedResults.NotFound(ApiErrorResponse.Of("store_unavailable", "充值暂未开放"));
        var currency = options.Value.Currency.ToUpperInvariant();
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProductId && p.Active && p.Currency == currency, ct);
        if (product is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "商品不存在或已下架"));

        var now = time.GetUtcNow();
        var order = new Order
        {
            OrderNo = OrderNumbers.New(now),
            UserId = UserAuth.UserId(http),
            ProductId = product.Id,
            Kind = product.Kind,
            ProductName = product.Name,
            PriceCents = product.PriceCents,
            Currency = product.Currency,
            Coins = product.Coins,
            BonusCoins = product.BonusCoins,
            VipDays = product.VipDays,
            Status = OrderStatus.Pending,
            Provider = provider.Name,
            Country = resolver.Resolve(http),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        CheckoutSession session;
        try
        {
            session = await provider.CreateCheckoutAsync(new CheckoutRequest(order, OrderNumbers.ReturnUrl(options.Value, order.OrderNo)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Checkout with {Provider} failed for order {OrderNo}", provider.Name, order.OrderNo);
            order.Status = OrderStatus.Failed;
            order.StatusReason = "checkout_unavailable";
            order.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            return TypedResults.Json(ApiErrorResponse.Of("payment_unavailable", "支付渠道暂时不可用，请稍后再试"), statusCode: StatusCodes.Status502BadGateway);
        }

        order.ProviderRef = session.ProviderRef;
        await db.SaveChangesAsync(ct);
        ReelGuoTelemetry.Orders.Add(1, new("outcome", "created"), new("kind", order.Kind.ToString().ToLowerInvariant()));
        return TypedResults.Ok(new CheckoutDto(ToDto(order), session.Url));
    }

    private static async Task<Results<Ok<OrderDto>, NotFound<ApiErrorResponse>>> GetOrder(
        string orderNo, HttpContext http, ReelGuoDbContext db, CancellationToken ct)
    {
        var uid = UserAuth.UserId(http);
        var order = OrderNumbers.LooksValid(orderNo)
            ? await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderNo == orderNo && o.UserId == uid, ct)
            : null;
        return order is null ? TypedResults.NotFound(ApiErrorResponse.Of("not_found", "订单不存在")) : TypedResults.Ok(ToDto(order));
    }
}

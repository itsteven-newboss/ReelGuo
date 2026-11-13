using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Billing;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Admin;

public sealed record AdminProductDto(
    int Id, ProductKind Kind, string Name, int PriceCents, string Currency, int Coins, int BonusCoins, int VipDays, string? Badge,
    bool Active, int SortOrder, int PaidOrders, DateTimeOffset UpdatedAt);

public sealed record ProductInput(
    ProductKind Kind, string Name, int PriceCents, string Currency, int Coins, int BonusCoins, int VipDays, string? Badge, bool Active, int SortOrder);

public sealed record AdminOrderDto(
    long Id, string OrderNo, long UserId, string Username, string Nickname, ProductKind Kind, string ProductName, int PriceCents, string Currency,
    int Coins, int BonusCoins, int VipDays, OrderStatus Status, string Provider, string? ProviderRef, string Country, string? StatusReason,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, DateTimeOffset? ReversedAt);

public sealed record AdminOrderPage(List<AdminOrderDto> Items, int Page, int Size, int Total);

public sealed record RefundRequest(string? Note);

/// <summary><c>pending</c>: the provider accepted the refund and will confirm it with a notice.</summary>
public sealed record RefundResponse(AdminOrderDto Order, bool Pending);

public sealed record AdminWalletEntryDto(
    long Id, WalletEntryKind Kind, int Coins, int BonusCoins, int CoinsAfter, int BonusAfter, int VipDays, string Description,
    string? OrderNo, string? Actor, string? Note, DateTimeOffset At);

public sealed record AdminMemberWalletDto(
    long UserId, WalletDto Wallet, int Unlocks, int PaidOrders, long SpentCents, List<AdminWalletEntryDto> Entries, List<AdminOrderDto> Orders);

/// <summary>Signed changes; <c>note</c> (required) says why, and the viewer's records show 客服调整 (adjusted by support).</summary>
public sealed record WalletAdjustRequest(int Coins, int BonusCoins, int VipDays, string Note);

public sealed record RevenueDayDto(DateOnly Day, long RevenueCents, int Orders, int Buyers, long ReversedCents);

public sealed record RevenueSeriesDto(long SeriesId, string Title, long Coins, int Unlocks, int Buyers);

/// <summary>
/// Revenue in the store currency. Gross = orders paid in the window (by paid date); reversed = refunds and
/// chargebacks in the window (by their date). The chargeback rate is chargebacks over paid orders in the window.
/// </summary>
public sealed record RevenueDto(
    int Days, string Currency, long RevenueCents, long ReversedCents, long NetCents, int PaidOrders, int Buyers, long ArppuCents,
    int Refunds, int Chargebacks, double ChargebackRate, int VipOrders, long CoinsSpent, int Unlocks,
    List<RevenueDayDto> Daily, List<RevenueSeriesDto> TopSeries);

/// <summary>商品, 订单, member wallets and revenue.</summary>
public static class AdminBillingEndpoints
{
    public static RouteGroupBuilder MapAdminBilling(this RouteGroupBuilder admin)
    {
        var products = admin.MapGroup("/products").WithTags("Admin: billing");
        products.MapGet("/", ListProducts).WithSummary("Every product, including retired ones.");
        products.MapPost("/", CreateProduct).WithSummary("Adds a coin pack or VIP pass.");
        products.MapPut("/{id:int}", UpdateProduct).WithSummary("Edits a product. Past orders keep the price and contents they were bought with.");

        var orders = admin.MapGroup("/orders").WithTags("Admin: billing");
        orders.MapGet("/", ListOrders).WithSummary("Orders, newest first. q = order number, provider reference or member.");
        orders.MapPost("/{id:long}/refund", Refund).WithSummary("Refunds a paid order through its provider and takes back what it granted.");

        admin.MapGet("/members/{id:long}/wallet", MemberWallet).WithTags("Admin: billing").WithSummary("A member's wallet, recent records and orders.");
        admin.MapPost("/members/{id:long}/wallet/adjust", Adjust).WithTags("Admin: billing")
            .WithSummary("Adds or removes coins, bonus coins or VIP days, with a note (goodwill credit, correction).");

        admin.MapGet("/analytics/revenue", Revenue).WithTags("Admin: analytics").WithSummary("Revenue, buyers, refunds and coin spending over the last N days (1-90, UTC).");
        return admin;
    }

    // ---------- products ----------

    private static async Task<Ok<List<AdminProductDto>>> ListProducts(ReelGuoDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Products.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new AdminProductDto(p.Id, p.Kind, p.Name, p.PriceCents, p.Currency, p.Coins, p.BonusCoins, p.VipDays, p.Badge, p.Active, p.SortOrder,
                db.Orders.Count(o => o.ProductId == p.Id && o.PaidAt != null), p.UpdatedAt))
            .ToListAsync(ct));

    private static string? Validate(ProductInput input)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 32) return "名称需为 1-32 个字符";
        if (input.PriceCents is < 1 or > 1_000_000) return "价格需在 0.01-10000 之间";
        if ((input.Currency ?? "").Trim() is not { Length: 3 } c || !c.All(char.IsAsciiLetter)) return "币种需为 3 位字母代码（如 USD）";
        if (input.Coins is < 0 or > 1_000_000 || input.BonusCoins is < 0 or > 1_000_000) return "金币数量无效";
        if (input.VipDays is < 0 or > 3660) return "VIP 天数需在 0-3660 之间";
        if (input.Kind == ProductKind.Coins && (input.Coins < 1 || input.VipDays != 0)) return "金币礼包需至少 1 金币，且不含 VIP 天数";
        if (input.Kind == ProductKind.Vip && input.VipDays < 1) return "VIP 商品需至少 1 天";
        if ((input.Badge ?? "").Trim().Length > 8) return "角标不超过 8 个字符";
        return null;
    }

    private static void Apply(Product p, ProductInput input, DateTimeOffset now)
    {
        p.Kind = input.Kind;
        p.Name = input.Name.Trim();
        p.PriceCents = input.PriceCents;
        p.Currency = input.Currency.Trim().ToUpperInvariant();
        p.Coins = input.Coins;
        p.BonusCoins = input.BonusCoins;
        p.VipDays = input.VipDays;
        p.Badge = string.IsNullOrWhiteSpace(input.Badge) ? null : input.Badge.Trim();
        p.Active = input.Active;
        p.SortOrder = input.SortOrder;
        p.UpdatedAt = now;
    }

    private static AdminProductDto ToDto(Product p, int paid) =>
        new(p.Id, p.Kind, p.Name, p.PriceCents, p.Currency, p.Coins, p.BonusCoins, p.VipDays, p.Badge, p.Active, p.SortOrder, paid, p.UpdatedAt);

    private static async Task<Results<Ok<AdminProductDto>, BadRequest<ApiErrorResponse>>> CreateProduct(
        ProductInput input, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (Validate(input) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_product", error));
        var now = time.GetUtcNow();
        var product = new Product { Name = "", Currency = "", CreatedAt = now };
        Apply(product, input, now);
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        db.AddAudit(now, AdminActor.From(http), "product.create", "product", product.Id.ToString(), input);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(product, 0));
    }

    private static async Task<Results<Ok<AdminProductDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> UpdateProduct(
        int id, ProductInput input, HttpContext http, ReelGuoDbContext db, TimeProvider time, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "商品不存在"));
        if (Validate(input) is { } error) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_product", error));
        var before = new { product.Name, product.PriceCents, product.Currency, product.Coins, product.BonusCoins, product.VipDays, product.Active };
        var now = time.GetUtcNow();
        Apply(product, input, now);
        db.AddAudit(now, AdminActor.From(http), "product.update", "product", id.ToString(), new { before, after = input });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(product, await db.Orders.CountAsync(o => o.ProductId == id && o.PaidAt != null, ct)));
    }

    // ---------- orders ----------

    private static IQueryable<AdminOrderDto> Project(IQueryable<Order> query) => query.Select(o => new AdminOrderDto(
        o.Id, o.OrderNo, o.UserId, o.User.Username, o.User.Nickname, o.Kind, o.ProductName, o.PriceCents, o.Currency, o.Coins, o.BonusCoins, o.VipDays,
        o.Status, o.Provider, o.ProviderRef, o.Country, o.StatusReason, o.CreatedAt, o.PaidAt, o.ReversedAt));

    private static async Task<Ok<AdminOrderPage>> ListOrders(
        ReelGuoDbContext db, [FromQuery] string? status, [FromQuery] string? q, [FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var p = Math.Max(1, page ?? 1);
        var s = Math.Clamp(size ?? 30, 1, 100);
        var query = db.Orders.AsNoTracking();
        // Accepts the JSON spelling (charged_back) as well as the enum name.
        if (Enum.TryParse<OrderStatus>((status ?? "").Replace("_", ""), ignoreCase: true, out var st)) query = query.Where(o => o.Status == st);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var like = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(o => o.OrderNo == term.ToUpper() || o.ProviderRef == term
                || EF.Functions.ILike(o.User.Username, like) || EF.Functions.ILike(o.User.Nickname, like));
        }
        var total = await query.CountAsync(ct);
        var items = await Project(query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id).Skip((p - 1) * s).Take(s)).ToListAsync(ct);
        return TypedResults.Ok(new AdminOrderPage(items, p, s, total));
    }

    private static async Task<Results<Ok<RefundResponse>, NotFound<ApiErrorResponse>, Conflict<ApiErrorResponse>, JsonHttpResult<ApiErrorResponse>>> Refund(
        long id, RefundRequest request, HttpContext http, ReelGuoDbContext db, PaymentGateway gateway, PaymentProcessor processor,
        TimeProvider time, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "订单不存在"));
        if (order.Status != OrderStatus.Paid) return TypedResults.Conflict(ApiErrorResponse.Of("not_refundable", "只有已支付的订单可以退款"));
        if (gateway.Find(order.Provider) is not { } provider)
            return TypedResults.Conflict(ApiErrorResponse.Of("provider_unavailable", $"支付渠道 {order.Provider} 已不可用，请在渠道后台退款"));

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()[..Math.Min(200, request.Note.Trim().Length)];
        var result = await provider.RefundAsync(order, ct);
        if (!result.Completed && result.Error is not null)
            return TypedResults.Json(ApiErrorResponse.Of("refund_failed", $"退款失败：{result.Error}"), statusCode: StatusCodes.Status502BadGateway);

        var actor = AdminActor.From(http);
        if (result.Completed)
        {
            // Recorded like a provider notice, so the order, the wallet and the event log change together.
            var notice = new PaymentNotice($"admin-refund:{order.Id}", "admin.refund", PaymentNoticeKind.Refunded, order.OrderNo, order.ProviderRef,
                order.PriceCents, order.Currency, note ?? "后台退款");
            await processor.ApplyAsync(provider.Name, notice, JsonSerializer.Serialize(new { actor, note }), ct);
        }
        db.AddAudit(time, actor, "order.refund", "order", order.OrderNo, new { note, completed = result.Completed, order.PriceCents, order.Currency });
        await db.SaveChangesAsync(ct);
        var dto = await Project(db.Orders.AsNoTracking().Where(o => o.Id == id)).SingleAsync(ct);
        return TypedResults.Ok(new RefundResponse(dto, !result.Completed));
    }

    // ---------- member wallets ----------

    private static async Task<Results<Ok<AdminMemberWalletDto>, NotFound<ApiErrorResponse>>> MemberWallet(
        long id, ReelGuoDbContext db, WalletService wallets, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id && u.Role == UserRole.User, ct))
            return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "会员不存在"));
        var wallet = await wallets.FindAsync(id, ct);
        var entries = await db.WalletEntries.AsNoTracking().Where(e => e.UserId == id).OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(50)
            .Select(e => new AdminWalletEntryDto(e.Id, e.Kind, e.CoinsDelta, e.BonusDelta, e.CoinsAfter, e.BonusAfter, e.VipDays, e.Description,
                db.Orders.Where(o => o.Id == e.OrderId).Select(o => o.OrderNo).FirstOrDefault(), e.Actor, e.Note, e.At))
            .ToListAsync(ct);
        var orders = await Project(db.Orders.AsNoTracking().Where(o => o.UserId == id).OrderByDescending(o => o.CreatedAt).Take(20)).ToListAsync(ct);
        var paid = db.Orders.Where(o => o.UserId == id && o.Status == OrderStatus.Paid);
        return TypedResults.Ok(new AdminMemberWalletDto(id, BillingEndpoints.ToDto(wallet, time.GetUtcNow()),
            await db.EpisodeUnlocks.CountAsync(u => u.UserId == id, ct), await paid.CountAsync(ct),
            await paid.SumAsync(o => (long)o.PriceCents, ct), entries, orders));
    }

    private static async Task<Results<Ok<WalletDto>, NotFound<ApiErrorResponse>, BadRequest<ApiErrorResponse>>> Adjust(
        long id, WalletAdjustRequest request, HttpContext http, ReelGuoDbContext db, WalletService wallets, TimeProvider time, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id && u.Role == UserRole.User, ct))
            return TypedResults.NotFound(ApiErrorResponse.Of("not_found", "会员不存在"));
        var note = (request.Note ?? "").Trim();
        if (note.Length is 0 or > AdminEndpoints.MaxNoteLength) return TypedResults.BadRequest(ApiErrorResponse.Of("note_required", "请填写调整原因"));
        if (request is { Coins: 0, BonusCoins: 0, VipDays: 0 }) return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_adjustment", "没有要调整的内容"));
        if (Math.Abs(request.Coins) > 1_000_000 || Math.Abs(request.BonusCoins) > 1_000_000 || Math.Abs(request.VipDays) > 3660)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_adjustment", "调整数额过大"));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wallet = await wallets.LockAsync(id, ct);
        if (request.Coins < 0 && wallet.Coins + request.Coins < 0 || wallet.BonusCoins + request.BonusCoins < 0)
            return TypedResults.BadRequest(ApiErrorResponse.Of("invalid_adjustment", "扣除后余额不能为负"));

        var actor = AdminActor.From(http);
        wallets.Apply(wallet, WalletEntryKind.Adjustment, request.Coins, request.BonusCoins, request.VipDays, "客服调整", actor: actor, note: note);
        db.AddAudit(time, actor, "member.wallet_adjust", "user", id.ToString(), new { request.Coins, request.BonusCoins, request.VipDays, note });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok(BillingEndpoints.ToDto(wallet, time.GetUtcNow()));
    }

    // ---------- revenue ----------

    // Raw-SQL rows map by snake_case column names.
    private sealed class DayRow
    {
        public DateOnly Day { get; set; }
        public long Cents { get; set; }
        public int Orders { get; set; }
        public int Buyers { get; set; }
    }

    private sealed class TotalsRow
    {
        public long Cents { get; set; }
        public int Orders { get; set; }
        public int Buyers { get; set; }
        public int VipOrders { get; set; }
    }

    private sealed class ReversalRow
    {
        public long Cents { get; set; }
        public int Refunds { get; set; }
        public int Chargebacks { get; set; }
    }

    private sealed class SeriesRow
    {
        public long SeriesId { get; set; }
        public long Coins { get; set; }
        public int Unlocks { get; set; }
        public int Buyers { get; set; }
    }

    private static async Task<Ok<RevenueDto>> Revenue(
        ReelGuoDbContext db, IOptions<BillingOptions> options, TimeProvider time, [FromQuery] int? days, CancellationToken ct)
    {
        var d = Math.Clamp(days ?? 30, 1, 90);
        var now = time.GetUtcNow();
        var from = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(1 - d);
        var currency = options.Value.Currency.ToUpperInvariant();

        var paidDays = (await db.Database.SqlQuery<DayRow>($"""
            SELECT (paid_at AT TIME ZONE 'UTC')::date AS day, coalesce(sum(price_cents), 0)::bigint AS cents,
                   count(*)::int AS orders, count(DISTINCT user_id)::int AS buyers
              FROM orders WHERE paid_at >= {from} AND currency = {currency} GROUP BY 1
            """).ToListAsync(ct)).ToDictionary(r => r.Day);
        var reversedDays = (await db.Database.SqlQuery<DayRow>($"""
            SELECT (reversed_at AT TIME ZONE 'UTC')::date AS day, coalesce(sum(price_cents), 0)::bigint AS cents,
                   count(*)::int AS orders, count(DISTINCT user_id)::int AS buyers
              FROM orders WHERE reversed_at >= {from} AND currency = {currency} GROUP BY 1
            """).ToListAsync(ct)).ToDictionary(r => r.Day);
        var totals = await db.Database.SqlQuery<TotalsRow>($"""
            SELECT coalesce(sum(price_cents), 0)::bigint AS cents, count(*)::int AS orders, count(DISTINCT user_id)::int AS buyers,
                   count(*) FILTER (WHERE kind = 'Vip')::int AS vip_orders
              FROM orders WHERE paid_at >= {from} AND currency = {currency}
            """).SingleAsync(ct);
        var reversed = await db.Database.SqlQuery<ReversalRow>($"""
            SELECT coalesce(sum(price_cents), 0)::bigint AS cents,
                   count(*) FILTER (WHERE status = 'Refunded')::int AS refunds,
                   count(*) FILTER (WHERE status = 'ChargedBack')::int AS chargebacks
              FROM orders WHERE reversed_at >= {from} AND currency = {currency}
            """).SingleAsync(ct);
        var series = await db.Database.SqlQuery<SeriesRow>($"""
            SELECT series_id AS series_id, coalesce(sum(coins), 0)::bigint AS coins, count(*)::int AS unlocks, count(DISTINCT user_id)::int AS buyers
              FROM episode_unlocks WHERE at >= {from} GROUP BY series_id ORDER BY 2 DESC
            """).ToListAsync(ct);

        var top = series.Take(10).ToList();
        var ids = top.Select(t => t.SeriesId).ToList();
        var titles = await db.Series.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Title, ct);
        var daily = Enumerable.Range(0, d).Select(i => DateOnly.FromDateTime(from.UtcDateTime.AddDays(i))).Select(day =>
        {
            var paid = paidDays.GetValueOrDefault(day);
            return new RevenueDayDto(day, paid?.Cents ?? 0, paid?.Orders ?? 0, paid?.Buyers ?? 0, reversedDays.GetValueOrDefault(day)?.Cents ?? 0);
        }).ToList();

        return TypedResults.Ok(new RevenueDto(
            d, currency, totals.Cents, reversed.Cents, totals.Cents - reversed.Cents, totals.Orders, totals.Buyers,
            totals.Buyers > 0 ? totals.Cents / totals.Buyers : 0, reversed.Refunds, reversed.Chargebacks,
            totals.Orders > 0 ? (double)reversed.Chargebacks / totals.Orders : 0, totals.VipOrders,
            series.Sum(s => s.Coins), series.Sum(s => s.Unlocks), daily,
            top.Select(t => new RevenueSeriesDto(t.SeriesId, titles.GetValueOrDefault(t.SeriesId, "(已删除)"), t.Coins, t.Unlocks, t.Buyers)).ToList()));
    }
}

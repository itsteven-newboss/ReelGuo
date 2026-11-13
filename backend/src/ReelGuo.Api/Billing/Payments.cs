using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Infrastructure;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using ReelGuo.Data.Telemetry;

namespace ReelGuo.Api.Billing;

public enum PaymentNoticeKind
{
    Paid,
    Failed,
    Refunded,
    ChargedBack,
}

/// <summary>A verified notice from a payment provider, in provider-neutral terms.</summary>
/// <param name="AmountCents">The amount the notice is about; must match the order for <see cref="PaymentNoticeKind.Paid"/>.</param>
public sealed record PaymentNotice(
    string EventId, string Type, PaymentNoticeKind Kind, string OrderNo, string? ProviderRef, long AmountCents, string Currency, string? Reason);

public sealed record CheckoutRequest(Order Order, string ReturnUrl);

/// <summary>Where to send the buyer (the provider's hosted payment page) and the provider's id for it.</summary>
public sealed record CheckoutSession(string Url, string? ProviderRef);

/// <summary><c>Completed</c>: the refund is done and no separate notice will be awaited.</summary>
public sealed record RefundResult(bool Completed, string? Error = null);

/// <summary>
/// One payment provider. Adult-content merchants generally use a hosted payment page plus signed server-to-server
/// notices (CCBill, Segpay, Verotel, Epoch and most crypto processors work this way), so that is the contract:
/// create a checkout, verify and read notices, refund.
/// </summary>
public interface IPaymentProvider
{
    /// <summary>Lower-case id, used in the notice URL (<c>/api/payments/{name}/webhook</c>) and stored on orders.</summary>
    string Name { get; }

    /// <summary>Shown to buyers as the payment method.</summary>
    string DisplayName { get; }

    bool IsSandbox { get; }

    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct);

    /// <summary>Verifies that a notice really comes from the provider, and reads it. Null when it is not authentic.</summary>
    PaymentNotice? ReadNotice(IHeaderDictionary headers, string body);

    /// <summary>Refunds a paid order in full.</summary>
    Task<RefundResult> RefundAsync(Order order, CancellationToken ct);
}

/// <summary>Finds providers by name. Notices for old orders still reach their provider after the active one changes.</summary>
public sealed class PaymentGateway(IEnumerable<IPaymentProvider> providers, IOptions<BillingOptions> options)
{
    /// <summary>The provider for new orders; null when the store is off or the configured provider isn't available.</summary>
    public IPaymentProvider? Active => options.Value.Enabled ? Find(options.Value.Provider) : null;

    public IPaymentProvider? Find(string? name) =>
        providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

public static class OrderNumbers
{
    // Crockford base32 without I, L, O, U: easy to read out to support.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>E.g. RG261111K3M9Q2X7HD: a date for humans, then 50 random bits so numbers can't be guessed.</summary>
    public static string New(DateTimeOffset now)
    {
        Span<char> random = stackalloc char[10];
        for (var i = 0; i < random.Length; i++) random[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"RG{now.UtcDateTime:yyMMdd}{random}";
    }

    public static bool LooksValid(string? orderNo) =>
        orderNo is { Length: >= 10 and <= 32 } && orderNo.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));

    /// <summary>The app page a hosted checkout returns to.</summary>
    public static string ReturnUrl(BillingOptions options, string orderNo) =>
        $"{options.AppBaseUrl.TrimEnd('/')}/pay/result?order={orderNo}";
}

/// <summary>
/// Applies provider notices to orders and wallets. Each notice is recorded once (unique per provider and event id),
/// so a provider re-sending it changes nothing. Payments are credited only when the amount and currency match the
/// order exactly. Refunds and chargebacks take back what the order granted.
/// </summary>
public sealed class PaymentProcessor(ReelGuoDbContext db, WalletService wallets, TimeProvider time, ILogger<PaymentProcessor> logger)
{
    public const int MaxPayloadChars = 8 * 1024;

    public static string Truncate(string payload) => payload.Length > MaxPayloadChars ? payload[..MaxPayloadChars] : payload;

    /// <summary>Records a notice that failed verification, for investigation.</summary>
    public async Task RecordUnverifiedAsync(string provider, string payload, string detail, CancellationToken ct)
    {
        db.PaymentEvents.Add(new PaymentEvent
        {
            Provider = provider,
            EventId = $"unverified:{Guid.NewGuid():N}",
            Type = "unverified",
            Outcome = PaymentEventOutcome.Rejected,
            Detail = detail,
            Payload = Truncate(payload),
            ReceivedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        ReelGuoTelemetry.PaymentNotices.Add(1, new("provider", provider), new("outcome", "rejected"));
        logger.LogWarning("Payment notice from {Provider} failed verification: {Detail}", provider, detail);
    }

    public async Task<(PaymentEventOutcome Outcome, string? Detail)> ApplyAsync(string provider, PaymentNotice notice, string payload, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var inserted = await db.Database.SqlQuery<long>($"""
            INSERT INTO payment_events (provider, event_id, type, order_no, amount_cents, currency, outcome, payload, received_at)
            VALUES ({provider}, {notice.EventId}, {notice.Type}, {notice.OrderNo}, {notice.AmountCents}, {notice.Currency}, 'Applied', {Truncate(payload)}, {now})
            ON CONFLICT (provider, event_id) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (inserted.Count == 0)
        {
            await tx.CommitAsync(ct);
            ReelGuoTelemetry.PaymentNotices.Add(1, new("provider", provider), new("outcome", "duplicate"));
            return (PaymentEventOutcome.Duplicate, null);
        }

        var (outcome, detail) = await ApplyCoreAsync(provider, notice, now, ct);
        await db.SaveChangesAsync(ct);
        await db.PaymentEvents.Where(e => e.Id == inserted[0])
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Outcome, outcome).SetProperty(e => e.Detail, detail), ct);
        await tx.CommitAsync(ct);

        ReelGuoTelemetry.PaymentNotices.Add(1, new("provider", provider), new("outcome", outcome.ToString().ToLowerInvariant()));
        if (outcome == PaymentEventOutcome.Rejected)
            logger.LogWarning("Payment notice {EventId} from {Provider} for order {OrderNo} rejected: {Detail}", notice.EventId, provider, notice.OrderNo, detail);
        return (outcome, detail);
    }

    private async Task<(PaymentEventOutcome, string?)> ApplyCoreAsync(string provider, PaymentNotice notice, DateTimeOffset now, CancellationToken ct)
    {
        // A tracked copy would shadow the locked row's current values.
        foreach (var entry in db.ChangeTracker.Entries<Order>().Where(e => e.Entity.OrderNo == notice.OrderNo).ToList())
            entry.State = EntityState.Detached;
        var order = (await db.Orders.FromSql($"SELECT * FROM orders WHERE order_no = {notice.OrderNo} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (order is null) return (PaymentEventOutcome.Rejected, "unknown order");
        if (!string.Equals(order.Provider, provider, StringComparison.OrdinalIgnoreCase))
            return (PaymentEventOutcome.Rejected, $"order belongs to provider {order.Provider}");

        var kind = order.Kind.ToString().ToLowerInvariant();
        switch (notice.Kind)
        {
            case PaymentNoticeKind.Paid:
            {
                if (order.Status is OrderStatus.Paid or OrderStatus.Refunded or OrderStatus.ChargedBack)
                    return (PaymentEventOutcome.Ignored, $"order already {order.Status}");
                if (notice.AmountCents != order.PriceCents || !string.Equals(notice.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
                    return (PaymentEventOutcome.Rejected,
                        $"amount {notice.AmountCents} {notice.Currency} does not match the order ({order.PriceCents} {order.Currency})");

                // Paid also from Failed, Canceled or Expired: the buyer may have retried, or the notice came late.
                order.Status = OrderStatus.Paid;
                order.PaidAt = now;
                order.ProviderRef ??= notice.ProviderRef;
                order.StatusReason = null;
                order.UpdatedAt = now;
                var wallet = await wallets.LockAsync(order.UserId, ct);
                wallets.Apply(wallet, WalletEntryKind.Purchase, order.Coins, order.BonusCoins, order.VipDays, order.ProductName, orderId: order.Id);

                ReelGuoTelemetry.Orders.Add(1, new("outcome", "paid"), new("kind", kind));
                ReelGuoTelemetry.Revenue.Add(order.PriceCents, new KeyValuePair<string, object?>("currency", order.Currency));
                return (PaymentEventOutcome.Applied, null);
            }
            case PaymentNoticeKind.Failed:
            {
                if (order.Status is not (OrderStatus.Pending or OrderStatus.Expired or OrderStatus.Canceled))
                    return (PaymentEventOutcome.Ignored, $"order already {order.Status}");
                order.Status = OrderStatus.Failed;
                order.StatusReason = Clip(notice.Reason);
                order.UpdatedAt = now;
                ReelGuoTelemetry.Orders.Add(1, new("outcome", "failed"), new("kind", kind));
                return (PaymentEventOutcome.Applied, null);
            }
            default:
            {
                if (order.Status != OrderStatus.Paid) return (PaymentEventOutcome.Ignored, $"order is {order.Status}");
                if (notice.AmountCents != 0 && notice.AmountCents != order.PriceCents)
                    return (PaymentEventOutcome.Rejected, $"partial reversal ({notice.AmountCents} of {order.PriceCents}) must be handled by hand");

                var chargeback = notice.Kind == PaymentNoticeKind.ChargedBack;
                order.Status = chargeback ? OrderStatus.ChargedBack : OrderStatus.Refunded;
                order.ReversedAt = now;
                order.StatusReason = Clip(notice.Reason);
                order.UpdatedAt = now;
                var wallet = await wallets.LockAsync(order.UserId, ct);
                var (bonusDelta, coinsDelta) = WalletService.Reversal(wallet.BonusCoins, order.Coins, order.BonusCoins);
                wallets.Apply(wallet, chargeback ? WalletEntryKind.Chargeback : WalletEntryKind.Refund, coinsDelta, bonusDelta, -order.VipDays,
                    $"{(chargeback ? "拒付" : "退款")}：{order.ProductName}", orderId: order.Id, note: Clip(notice.Reason));

                ReelGuoTelemetry.Orders.Add(1, new("outcome", chargeback ? "charged_back" : "refunded"), new("kind", kind));
                ReelGuoTelemetry.Revenue.Add(-order.PriceCents, new KeyValuePair<string, object?>("currency", order.Currency));
                return (PaymentEventOutcome.Applied, null);
            }
        }
    }

    private static string? Clip(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim() is { Length: > 200 } r ? r[..200] : reason.Trim();
}

public static class PaymentEndpoints
{
    private const int MaxNoticeBytes = 64 * 1024;

    /// <summary>
    /// Server-to-server notices from payment providers. Outside /api/v1 on purpose: providers call from anywhere, so
    /// these are not country-blocked or age-gated; the signature check is what protects them.
    /// </summary>
    public static IEndpointRouteBuilder MapPayments(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/api/payments").ExcludeFromDescription();
        payments.MapPost("/{provider}/webhook", Webhook).RequireRateLimiting(RateLimits.Webhooks);
        payments.MapSandboxCheckout();
        return app;
    }

    private static async Task<IResult> Webhook(string provider, HttpContext http, PaymentGateway gateway, PaymentProcessor processor, CancellationToken ct)
    {
        var p = gateway.Find(provider);
        if (p is null) return ApiResults.Error(StatusCodes.Status404NotFound, "not_found", "unknown payment provider");
        if (http.Request.ContentLength is > MaxNoticeBytes)
            return ApiResults.Error(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "notice too large");

        var buffer = new char[MaxNoticeBytes + 1];
        using var reader = new StreamReader(http.Request.Body);
        var read = await reader.ReadBlockAsync(buffer, ct);
        if (read > MaxNoticeBytes) return ApiResults.Error(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "notice too large");
        var body = new string(buffer, 0, read);

        var notice = p.ReadNotice(http.Request.Headers, body);
        if (notice is null)
        {
            await processor.RecordUnverifiedAsync(p.Name, body, "bad signature or unreadable notice", ct);
            return ApiResults.Error(StatusCodes.Status400BadRequest, "invalid_signature", "notice could not be verified");
        }
        // Authentic notices get 200 even when they don't apply, so the provider stops re-sending them; anything
        // that needs a human is recorded as Rejected and raised on the status page.
        var (outcome, detail) = await processor.ApplyAsync(p.Name, notice, body, ct);
        return TypedResults.Ok(new { outcome = outcome.ToString().ToLowerInvariant(), detail });
    }
}

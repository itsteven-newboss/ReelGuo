namespace ReelGuo.Data.Entities;

public enum ProductKind
{
    /// <summary>A coin pack (金币).</summary>
    Coins,
    /// <summary>A VIP pass: every episode free for a number of days. Not auto-renewing.</summary>
    Vip,
}

/// <summary>
/// Something the store sells (admin: 商品). Orders copy the price and contents, so editing or retiring a product
/// never changes a past order.
/// </summary>
public sealed class Product
{
    public int Id { get; set; }
    public ProductKind Kind { get; set; }
    public required string Name { get; set; }
    /// <summary>Price in the currency's smallest unit (cents for USD).</summary>
    public int PriceCents { get; set; }
    /// <summary>ISO 4217 code, e.g. USD.</summary>
    public required string Currency { get; set; }
    public int Coins { get; set; }
    public int BonusCoins { get; set; }
    public int VipDays { get; set; }
    /// <summary>Short label on the store card, e.g. 热门 or 超值.</summary>
    public string? Badge { get; set; }
    public bool Active { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum OrderStatus
{
    /// <summary>Created; the viewer is at (or never reached) the provider's checkout.</summary>
    Pending,
    Paid,
    Failed,
    Canceled,
    /// <summary>Still pending a day later. A late payment notice is still honoured.</summary>
    Expired,
    Refunded,
    ChargedBack,
}

/// <summary>One purchase attempt. The product's price and contents are copied here when the order is created.</summary>
public sealed class Order
{
    public long Id { get; set; }
    /// <summary>Public reference shown to the viewer and sent to the provider. Random, so it can't be guessed.</summary>
    public required string OrderNo { get; set; }
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public int? ProductId { get; set; }

    public ProductKind Kind { get; set; }
    public required string ProductName { get; set; }
    public int PriceCents { get; set; }
    public required string Currency { get; set; }
    public int Coins { get; set; }
    public int BonusCoins { get; set; }
    public int VipDays { get; set; }

    public OrderStatus Status { get; set; }
    /// <summary>Payment provider name (e.g. sandbox). Refunds and chargebacks go back to the same provider.</summary>
    public required string Provider { get; set; }
    /// <summary>The provider's transaction or checkout id.</summary>
    public string? ProviderRef { get; set; }
    public required string Country { get; set; }
    /// <summary>Why a payment failed, or the reason given for a refund or chargeback.</summary>
    public string? StatusReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    /// <summary>When the payment was refunded or charged back.</summary>
    public DateTimeOffset? ReversedAt { get; set; }
}

/// <summary>
/// A viewer's coins and VIP. Every change is made with the row locked and is recorded in <see cref="WalletEntry"/>
/// in the same transaction.
/// </summary>
public sealed class Wallet
{
    public long UserId { get; set; }
    /// <summary>
    /// Purchased coins. Can be negative after a refund or chargeback of coins that were already spent; the next
    /// top-up fills the gap first.
    /// </summary>
    public int Coins { get; set; }
    /// <summary>Free coins (pack bonuses, goodwill credits). Spent before purchased coins; never negative.</summary>
    public int BonusCoins { get; set; }
    public DateTimeOffset? VipUntil { get; set; }
    /// <summary>Unlock the next paid episode automatically when the balance allows (the viewer's choice).</summary>
    public bool AutoUnlock { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum WalletEntryKind
{
    Purchase,
    Unlock,
    Refund,
    Chargeback,
    /// <summary>Made by the team (goodwill credit, correction), with a note.</summary>
    Adjustment,
}

/// <summary>Append-only ledger of every change to a wallet; the viewer's 消费记录 (records) page shows it.</summary>
public sealed class WalletEntry
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public WalletEntryKind Kind { get; set; }
    public int CoinsDelta { get; set; }
    public int BonusDelta { get; set; }
    public int CoinsAfter { get; set; }
    public int BonusAfter { get; set; }
    /// <summary>VIP days added; negative when a VIP purchase is reversed.</summary>
    public int VipDays { get; set; }
    public DateTimeOffset? VipUntilAfter { get; set; }
    public long? OrderId { get; set; }
    public long? SeriesId { get; set; }
    public int? EpisodeNumber { get; set; }
    /// <summary>Shown to the viewer, e.g. the pack name or 《剧名》第 3 集.</summary>
    public required string Description { get; set; }
    /// <summary>The admin who made an adjustment; null for the viewer's own actions and payments.</summary>
    public string? Actor { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>An episode a viewer bought with coins. Theirs for good, even after a later refund of the coins.</summary>
public sealed class EpisodeUnlock
{
    public long UserId { get; set; }
    public long SeriesId { get; set; }
    public int EpisodeNumber { get; set; }
    /// <summary>Total coins paid (bonus plus purchased).</summary>
    public int Coins { get; set; }
    public DateTimeOffset At { get; set; }
}

public enum PaymentEventOutcome
{
    /// <summary>Changed the order (and the wallet).</summary>
    Applied,
    /// <summary>The same event was received before.</summary>
    Duplicate,
    /// <summary>Authentic but didn't apply, e.g. a failure notice for an order that is already paid.</summary>
    Ignored,
    /// <summary>Bad signature, unknown order, or an amount that doesn't match the order. Needs a look.</summary>
    Rejected,
}

/// <summary>Every notification received from a payment provider, kept for reconciliation and support.</summary>
public sealed class PaymentEvent
{
    public long Id { get; set; }
    public required string Provider { get; set; }
    /// <summary>The provider's event id; unique per provider, which makes repeated deliveries harmless.</summary>
    public required string EventId { get; set; }
    public required string Type { get; set; }
    public string? OrderNo { get; set; }
    public long AmountCents { get; set; }
    public string? Currency { get; set; }
    public PaymentEventOutcome Outcome { get; set; }
    public string? Detail { get; set; }
    /// <summary>The raw body (truncated to 8 KB).</summary>
    public required string Payload { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

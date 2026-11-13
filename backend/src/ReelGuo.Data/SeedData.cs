using ReelGuo.Data.Entities;

namespace ReelGuo.Data;

/// <summary>
/// Rows created by the initial migration. Values must be constants: EF compares them on every
/// new migration, so changing them here produces an update migration.
/// </summary>
internal static class SeedData
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    // Channel names follow the reference product; the descriptions are our own wording.
    public static readonly Category[] Categories =
    [
        new() { Id = 1, Slug = "ai-duanju", Name = "AI成人短剧", Description = "AI 原创剧情 · 连续追更", Status = CategoryStatus.Active, SortOrder = 10, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 2, Slug = "ai-manju", Name = "AI成人漫剧", Description = "动态漫画 · 每周上新", Status = CategoryStatus.Active, SortOrder = 20, CreatedAt = Epoch, UpdatedAt = Epoch },
        // Placeholder only: no face-swap feature exists or is planned. See docs/ARCHITECTURE.md.
        new() { Id = 3, Slug = "ai-huanlian", Name = "AI换脸", Description = "即将上线 · 敬请期待", Status = CategoryStatus.ComingSoon, SortOrder = 30, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 4, Slug = "ai-mogai", Name = "AI魔改", Description = "经典桥段 · 全新演绎", Status = CategoryStatus.Active, SortOrder = 40, CreatedAt = Epoch, UpdatedAt = Epoch },
    ];

    /// <summary>Starter store (Phase 5). Placeholder prices for the business to review in Admin → 商品 before launch.</summary>
    public static readonly Product[] Products =
    [
        new() { Id = 1, Kind = ProductKind.Coins, Name = "100 金币", PriceCents = 99, Currency = "USD", Coins = 100, Active = true, SortOrder = 10, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 2, Kind = ProductKind.Coins, Name = "500 金币", PriceCents = 499, Currency = "USD", Coins = 500, BonusCoins = 50, Badge = "热门", Active = true, SortOrder = 20, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 3, Kind = ProductKind.Coins, Name = "1000 金币", PriceCents = 999, Currency = "USD", Coins = 1000, BonusCoins = 150, Badge = "超值", Active = true, SortOrder = 30, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 4, Kind = ProductKind.Coins, Name = "2000 金币", PriceCents = 1999, Currency = "USD", Coins = 2000, BonusCoins = 400, Active = true, SortOrder = 40, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 5, Kind = ProductKind.Vip, Name = "VIP 周卡", PriceCents = 599, Currency = "USD", VipDays = 7, Active = true, SortOrder = 110, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 6, Kind = ProductKind.Vip, Name = "VIP 月卡", PriceCents = 1999, Currency = "USD", VipDays = 30, Badge = "推荐", Active = true, SortOrder = 120, CreatedAt = Epoch, UpdatedAt = Epoch },
        new() { Id = 7, Kind = ProductKind.Vip, Name = "VIP 年卡", PriceCents = 9999, Currency = "USD", VipDays = 365, Active = true, SortOrder = 130, CreatedAt = Epoch, UpdatedAt = Epoch },
    ];

    public static readonly BlockedCountry[] BlockedCountries =
    [
        new() { CountryCode = "CN", Note = "默认屏蔽：中国大陆。待法务审核确认。", CreatedAt = Epoch, UpdatedAt = Epoch, UpdatedBy = "system" },
    ];
}

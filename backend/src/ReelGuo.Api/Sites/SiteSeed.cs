using Microsoft.EntityFrameworkCore;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Sites;

/// <summary>
/// A new database starts with the Chinese channels and coin packs (SeedData, in the migrations). On a site in another
/// language they are renamed to that language at start-up, once: only rows still exactly as seeded are touched, so
/// whatever an admin has changed stays. The face-swap placeholder channel is hidden there (no such feature exists;
/// an admin can show it in 频道管理).
/// </summary>
public static class SiteSeed
{
    public static async Task<int> LocalizeAsync(ReelGuoDbContext db, SiteBrand brand, TimeProvider time, ILogger log, CancellationToken ct = default)
    {
        if (brand.Chinese) return 0;
        var zh = SiteText.For("zh-CN");
        var t = brand.Text;
        var now = time.GetUtcNow();
        var changed = 0;

        var seeded = SeedData.Categories.ToDictionary(c => c.Id);
        foreach (var category in await db.Categories.Where(c => seeded.Keys.Contains(c.Id)).ToListAsync(ct))
        {
            var seed = seeded[category.Id];
            if (category.Slug != seed.Slug || category.Name != seed.Name || category.Description != seed.Description) continue;
            category.Name = t[$"seed.category.{seed.Slug}.name"];
            category.Description = t[$"seed.category.{seed.Slug}.description"];
            if (seed.Slug == "ai-huanlian" && category.Status == seed.Status) category.Status = CategoryStatus.Hidden;
            category.UpdatedAt = now;
            changed++;
        }

        var badges = new Dictionary<string, string>
        {
            [zh["seed.badge.hot"]] = t["seed.badge.hot"],
            [zh["seed.badge.value"]] = t["seed.badge.value"],
            [zh["seed.badge.recommended"]] = t["seed.badge.recommended"],
        };
        var products = SeedData.Products.ToDictionary(p => p.Id);
        foreach (var product in await db.Products.Where(p => products.Keys.Contains(p.Id)).ToListAsync(ct))
        {
            var seed = products[product.Id];
            if (product.Name != seed.Name || product.Badge != seed.Badge) continue;
            product.Name = seed.Kind == ProductKind.Coins
                ? t.Get("seed.product.coins", ("coins", seed.Coins))
                : t[seed.VipDays switch { 7 => "seed.product.vipWeek", 30 => "seed.product.vipMonth", _ => "seed.product.vipYear" }];
            if (seed.Badge is not null && badges.TryGetValue(seed.Badge, out var badge)) product.Badge = badge;
            product.UpdatedAt = now;
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("Translated {Count} default channel(s) and product(s) into {Locale}", changed, brand.Locale);
        }
        return changed;
    }
}

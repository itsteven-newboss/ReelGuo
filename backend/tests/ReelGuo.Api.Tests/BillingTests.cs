using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelGuo.Api.Billing;
using ReelGuo.Api.Users;
using ReelGuo.Data;
using ReelGuo.Data.Entities;
using static ReelGuo.Api.Tests.HttpTestExtensions;

namespace ReelGuo.Api.Tests;

/// <summary>Phase 5: paid episodes, coins, VIP, the sandbox payment flow, refunds and chargebacks.</summary>
public sealed class BillingTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private sealed record Viewer(long Id, string Token);

    private static async Task<Viewer> RegisterAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { username = $"b{Guid.NewGuid():N}"[..14], password = "viewer-pass-1", accept_terms = true }),
        };
        request.Headers.Add(UserAuth.AppHeader, "1");
        var body = await client.SendAsync(request, Ct).OkJsonAsync();
        return new Viewer(body.GetProperty("user").GetProperty("id").GetInt64(), body.GetProperty("access_token").GetString()!);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, Viewer? viewer = null, object? body = null, string country = "US")
    {
        var request = new HttpRequestMessage(method, url).From(country);
        if (viewer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request, Ct);
    }

    /// <summary>A published series whose episodes are ready to play (no real media needed: /play only signs a path).</summary>
    private static async Task<long> SeedSeriesAsync(ApiFactory api, int episodes, int price, int free, params int[] promo)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        var now = DateTimeOffset.UtcNow;
        var series = new Series
        {
            Title = "付费测试剧", CategoryId = 1, Status = SeriesStatus.Published, PublishedAt = now.AddDays(-1), EpisodeCount = episodes, TotalEpisodes = episodes,
            Territories = ["*"], LicenceStart = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-30)), RightsHolder = "T", ContractRef = "C",
            AiGenerated = true, HasRealPerson = false, EpisodePrice = price, FreeEpisodes = free, CreatedAt = now, UpdatedAt = now,
            Episodes = Enumerable.Range(1, episodes).Select(n => new Episode
            {
                EpisodeNumber = n, Title = $"第{n}集", DurationSeconds = 60, Status = EpisodeStatus.Ready, HlsPrefix = $"s/0/e/{n}/v1",
                IsFree = promo.Contains(n), CreatedAt = now, UpdatedAt = now,
            }).ToList(),
        };
        ctx.Series.Add(series);
        await ctx.SaveChangesAsync(Ct);
        return series.Id;
    }

    private static async Task<JsonElement> WalletAsync(HttpClient client, Viewer viewer) =>
        await Send(client, HttpMethod.Get, "/api/v1/me/wallet", viewer).OkJsonAsync();

    private static Task<HttpResponseMessage> Unlock(HttpClient client, Viewer viewer, long series, int ep) =>
        Send(client, HttpMethod.Post, $"/api/v1/series/{series}/episodes/{ep}/unlock", viewer);

    private static Task<HttpResponseMessage> Play(HttpClient client, long series, int ep, Viewer? viewer = null) =>
        Send(client, HttpMethod.Get, $"/api/v1/series/{series}/episodes/{ep}/play", viewer);

    private static SandboxPaymentProvider SandboxOf(ApiFactory api) =>
        api.Services.GetServices<IPaymentProvider>().OfType<SandboxPaymentProvider>().Single();

    private static async Task<Order> OrderAsync(ApiFactory api, string orderNo)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>().Orders.AsNoTracking().SingleAsync(o => o.OrderNo == orderNo, Ct);
    }

    private static Task<HttpResponseMessage> NotifyAsync(HttpClient client, string body, string signature, string country = "US")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/sandbox/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }.From(country);
        request.Headers.Add(SandboxPaymentProvider.SignatureHeader, signature);
        return client.SendAsync(request, Ct);
    }

    /// <summary>Sends a signed provider notice for an order; returns the outcome the API reports.</summary>
    private static async Task<string> NotifyAsync(ApiFactory api, HttpClient client, string orderNo, string type, long? amount = null)
    {
        var (body, signature) = SandboxOf(api).CreateNotice(type, await OrderAsync(api, orderNo), amountCents: amount);
        return (await NotifyAsync(client, body, signature).OkJsonAsync()).GetProperty("outcome").GetString()!;
    }

    private static async Task<string> OrderNoAsync(HttpClient client, Viewer viewer, int productId) =>
        (await Send(client, HttpMethod.Post, "/api/v1/orders", viewer, new { product_id = productId }).OkJsonAsync())
            .GetProperty("order").GetProperty("order_no").GetString()!;

    private static async Task<string> BuyAsync(ApiFactory api, HttpClient client, Viewer viewer, int productId)
    {
        var no = await OrderNoAsync(client, viewer, productId);
        Assert.Equal("applied", await NotifyAsync(api, client, no, "payment.succeeded"));
        return no;
    }

    private static async Task GrantAsync(HttpClient admin, long userId, int coins, int bonus = 0, int vipDays = 0) =>
        await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/members/{userId}/wallet/adjust",
            new { coins, bonus_coins = bonus, vip_days = vipDays, note = "测试发放" }).OkJsonAsync();

    private static DateTimeOffset VipUntil(JsonElement wallet) => wallet.GetProperty("vip_until").GetDateTimeOffset();

    [Fact]
    public async Task Paid_episodes_need_coins_and_unlocking_is_idempotent()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);
        var id = await SeedSeriesAsync(api, episodes: 6, price: 30, free: 2, promo: 4);

        Assert.True((await Send(client, HttpMethod.Get, "/api/v1/config").OkJsonAsync()).GetProperty("monetization").GetBoolean());
        var episodes = (await Send(client, HttpMethod.Get, $"/api/v1/series/{id}").OkJsonAsync()).GetProperty("episodes").EnumerateArray()
            .Select(e => (Ep: e.GetProperty("ep_num").GetInt32(), Free: e.GetProperty("is_free").GetBoolean(), Price: e.GetProperty("price").GetInt32())).ToList();
        Assert.Equal([(1, true, 0), (2, true, 0), (3, false, 30), (4, true, 0), (5, false, 30), (6, false, 30)], episodes);

        // Signed out: free episodes play, paid ones don't.
        Assert.Equal(HttpStatusCode.OK, (await Play(client, id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Play(client, id, 4)).StatusCode);
        var locked = await Play(client, id, 3);
        Assert.Equal(HttpStatusCode.PaymentRequired, locked.StatusCode);
        Assert.Equal("episode_locked", await locked.ErrorCodeAsync());

        var viewer = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await Play(client, id, 3, viewer)).StatusCode);
        var broke = await Unlock(client, viewer, id, 3);
        Assert.Equal(HttpStatusCode.PaymentRequired, broke.StatusCode);
        Assert.Equal("insufficient_coins", await broke.ErrorCodeAsync());

        await GrantAsync(admin, viewer.Id, coins: 50, bonus: 20);
        Assert.Equal(70, (await WalletAsync(client, viewer)).GetProperty("balance").GetInt32());

        var bought = await Unlock(client, viewer, id, 3).OkJsonAsync();
        Assert.Equal("unlocked", bought.GetProperty("result").GetString());
        Assert.Equal(30, bought.GetProperty("spent").GetInt32());
        Assert.Equal(0, bought.GetProperty("wallet").GetProperty("bonus_coins").GetInt32());   // bonus coins go first
        Assert.Equal(40, bought.GetProperty("wallet").GetProperty("coins").GetInt32());

        var again = await Unlock(client, viewer, id, 3).OkJsonAsync();
        Assert.Equal("owned", again.GetProperty("result").GetString());
        Assert.Equal(0, again.GetProperty("spent").GetInt32());
        Assert.Equal("free", (await Unlock(client, viewer, id, 1).OkJsonAsync()).GetProperty("result").GetString());
        Assert.Equal(40, (await WalletAsync(client, viewer)).GetProperty("balance").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await Play(client, id, 3, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await Play(client, id, 5, viewer)).StatusCode);
        var access = await Send(client, HttpMethod.Get, $"/api/v1/series/{id}/access", viewer).OkJsonAsync();
        Assert.Equal([3], access.GetProperty("unlocked_episodes").EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(access.GetProperty("vip_active").GetBoolean());

        var entries = (await Send(client, HttpMethod.Get, "/api/v1/me/wallet/entries", viewer).OkJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["unlock", "adjustment"], entries.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal(-10, entries[0].GetProperty("coins").GetInt32());
        Assert.Equal(-20, entries[0].GetProperty("bonus_coins").GetInt32());
        Assert.Contains("第 3 集", entries[0].GetProperty("description").GetString());

        // Support can't take a balance below zero.
        var tooMuch = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/members/{viewer.Id}/wallet/adjust", new { coins = -41, bonus_coins = 0, vip_days = 0, note = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        var audit = await admin.GetAsync("/api/admin/audit-log", Ct).OkJsonAsync();
        Assert.Contains(audit.EnumerateArray(), a => a.GetProperty("action").GetString() == "member.wallet_adjust");
    }

    [Fact]
    public async Task Concurrent_unlocks_never_overspend_or_double_charge()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);
        var id = await SeedSeriesAsync(api, episodes: 10, price: 30, free: 0);

        // 100 coins buy three 30-coin episodes; ten requests at once must not get more.
        var a = await RegisterAsync(client);
        await GrantAsync(admin, a.Id, coins: 100);
        var results = await Task.WhenAll(Enumerable.Range(1, 10).Select(ep => Unlock(client, a, id, ep)));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.PaymentRequired));
        Assert.Equal(10, (await WalletAsync(client, a)).GetProperty("balance").GetInt32());

        // The same episode five times at once is charged once.
        var b = await RegisterAsync(client);
        await GrantAsync(admin, b.Id, coins: 100);
        var same = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Unlock(client, b, id, 1).OkJsonAsync()));
        Assert.Single(same, r => r.GetProperty("result").GetString() == "unlocked");
        Assert.Equal(70, (await WalletAsync(client, b)).GetProperty("balance").GetInt32());

        await using var scope = api.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ReelGuoDbContext>();
        Assert.Equal(3, await ctx.EpisodeUnlocks.CountAsync(u => u.UserId == a.Id, Ct));
        Assert.Equal(3, await ctx.WalletEntries.CountAsync(e => e.UserId == a.Id && e.Kind == WalletEntryKind.Unlock, Ct));
        // The ledger always adds up to the balance.
        var wallet = await ctx.Wallets.SingleAsync(w => w.UserId == a.Id, Ct);
        Assert.Equal(wallet.Coins, await ctx.WalletEntries.Where(e => e.UserId == a.Id).SumAsync(e => e.CoinsDelta, Ct));
    }

    [Fact]
    public async Task Sandbox_checkout_credits_once_and_rejects_forged_or_mismatched_notices()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var viewer = await RegisterAsync(client);

        var store = await Send(client, HttpMethod.Get, "/api/v1/store").OkJsonAsync();
        Assert.True(store.GetProperty("sandbox").GetBoolean());
        Assert.Equal("USD", store.GetProperty("currency").GetString());
        Assert.Equal(7, store.GetProperty("products").GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, HttpMethod.Post, "/api/v1/orders", body: new { product_id = 2 })).StatusCode);

        // The full browser flow: order → hosted checkout page → 支付成功 → back to the app.
        var checkout = await Send(client, HttpMethod.Post, "/api/v1/orders", viewer, new { product_id = 2 }).OkJsonAsync();
        var no = checkout.GetProperty("order").GetProperty("order_no").GetString()!;
        Assert.Equal("pending", checkout.GetProperty("order").GetProperty("status").GetString());
        var url = checkout.GetProperty("checkout_url").GetString()!;
        Assert.StartsWith($"/api/payments/sandbox/checkout/{no}?sig=", url);
        var page = await client.GetStringAsync(url, Ct);
        Assert.Contains("模拟支付", page);
        Assert.Contains(no, page);
        var sig = url[(url.IndexOf("sig=", StringComparison.Ordinal) + 4)..];
        var submit = await client.PostAsync($"/api/payments/sandbox/checkout/{no}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["sig"] = sig, ["outcome"] = "paid" }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);
        Assert.Equal($"/pay/result?order={no}", submit.Headers.Location?.ToString());

        Assert.Equal("paid", (await Send(client, HttpMethod.Get, $"/api/v1/orders/{no}", viewer).OkJsonAsync()).GetProperty("status").GetString());
        var wallet = await WalletAsync(client, viewer);
        Assert.Equal(500, wallet.GetProperty("coins").GetInt32());
        Assert.Equal(50, wallet.GetProperty("bonus_coins").GetInt32());

        // Re-sent notices change nothing: the same event is a duplicate, a second "paid" for the order is ignored.
        var (body, signature) = SandboxOf(api).CreateNotice("payment.succeeded", await OrderAsync(api, no));
        Assert.Equal("ignored", (await NotifyAsync(client, body, signature).OkJsonAsync()).GetProperty("outcome").GetString());
        Assert.Equal("duplicate", (await NotifyAsync(client, body, signature).OkJsonAsync()).GetProperty("outcome").GetString());
        Assert.Equal(550, (await WalletAsync(client, viewer)).GetProperty("balance").GetInt32());

        // Forged: bad signature. Wrong amount: recorded, not credited, then the real notice still works.
        var forged = await NotifyAsync(client, body.Replace("499", "1"), signature);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal("invalid_signature", await forged.ErrorCodeAsync());
        var cheap = await OrderNoAsync(client, viewer, 1);
        Assert.Equal("rejected", await NotifyAsync(api, client, cheap, "payment.succeeded", amount: 1));
        Assert.Equal("pending", (await Send(client, HttpMethod.Get, $"/api/v1/orders/{cheap}", viewer).OkJsonAsync()).GetProperty("status").GetString());
        Assert.Equal(550, (await WalletAsync(client, viewer)).GetProperty("balance").GetInt32());
        // Providers call from anywhere: notices are not country-blocked (CN is on the default block list).
        var (paidBody, paidSig) = SandboxOf(api).CreateNotice("payment.succeeded", await OrderAsync(api, cheap));
        Assert.Equal("applied", (await NotifyAsync(client, paidBody, paidSig, country: "CN").OkJsonAsync()).GetProperty("outcome").GetString());
        Assert.Equal(650, (await WalletAsync(client, viewer)).GetProperty("balance").GetInt32());

        // A failed payment and a cancelled checkout leave the wallet alone.
        var failing = await OrderNoAsync(client, viewer, 3);
        Assert.Equal("applied", await NotifyAsync(api, client, failing, "payment.failed"));
        Assert.Equal("failed", (await Send(client, HttpMethod.Get, $"/api/v1/orders/{failing}", viewer).OkJsonAsync()).GetProperty("status").GetString());

        // Orders are private.
        var other = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"/api/v1/orders/{no}", other)).StatusCode);

        var entries = (await Send(client, HttpMethod.Get, "/api/v1/me/wallet/entries", viewer).OkJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("purchase", e.GetProperty("kind").GetString()));
        Assert.Equal(no, entries[1].GetProperty("order_no").GetString());

        var admin = await AdminClient.SignInAsync(api);
        var status = await admin.GetAsync("/api/admin/system/status", Ct).OkJsonAsync();
        Assert.Equal("sandbox", status.GetProperty("payments").GetProperty("provider").GetString());
        Assert.True(status.GetProperty("payments").GetProperty("rejected_notices24h").GetInt32() >= 2);
    }

    [Fact]
    public async Task Vip_covers_paid_episodes_and_refunds_or_chargebacks_take_back_what_was_granted()
    {
        await using var api = db.CreateApi();
        var client = api.CreateClient();
        var admin = await AdminClient.SignInAsync(api);
        var id = await SeedSeriesAsync(api, episodes: 5, price: 30, free: 1);
        var viewer = await RegisterAsync(client);
        // Tests in this class share a database (and run in random order), so revenue is compared with what was there.
        var before = await admin.GetAsync("/api/admin/analytics/revenue?days=7", Ct).OkJsonAsync();
        long Delta(JsonElement after, string field) => after.GetProperty(field).GetInt64() - before.GetProperty(field).GetInt64();

        // VIP 周卡 (7 days) twice: the second extends the first.
        var first = await BuyAsync(api, client, viewer, productId: 5);
        var until = VipUntil(await WalletAsync(client, viewer));
        Assert.InRange(until, DateTimeOffset.UtcNow.AddDays(7).AddMinutes(-2), DateTimeOffset.UtcNow.AddDays(7).AddMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await Play(client, id, 4, viewer)).StatusCode);
        Assert.Equal("vip", (await Unlock(client, viewer, id, 4).OkJsonAsync()).GetProperty("result").GetString());
        Assert.True((await Send(client, HttpMethod.Get, $"/api/v1/series/{id}/access", viewer).OkJsonAsync()).GetProperty("vip_active").GetBoolean());
        var second = await BuyAsync(api, client, viewer, productId: 5);
        Assert.Equal(until.AddDays(7), VipUntil(await WalletAsync(client, viewer)), TimeSpan.FromSeconds(5));

        // Refunding the second pass from the admin console takes its 7 days back.
        var orders = await admin.GetAsync($"/api/admin/orders?q={second}", Ct).OkJsonAsync();
        var orderId = Assert.Single(orders.GetProperty("items").EnumerateArray()).GetProperty("id").GetInt64();
        var refund = await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/orders/{orderId}/refund", new { note = "用户误购" }).OkJsonAsync();
        Assert.Equal("refunded", refund.GetProperty("order").GetProperty("status").GetString());
        Assert.False(refund.GetProperty("pending").GetBoolean());
        Assert.Equal(until, VipUntil(await WalletAsync(client, viewer)), TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.AdminSendAsync(HttpMethod.Post, $"/api/admin/orders/{orderId}/refund", new { note = "again" })).StatusCode);

        // A chargeback on the first ends VIP.
        Assert.Equal("applied", await NotifyAsync(api, client, first, "chargeback.created"));
        Assert.False((await WalletAsync(client, viewer)).GetProperty("vip_active").GetBoolean());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await Play(client, id, 4, viewer)).StatusCode);

        // A coin pack, partly spent (bonus first), then refunded: purchased coins go negative and block spending.
        var pack = await BuyAsync(api, client, viewer, productId: 2);   // 500 + 50 bonus
        await Unlock(client, viewer, id, 2).OkJsonAsync();               // 30 from the bonus
        Assert.Equal("applied", await NotifyAsync(api, client, pack, "refund.succeeded"));
        var wallet = await WalletAsync(client, viewer);
        Assert.Equal(-30, wallet.GetProperty("coins").GetInt32());
        Assert.Equal(0, wallet.GetProperty("bonus_coins").GetInt32());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await Unlock(client, viewer, id, 3)).StatusCode);
        // What was bought stays bought.
        Assert.Equal(HttpStatusCode.OK, (await Play(client, id, 2, viewer)).StatusCode);

        var revenue = await admin.GetAsync("/api/admin/analytics/revenue?days=7", Ct).OkJsonAsync();
        Assert.Equal(3, Delta(revenue, "paid_orders"));
        Assert.Equal(599 + 599 + 499, Delta(revenue, "revenue_cents"));
        Assert.Equal(2, Delta(revenue, "refunds"));
        Assert.Equal(1, Delta(revenue, "chargebacks"));
        Assert.Equal(599 + 599 + 499, Delta(revenue, "reversed_cents"));
        Assert.Equal(30, Delta(revenue, "coins_spent"));
        var top = revenue.GetProperty("top_series").EnumerateArray().Single(s => s.GetProperty("series_id").GetInt64() == id);
        Assert.Equal(30, top.GetProperty("coins").GetInt64());
        Assert.Equal(1, top.GetProperty("buyers").GetInt32());

        var memberWallet = await admin.GetAsync($"/api/admin/members/{viewer.Id}/wallet", Ct).OkJsonAsync();
        Assert.Equal(1, memberWallet.GetProperty("unlocks").GetInt32());
        Assert.Contains(memberWallet.GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString() == "chargeback");
        var audit = await admin.GetAsync("/api/admin/audit-log", Ct).OkJsonAsync();
        Assert.Contains(audit.EnumerateArray(), a => a.GetProperty("action").GetString() == "order.refund");
    }

    [Fact]
    public async Task Products_and_series_prices_are_managed_in_admin()
    {
        await using var api = db.CreateApi();
        var admin = await AdminClient.SignInAsync(api);

        var invalid = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/products",
            new { kind = "vip", name = "坏的", price_cents = 100, currency = "USD", coins = 0, bonus_coins = 0, vip_days = 0, badge = (string?)null, active = true, sort_order = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var created = await admin.AdminSendAsync(HttpMethod.Post, "/api/admin/products",
            new { kind = "coins", name = "新人礼包", price_cents = 199, currency = "usd", coins = 300, bonus_coins = 100, vip_days = 0, badge = "限时", active = true, sort_order = 5 }).OkJsonAsync();
        Assert.Equal("USD", created.GetProperty("currency").GetString());
        var productId = created.GetProperty("id").GetInt32();
        await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/products/{productId}",
            new { kind = "coins", name = "新人礼包", price_cents = 199, currency = "USD", coins = 300, bonus_coins = 100, vip_days = 0, badge = "限时", active = false, sort_order = 5 }).OkJsonAsync();
        var client = api.CreateClient();
        var store = await Send(client, HttpMethod.Get, "/api/v1/store").OkJsonAsync();
        Assert.DoesNotContain(store.GetProperty("products").EnumerateArray(), p => p.GetProperty("id").GetInt32() == productId);

        // Pricing set through the series editor.
        var id = await SeedSeriesAsync(api, episodes: 3, price: 0, free: 3);
        var series = await admin.GetAsync($"/api/admin/series/{id}", Ct).OkJsonAsync();
        var input = new Dictionary<string, object?>
        {
            ["category_slug"] = series.GetProperty("category_slug").GetString(), ["title"] = series.GetProperty("title").GetString(),
            ["description"] = "", ["tags"] = Array.Empty<string>(), ["score"] = 0, ["is_finished"] = false, ["is_original"] = false, ["total_episodes"] = 3,
            ["rights_holder"] = "T", ["contract_ref"] = "C", ["licence_start"] = series.GetProperty("licence_start").GetString(),
            ["territories"] = new[] { "*" }, ["ai_generated"] = true, ["has_real_person"] = false, ["episode_price"] = 25, ["free_episodes"] = 1,
        };
        var updated = await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{id}", input).OkJsonAsync();
        Assert.Equal(25, updated.GetProperty("episode_price").GetInt32());
        Assert.Equal(1, updated.GetProperty("free_episodes").GetInt32());
        var prices = (await Send(client, HttpMethod.Get, $"/api/v1/series/{id}").OkJsonAsync()).GetProperty("episodes").EnumerateArray()
            .Select(e => e.GetProperty("price").GetInt32());
        Assert.Equal([0, 25, 25], prices);
        input["episode_price"] = Data.Catalog.Pricing.MaxEpisodePrice + 1;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.AdminSendAsync(HttpMethod.Put, $"/api/admin/series/{id}", input)).StatusCode);
    }

    [Fact]
    public async Task With_monetization_off_everything_is_free_and_the_store_is_closed()
    {
        await using var api = db.CreateApi(new Dictionary<string, string?> { ["Billing:Enabled"] = "false" });
        var client = api.CreateClient();
        var id = await SeedSeriesAsync(api, episodes: 4, price: 30, free: 0);

        Assert.False((await Send(client, HttpMethod.Get, "/api/v1/config").OkJsonAsync()).GetProperty("monetization").GetBoolean());
        Assert.All((await Send(client, HttpMethod.Get, $"/api/v1/series/{id}").OkJsonAsync()).GetProperty("episodes").EnumerateArray(),
            e => Assert.True(e.GetProperty("is_free").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await Play(client, id, 4)).StatusCode);
        var store = await Send(client, HttpMethod.Get, "/api/v1/store");
        Assert.Equal(HttpStatusCode.NotFound, store.StatusCode);
        Assert.Equal("store_unavailable", await store.ErrorCodeAsync());
    }
}

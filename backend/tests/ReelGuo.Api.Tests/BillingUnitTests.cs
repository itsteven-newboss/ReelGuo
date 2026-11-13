using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReelGuo.Api.Billing;
using ReelGuo.Data.Catalog;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Tests;

/// <summary>Phase 5: the money rules that don't need a database.</summary>
public sealed class BillingUnitTests
{
    [Theory]
    [InlineData(true, 30, 3, 1, false, 0)]     // leading free episodes
    [InlineData(true, 30, 3, 3, false, 0)]
    [InlineData(true, 30, 3, 4, false, 30)]
    [InlineData(true, 30, 3, 9, true, 0)]      // promotional free episode
    [InlineData(true, 0, 0, 9, false, 0)]      // free series
    [InlineData(false, 30, 0, 9, false, 0)]    // monetization off
    public void Episode_price_follows_the_series_rule(bool on, int price, int free, int ep, bool isFree, int expected) =>
        Assert.Equal(expected, Pricing.EpisodePrice(on, price, free, ep, isFree));

    [Fact]
    public void Unlocks_spend_bonus_coins_first_and_never_more_than_the_balance()
    {
        Assert.Equal((20, 10), WalletService.Split(coins: 100, bonusCoins: 20, price: 30));
        Assert.Equal((30, 0), WalletService.Split(coins: 0, bonusCoins: 50, price: 30));
        Assert.Equal((0, 30), WalletService.Split(coins: 30, bonusCoins: 0, price: 30));
        Assert.Null(WalletService.Split(coins: 20, bonusCoins: 9, price: 30));
        // After a chargeback purchased coins can be negative; they count against the bonus.
        Assert.Null(WalletService.Split(coins: -40, bonusCoins: 50, price: 30));
        Assert.Equal((30, 0), WalletService.Split(coins: -10, bonusCoins: 50, price: 30));
    }

    [Fact]
    public void A_reversal_takes_back_the_bonus_that_is_left_and_the_rest_from_purchased_coins()
    {
        // Bought 500 + 50 bonus, untouched: both come back.
        Assert.Equal((-50, -500), WalletService.Reversal(bonusBalance: 50, coinsGranted: 500, bonusGranted: 50));
        // 30 of the bonus already spent: the missing 30 comes out of purchased coins.
        Assert.Equal((-20, -530), WalletService.Reversal(bonusBalance: 20, coinsGranted: 500, bonusGranted: 50));
        // Other bonus coins in the wallet are not touched beyond what the order granted.
        Assert.Equal((-50, -500), WalletService.Reversal(bonusBalance: 500, coinsGranted: 500, bonusGranted: 50));
    }

    [Fact]
    public void Order_numbers_are_readable_and_unguessable()
    {
        var a = OrderNumbers.New(new DateTimeOffset(2026, 11, 11, 3, 0, 0, TimeSpan.Zero));
        Assert.Matches("^RG261111[0-9A-HJKMNP-TV-Z]{10}$", a);
        Assert.NotEqual(a, OrderNumbers.New(DateTimeOffset.UtcNow));
        Assert.True(OrderNumbers.LooksValid(a));
        Assert.False(OrderNumbers.LooksValid("rg' or 1=1"));
    }

    private static (SandboxPaymentProvider Provider, FakeTimeProvider Clock) Sandbox(string secret = "unit-secret")
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));
        return (new SandboxPaymentProvider(Options.Create(new BillingOptions { SandboxSecret = secret }), clock), clock);
    }

    private static Order SampleOrder() => new()
    {
        OrderNo = "RG261111ABCDEFGHJK", ProductName = "500 金币", PriceCents = 499, Currency = "USD", Provider = "sandbox", Country = "US",
    };

    [Fact]
    public void Sandbox_notices_verify_only_with_the_right_secret_unchanged_and_recent()
    {
        var (sandbox, clock) = Sandbox();
        var (body, signature) = sandbox.CreateNotice("payment.succeeded", SampleOrder());
        HeaderDictionary Headers(string sig) => new() { [SandboxPaymentProvider.SignatureHeader] = sig };

        var notice = sandbox.ReadNotice(Headers(signature), body);
        Assert.NotNull(notice);
        Assert.Equal(PaymentNoticeKind.Paid, notice.Kind);
        Assert.Equal(499, notice.AmountCents);
        Assert.Equal("RG261111ABCDEFGHJK", notice.OrderNo);

        Assert.Null(sandbox.ReadNotice(Headers(signature), body.Replace("499", "1")));         // tampered
        Assert.Null(Sandbox("other-secret").Provider.ReadNotice(Headers(signature), body));   // wrong key
        Assert.Null(sandbox.ReadNotice(Headers(""), body));                                   // unsigned
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(sandbox.ReadNotice(Headers(signature), body));                            // replayed later
    }

    [Fact]
    public void Sandbox_checkout_links_are_tied_to_their_order()
    {
        var (sandbox, _) = Sandbox();
        var sig = sandbox.LinkSignature("RG261111ABCDEFGHJK");
        Assert.True(sandbox.LinkValid("RG261111ABCDEFGHJK", sig));
        Assert.False(sandbox.LinkValid("RG261111ABCDEFGHJM", sig));
        Assert.False(sandbox.LinkValid("RG261111ABCDEFGHJK", null));
    }
}

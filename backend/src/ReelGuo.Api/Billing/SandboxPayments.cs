using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelGuo.Data;
using ReelGuo.Data.Entities;

namespace ReelGuo.Api.Billing;

/// <summary>
/// Development stand-in for a real payment provider. It follows the same shape as a real one: a hosted checkout page
/// (served by the API, with 支付成功 / 支付失败 / 取消 buttons) and HMAC-signed notices to
/// <c>/api/payments/sandbox/webhook</c>, so the whole purchase flow can be exercised without money. It runs only in
/// Development unless <see cref="BillingOptions.AllowSandboxOutsideDevelopment"/> is set.
/// </summary>
/// <remarks>
/// Notice format: JSON <c>{ id, type, order_no, amount_cents, currency, reason?, provider_ref? }</c> where type is
/// payment.succeeded, payment.failed, refund.succeeded or chargeback.created. Signature header
/// <c>X-Sandbox-Signature: t={unix seconds},v1={hex HMAC-SHA256 of "{t}.{body}"}</c>, accepted within 5 minutes.
/// </remarks>
public sealed class SandboxPaymentProvider(IOptions<BillingOptions> options, TimeProvider time) : IPaymentProvider
{
    public const string ProviderName = "sandbox";
    public const string SignatureHeader = "X-Sandbox-Signature";
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string Name => ProviderName;
    public string DisplayName => "模拟支付（测试环境）";
    public bool IsSandbox => true;

    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.SandboxSecret);

    private string Hmac(string message) => Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(message)));

    /// <summary>Ties a checkout link to its order, so the page can't be pointed at another order.</summary>
    public string LinkSignature(string orderNo) => Hmac("checkout:" + orderNo)[..32];

    public bool LinkValid(string orderNo, string? sig) =>
        sig is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(LinkSignature(orderNo)), Encoding.ASCII.GetBytes(sig));

    public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        var no = request.Order.OrderNo;
        return Task.FromResult(new CheckoutSession($"/api/payments/sandbox/checkout/{no}?sig={LinkSignature(no)}", "sbx_" + no));
    }

    public string Sign(string body, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Hmac($"{t}.{body}")}";
    }

    public bool Verify(string? header, string body)
    {
        if (string.IsNullOrEmpty(header)) return false;
        string? t = null, v1 = null;
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t") t = kv[1].Trim();
            else if (kv[0].Trim() == "v1") v1 = kv[1].Trim();
        }
        if (t is null || v1 is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        if ((time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance) return false;
        var expected = Hmac($"{t}.{body}");
        return v1.Length == expected.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(v1));
    }

    private sealed record Body(string? Id, string? Type, string? OrderNo, long AmountCents, string? Currency, string? Reason, string? ProviderRef);

    public PaymentNotice? ReadNotice(IHeaderDictionary headers, string body)
    {
        if (options.Value.SandboxSecret.Length == 0 || !Verify(headers[SignatureHeader].ToString(), body)) return null;
        Body? b;
        try { b = JsonSerializer.Deserialize<Body>(body, Json); }
        catch (JsonException) { return null; }
        if (b?.Id is not { Length: > 0 and <= 128 } id || b.OrderNo is null || b.Currency is null) return null;
        PaymentNoticeKind? kind = b.Type switch
        {
            "payment.succeeded" => PaymentNoticeKind.Paid,
            "payment.failed" => PaymentNoticeKind.Failed,
            "refund.succeeded" => PaymentNoticeKind.Refunded,
            "chargeback.created" => PaymentNoticeKind.ChargedBack,
            _ => null,
        };
        return kind is null ? null : new PaymentNotice(id, b.Type!, kind.Value, b.OrderNo, b.ProviderRef, b.AmountCents, b.Currency, b.Reason);
    }

    /// <summary>Builds a signed notice body, as the sandbox's checkout page (and tests) send it.</summary>
    public (string Body, string Signature) CreateNotice(string type, Order order, string? reason = null, long? amountCents = null)
    {
        var body = JsonSerializer.Serialize(new Body($"sbx_evt_{Guid.NewGuid():N}", type, order.OrderNo, amountCents ?? order.PriceCents,
            order.Currency, reason, "sbx_" + order.OrderNo), Json);
        return (body, Sign(body, time.GetUtcNow()));
    }

    public Task<RefundResult> RefundAsync(Order order, CancellationToken ct) => Task.FromResult(new RefundResult(true));
}

public static class SandboxCheckout
{
    public static RouteGroupBuilder MapSandboxCheckout(this RouteGroupBuilder payments)
    {
        payments.MapGet("/sandbox/checkout/{orderNo}", Page);
        payments.MapPost("/sandbox/checkout/{orderNo}", Submit);
        return payments;
    }

    private static SandboxPaymentProvider? Provider(PaymentGateway gateway) => gateway.Find(SandboxPaymentProvider.ProviderName) as SandboxPaymentProvider;

    private static async Task<IResult> Page(string orderNo, string? sig, PaymentGateway gateway, ReelGuoDbContext db, IOptions<BillingOptions> options, CancellationToken ct)
    {
        if (Provider(gateway) is not { } sandbox || !sandbox.LinkValid(orderNo, sig)) return Html(Shell("链接无效", "<p>支付链接无效或已失效。</p>"), 404);
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderNo == orderNo, ct);
        if (order is null) return Html(Shell("订单不存在", "<p>订单不存在。</p>"), 404);

        var back = Enc(OrderNumbers.ReturnUrl(options.Value, orderNo));
        var details = $"""
            <dl>
              <dt>商品</dt><dd>{Enc(order.ProductName)}</dd>
              <dt>金额</dt><dd>{Enc(Money(order.PriceCents, order.Currency))}</dd>
              <dt>订单号</dt><dd><code>{Enc(order.OrderNo)}</code></dd>
            </dl>
            """;
        if (order.Status is not (OrderStatus.Pending or OrderStatus.Failed or OrderStatus.Canceled or OrderStatus.Expired))
            return Html(Shell("模拟支付", details + $"""<p>该订单已处理（{Enc(order.Status.ToString())}）。</p><p><a href="{back}">返回应用</a></p>"""));

        return Html(Shell("模拟支付", details + $"""
            <form method="post">
              <input type="hidden" name="sig" value="{Enc(sig!)}">
              <button name="outcome" value="paid" class="pay">支付成功</button>
              <button name="outcome" value="failed">支付失败（卡被拒绝）</button>
              <button name="outcome" value="cancel">取消支付</button>
            </form>
            """));
    }

    private static async Task<IResult> Submit(
        string orderNo, HttpContext http, PaymentGateway gateway, PaymentProcessor processor, ReelGuoDbContext db,
        IOptions<BillingOptions> options, TimeProvider time, CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        if (Provider(gateway) is not { } sandbox || !sandbox.LinkValid(orderNo, form["sig"])) return Html(Shell("链接无效", "<p>支付链接无效或已失效。</p>"), 404);
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderNo == orderNo, ct);
        if (order is null) return Html(Shell("订单不存在", "<p>订单不存在。</p>"), 404);

        switch (form["outcome"].ToString())
        {
            case "paid":
            case "failed":
            {
                // Goes through the same verification and processing as a notice from a real provider.
                var paid = form["outcome"] == "paid";
                var (body, signature) = sandbox.CreateNotice(paid ? "payment.succeeded" : "payment.failed", order, paid ? null : "模拟：卡被拒绝");
                var headers = new HeaderDictionary { [SandboxPaymentProvider.SignatureHeader] = signature };
                var notice = sandbox.ReadNotice(headers, body) ?? throw new InvalidOperationException("sandbox notice did not verify");
                await processor.ApplyAsync(sandbox.Name, notice, body, ct);
                break;
            }
            case "cancel":
                await db.Orders.Where(o => o.Id == order.Id && o.Status == OrderStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Canceled).SetProperty(o => o.UpdatedAt, time.GetUtcNow()), ct);
                break;
            default:
                return Html(Shell("模拟支付", "<p>未知操作。</p>"), 400);
        }
        return Results.Redirect(OrderNumbers.ReturnUrl(options.Value, orderNo), permanent: false, preserveMethod: false);
    }

    public static string Money(long cents, string currency) =>
        currency.Trim().ToUpperInvariant() == "USD"
            ? $"US${cents / 100m:0.00}"
            : $"{cents / 100m:0.00} {currency.Trim().ToUpperInvariant()}";

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    private static string Shell(string title, string body) => $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <link rel="icon" href="data:,">
        <title>{{Enc(title)}} · ReelGuo</title>
        <style>
          body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; background: #0f0f12; color: #ececf1; font: 15px/1.6 system-ui, sans-serif; }
          main { width: min(420px, 100% - 32px); background: #1c1c21; border-radius: 16px; padding: 24px; }
          .tag { display: inline-block; margin: 0 0 8px; padding: 2px 10px; border-radius: 999px; background: #4a3b12; color: #ffd27a; font-size: 12px; }
          h1 { margin: 0 0 16px; font-size: 20px; }
          dl { display: grid; grid-template-columns: auto 1fr; gap: 6px 16px; margin: 0 0 20px; }
          dt { color: #9a9aa5; } dd { margin: 0; }
          form { display: flex; flex-direction: column; gap: 10px; }
          button { height: 44px; border: 0; border-radius: 10px; background: #2c2c33; color: inherit; font: inherit; cursor: pointer; }
          button.pay { background: #8b6cff; color: #fff; font-weight: 700; }
          a { color: #b9a5ff; }
        </style>
        </head>
        <body><main>
        <p class="tag">测试环境 · 不会产生真实扣款</p>
        <h1>{{Enc(title)}}</h1>
        {{body}}
        </main></body>
        </html>
        """;

    private static IResult Html(string html, int status = 200) =>
        Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
}

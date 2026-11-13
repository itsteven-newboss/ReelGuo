using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ReelGuo.Api.Geo;
using ReelGuo.Api.Infrastructure;

namespace ReelGuo.Api.Media;

public sealed class EdgeOptions
{
    public const string Section = "Edge";

    /// <summary>Shared secret the CDN edge worker sends (X-Edge-Secret) to read the block list. Empty disables the endpoint.</summary>
    public string Secret { get; set; } = "";
}

/// <summary>What the CDN edge enforces: blocked countries (451) and taken-down series whose media must not play (410).</summary>
public sealed record EdgeConfigDto(IReadOnlyList<string> BlockedCountries, IReadOnlyList<long> RevokedSeries);

/// <summary>Configuration the CDN edge needs so media blocking matches the API's.</summary>
public static class EdgeEndpoints
{
    public static IEndpointRouteBuilder MapEdgeApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/edge/config", async (HttpContext http, IOptions<EdgeOptions> options, IBlockedCountryStore store,
                IRevokedSeriesStore revoked, CancellationToken ct) =>
            {
                var secret = options.Value.Secret;
                var provided = http.Request.Headers["X-Edge-Secret"].ToString();
                if (string.IsNullOrEmpty(secret) || !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(secret))))
                    return ApiResults.Error(StatusCodes.Status401Unauthorized, "unauthorized", "edge secret invalid");

                var blocked = await store.GetAsync(ct);
                http.Response.Headers.CacheControl = "no-store";
                var series = await revoked.GetAsync(ct);
                return TypedResults.Ok(new EdgeConfigDto(blocked.Order(StringComparer.Ordinal).ToList(), series.Order().ToList()));
            })
            .WithTags("Edge")
            .WithSummary("Block list and taken-down series for the CDN edge worker (requires X-Edge-Secret).");
        return app;
    }
}

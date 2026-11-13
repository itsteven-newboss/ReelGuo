namespace ReelGuo.Data.Entities;

/// <summary>
/// A country (or special region code) where the public app is not served.
/// Managed by the team in the admin console after legal review; see docs/ARCHITECTURE.md, "Country blocking".
/// </summary>
public sealed class BlockedCountry
{
    /// <summary>Upper-case ISO 3166-1 alpha-2 code, or one of the special codes in <see cref="Geo.CountryCodes"/>.</summary>
    public required string CountryCode { get; set; }
    /// <summary>Why it is blocked, e.g. the legal review reference.</summary>
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}

/// <summary>Append-only record of admin and moderation actions.</summary>
public sealed class AuditLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    /// <summary>Free-form JSON (jsonb) with before/after values.</summary>
    public string? DetailsJson { get; set; }
}

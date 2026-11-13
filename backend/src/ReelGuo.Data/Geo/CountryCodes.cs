using System.Collections.Frozen;

namespace ReelGuo.Data.Geo;

/// <summary>Country codes that may appear in the block list.</summary>
public static class CountryCodes
{
    /// <summary>Country could not be determined (no trusted header, or the CDN did not know).</summary>
    public const string Unknown = "XX";

    /// <summary>Cloudflare reports Tor exit traffic as T1.</summary>
    public const string Tor = "T1";

    /// <summary>Kosovo: not in ISO 3166-1, but Cloudflare and MaxMind report it as XK.</summary>
    public const string Kosovo = "XK";

    /// <summary>All 249 officially assigned ISO 3166-1 alpha-2 codes.</summary>
    public static readonly FrozenSet<string> Iso3166 = new[]
    {
        "AD", "AE", "AF", "AG", "AI", "AL", "AM", "AO", "AQ", "AR", "AS", "AT", "AU", "AW", "AX", "AZ",
        "BA", "BB", "BD", "BE", "BF", "BG", "BH", "BI", "BJ", "BL", "BM", "BN", "BO", "BQ", "BR", "BS", "BT", "BV", "BW", "BY", "BZ",
        "CA", "CC", "CD", "CF", "CG", "CH", "CI", "CK", "CL", "CM", "CN", "CO", "CR", "CU", "CV", "CW", "CX", "CY", "CZ",
        "DE", "DJ", "DK", "DM", "DO", "DZ",
        "EC", "EE", "EG", "EH", "ER", "ES", "ET",
        "FI", "FJ", "FK", "FM", "FO", "FR",
        "GA", "GB", "GD", "GE", "GF", "GG", "GH", "GI", "GL", "GM", "GN", "GP", "GQ", "GR", "GS", "GT", "GU", "GW", "GY",
        "HK", "HM", "HN", "HR", "HT", "HU",
        "ID", "IE", "IL", "IM", "IN", "IO", "IQ", "IR", "IS", "IT",
        "JE", "JM", "JO", "JP",
        "KE", "KG", "KH", "KI", "KM", "KN", "KP", "KR", "KW", "KY", "KZ",
        "LA", "LB", "LC", "LI", "LK", "LR", "LS", "LT", "LU", "LV", "LY",
        "MA", "MC", "MD", "ME", "MF", "MG", "MH", "MK", "ML", "MM", "MN", "MO", "MP", "MQ", "MR", "MS", "MT", "MU", "MV", "MW", "MX", "MY", "MZ",
        "NA", "NC", "NE", "NF", "NG", "NI", "NL", "NO", "NP", "NR", "NU", "NZ",
        "OM",
        "PA", "PE", "PF", "PG", "PH", "PK", "PL", "PM", "PN", "PR", "PS", "PT", "PW", "PY",
        "QA",
        "RE", "RO", "RS", "RU", "RW",
        "SA", "SB", "SC", "SD", "SE", "SG", "SH", "SI", "SJ", "SK", "SL", "SM", "SN", "SO", "SR", "SS", "ST", "SV", "SX", "SY", "SZ",
        "TC", "TD", "TF", "TG", "TH", "TJ", "TK", "TL", "TM", "TN", "TO", "TR", "TT", "TV", "TW", "TZ",
        "UA", "UG", "UM", "US", "UY", "UZ",
        "VA", "VC", "VE", "VG", "VI", "VN", "VU",
        "WF", "WS",
        "YE", "YT",
        "ZA", "ZM", "ZW",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Non-ISO codes the block list also accepts.</summary>
    public static readonly FrozenSet<string> Special = new[] { Unknown, Tor, Kosovo }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every code accepted by the block list, sorted.</summary>
    public static readonly IReadOnlyList<string> All = Iso3166.Concat(Special).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Upper-cases and trims; returns null if the code is not accepted.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var upper = code.Trim().ToUpperInvariant();
        return Iso3166.Contains(upper) || Special.Contains(upper) ? upper : null;
    }
}

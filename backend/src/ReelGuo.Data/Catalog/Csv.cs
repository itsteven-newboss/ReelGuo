using System.Text;

namespace ReelGuo.Data.Catalog;

/// <summary>
/// Minimal RFC 4180 CSV reader: quoted fields, doubled quotes, embedded commas and line breaks,
/// CRLF or LF line endings, and the UTF-8 BOM that Excel writes.
/// </summary>
public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        // Drop blank lines (a single empty field).
        rows.RemoveAll(r => r.Count == 1 && r[0].Trim().Length == 0);
        return rows;
    }

    /// <summary>Rows as dictionaries keyed by lower-cased, trimmed header name.</summary>
    public static (List<string> Headers, List<Dictionary<string, string>> Rows) ParseWithHeader(string text)
    {
        var all = Parse(text);
        if (all.Count == 0) return ([], []);
        var headers = all[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var rows = all.Skip(1).Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 0; c < headers.Count; c++) d[headers[c]] = c < r.Count ? r[c].Trim() : "";
            return d;
        }).ToList();
        return (headers, rows);
    }
}

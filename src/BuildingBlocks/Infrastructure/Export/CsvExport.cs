using System.Text;
using System.Text.Json;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Export;

public enum ExportFormat { Csv = 1, Json = 2 }

/// <summary>
/// Pluggable export serialization (mt6_8) — CSV/JSON first. CSV follows RFC 4180: a field is quoted
/// when it contains a comma, quote, or newline, and embedded quotes are doubled, so spreadsheet/CSV
/// injection of structure is not possible from data.
/// </summary>
public static class CsvExport
{
    public static string Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', headers.Select(Escape))).Append("\r\n");
        foreach (var row in rows)
        {
            builder.Append(string.Join(',', row.Select(Escape))).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Escape(string? field)
    {
        field ??= string.Empty;
        if (field.AsSpan().IndexOfAny(",\"\n\r") >= 0)
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }
}

/// <summary>Serialize export rows to the requested format (mt6_8).</summary>
public static class ExportSerializer
{
    public static (string Content, string ContentType, string Extension) Serialize<T>(ExportFormat format, IReadOnlyList<string> headers, IReadOnlyList<T> rows, Func<T, IReadOnlyList<string?>> project)
    {
        return format switch
        {
            ExportFormat.Json => (JsonSerializer.Serialize(rows), "application/json", "json"),
            _ => (CsvExport.Write(headers, rows.Select(project)), "text/csv", "csv"),
        };
    }
}

using ThreeCommerce.BuildingBlocks.Infrastructure.Export;

namespace ThreeCommerce.Entity.Tests;

public class ExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    // ---- CSV (RFC 4180) ----

    [Fact]
    public void Csv_writes_a_header_and_crlf_rows()
    {
        var csv = CsvExport.Write(["id", "name"], new[] { new string?[] { "1", "Acme" }, new string?[] { "2", "Globex" } });
        Assert.Equal("id,name\r\n1,Acme\r\n2,Globex\r\n", csv);
    }

    [Fact]
    public void Csv_quotes_fields_with_commas_quotes_and_newlines()
    {
        var csv = CsvExport.Write(["name", "note"], new[] { new string?[] { "Acme, Inc", "a\nb" }, new string?[] { "say \"hi\"", null } });
        Assert.Equal("name,note\r\n\"Acme, Inc\",\"a\nb\"\r\n\"say \"\"hi\"\"\",\r\n", csv);
    }

    [Fact]
    public void Serializer_switches_format_and_content_type()
    {
        var rows = new[] { new { Id = 1 } };
        var (_, csvType, csvExt) = ExportSerializer.Serialize(ExportFormat.Csv, ["Id"], rows, r => new string?[] { r.Id.ToString() });
        var (json, jsonType, _) = ExportSerializer.Serialize(ExportFormat.Json, ["Id"], rows, r => new string?[] { r.Id.ToString() });

        Assert.Equal("text/csv", csvType);
        Assert.Equal("csv", csvExt);
        Assert.Equal("application/json", jsonType);
        Assert.Contains("\"Id\":1", json);
    }

    // ---- expiring signed downloads ----

    [Fact]
    public void Signed_download_is_valid_until_it_expires()
    {
        var token = SignedDownload.CreateToken("k", "export/123", Now.AddMinutes(10));

        Assert.True(SignedDownload.IsValid("k", "export/123", token, Now));
        Assert.False(SignedDownload.IsValid("k", "export/123", token, Now.AddMinutes(11))); // expired
    }

    [Fact]
    public void Signed_download_rejects_a_different_resource_secret_or_tampered_token()
    {
        var token = SignedDownload.CreateToken("k", "export/123", Now.AddMinutes(10));

        Assert.False(SignedDownload.IsValid("k", "export/999", token, Now));   // different artifact
        Assert.False(SignedDownload.IsValid("other", "export/123", token, Now)); // different secret
        Assert.False(SignedDownload.IsValid("k", "export/123", token + "00", Now)); // tampered signature
        Assert.False(SignedDownload.IsValid("k", "export/123", "garbage", Now));
    }

    // ---- redaction (erasure where retention is required) ----

    [Fact]
    public void Redaction_anonymizes_pii()
    {
        Assert.Equal("[redacted]@acme.com", Redaction.Email("jane.doe@acme.com"));
        Assert.Equal("[redacted]", Redaction.Name("Jane Doe"));
        Assert.Equal("***6789", Redaction.Phone("+61 412 346 789"));
    }

    [Fact]
    public void Pseudonym_is_stable_opaque_and_irreversible()
    {
        var first = Redaction.Pseudonym("k", "user-1");
        Assert.Equal(first, Redaction.Pseudonym("k", "user-1")); // stable — retained rows still correlate
        Assert.NotEqual(first, Redaction.Pseudonym("k", "user-2"));
        Assert.Equal(16, first.Length);
        Assert.DoesNotContain("user-1", first); // opaque
    }
}

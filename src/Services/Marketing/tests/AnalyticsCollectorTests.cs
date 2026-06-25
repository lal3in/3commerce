using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Tests;

public class AnalyticsCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static AnalyticsEventInput Event(string id, int schema = 1, string type = "page_view", IReadOnlyDictionary<string, string>? payload = null) =>
        new(schema, type, "visitor-1", "session-1", null, AnalyticsConsent: true, Now, id, payload);

    [Theory]
    [InlineData("203.0.113.42", "203.0.113.0")]
    [InlineData("8.8.8.8", "8.8.8.0")]
    [InlineData("not-an-ip", "unknown")]
    [InlineData("", "unknown")]
    public void Ip_is_anonymised_never_stored_raw(string raw, string expected)
    {
        Assert.Equal(expected, IpAnonymizer.Anonymize(raw));
    }

    [Fact]
    public void IPv6_is_truncated_to_a_48_prefix()
    {
        var anonymized = IpAnonymizer.Anonymize("2001:db8:abcd:1234:5678:9abc:def0:1234");
        Assert.StartsWith("2001:db8:abcd:", anonymized);
        Assert.DoesNotContain("5678", anonymized);
    }

    [Fact]
    public void Payment_and_account_fields_are_stripped_regardless_of_spelling()
    {
        var payload = new Dictionary<string, string>
        {
            ["page"] = "/checkout",
            ["Card_Number"] = "4111111111111111",
            ["cvv"] = "123",
            ["password"] = "hunter2",
            ["accountNumber"] = "12345678",
            ["productId"] = "p-1",
        };

        var clean = AnalyticsPayload.Sanitize(payload);

        Assert.Equal(2, clean.Count);
        Assert.True(clean.ContainsKey("page"));
        Assert.True(clean.ContainsKey("productId"));
        Assert.DoesNotContain(clean.Keys, k => k.Contains("ard") || k == "cvv" || k == "password");
    }

    [Fact]
    public void A_batch_accepts_valid_events_and_sanitizes_payloads()
    {
        var result = AnalyticsCollector.Accept(Tenant, new[]
        {
            Event("e1", payload: new Dictionary<string, string> { ["cvv"] = "999", ["page"] = "/" }),
        }, new HashSet<string>());

        Assert.Single(result.Accepted);
        Assert.Empty(result.Rejected);
        Assert.Equal(Tenant, result.Accepted[0].TenantId);
        Assert.False(result.Accepted[0].Payload.ContainsKey("cvv")); // sanitized
        Assert.True(result.Accepted[0].AnalyticsConsent);            // consent snapshot kept
    }

    [Fact]
    public void Duplicate_event_ids_are_idempotent_within_a_batch_and_against_stored()
    {
        var stored = new HashSet<string> { "already" };
        var result = AnalyticsCollector.Accept(Tenant, new[]
        {
            Event("e1"), Event("e1"), Event("already"),
        }, stored);

        Assert.Single(result.Accepted); // e1 once; "already" skipped; second e1 skipped
        Assert.Equal("e1", result.Accepted[0].EventId);
        Assert.Empty(result.Rejected); // dedupe is a no-op, not a rejection
    }

    [Fact]
    public void Unsupported_schema_or_missing_fields_are_rejected_not_thrown()
    {
        var result = AnalyticsCollector.Accept(Tenant, new[]
        {
            Event("good"),
            Event("future", schema: 99),
            Event("notype", type: ""),
        }, new HashSet<string>());

        Assert.Single(result.Accepted);
        Assert.Equal(2, result.Rejected.Count);
    }
}

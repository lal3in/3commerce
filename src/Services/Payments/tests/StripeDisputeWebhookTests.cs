using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers.Stripe;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// The Stripe adapter must normalize the FULL payment/dispute event set — not just PaymentIntent events.
/// Regression guard: the parser used to early-return null for any event whose object was not a
/// PaymentIntent, silently dropping every charge.dispute.* notification. Signatures are computed exactly as
/// Stripe does: HMAC-SHA256 over "{timestamp}.{payload}".
/// </summary>
public class StripeDisputeWebhookTests
{
    private static StripePaymentProvider Provider() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:SecretKey"] = "sk_test_dummy" })
        .Build());

    private static string Sign(string secret, string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
        return $"t={timestamp},v1={signature}";
    }

    private static string Envelope(string type, string dataObject) =>
        "{\"id\":\"evt_1\",\"object\":\"event\",\"api_version\":\"2025-01-01\","
        + "\"request\":{\"id\":\"req_1\",\"idempotency_key\":null},"
        + "\"type\":\"" + type + "\",\"data\":{\"object\":" + dataObject + "}}";

    private const string DisputeObject =
        """{"id":"dp_test_1","object":"dispute","charge":"ch_test_1","payment_intent":"pi_test_1","status":"needs_response","amount":9000,"balance_transactions":[{"id":"txn_1","object":"balance_transaction","fee":1500}]}""";

    private static string DisputeObjectWith(string status) =>
        $$"""{"id":"dp_test_1","object":"dispute","charge":"ch_test_1","payment_intent":"pi_test_1","status":"{{status}}","amount":9000,"balance_transactions":[]}""";

    private PaymentWebhookEvent Parse(string payload) =>
        Provider().ParseWebhook(payload, Sign("whsec_x", payload), ["whsec_x"])!;

    [Fact]
    public void Parses_dispute_created_with_intent_dispute_id_and_fee()
    {
        var ev = Parse(Envelope("charge.dispute.created", DisputeObject));

        Assert.NotNull(ev);
        Assert.Equal(PaymentWebhookKind.DisputeCreated, ev.Kind);
        Assert.Equal("pi_test_1", ev.PaymentIntentId);
        Assert.Equal("dp_test_1", ev.ProviderDisputeId);
        Assert.Equal(9000, ev.AmountMinor);
        Assert.Equal(1500, ev.FeeMinor);
    }

    [Theory]
    [InlineData("charge.dispute.updated", PaymentWebhookKind.DisputeUpdated)]
    [InlineData("charge.dispute.funds_withdrawn", PaymentWebhookKind.DisputeFundsWithdrawn)]
    [InlineData("charge.dispute.funds_reinstated", PaymentWebhookKind.DisputeFundsReinstated)]
    public void Parses_the_intermediate_dispute_events(string type, PaymentWebhookKind expected)
    {
        var ev = Parse(Envelope(type, DisputeObject));

        Assert.NotNull(ev);
        Assert.Equal(expected, ev.Kind);
        Assert.Equal("pi_test_1", ev.PaymentIntentId);
    }

    [Fact]
    public void Dispute_closed_lost_maps_to_the_terminal_chargeback_kind()
    {
        var ev = Parse(Envelope("charge.dispute.closed", DisputeObjectWith("lost")));

        Assert.Equal(PaymentWebhookKind.DisputeClosedLost, ev.Kind);
        Assert.Equal("lost", ev.DisputeStatusRaw);
    }

    [Fact]
    public void Dispute_closed_won_maps_to_the_won_kind()
    {
        var ev = Parse(Envelope("charge.dispute.closed", DisputeObjectWith("won")));

        Assert.Equal(PaymentWebhookKind.DisputeClosedWon, ev.Kind);
        Assert.Equal("won", ev.DisputeStatusRaw);
    }

    [Fact]
    public void Payment_intent_canceled_maps_to_voided()
    {
        var pi = """{"id":"pi_test_2","object":"payment_intent","amount":4200,"amount_received":0,"status":"canceled"}""";
        var ev = Parse(Envelope("payment_intent.canceled", pi));

        Assert.Equal(PaymentWebhookKind.PaymentVoided, ev.Kind);
        Assert.Equal("pi_test_2", ev.PaymentIntentId);
        Assert.Equal(4200, ev.AmountMinor);
    }

    [Fact]
    public void Still_parses_the_plain_payment_intent_events()
    {
        var pi = """{"id":"pi_test_3","object":"payment_intent","amount":4200,"amount_received":4200,"status":"succeeded"}""";
        var ev = Parse(Envelope("payment_intent.succeeded", pi));

        Assert.Equal(PaymentWebhookKind.PaymentSucceeded, ev.Kind);
        Assert.Equal(4200, ev.AmountMinor);
    }
}

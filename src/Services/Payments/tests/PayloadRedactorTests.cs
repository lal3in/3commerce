using System.Text.Json.Nodes;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Mandatory redaction rules for the TEST-ONLY payload (pay_3, ADR-0039): never PAN/CVV/wallet
/// token/raw secrets; provider payment-method refs reduced to last-4; sensitive-named fields
/// stripped from arbitrary JSON.
/// </summary>
public class PayloadRedactorTests
{
    private static PaymentRequest Request(
        string? walletToken = null,
        string? providerPaymentMethodId = null,
        string? providerCustomerId = "cus_test_123",
        MockScenario? scenario = null) =>
        new(
            OrderId: Guid.Parse("3f2a0000-0000-0000-0000-000000000c91"),
            AmountMinor: 4990,
            Currency: "EUR",
            IdempotencyKey: "ord-3f2a-1",
            MethodKind: PaymentMethodKind.ApplePay,
            Account: PaymentTestSupport.Account(PaymentProviderMode.Test),
            ProviderCustomerId: providerCustomerId,
            ProviderPaymentMethodId: providerPaymentMethodId,
            WalletToken: walletToken,
            SetupFutureUsage: false,
            Scenario: scenario);

    [Fact]
    public void Authorize_payload_never_contains_the_wallet_token()
    {
        var json = PayloadRedactor.ToJson(Request(walletToken: "APPLEPAY_DPAN_CRYPTOGRAM_BLOB_XYZ"));

        Assert.DoesNotContain("APPLEPAY_DPAN_CRYPTOGRAM_BLOB_XYZ", json);
        Assert.DoesNotContain("walletToken", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Authorize_payload_masks_the_payment_method_ref_to_last4()
    {
        var json = PayloadRedactor.ToJson(Request(providerPaymentMethodId: "pm_1PxYzSecretRef4242"));

        Assert.Contains("pm_redacted_4242", json);
        Assert.DoesNotContain("pm_1PxYzSecretRef4242", json);
    }

    [Fact]
    public void Authorize_payload_carries_the_non_sensitive_fields_with_numeric_method_kind()
    {
        var node = JsonNode.Parse(PayloadRedactor.ToJson(Request()))!.AsObject();

        Assert.Equal(4990, (long)node["amountMinor"]!);
        Assert.Equal("EUR", (string)node["currency"]!);
        Assert.Equal("ord-3f2a-1", (string)node["idempotencyKey"]!);
        Assert.Equal((int)PaymentMethodKind.ApplePay, (int)node["methodKind"]!); // numeric enum on the wire
        Assert.Equal("cus_test_123", (string)node["providerCustomerId"]!);
        Assert.False((bool)node["setupFutureUsage"]!);
    }

    [Fact]
    public void Refund_payload_contains_only_intent_amount_and_key()
    {
        var node = JsonNode.Parse(PayloadRedactor.RefundToJson("pi_fake_abc", 500, "refund-1"))!.AsObject();

        Assert.Equal(3, node.Count);
        Assert.Equal("pi_fake_abc", (string)node["paymentIntentId"]!);
        Assert.Equal(500, (long)node["amountMinor"]!);
        Assert.Equal("refund-1", (string)node["idempotencyKey"]!);
    }

    [Theory]
    [InlineData("walletToken")]
    [InlineData("client_secret")]
    [InlineData("cardCvv")]
    [InlineData("PanNumber")]
    [InlineData("ApiSecret")]
    public void Redact_strips_sensitive_named_fields(string sensitiveName)
    {
        var input = new JsonObject { ["amountMinor"] = 100, [sensitiveName] = "SENSITIVE" };

        var result = PayloadRedactor.Redact(input);

        Assert.False(result.ContainsKey(sensitiveName));
        Assert.Equal(100, (int)result["amountMinor"]!);
    }

    [Fact]
    public void MaskPaymentMethodId_handles_short_and_empty_values()
    {
        Assert.Equal(string.Empty, PayloadRedactor.MaskPaymentMethodId(null));
        Assert.Equal(string.Empty, PayloadRedactor.MaskPaymentMethodId("  "));
        Assert.Equal("pm_redacted_ab1", PayloadRedactor.MaskPaymentMethodId("ab1"));
    }
}

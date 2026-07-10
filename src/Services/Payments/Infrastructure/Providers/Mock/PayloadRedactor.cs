using System.Text.Json;
using System.Text.Json.Nodes;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.Mock;

/// <summary>
/// Turns a <see cref="PaymentRequest"/> (or a refund request) into the redacted JSON that the
/// TEST-ONLY payload email embeds (pay_3, ADR-0039). The redaction rules are mandatory and
/// fail-safe by construction — the payload is built explicitly from a known allow-list of
/// non-sensitive fields, so a sensitive value cannot leak by being added later:
/// <list type="bullet">
///   <item>PAN, CVV, full card numbers and <c>WalletToken</c> are NEVER included.</item>
///   <item>Provider payment-method refs are reduced to a <c>pm_redacted_&lt;last4&gt;</c> marker (SAQ-A).</item>
///   <item>Any field whose name contains token/secret/cvv/pan is dropped (defence-in-depth for
///         <see cref="Redact"/> over arbitrary objects).</item>
/// </list>
/// </summary>
public static class PayloadRedactor
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly string[] SensitiveFragments = ["token", "secret", "cvv", "pan"];

    /// <summary>True if a field name looks sensitive (token/secret/cvv/pan), case-insensitively.</summary>
    public static bool IsSensitiveFieldName(string fieldName) =>
        SensitiveFragments.Any(fragment => fieldName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reduces a provider payment-method id to a <c>pm_redacted_&lt;last4&gt;</c> marker (never the full ref).</summary>
    public static string MaskPaymentMethodId(string? providerPaymentMethodId)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentMethodId))
        {
            return string.Empty;
        }

        var alnum = new string(providerPaymentMethodId.Where(char.IsLetterOrDigit).ToArray());
        var last4 = alnum.Length <= 4 ? alnum : alnum[^4..];
        return $"pm_redacted_{last4}";
    }

    /// <summary>The redacted "what we would have sent" payload for an authorize request.</summary>
    public static string ToJson(PaymentRequest request)
    {
        var payload = new JsonObject
        {
            ["amountMinor"] = request.AmountMinor,
            ["currency"] = request.Currency,
            ["idempotencyKey"] = request.IdempotencyKey,
            ["methodKind"] = (int)request.MethodKind,
            ["providerCustomerId"] = request.ProviderCustomerId,
            ["providerPaymentMethodId"] = request.ProviderPaymentMethodId is null
                ? null
                : MaskPaymentMethodId(request.ProviderPaymentMethodId),
            ["setupFutureUsage"] = request.SetupFutureUsage,
            // walletToken / PAN / CVV are deliberately absent — they are never materialised anywhere.
        };
        return payload.ToJsonString(Indented);
    }

    /// <summary>The redacted "what we would have sent" payload for a refund request.</summary>
    public static string RefundToJson(string paymentIntentId, long amountMinor, string idempotencyKey)
    {
        var payload = new JsonObject
        {
            ["paymentIntentId"] = paymentIntentId,
            ["amountMinor"] = amountMinor,
            ["idempotencyKey"] = idempotencyKey,
        };
        return payload.ToJsonString(Indented);
    }

    /// <summary>
    /// Defence-in-depth strip of sensitive-named properties from an arbitrary JSON object, so any
    /// future free-form payload passed through here is scrubbed by field name too.
    /// </summary>
    public static JsonObject Redact(JsonObject input)
    {
        var result = new JsonObject();
        foreach (var (key, value) in input)
        {
            if (IsSensitiveFieldName(key))
            {
                continue;
            }

            result[key] = value?.DeepClone();
        }

        return result;
    }
}

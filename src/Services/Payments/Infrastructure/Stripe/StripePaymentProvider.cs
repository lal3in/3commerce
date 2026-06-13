using Microsoft.Extensions.Configuration;
using Stripe;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Stripe;

/// <summary>
/// Real Stripe adapter (test mode). Card data never touches us — the client confirms with
/// the Payment Element using the returned client secret (SAQ-A). The webhook is the single
/// trusted source of payment outcome.
/// </summary>
public sealed class StripePaymentProvider : IPaymentProvider
{
    private readonly string _webhookSecret;
    private readonly PaymentIntentService _intents = new();
    private readonly RefundService _refunds = new();

    public StripePaymentProvider(IConfiguration configuration)
    {
        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"]
            ?? throw new InvalidOperationException("Stripe:SecretKey is not configured.");
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? string.Empty;
    }

    public async Task<PaymentIntentResult> CreateIntentAsync(Guid orderId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct)
    {
        var intent = await _intents.CreateAsync(
            new PaymentIntentCreateOptions
            {
                Amount = amountMinor,
                Currency = currency.ToLowerInvariant(),
                AutomaticPaymentMethods = new() { Enabled = true },
                Metadata = new() { ["order_id"] = orderId.ToString() },
            },
            new RequestOptions { IdempotencyKey = idempotencyKey },
            ct);

        return new PaymentIntentResult(intent.Id, intent.ClientSecret);
    }

    public async Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct)
    {
        var refund = await _refunds.CreateAsync(
            new RefundCreateOptions { PaymentIntent = paymentIntentId, Amount = amountMinor },
            new RequestOptions { IdempotencyKey = idempotencyKey },
            ct);

        return new ProviderRefundResult(refund.Id, refund.Status is "succeeded" or "pending");
    }

    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader)
    {
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, _webhookSecret);
        }
        catch (StripeException)
        {
            return null; // bad signature
        }

        if (stripeEvent.Data.Object is not PaymentIntent intent)
        {
            return null;
        }

        return stripeEvent.Type switch
        {
            "payment_intent.succeeded" => new PaymentWebhookEvent(
                stripeEvent.Id, PaymentWebhookKind.PaymentSucceeded, intent.Id, intent.AmountReceived, 0, null),
            "payment_intent.payment_failed" => new PaymentWebhookEvent(
                stripeEvent.Id, PaymentWebhookKind.PaymentFailed, intent.Id, intent.Amount, 0,
                intent.LastPaymentError?.Message ?? "payment failed"),
            _ => null,
        };
    }
}

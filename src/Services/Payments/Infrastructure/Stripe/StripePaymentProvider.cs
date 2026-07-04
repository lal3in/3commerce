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
    private readonly PaymentIntentService _intents = new();
    private readonly RefundService _refunds = new();
    private readonly CustomerService _customers = new();
    private readonly SetupIntentService _setupIntents = new();
    private readonly PaymentMethodService _paymentMethods = new();

    public StripePaymentProvider(IConfiguration configuration)
    {
        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"]
            ?? throw new InvalidOperationException("Stripe:SecretKey is not configured.");
    }

    public async Task<PaymentIntentResult> CreateIntentAsync(
        Guid orderId,
        long amountMinor,
        string currency,
        string idempotencyKey,
        string? providerCustomerId,
        string? providerPaymentMethodId,
        bool setupFutureUsage,
        CancellationToken ct)
    {
        var options = new PaymentIntentCreateOptions
        {
            Amount = amountMinor,
            Currency = currency.ToLowerInvariant(),
            AutomaticPaymentMethods = providerPaymentMethodId is null ? new() { Enabled = true } : null,
            Customer = providerCustomerId,
            PaymentMethod = providerPaymentMethodId,
            Confirm = providerPaymentMethodId is not null,
            OffSession = providerPaymentMethodId is not null,
            SetupFutureUsage = setupFutureUsage ? "off_session" : null,
            Metadata = new() { ["order_id"] = orderId.ToString() },
        };

        var intent = await _intents.CreateAsync(
            options,
            new RequestOptions { IdempotencyKey = idempotencyKey },
            ct);

        return new PaymentIntentResult(intent.Id, intent.ClientSecret);
    }

    public async Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct)
    {
        var customer = await _customers.CreateAsync(
            new CustomerCreateOptions
            {
                Email = email,
                Metadata = new() { ["user_id"] = userId.ToString() },
            },
            cancellationToken: ct);
        return customer.Id;
    }

    public async Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct)
    {
        var intent = await _setupIntents.CreateAsync(
            new SetupIntentCreateOptions
            {
                Customer = providerCustomerId,
                AutomaticPaymentMethods = new() { Enabled = true },
                Usage = "off_session",
            },
            cancellationToken: ct);
        return new SetupIntentResult(intent.Id, intent.ClientSecret);
    }

    public async Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct)
    {
        var method = await _paymentMethods.GetAsync(providerPaymentMethodId, cancellationToken: ct);
        return new SavedPaymentMethodDetails(
            method.Id,
            method.Card?.Brand ?? "card",
            method.Card?.Last4 ?? "unknown",
            (int)(method.Card?.ExpMonth ?? 0),
            (int)(method.Card?.ExpYear ?? 0));
    }

    public async Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct)
    {
        var refund = await _refunds.CreateAsync(
            new RefundCreateOptions { PaymentIntent = paymentIntentId, Amount = amountMinor },
            new RequestOptions { IdempotencyKey = idempotencyKey },
            ct);

        return new ProviderRefundResult(refund.Id, refund.Status is "succeeded" or "pending");
    }

    public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets)
    {
        // Rotation-safe: any active secret verifies (def_2). throwOnApiVersionMismatch off — the
        // signature is the trust boundary; an SDK/api-version skew must not silently drop payments.
        Event? stripeEvent = null;
        foreach (var secret in secrets)
        {
            try
            {
                stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, secret, throwOnApiVersionMismatch: false);
                break;
            }
            catch (StripeException)
            {
                // try the next active secret
            }
        }

        if (stripeEvent is null)
        {
            return null; // bad signature (or no secret configured)
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

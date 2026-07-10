using Microsoft.Extensions.Configuration;
using Stripe;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.Stripe;

/// <summary>
/// Real Stripe adapter. Card data never touches us — the client confirms with the Payment Element
/// using the returned client secret (SAQ-A). The webhook is the single trusted source of payment
/// outcome. Resolved through <see cref="IPaymentProviderRegistry"/> by <see cref="ProviderKey"/>.
/// The secret key is read lazily (on first API call, not construction) so the adapter can live in
/// DI alongside the mock adapter without a key present in LocalMock/dev.
/// </summary>
public sealed class StripePaymentProvider : IPaymentProvider
{
    private readonly IConfiguration _configuration;
    private readonly PaymentIntentService _intents = new();
    private readonly RefundService _refunds = new();
    private readonly CustomerService _customers = new();
    private readonly SetupIntentService _setupIntents = new();
    private readonly PaymentMethodService _paymentMethods = new();

    public StripePaymentProvider(IConfiguration configuration) => _configuration = configuration;

    public string ProviderKey => "stripe";

    /// <summary>Sets the process-wide Stripe key on first use; refuses (typed) if it is not configured.</summary>
    private void EnsureApiKey() =>
        StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"] is { Length: > 0 } key
            ? key
            : throw new PaymentConfigurationException("Stripe:SecretKey is not configured.");

    public async Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
    {
        EnsureApiKey();
        var options = new PaymentIntentCreateOptions
        {
            Amount = request.AmountMinor,
            Currency = request.Currency.ToLowerInvariant(),
            AutomaticPaymentMethods = request.ProviderPaymentMethodId is null ? new() { Enabled = true } : null,
            Customer = request.ProviderCustomerId,
            PaymentMethod = request.ProviderPaymentMethodId,
            Confirm = request.ProviderPaymentMethodId is not null,
            OffSession = request.ProviderPaymentMethodId is not null,
            SetupFutureUsage = request.SetupFutureUsage ? "off_session" : null,
            Metadata = new() { ["order_id"] = request.OrderId.ToString() },
        };

        var intent = await _intents.CreateAsync(
            options,
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            ct);

        return new PaymentResponse(intent.Id, intent.ClientSecret, MapOutcome(intent.Status));
    }

    /// <summary>Maps Stripe's intent status to the provider-agnostic outcome. The webhook still owns the final truth.</summary>
    private static PaymentOutcome MapOutcome(string? status) => status switch
    {
        "succeeded" => PaymentOutcome.Succeeded,
        "canceled" => PaymentOutcome.Cancelled,
        _ => PaymentOutcome.RequiresAction, // requires_confirmation / requires_action / processing — client confirms via secret
    };

    public async Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct)
    {
        EnsureApiKey();
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
        EnsureApiKey();
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
        EnsureApiKey();
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
        EnsureApiKey();
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

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
public sealed class StripePaymentProvider : IPaymentProvider, IDirectDebitProvider, IWebhookRegistrationProvider
{
    private readonly IConfiguration _configuration;
    private readonly PaymentIntentService _intents = new();
    private readonly RefundService _refunds = new();
    private readonly CustomerService _customers = new();
    private readonly SetupIntentService _setupIntents = new();
    private readonly PaymentMethodService _paymentMethods = new();
    private readonly WebhookEndpointService _webhookEndpoints = new();

    // The full payment/dispute event set the processor understands (Phase 1). Registering the endpoint with
    // exactly these keeps the provider from sending — and us from being billed for parsing — anything else.
    private static readonly List<string> SubscribedEvents =
    [
        "payment_intent.succeeded", "payment_intent.payment_failed", "payment_intent.canceled",
        "charge.dispute.created", "charge.dispute.updated", "charge.dispute.funds_withdrawn",
        "charge.dispute.funds_reinstated", "charge.dispute.closed",
    ];

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

    public async Task<MandateSetupResult> CreateMandateSetupAsync(string providerCustomerId, DirectDebitScheme scheme, string currency, CancellationToken ct)
    {
        EnsureApiKey();
        // A SetupIntent scoped to the scheme's bank-debit payment-method type, saved for off-session reuse.
        // The client collects bank details + mandate acceptance with the returned secret (SAQ-A).
        var intent = await _setupIntents.CreateAsync(
            new SetupIntentCreateOptions
            {
                Customer = providerCustomerId,
                PaymentMethodTypes = [scheme.ToStripePaymentMethodType()],
                Usage = "off_session",
            },
            cancellationToken: ct);
        return new MandateSetupResult(intent.Id, intent.ClientSecret);
    }

    public async Task<WebhookRegistrationResult> RegisterWebhookAsync(string url, CancellationToken ct)
    {
        EnsureApiKey();
        var endpoint = await _webhookEndpoints.CreateAsync(
            new WebhookEndpointCreateOptions
            {
                Url = url,
                EnabledEvents = SubscribedEvents,
                Description = "3commerce payments webhook",
            },
            cancellationToken: ct);
        // Secret is only returned on create — the caller stores it (rotation-safe: old secrets stay valid).
        return new WebhookRegistrationResult(endpoint.Id, endpoint.Secret);
    }

    public async Task DisableWebhookAsync(string endpointId, CancellationToken ct)
    {
        EnsureApiKey();
        try
        {
            await _webhookEndpoints.DeleteAsync(endpointId, cancellationToken: ct);
        }
        catch (StripeException)
        {
            // Best-effort: an already-deleted/unknown endpoint is not an error for our purposes.
        }
    }

    public async Task<MandateConfirmation> GetMandateAsync(string setupIntentId, CancellationToken ct)
    {
        EnsureApiKey();
        var intent = await _setupIntents.GetAsync(setupIntentId, cancellationToken: ct);
        // Bank debits settle/confirm asynchronously — only a succeeded setup carries the mandate + method.
        var confirmed = intent.Status == "succeeded";
        return new MandateConfirmation(confirmed, intent.MandateId, intent.PaymentMethodId);
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

        // Branch on the event TYPE first: dispute events carry a Dispute object (not a PaymentIntent), so
        // an early "is not PaymentIntent" return would silently drop every charge.dispute.* notification.
        return stripeEvent.Type switch
        {
            "payment_intent.succeeded" when stripeEvent.Data.Object is PaymentIntent pi => new PaymentWebhookEvent(
                stripeEvent.Id, PaymentWebhookKind.PaymentSucceeded, pi.Id, pi.AmountReceived, 0, null),
            "payment_intent.payment_failed" when stripeEvent.Data.Object is PaymentIntent pi => new PaymentWebhookEvent(
                stripeEvent.Id, PaymentWebhookKind.PaymentFailed, pi.Id, pi.Amount, 0,
                pi.LastPaymentError?.Message ?? "payment failed"),
            "payment_intent.canceled" when stripeEvent.Data.Object is PaymentIntent pi => new PaymentWebhookEvent(
                stripeEvent.Id, PaymentWebhookKind.PaymentVoided, pi.Id, pi.Amount, 0, "payment voided"),
            "charge.dispute.created" when stripeEvent.Data.Object is Dispute d => Dispute(stripeEvent.Id, PaymentWebhookKind.DisputeCreated, d),
            "charge.dispute.updated" when stripeEvent.Data.Object is Dispute d => Dispute(stripeEvent.Id, PaymentWebhookKind.DisputeUpdated, d),
            "charge.dispute.funds_withdrawn" when stripeEvent.Data.Object is Dispute d => Dispute(stripeEvent.Id, PaymentWebhookKind.DisputeFundsWithdrawn, d),
            "charge.dispute.funds_reinstated" when stripeEvent.Data.Object is Dispute d => Dispute(stripeEvent.Id, PaymentWebhookKind.DisputeFundsReinstated, d),
            // A dispute closes won or lost — the dispute's own status carries the outcome.
            "charge.dispute.closed" when stripeEvent.Data.Object is Dispute d => Dispute(
                stripeEvent.Id, d.Status == "lost" ? PaymentWebhookKind.DisputeClosedLost : PaymentWebhookKind.DisputeClosedWon, d),
            _ => null,
        };
    }

    /// <summary>Normalizes a Stripe dispute event; the intent id resolves the payment, the fee comes from the
    /// dispute's balance transactions when present.</summary>
    private static PaymentWebhookEvent? Dispute(string eventId, PaymentWebhookKind kind, Dispute dispute)
    {
        if (string.IsNullOrEmpty(dispute.PaymentIntentId))
        {
            return null; // a dispute we can't tie back to a payment intent is not actionable here
        }

        var fee = dispute.BalanceTransactions?
            .Sum(bt => bt.Fee) is long f and > 0 ? f : 0;

        return new PaymentWebhookEvent(
            eventId, kind, dispute.PaymentIntentId, dispute.Amount, fee, null, dispute.Id, dispute.Status);
    }
}

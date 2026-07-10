using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;

namespace ThreeCommerce.Payments.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        // Anonymous but signature-verified inside the resolved provider adapter (api.md §7, mt6_7).
        // Generalized per-provider route (pay_4): the gateway forwards /webhooks/{provider} verbatim;
        // ResolveByKey picks the adapter, per-provider def_2 secrets verify. /webhooks/stripe still works.
        app.MapPost("/webhooks/{provider}", ProviderWebhook).WithTags("Webhooks").ExcludeFromDescription();

        // Dev/test only: simulate a successful payment for the fake provider.
        app.MapPost("/dev/simulate-payment/{intentId}", SimulatePayment).WithTags("Dev").ExcludeFromDescription();
        return app;
    }

    private static async Task<Results<Ok, BadRequest, NotFound>> ProviderWebhook(
        string provider, HttpContext http, IPaymentProviderRegistry registry, WebhookSecretService secrets, PaymentEventProcessor processor, CancellationToken ct)
    {
        var key = provider.Trim().ToLowerInvariant();

        // Unknown provider → 404 (the config exception is the "no adapter registered" signal).
        IPaymentProvider adapter;
        try
        {
            adapter = registry.ResolveByKey(key);
        }
        catch (PaymentConfigurationException)
        {
            return TypedResults.NotFound();
        }

        using var reader = new StreamReader(http.Request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        // Signature header name is provider-specific (Stripe-Signature, Paypal-Transmission-Sig, …).
        var signature = http.Request.Headers[WebhookSignatureHeaders.For(key)].ToString();

        // Registry secrets (newest first) with config fallback (def_2) — rotation-safe verification.
        var ev = adapter.ParseWebhook(payload, signature, await secrets.GetActiveSecretsAsync(key, ct));
        if (ev is null)
        {
            return TypedResults.BadRequest();
        }

        await processor.ProcessAsync(ev, ct);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, NotFound>> SimulatePayment(
        string intentId, IHostEnvironment env, PaymentEventProcessor processor, PaymentsDbContext db, long? amountMinor, CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        // Use the explicit amount if given, else the pending payment's gross.
        var amount = amountMinor ?? await db.Payments
            .Where(p => p.PaymentIntentId == intentId)
            .Select(p => (long?)p.AmountMinor)
            .FirstOrDefaultAsync(ct) ?? 0;

        var ev = new PaymentWebhookEvent(
            EventId: $"evt_sim_{intentId}",
            Kind: PaymentWebhookKind.PaymentSucceeded,
            PaymentIntentId: intentId,
            AmountMinor: amount,
            FeeMinor: FakePaymentProvider.FakeFee(amount),
            FailureReason: null);

        await processor.ProcessAsync(ev, ct);
        return TypedResults.Ok();
    }
}

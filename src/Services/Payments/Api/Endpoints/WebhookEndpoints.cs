using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Payments;

namespace ThreeCommerce.Payments.Api.Endpoints;

public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        // Anonymous but signature-verified inside the provider (api.md §7).
        app.MapPost("/webhooks/stripe", StripeWebhook).WithTags("Webhooks").ExcludeFromDescription();

        // Dev/test only: simulate a successful payment for the fake provider.
        app.MapPost("/dev/simulate-payment/{intentId}", SimulatePayment).WithTags("Dev").ExcludeFromDescription();
        return app;
    }

    private static async Task<Results<Ok, BadRequest>> StripeWebhook(
        HttpContext http, IPaymentProvider provider, WebhookSecretService secrets, PaymentEventProcessor processor, CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        var signature = http.Request.Headers["Stripe-Signature"].ToString();

        // Registry secrets (newest first) with config fallback (def_2) — rotation-safe verification.
        var ev = provider.ParseWebhook(payload, signature, await secrets.GetActiveSecretsAsync("stripe", ct));
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

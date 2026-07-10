using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Providers.Mock;

/// <summary>
/// Publishes the TEST-ONLY <see cref="MockPaymentCaptured"/> event (pay_3, ADR-0039). It is used by
/// the LocalMock adapter (<see cref="MockEmailPaymentProvider"/>) and, in Sandbox, by the
/// <see cref="SandboxCaptureProvider"/> decorator — the two modes that also send the payload email.
/// Publishing goes through the caller's scoped <see cref="IPublishEndpoint"/> so it rides the EF
/// transactional outbox: the capture commits atomically with the consumer's state save
/// (publish-before-<c>SaveChangesAsync</c> invariant).
/// </summary>
public interface IMockPaymentCapture
{
    public Task CaptureAuthorizeAsync(PaymentRequest request, string paymentIntentId, PaymentMode mode, CancellationToken ct);

    public Task CaptureRefundAsync(string providerKey, string paymentIntentId, long amountMinor, string idempotencyKey, PaymentMode mode, CancellationToken ct);
}

/// <inheritdoc cref="IMockPaymentCapture"/>
public sealed class MockPaymentCapture(IPublishEndpoint publisher, TimeProvider time) : IMockPaymentCapture
{
    public Task CaptureAuthorizeAsync(PaymentRequest request, string paymentIntentId, PaymentMode mode, CancellationToken ct) =>
        publisher.Publish(BuildAuthorize(request, paymentIntentId, mode, time.GetUtcNow()), ct);

    public Task CaptureRefundAsync(string providerKey, string paymentIntentId, long amountMinor, string idempotencyKey, PaymentMode mode, CancellationToken ct) =>
        publisher.Publish(BuildRefund(providerKey, paymentIntentId, amountMinor, idempotencyKey, mode, time.GetUtcNow()), ct);

    /// <summary>Builds the authorize capture event (pure — the redaction happens here).</summary>
    public static MockPaymentCaptured BuildAuthorize(PaymentRequest request, string paymentIntentId, PaymentMode mode, DateTimeOffset capturedAt) =>
        new(
            Operation: "Authorize",
            OrderId: request.OrderId,
            TenantId: request.Account.TenantId,
            Provider: request.Account.Provider,
            MethodKind: request.MethodKind.ToString(),
            AmountMinor: request.AmountMinor,
            Currency: request.Currency,
            PaymentIntentId: paymentIntentId,
            Mode: mode.ToString(),
            Scenario: (request.Scenario ?? MockScenario.Success).ToString(),
            RedactedPayloadJson: PayloadRedactor.ToJson(request),
            CapturedAt: capturedAt);

    /// <summary>Builds the refund capture event (the refund seam carries no order/currency context).</summary>
    public static MockPaymentCaptured BuildRefund(string providerKey, string paymentIntentId, long amountMinor, string idempotencyKey, PaymentMode mode, DateTimeOffset capturedAt) =>
        new(
            Operation: "Refund",
            OrderId: Guid.Empty,
            TenantId: Guid.Empty,
            Provider: providerKey,
            MethodKind: "n/a",
            AmountMinor: amountMinor,
            Currency: string.Empty,
            PaymentIntentId: paymentIntentId,
            Mode: mode.ToString(),
            Scenario: "n/a",
            RedactedPayloadJson: PayloadRedactor.RefundToJson(paymentIntentId, amountMinor, idempotencyKey),
            CapturedAt: capturedAt);
}

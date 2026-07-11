using ThreeCommerce.BuildingBlocks.Contracts.Abstractions;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// pay_7: typed payment failures map to precise HTTP statuses + machine-readable codes via the
/// shared <see cref="IProblemException"/> seam, so a decline is a 402 (not a bare 500) and an
/// idempotency-key reuse is a 409. The exception message stays provider-safe (no PAN/CVV/token).
/// </summary>
public class PaymentProblemMappingTests
{
    [Theory]
    [InlineData(PaymentErrorCode.CardDeclined, 402, "card_declined")]
    [InlineData(PaymentErrorCode.ExpiredCard, 402, "expired_card")]
    [InlineData(PaymentErrorCode.InsufficientFunds, 402, "insufficient_funds")]
    [InlineData(PaymentErrorCode.AuthenticationRequired, 402, "authentication_required")]
    [InlineData(PaymentErrorCode.RateLimited, 429, "rate_limited")]
    [InlineData(PaymentErrorCode.ProviderUnavailable, 502, "provider_unavailable")]
    [InlineData(PaymentErrorCode.ProcessingError, 502, "processing_error")]
    [InlineData(PaymentErrorCode.ConfigurationError, 500, "configuration_error")]
    public void PaymentAuthorizationException_maps_each_code_to_status_and_code(
        PaymentErrorCode code, int expectedStatus, string expectedErrorCode)
    {
        var ex = new PaymentAuthorizationException(new PaymentError(code, "Your card was declined.", Retryable: code == PaymentErrorCode.RateLimited));

        Assert.IsAssignableFrom<IProblemException>(ex);
        Assert.Equal(expectedStatus, ex.StatusCode);
        Assert.Equal(expectedErrorCode, ex.ErrorCode);
        Assert.Equal("Your card was declined.", ex.Message);
        Assert.Equal(code == PaymentErrorCode.RateLimited, ex.Retryable);
    }

    [Fact]
    public void IdempotencyConflict_maps_to_409()
    {
        var ex = new IdempotencyConflictException("key-123");
        Assert.IsAssignableFrom<IProblemException>(ex);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("idempotency_conflict", ex.ErrorCode);
        Assert.DoesNotContain("key-123".ToUpperInvariant(), ex.ErrorCode); // code is stable, not the key
    }

    [Theory]
    [InlineData(typeof(PaymentModeException), 500, "payment_mode_unsafe")]
    [InlineData(typeof(PaymentConfigurationException), 500, "payment_configuration")]
    public void Config_and_mode_failures_are_server_errors_with_codes(Type type, int status, string errorCode)
    {
        var ex = (IProblemException)Activator.CreateInstance(type, "boom")!;
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(errorCode, ex.ErrorCode);
    }
}

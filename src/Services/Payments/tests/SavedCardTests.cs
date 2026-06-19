using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

public class SavedCardTests
{
    [Fact]
    public void Saved_payment_method_snapshot_requires_active_method()
    {
        var method = NewMethod();
        method.Remove(DateTimeOffset.UtcNow);

        Assert.Throws<SavedPaymentMethodRuleException>(() => method.SnapshotForCharge());
    }

    [Fact]
    public void Saved_payment_method_snapshot_contains_only_safe_card_display_data()
    {
        var method = NewMethod();

        var snapshot = method.SnapshotForCharge();

        Assert.Equal("stripe", snapshot.Provider);
        Assert.Equal("pm_card_visa", snapshot.ProviderPaymentMethodId);
        Assert.Equal("visa", snapshot.Brand);
        Assert.Equal("4242", snapshot.Last4);
    }

    [Fact]
    public void Saved_payment_method_default_is_cleared_when_removed()
    {
        var method = NewMethod();
        method.MakeDefault(DateTimeOffset.UtcNow);

        method.Remove(DateTimeOffset.UtcNow);

        Assert.False(method.IsDefault);
        Assert.Equal(SavedPaymentMethodState.Removed, method.State);
    }

    [Fact]
    public void Payment_customer_keeps_provider_reference_in_payments_boundary()
    {
        var customer = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            UserId = Guid.CreateVersion7(),
            Provider = "stripe",
            ProviderCustomerId = "cus_123",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("cus_123", customer.ProviderCustomerId);
        Assert.Equal("stripe", customer.Provider);
    }

    private static SavedPaymentMethod NewMethod() => new()
    {
        Id = Guid.CreateVersion7(),
        PaymentCustomerId = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        Provider = "stripe",
        ProviderPaymentMethodId = "pm_card_visa",
        Brand = "visa",
        Last4 = "4242",
        ExpMonth = 12,
        ExpYear = DateTimeOffset.UtcNow.Year + 3,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}

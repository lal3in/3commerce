using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

public class PaymentAccountTests
{
    [Fact]
    public void PaymentAccount_live_mode_requires_provider_account_ref()
    {
        var account = NewAccount(PaymentProviderMode.Live, externalRef: null);

        var readiness = account.CheckReadiness();

        Assert.False(readiness.IsReady);
        Assert.Contains("live provider account reference", readiness.MissingRequirements);
    }

    [Fact]
    public void PaymentAccount_activates_after_approval_when_ready()
    {
        var account = NewAccount(PaymentProviderMode.Live, externalRef: "acct_123");

        account.SubmitForApproval(DateTimeOffset.UtcNow);
        account.Activate(DateTimeOffset.UtcNow);

        Assert.Equal(PaymentAccountState.Active, account.State);
        Assert.NotNull(account.ActivatedAt);
    }

    [Fact]
    public void PaymentAccount_snapshot_requires_active_account()
    {
        var account = NewAccount(PaymentProviderMode.Test, externalRef: null);

        Assert.Throws<PaymentAccountRuleException>(() => account.SnapshotForCheckout(Guid.CreateVersion7()));
    }

    [Fact]
    public void PaymentAccount_snapshot_rejects_wrong_storefront_override()
    {
        var storefrontId = Guid.CreateVersion7();
        var account = NewAccount(PaymentProviderMode.Test, externalRef: null, storefrontId: storefrontId);
        account.SubmitForApproval(DateTimeOffset.UtcNow);
        account.Activate(DateTimeOffset.UtcNow);

        Assert.Throws<PaymentAccountRuleException>(() => account.SnapshotForCheckout(Guid.CreateVersion7()));
    }

    [Fact]
    public void PaymentAccount_snapshot_captures_provider_mode_for_checkout()
    {
        var storefrontId = Guid.CreateVersion7();
        var account = NewAccount(PaymentProviderMode.Test, externalRef: null, storefrontId: storefrontId);
        account.SubmitForApproval(DateTimeOffset.UtcNow);
        account.Activate(DateTimeOffset.UtcNow);

        var snapshot = account.SnapshotForCheckout(storefrontId);

        Assert.Equal(account.Id, snapshot.PaymentAccountId);
        Assert.Equal(PaymentProviderMode.Test, snapshot.Mode);
        Assert.Equal(storefrontId, snapshot.StorefrontId);
    }

    private static PaymentAccount NewAccount(PaymentProviderMode mode, string? externalRef, Guid? storefrontId = null) =>
        PaymentAccount.Create(
            tenantId: Guid.CreateVersion7(),
            storefrontId: storefrontId,
            name: "Stripe test",
            provider: "stripe",
            mode: mode,
            isDefaultForTenant: storefrontId is null,
            externalAccountRef: externalRef,
            now: DateTimeOffset.UtcNow);
}

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

    [Fact]
    public void PaymentAccount_requires_a_storefront()
    {
        Assert.Throws<PaymentAccountRuleException>(() => PaymentAccount.Create(
            tenantId: Guid.CreateVersion7(), storefrontId: Guid.Empty, name: "x", provider: "stripe",
            mode: PaymentProviderMode.Test, isDefaultForStorefront: true, externalAccountRef: null, now: DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PaymentAccount_clone_copies_descriptor_state_and_default_onto_the_new_storefront()
    {
        var target = Guid.CreateVersion7();
        var source = NewAccount(PaymentProviderMode.Live, externalRef: "acct_1");
        source.SubmitForApproval(DateTimeOffset.UtcNow);
        source.Activate(DateTimeOffset.UtcNow);

        var clone = source.CloneForStorefront(target, DateTimeOffset.UtcNow);

        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal(target, clone.StorefrontId);
        Assert.Equal(source.Provider, clone.Provider);
        Assert.Equal(source.Mode, clone.Mode);
        Assert.Equal(source.ExternalAccountRef, clone.ExternalAccountRef);
        Assert.Equal(source.State, clone.State);
        Assert.Equal(source.IsDefaultForStorefront, clone.IsDefaultForStorefront);
    }

    private static PaymentAccount NewAccount(PaymentProviderMode mode, string? externalRef, Guid? storefrontId = null) =>
        PaymentAccount.Create(
            tenantId: Guid.CreateVersion7(),
            storefrontId: storefrontId ?? Guid.CreateVersion7(),
            name: "Stripe test",
            provider: "stripe",
            mode: mode,
            isDefaultForStorefront: true,
            externalAccountRef: externalRef,
            now: DateTimeOffset.UtcNow);
}

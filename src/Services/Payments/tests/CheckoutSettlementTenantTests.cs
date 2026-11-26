using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// settlement_account_tenant (ADR-0039 amendment 2026-11-26): a checkout's settling payment account is looked up in the
/// ORDER's tenant (<see cref="AuthorizePayment.TenantId"/>), not the host's configured default tenant. Before, a second
/// tenant's order matched none of its storefront's accounts and silently settled through the synthetic host default
/// (stripe) — so its configured acquirer was never used and the sale booked to the wrong PSP's cash. A message without a
/// tenant (an in-flight one from an older Ordering) still falls back to the host default, and says so in the log.
/// Runs the REAL <see cref="AuthorizePaymentConsumer"/> through the REAL keyed <see cref="PaymentProviderRegistry"/> and
/// <see cref="PaymentModeResolver"/> (Sandbox host, Test accounts) on the in-memory harness.
/// </summary>
public class CheckoutSettlementTenantTests
{
    private static readonly Guid HostTenant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TenantB = Guid.CreateVersion7();

    [Fact]
    public void The_orders_tenant_is_used_when_the_message_carries_one()
    {
        var log = new ListLogger<AuthorizePaymentConsumer>();

        var tenant = CheckoutSettlement.TenantOf(Message(TenantB), Config(), log);

        Assert.Equal(TenantB, tenant);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void A_message_without_a_tenant_falls_back_to_the_configured_host_default_and_logs_it()
    {
        var log = new ListLogger<AuthorizePaymentConsumer>();
        var message = Message(tenantId: null);

        var tenant = CheckoutSettlement.TenantOf(message, Config(), log);

        Assert.Equal(HostTenant, tenant);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(message.OrderId.ToString(), entry.Message);
        Assert.Contains(HostTenant.ToString(), entry.Message);
    }

    [Fact]
    public void An_empty_tenant_is_treated_as_missing_and_an_unconfigured_host_uses_the_seeded_default()
    {
        var log = new ListLogger<AuthorizePaymentConsumer>();

        var tenant = CheckoutSettlement.TenantOf(Message(Guid.Empty), PaymentTestSupport.Config(), log);

        Assert.Equal(CheckoutSettlement.SeededDefaultTenantId, tenant);
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Entries).Level);
    }

    [Fact]
    public async Task A_second_tenants_card_checkout_acquires_through_its_own_storefront_default_account()
    {
        var name = Guid.NewGuid().ToString();
        var storeB = Guid.CreateVersion7();
        var polarB = await AddActiveAccountAsync(name, TenantB, storeB, "polar", isDefault: true);
        var (stripe, polar) = Rails();

        var (payment, _) = await AuthorizeAsync(name, stripe, polar, Message(TenantB, storeB));

        Assert.Equal("polar", payment.Provider);                        // ledger → cash.store.{storeB}.polar
        var request = Assert.Single(polar.Requests);                   // the money moved on tenant B's acquirer
        Assert.Equal(polarB, request.Account.PaymentAccountId);
        Assert.Equal(TenantB, request.Account.TenantId);
        Assert.Empty(stripe.Requests);
    }

    [Fact]
    public async Task A_standalone_psp_checkout_settles_on_the_orders_tenants_account_for_that_provider()
    {
        var name = Guid.NewGuid().ToString();
        var storeB = Guid.CreateVersion7();
        var paypalB = await AddActiveAccountAsync(name, TenantB, storeB, "paypal", isDefault: false);
        var (stripe, polar) = Rails();
        var paypal = new RecordingProvider { ProviderKey = "paypal" };

        var (payment, _) = await AuthorizeAsync(name, stripe, polar, Message(TenantB, storeB, "PayPal"), paypal);

        Assert.Equal("paypal", payment.Provider);
        Assert.Equal(paypalB, Assert.Single(paypal.Requests).Account.PaymentAccountId); // the configured account, not a synthetic one
    }

    [Fact]
    public async Task Another_tenant_naming_the_storefront_never_reaches_its_account()
    {
        // Negative control: tenant B's account is only ever used for tenant B's orders. An order of another tenant that
        // names storefront B (it cannot own it) degrades to the synthetic host default — never B's acquirer.
        var name = Guid.NewGuid().ToString();
        var storeB = Guid.CreateVersion7();
        await AddActiveAccountAsync(name, TenantB, storeB, "polar", isDefault: true);
        var (stripe, polar) = Rails();

        var (payment, _) = await AuthorizeAsync(name, stripe, polar, Message(HostTenant, storeB));

        Assert.Equal("stripe", payment.Provider);
        Assert.Equal(Guid.Empty, Assert.Single(stripe.Requests).Account.PaymentAccountId); // the synthetic host-default account
        Assert.Empty(polar.Requests);
    }

    [Fact]
    public async Task An_in_flight_message_without_a_tenant_resolves_in_the_host_default_tenant_and_logs_the_fallback()
    {
        // The old-message fallback, end to end: no TenantId → the host default tenant, as before the fix. The host
        // tenant's own account is found; tenant B's is not (exactly the pre-fix behaviour for such a message).
        var name = Guid.NewGuid().ToString();
        var storeA = Guid.CreateVersion7();
        var hostPolar = await AddActiveAccountAsync(name, HostTenant, storeA, "polar", isDefault: true);
        var (stripe, polar) = Rails();

        var message = Message(tenantId: null, storeA);
        var (payment, log) = await AuthorizeAsync(name, stripe, polar, message);

        Assert.Equal("polar", payment.Provider);
        Assert.Equal(hostPolar, Assert.Single(polar.Requests).Account.PaymentAccountId);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(message.OrderId.ToString()));
    }

    private static IConfiguration Config() =>
        PaymentTestSupport.Config(("Payments:Mode", "Sandbox"), ("Tenancy:DefaultTenantId", HostTenant.ToString()));

    private static (RecordingProvider Stripe, RecordingProvider Polar) Rails() =>
        (new RecordingProvider { ProviderKey = "stripe" }, new RecordingProvider { ProviderKey = "polar" });

    private static AuthorizePayment Message(Guid? tenantId, Guid? storefrontId = null, string paymentOption = "CreditCard") =>
        new(Guid.CreateVersion7(), 5_000, "EUR", $"idem-{Guid.NewGuid():N}",
            PaymentOption: paymentOption, StorefrontId: storefrontId, TenantId: tenantId);

    private static async Task<Guid> AddActiveAccountAsync(string name, Guid tenantId, Guid storefrontId, string provider, bool isDefault)
    {
        await using var db = NewDb(name);
        var now = DateTimeOffset.UtcNow;
        var account = PaymentAccount.Create(tenantId, storefrontId, $"{provider} {tenantId:N}", provider, PaymentProviderMode.Test, isDefault, null, now);
        account.SubmitForApproval(now);
        account.Activate(now);
        db.PaymentAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);

    /// <summary>Runs <paramref name="message"/> through the real consumer; returns the Payment it persisted and its log.</summary>
    private static async Task<(Payment Payment, ListLogger<AuthorizePaymentConsumer> Log)> AuthorizeAsync(
        string name, RecordingProvider stripe, RecordingProvider polar, AuthorizePayment message, params RecordingProvider[] more)
    {
        var config = Config();
        var modes = new PaymentModeResolver(config, PaymentTestSupport.Env(Environments.Development));
        IPaymentProvider[] adapters = [stripe, polar, .. more];
        var log = new ListLogger<AuthorizePaymentConsumer>();
        await using var services = new ServiceCollection()
            .AddDbContext<PaymentsDbContext>(o => o.UseInMemoryDatabase(name))
            .AddSingleton(config)
            .AddSingleton(modes)
            .AddSingleton<IPaymentProviderRegistry>(new PaymentProviderRegistry(adapters, modes))
            .AddSingleton(TimeProvider.System)
            .AddLogging()
            .AddSingleton<ILogger<AuthorizePaymentConsumer>>(log)
            .AddMassTransitTestHarness(x => x.AddConsumer<AuthorizePaymentConsumer>())
            .BuildServiceProvider(true);
        var harness = services.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var response = await harness.GetRequestClient<AuthorizePayment>().GetResponse<AuthorizePaymentResult>(message);
            Assert.False(string.IsNullOrEmpty(response.Message.PaymentIntentId));
        }
        finally
        {
            await harness.Stop();
        }

        await using var db = NewDb(name);
        return (await db.Payments.SingleAsync(p => p.OrderId == message.OrderId), log);
    }
}

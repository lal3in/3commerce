using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// payment_customer_email_backfill: Payments applies a backfilled account email (<see cref="RecordPaymentCustomerEmail"/>,
/// sent by the backfill tool's <c>payment-customer-email</c> command) only to the member's vaults that have NONE — a known
/// email is never overwritten — and, when the member already has a default card, announces
/// <see cref="CustomerPaymentMethodReady"/> so Usage resumes the top-ups it paused. A redelivery does nothing; another
/// tenant's vault of the same user id is untouched; and the auto top-up then finds the card by email.
/// </summary>
public class PaymentCustomerEmailRecordTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero).AddTicks(1_755_249);

    [Fact]
    public async Task A_vault_without_an_email_learns_it_and_its_default_card_becomes_findable()
    {
        var name = $"pce_{Guid.NewGuid():N}";
        var user = Guid.CreateVersion7();
        await using (var db = NewDb(name))
        {
            AddVault(db, Tenant, user, email: null, defaultCard: "pm_default");
            await db.SaveChangesAsync();
        }

        var published = new RecordingPublisher();
        await using (var db = NewDb(name))
        {
            Assert.Null(await DefaultSavedCard.FindAsync(db, Tenant, "ada@example.com", default)); // hidden before
            var outcome = await Backfill(db, published).RecordAsync(new RecordPaymentCustomerEmail(Tenant, user, " Ada@Example.com "), default);
            Assert.Equal(PaymentCustomerEmailOutcome.RecordedAndReady, outcome);
        }

        var ready = Assert.IsType<CustomerPaymentMethodReady>(Assert.Single(published.Published));
        Assert.Equal((Tenant, "ada@example.com", StoredTime.Of(Now)), (ready.TenantId, ready.CustomerEmail, ready.ReadyAt));
        await using var fresh = NewDb(name);
        Assert.Equal("pm_default", (await DefaultSavedCard.FindAsync(fresh, Tenant, "ada@example.com", default))?.ProviderPaymentMethodId);
    }

    [Fact]
    public async Task A_redelivery_changes_nothing_and_announces_nothing()
    {
        var name = $"pce_{Guid.NewGuid():N}";
        var user = Guid.CreateVersion7();
        await using (var db = NewDb(name))
        {
            AddVault(db, Tenant, user, email: null, defaultCard: "pm_default");
            await db.SaveChangesAsync();
        }

        var published = new RecordingPublisher();
        var command = new RecordPaymentCustomerEmail(Tenant, user, "ada@example.com");
        await using (var db = NewDb(name))
        {
            await Backfill(db, published).RecordAsync(command, default);
        }

        await using (var db = NewDb(name))
        {
            Assert.Equal(PaymentCustomerEmailOutcome.AlreadyKnown, await Backfill(db, published).RecordAsync(command, default));
        }

        Assert.Single(published.Published);
    }

    [Fact]
    public async Task A_known_email_is_never_overwritten()
    {
        var name = $"pce_{Guid.NewGuid():N}";
        var user = Guid.CreateVersion7();
        await using (var db = NewDb(name))
        {
            AddVault(db, Tenant, user, email: "current@example.com", defaultCard: "pm_default");
            await db.SaveChangesAsync();
        }

        var published = new RecordingPublisher();
        await using (var db = NewDb(name))
        {
            var outcome = await Backfill(db, published).RecordAsync(new RecordPaymentCustomerEmail(Tenant, user, "stale@example.com"), default);
            Assert.Equal(PaymentCustomerEmailOutcome.AlreadyKnown, outcome);
        }

        await using var fresh = NewDb(name);
        Assert.Equal("current@example.com", (await fresh.PaymentCustomers.SingleAsync()).Email);
        Assert.Empty(published.Published);
    }

    [Fact]
    public async Task Without_a_default_card_the_email_is_recorded_but_nothing_is_announced()
    {
        var name = $"pce_{Guid.NewGuid():N}";
        var user = Guid.CreateVersion7();
        await using (var db = NewDb(name))
        {
            AddVault(db, Tenant, user, email: null, defaultCard: null);
            await db.SaveChangesAsync();
        }

        var published = new RecordingPublisher();
        await using (var db = NewDb(name))
        {
            var outcome = await Backfill(db, published).RecordAsync(new RecordPaymentCustomerEmail(Tenant, user, "ada@example.com"), default);
            Assert.Equal(PaymentCustomerEmailOutcome.Recorded, outcome);
        }

        await using var fresh = NewDb(name);
        Assert.Equal("ada@example.com", (await fresh.PaymentCustomers.SingleAsync()).Email);
        Assert.Empty(published.Published);
    }

    [Fact]
    public async Task Only_the_commands_tenant_is_touched_and_unknown_or_invalid_commands_do_nothing()
    {
        var name = $"pce_{Guid.NewGuid():N}";
        var user = Guid.CreateVersion7();
        await using (var db = NewDb(name))
        {
            AddVault(db, OtherTenant, user, email: null, defaultCard: "pm_other");
            await db.SaveChangesAsync();
        }

        var published = new RecordingPublisher();
        await using (var db = NewDb(name))
        {
            var backfill = Backfill(db, published);
            Assert.Equal(PaymentCustomerEmailOutcome.NoVault, await backfill.RecordAsync(new RecordPaymentCustomerEmail(Tenant, user, "ada@example.com"), default));
            Assert.Equal(PaymentCustomerEmailOutcome.Invalid, await backfill.RecordAsync(new RecordPaymentCustomerEmail(OtherTenant, user, "  "), default));
            Assert.Equal(PaymentCustomerEmailOutcome.Invalid, await backfill.RecordAsync(new RecordPaymentCustomerEmail(Guid.Empty, user, "ada@example.com"), default));
        }

        await using var fresh = NewDb(name);
        Assert.Null((await fresh.PaymentCustomers.SingleAsync()).Email);
        Assert.Empty(published.Published);
    }

    private static PaymentCustomerEmailBackfill Backfill(PaymentsDbContext db, RecordingPublisher published) =>
        new(db, published, new FixedTime(Now));

    private static void AddVault(PaymentsDbContext db, Guid tenantId, Guid userId, string? email, string? defaultCard)
    {
        var vault = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Provider = "stripe",
            ProviderCustomerId = $"cus_{userId:N}",
            CreatedAt = StoredTime.Of(Now),
        };
        if (email is not null)
        {
            vault.RecordEmail(email);
        }

        db.PaymentCustomers.Add(vault);
        if (defaultCard is null)
        {
            return;
        }

        var card = new SavedPaymentMethod
        {
            Id = Guid.CreateVersion7(),
            PaymentCustomerId = vault.Id,
            TenantId = tenantId,
            UserId = userId,
            Provider = "stripe",
            ProviderPaymentMethodId = defaultCard,
            Brand = "visa",
            Last4 = "4242",
            ExpMonth = 12,
            ExpYear = 2030,
            CreatedAt = StoredTime.Of(Now),
        };
        card.MakeDefault(StoredTime.Of(Now));
        db.SavedPaymentMethods.Add(card);
    }

    private static PaymentsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase(name).Options);
}

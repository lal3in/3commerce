using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Tools.OrderConfirmedBackfill.PaymentCustomers;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>
/// payment_customer_email_backfill: a vault opened before #332 never recorded its member's email, so a usage auto top-up
/// could not find the member's default saved card. The <c>payment-customer-email</c> command reads each vault's account
/// email from Identity (per tenant), selects only vaults with NO email whose account exists in the vault's tenant, sends
/// one command per member to Payments' own queue, and Payments records it; a re-run selects nothing.
/// </summary>
public class PaymentCustomerEmailBackfillTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherTenant = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void Selects_only_vaults_without_an_email_whose_account_is_in_the_vaults_tenant()
    {
        var missing = Vault(Tenant, Guid.NewGuid(), email: null, day: 1);
        var recorded = Vault(Tenant, Guid.NewGuid(), email: "kept@example.com", day: 2);
        var differs = Vault(Tenant, Guid.NewGuid(), email: "old@example.com", day: 3);
        var orphan = Vault(Tenant, Guid.NewGuid(), email: null, day: 4);
        var elsewhere = Vault(Tenant, Guid.NewGuid(), email: null, day: 5);   // the account lives in another tenant
        var accounts = new List<AccountFact>
        {
            new(missing.UserId, Tenant, " Ada@Example.com "),
            new(recorded.UserId, Tenant, "KEPT@example.com"),
            new(differs.UserId, Tenant, "new@example.com"),
            new(elsewhere.UserId, OtherTenant, "bea@example.com"),
        };

        var verdicts = PaymentCustomerEmailPlanner.Plan([missing, recorded, differs, orphan, elsewhere], accounts)
            .ToDictionary(v => v.Vault.VaultId);

        Assert.Equal(EmailOutcome.Missing, verdicts[missing.VaultId].Outcome);
        Assert.Equal(EmailOutcome.Recorded, verdicts[recorded.VaultId].Outcome);
        Assert.Equal(EmailOutcome.Differs, verdicts[differs.VaultId].Outcome);
        Assert.Equal(EmailOutcome.NoAccount, verdicts[orphan.VaultId].Outcome);
        Assert.Equal(EmailOutcome.NoAccount, verdicts[elsewhere.VaultId].Outcome);
        var command = Assert.Single(PaymentCustomerEmailPlanner.From(verdicts.Values, limit: null).ToSend);
        Assert.Equal((Tenant, missing.UserId, "ada@example.com"), (command.TenantId, command.UserId, command.Email));
    }

    [Fact]
    public void One_command_per_member_and_limit_caps_the_sends_not_the_counts()
    {
        var user = Guid.NewGuid();
        var vaults = new List<VaultFact>
        {
            Vault(Tenant, user, email: null, day: 1, provider: "stripe"),
            Vault(Tenant, user, email: null, day: 2, provider: "mock"),   // the same member's second vault
            Vault(Tenant, Guid.NewGuid(), email: null, day: 3),
            Vault(Tenant, Guid.NewGuid(), email: null, day: 4),
        };
        var accounts = vaults.Select(v => v.UserId).Distinct().Select(u => new AccountFact(u, Tenant, $"{u:N}@example.com")).ToList();

        var all = PaymentCustomerEmailPlanner.From(PaymentCustomerEmailPlanner.Plan(vaults, accounts), limit: null);
        var capped = PaymentCustomerEmailPlanner.From(PaymentCustomerEmailPlanner.Plan(vaults, accounts), limit: 2);

        Assert.Equal((4, 3, 3), (all.Count(EmailOutcome.Missing), all.MembersMissing, all.ToSend.Count));
        Assert.Equal(user, all.ToSend[0].UserId); // oldest first
        Assert.Equal((4, 2), (capped.Count(EmailOutcome.Missing), capped.ToSend.Count));
    }

    [Fact]
    public async Task Payments_records_the_sent_email_once_and_a_rerun_selects_nothing()
    {
        var user = Guid.CreateVersion7();
        await using var identity = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseInMemoryDatabase($"pce_i_{Guid.NewGuid():N}").Options);
        identity.Users.Add(new User { Id = user, TenantId = Tenant, Email = "ada@example.com", PasswordHash = "x" });
        await identity.SaveChangesAsync();
        await using var payments = new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase($"pce_p_{Guid.NewGuid():N}").Options);
        payments.PaymentCustomers.Add(new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            UserId = user,
            Provider = "stripe",
            ProviderCustomerId = "cus_legacy",
            CreatedAt = BackfillFixtures.T0,
        });
        await payments.SaveChangesAsync();

        var runner = new PaymentCustomerEmailRunner(
            new PaymentsVaultSource(payments, new PassThroughTenantScope()), new IdentityAccountSource(identity, new PassThroughTenantScope()));
        var options = PaymentCustomerEmailOptions.Parse(["--dry-run"]);
        var first = await runner.PlanAsync(options, default);
        var command = Assert.Single(first.ToSend);

        // What Payments' PaymentCustomerEmailConsumer does with the sent command — delivered twice (at-least-once).
        var backfill = new PaymentCustomerEmailBackfill(payments, null!, TimeProvider.System); // no default card: nothing published
        Assert.Equal(PaymentCustomerEmailOutcome.Recorded, await backfill.RecordAsync(command, default));
        Assert.Equal(PaymentCustomerEmailOutcome.AlreadyKnown, await backfill.RecordAsync(command, default));

        Assert.Equal("ada@example.com", (await payments.PaymentCustomers.AsNoTracking().SingleAsync()).Email);
        var rerun = await runner.PlanAsync(options, default);
        Assert.Empty(rerun.ToSend);
        Assert.Equal(EmailOutcome.Recorded, Assert.Single(rerun.Verdicts).Outcome);
    }

    [Fact]
    public void The_report_never_prints_an_email()
    {
        var vault = Vault(Tenant, Guid.NewGuid(), email: "secret-old@example.com", day: 1);
        var plan = PaymentCustomerEmailPlanner.From(
            PaymentCustomerEmailPlanner.Plan([vault], [new AccountFact(vault.UserId, Tenant, "secret-new@example.com")]), limit: null);
        using var output = new StringWriter();

        PaymentCustomerEmailRunner.WriteReport(plan, PaymentCustomerEmailOptions.Parse(["--dry-run", "--list"]), output);

        Assert.Contains($"vault={vault.VaultId}", output.ToString());
        Assert.DoesNotContain("@", output.ToString());
    }

    [Fact]
    public void Options_require_an_explicit_mode_and_parse_the_rest()
    {
        var tenant = Guid.NewGuid();
        var o = PaymentCustomerEmailOptions.Parse(["--execute", "--tenant", tenant.ToString(), "--limit", "5", "--list", "--skip-preflight"]);
        Assert.True(o.Execute);
        Assert.Equal([tenant], o.Tenants);
        Assert.Equal(5, o.Limit);
        Assert.True(o.List && o.SkipPreflight);

        var dry = PaymentCustomerEmailOptions.Parse(["--dry-run"]);
        Assert.False(dry.Execute || dry.SkipPreflight);
        Assert.Throws<BackfillUsageException>(() => PaymentCustomerEmailOptions.Parse([]));
        Assert.Throws<BackfillUsageException>(() => PaymentCustomerEmailOptions.Parse(["--dry-run", "--execute"]));
        Assert.Throws<BackfillUsageException>(() => PaymentCustomerEmailOptions.Parse(["--dry-run", "--limit", "0"]));
    }

    [Fact]
    public void Sends_go_to_Payments_own_queue_with_a_stable_message_id_per_member()
    {
        Assert.Equal("payment-customer-email", PaymentCustomerEmailQueues.QueueName);
        Assert.Equal(new Uri("queue:payment-customer-email"), PaymentCustomerEmailQueues.Address);
        var user = Guid.NewGuid();
        Assert.Equal(PaymentCustomerEmailQueues.MessageId(Tenant, user), PaymentCustomerEmailQueues.MessageId(Tenant, user));
        Assert.NotEqual(PaymentCustomerEmailQueues.MessageId(Tenant, user), PaymentCustomerEmailQueues.MessageId(OtherTenant, user));
    }

    private static VaultFact Vault(Guid tenant, Guid user, string? email, int day, string provider = "stripe") =>
        new(Guid.NewGuid(), tenant, user, provider, email, BackfillFixtures.T0.AddDays(day));
}

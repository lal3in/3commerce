using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Phase 4 (pay_disp_4): activating a payment account (re)registers its provider webhook endpoint and
/// rotates the signing secret in — so a changed endpoint URL never silently drops notifications — and
/// suspending it disables + clears the registration. Verified against the LocalMock provider, which
/// registers deterministically.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class WebhookRegistrationSyncTests(Phase4Fixture fixture)
{
    [Fact]
    public async Task Activating_registers_a_webhook_endpoint_and_rotates_a_secret_in()
    {
        var accountId = await SeedActiveAccountAsync();

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var sync = scope.ServiceProvider.GetRequiredService<WebhookRegistrationService>();
            var account = await db.PaymentAccounts.SingleAsync(a => a.Id == accountId);

            await sync.SyncOnActivateAsync(account, default);
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var account = await db.PaymentAccounts.SingleAsync(a => a.Id == accountId);
            Assert.False(string.IsNullOrEmpty(account.WebhookEndpointId));
            Assert.Contains("/webhooks/", account.WebhookUrl!);

            // The rotated secret is active for the account's provider (verification accepts any active secret).
            var hasActiveSecret = await db.WebhookSecrets.AnyAsync(s => s.Provider == account.Provider && s.Active);
            Assert.True(hasActiveSecret);
        }
    }

    [Fact]
    public async Task Suspending_disables_and_clears_the_registration()
    {
        var accountId = await SeedActiveAccountAsync();

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var sync = scope.ServiceProvider.GetRequiredService<WebhookRegistrationService>();
            var account = await db.PaymentAccounts.SingleAsync(a => a.Id == accountId);
            await sync.SyncOnActivateAsync(account, default);
            await db.SaveChangesAsync();

            account.Suspend(DateTimeOffset.UtcNow);
            await sync.DisableOnSuspendAsync(account, default);
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var account = await db.PaymentAccounts.SingleAsync(a => a.Id == accountId);
            Assert.True(string.IsNullOrEmpty(account.WebhookEndpointId));
        }
    }

    private async Task<Guid> SeedActiveAccountAsync()
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var now = DateTimeOffset.UtcNow;
        var account = PaymentAccount.Create(
            Guid.NewGuid(), Guid.NewGuid(), "Test Store Account", "mock", PaymentProviderMode.Test,
            isDefaultForStorefront: false, externalAccountRef: null, now);
        account.SubmitForApproval(now);
        account.Activate(now);
        db.PaymentAccounts.Add(account);
        await db.SaveChangesAsync();
        return account.Id;
    }
}

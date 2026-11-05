using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Usage.Domain;
using ThreeCommerce.Usage.Infrastructure;
using UsageChargeRow = ThreeCommerce.Usage.Domain.UsageCharge;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// jobmgr_3_settlement (owner decision 2026-11-06), end to end over the real broker and databases (Payments + Usage on one
/// RabbitMQ): an auto top-up for a customer with no DEFAULT saved card is skipped by Payments (no attempt, no posting) and
/// <c>UsageAutoLoadSkipped</c> comes back through both outboxes — Usage takes the credit back and pauses auto-load. When the
/// customer then makes their card the default (the real HTTP endpoint, as the signed-in customer), Payments learns the
/// vault's email and publishes <c>CustomerPaymentMethodReady</c>; Usage resumes and tops up, and Payments charges THAT card
/// off-session and settles it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class UsageAutoLoadSavedCardIntegrationTests(Phase4Fixture fixture)
{
    [Fact]
    public async Task A_top_up_with_no_default_card_is_skipped_then_charged_to_the_card_once_it_is_made_the_default()
    {
        var tenant = Guid.NewGuid();
        const string email = "no-card-autoload@example.com";
        // A vault from before Payments kept the customer's email, with a card that is NOT the default: neither is enough.
        var (userId, cardId) = await SavedCardSeed.AddCardAsync(
            fixture.Payments.Services, tenant, email, pm: "pm_not_default_yet", isDefault: false, recordEmail: false);

        await using (var scope = fixture.Usage.Services.CreateAsyncScope())
        {
            var usage = scope.ServiceProvider.GetRequiredService<UsageService>();
            await usage.ProvisionAsync(tenant, email, MeterType.Token, 0, false, 5, "AUD", null, default);
            await usage.ConfigureAutoLoadAsync(tenant, email, MeterType.Token, true, 10, 100, default);
            await usage.RecordAsync(tenant, email, MeterType.Token, 1, null, default); // prepaid 0 → top up 100 (credited optimistically)
        }

        await WaitAsync(async () => (await ChargesAsync(tenant)).FirstOrDefault()?.State == UsageChargeState.Skipped, "Usage never saw the top-up skipped");
        var skipped = (await ChargesAsync(tenant)).Single();
        var balance = await BalanceAsync(tenant);
        Assert.Equal((0L, true, UsagePaymentHold.None), (balance.PrepaidRemainingQuantity, balance.AutoLoadAwaitingPaymentMethod, balance.PaymentHold));
        await using (var scope = fixture.Payments.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var charge = await db.UsageCharges.AsNoTracking().SingleAsync(c => c.Reference == skipped.Reference);
            Assert.Equal(UsageChargeStatus.Skipped, charge.Status);
            Assert.False(await db.UsageChargeAttempts.AnyAsync(a => a.UsageChargeId == charge.Id)); // never attempted
            Assert.False(await db.JournalEntries.AnyAsync(e => e.Reference == skipped.Reference));  // nothing posted
        }

        // The customer makes their card the default, signed in (the gateway's internal claims carry tenant + email).
        using (var client = fixture.Payments.CreateClient())
        {
            client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(userId, "customer", email, tenant.ToString()));
            var response = await client.PostAsync($"/payment-methods/{cardId}/default", null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await WaitAsync(
            async () => (await ChargesAsync(tenant)).Any(c => c.Reference != skipped.Reference && c.State == UsageChargeState.Settled),
            "the resumed top-up never settled");
        balance = await BalanceAsync(tenant);
        Assert.Equal((100L, false, UsagePaymentHold.None), (balance.PrepaidRemainingQuantity, balance.AutoLoadAwaitingPaymentMethod, balance.PaymentHold));
        var resumed = (await ChargesAsync(tenant)).Single(c => c.Reference != skipped.Reference);
        await using (var scope = fixture.Payments.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var charge = await db.UsageCharges.AsNoTracking().SingleAsync(c => c.Reference == resumed.Reference);
            Assert.Equal((UsageChargeStatus.Settled, "pm_not_default_yet"), (charge.Status, charge.ProviderPaymentMethodId)); // the card made default
            Assert.True(await db.JournalEntries.AnyAsync(e => e.Reference == resumed.Reference));
            Assert.Equal(email, (await db.PaymentCustomers.AsNoTracking().SingleAsync(c => c.UserId == userId)).Email); // learnt from the claims
        }
    }

    private async Task<List<UsageChargeRow>> ChargesAsync(Guid tenant)
    {
        await using var scope = fixture.Usage.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UsageDbContext>().UsageCharges.AsNoTracking()
            .Where(c => c.TenantId == tenant).OrderBy(c => c.CreatedAt).ToListAsync();
    }

    private async Task<UsageBalance> BalanceAsync(Guid tenant)
    {
        await using var scope = fixture.Usage.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UsageDbContext>().UsageBalances.AsNoTracking().SingleAsync(b => b.TenantId == tenant);
    }

    private static Task WaitAsync(Func<Task<bool>> settled, string what) =>
        Eventually.UntilAsync(settled, () => new TimeoutException($"{what}."), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));
}

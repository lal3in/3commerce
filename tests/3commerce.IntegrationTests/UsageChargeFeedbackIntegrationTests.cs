using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Usage.Domain;
using ThreeCommerce.Usage.Infrastructure;
using UsageChargeRow = ThreeCommerce.Usage.Domain.UsageCharge;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P4b + usage_charge_failure_feedback, end to end over the real broker and databases (Payments + Usage on one
/// RabbitMQ): an auto-load Usage publishes is charged by Payments and its <see cref="UsageChargeSettled"/> comes back
/// through both outboxes to Usage; a DECLINED charge comes back as <see cref="UsageChargeFailed"/> and Usage takes the
/// credit back and holds the balance; the day-1 retry (a fresh key, the same pinned amount) settles it and Usage re-grants
/// the credit and releases the hold.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class UsageChargeFeedbackIntegrationTests(Phase4Fixture fixture)
{
    [Fact]
    public async Task A_settled_auto_load_reports_back_to_usage_through_both_outboxes()
    {
        var tenant = Guid.NewGuid();
        const string email = "settled-feedback@example.com";
        await using (var scope = fixture.Usage.Services.CreateAsyncScope())
        {
            var usage = scope.ServiceProvider.GetRequiredService<UsageService>();
            await usage.ProvisionAsync(tenant, email, MeterType.Token, 0, false, 5, "AUD", null, default);
            await usage.ConfigureAutoLoadAsync(tenant, email, MeterType.Token, true, 10, 100, default);
            await usage.RecordAsync(tenant, email, MeterType.Token, 1, null, default); // prepaid 0 → top up 100
        }

        await WaitAsync(async () => (await UsageChargeAsync(tenant))?.State == UsageChargeState.Settled, "Usage never saw the auto-load settle");

        var reference = (await UsageChargeAsync(tenant))!.Reference;
        await using var payments = fixture.Payments.Services.CreateAsyncScope();
        var db = payments.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        Assert.Equal(UsageChargeStatus.Settled, (await db.UsageCharges.AsNoTracking().SingleAsync(c => c.Reference == reference)).Status);
        Assert.True(await db.JournalEntries.AnyAsync(e => e.Reference == reference));
        Assert.Equal((100L, UsagePaymentHold.None), await BalanceAsync(tenant));
    }

    [Fact]
    public async Task A_declined_auto_load_is_taken_back_and_re_granted_when_its_retry_settles()
    {
        var tenant = Guid.NewGuid();
        const string email = "declined-feedback@example.com";
        var reference = $"autoload-it-{Guid.NewGuid():N}";

        await using (var scope = fixture.Usage.Services.CreateAsyncScope())
        {
            // A top-up Usage credited optimistically and recorded as open — without publishing it, so only the scripted
            // rail below ever charges it.
            var usage = scope.ServiceProvider.GetRequiredService<UsageService>();
            var balance = await usage.ProvisionAsync(tenant, email, MeterType.Token, 0, false, 5, "AUD", null, default);
            await usage.ConfigureAutoLoadAsync(tenant, email, MeterType.Token, true, 0, 100, default);
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var tracked = await db.UsageBalances.SingleAsync(b => b.Id == balance.Id);
            tracked.ApplyAutoLoad(DateTimeOffset.UtcNow);
            db.UsageCharges.Add(UsageChargeRow.Open(tenant, tracked.Id, reference, UsageChargeKind.AutoLoad, 100, 500, "AUD", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

        }

        var provider = new ScriptedProvider(PaymentOutcome.Failed);
        var start = DateTimeOffset.UtcNow;
        await using (var scope = fixture.Payments.Services.CreateAsyncScope())
        {
            await Billing(scope, provider, start).ChargeAsync(
                new UsageChargeRequest(tenant, null, email, UsageChargeKind.AutoLoad, MeterType.Token, 100, 5, 500, "AUD", reference, "Usage auto-load"),
                default);
        }

        await WaitAsync(async () => await BalanceAsync(tenant) == (0L, UsagePaymentHold.Unpaid), "Usage never took the declined credit back");

        provider.Outcome = PaymentOutcome.Succeeded;
        await using (var scope = fixture.Payments.Services.CreateAsyncScope())
        {
            // Retry only this charge (the shared fixture may hold others): the schedule's day-1 retry.
            var billing = Billing(scope, provider, start.AddDays(1).AddHours(1));
            await billing.RetryDueAsync(default);
        }

        await WaitAsync(async () => await BalanceAsync(tenant) == (100L, UsagePaymentHold.None), "Usage never re-granted the settled credit");
        var ours = provider.Requests.Where(r => r.IdempotencyKey.StartsWith(reference, StringComparison.Ordinal)).ToList();
        Assert.Equal([reference, $"{reference}-a1"], ours.Select(r => r.IdempotencyKey)); // a fresh key for the retry
        Assert.All(ours, r => Assert.Equal(500, r.AmountMinor));                            // the same pinned amount
        Assert.Equal(UsageChargeState.Settled, (await UsageChargeAsync(tenant))!.State);
    }

    private static UsageChargeBilling Billing(AsyncServiceScope scope, ScriptedProvider provider, DateTimeOffset now)
    {
        var sp = scope.ServiceProvider;
        return new UsageChargeBilling(
            sp.GetRequiredService<PaymentsDbContext>(), new ScriptedRegistry(provider), sp.GetRequiredService<PaymentModeResolver>(),
            new FixedClock(now), sp.GetRequiredService<ILogger<UsageChargeBilling>>(), sp.GetRequiredService<IPublishEndpoint>());
    }

    private async Task<UsageChargeRow?> UsageChargeAsync(Guid tenant)
    {
        await using var scope = fixture.Usage.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UsageDbContext>().UsageCharges.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(c => c.TenantId == tenant);
    }

    private async Task<(long Prepaid, UsagePaymentHold Hold)> BalanceAsync(Guid tenant)
    {
        await using var scope = fixture.Usage.Services.CreateAsyncScope();
        var balance = await scope.ServiceProvider.GetRequiredService<UsageDbContext>().UsageBalances.AsNoTracking().SingleAsync(b => b.TenantId == tenant);
        return (balance.PrepaidRemainingQuantity, balance.PaymentHold);
    }

    private static Task WaitAsync(Func<Task<bool>> settled, string what) =>
        Eventually.UntilAsync(settled, () => new TimeoutException($"{what}."), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));

    private sealed class ScriptedProvider(PaymentOutcome outcome) : IPaymentProvider
    {
        public PaymentOutcome Outcome { get; set; } = outcome;

        public List<PaymentRequest> Requests { get; } = [];

        public string ProviderKey => "mock";

        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var error = Outcome == PaymentOutcome.Failed ? new PaymentError(PaymentErrorCode.CardDeclined, "Card declined.", Retryable: false) : null;
            return Task.FromResult(new PaymentResponse($"pi_usage_it_{Guid.NewGuid():N}", null, Outcome, error));
        }

        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => throw new NotSupportedException();
        public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScriptedRegistry(IPaymentProvider provider) : IPaymentProviderRegistry
    {
        public IPaymentProvider Resolve(PaymentAccountSnapshot account) => provider;
        public IPaymentProvider ResolveDefault() => provider;
        public IPaymentProvider ResolveByKey(string providerKey) => provider;
    }
}

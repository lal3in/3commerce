using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Usage.Domain;
using ThreeCommerce.Usage.Infrastructure;
using UsageChargeRow = ThreeCommerce.Usage.Domain.UsageCharge;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// usage_charge_pay_page over HTTP, the real broker and both databases (Payments + Usage on one RabbitMQ): a DECLINED
/// auto top-up takes its credit back and holds the balance (Unpaid); the customer lists it (GET /me/usage-charges), opens
/// it and pays it (POST /me/usage-charges/{reference}/pay, Idempotency-Key) — the replayed key pays nothing more — and the
/// settlement reaches Usage, which re-grants the credit and lifts the hold. Someone else's charge is a 404, a missing key
/// a 400, a paid charge a 409.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class UsageChargePayPageIntegrationTests(Phase4Fixture fixture)
{
    [Fact]
    public async Task A_declined_top_up_is_paid_from_the_page_once_and_usage_lifts_its_hold()
    {
        var tenant = Guid.NewGuid();
        const string email = "pay-page@example.com";
        var userId = Guid.NewGuid();
        var reference = $"autoload-paypage-{Guid.NewGuid():N}";
        await SavedCardSeed.AddCardAsync(fixture.Payments.Services, tenant, email, pm: "pm_declining");

        await using (var scope = fixture.Usage.Services.CreateAsyncScope())
        {
            // A top-up Usage credited optimistically and recorded as open (not published, so only the scripted rail charges it).
            var usage = scope.ServiceProvider.GetRequiredService<UsageService>();
            var balance = await usage.ProvisionAsync(tenant, email, MeterType.Token, 0, false, 5, "AUD", null, default);
            await usage.ConfigureAutoLoadAsync(tenant, email, MeterType.Token, true, 0, 100, default);
            var db = scope.ServiceProvider.GetRequiredService<UsageDbContext>();
            var tracked = await db.UsageBalances.SingleAsync(b => b.Id == balance.Id);
            tracked.ApplyAutoLoad(DateTimeOffset.UtcNow);
            db.UsageCharges.Add(UsageChargeRow.Open(tenant, tracked.Id, reference, UsageChargeKind.AutoLoad, 100, 500, "AUD", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await using (var scope = fixture.Payments.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var declining = new UsageChargeBilling(
                sp.GetRequiredService<PaymentsDbContext>(), new OneProvider(new DecliningProvider()), sp.GetRequiredService<PaymentModeResolver>(),
                TimeProvider.System, sp.GetRequiredService<ILogger<UsageChargeBilling>>(), sp.GetRequiredService<IPublishEndpoint>());
            await declining.ChargeAsync(
                new UsageChargeRequest(tenant, null, email, UsageChargeKind.AutoLoad, MeterType.Token, 100, 5, 500, "AUD", reference, "Usage auto-load"),
                default);
        }

        await WaitAsync(async () => await BalanceAsync(tenant) == (0L, UsagePaymentHold.Unpaid), "Usage never took the declined credit back");

        using var customer = Client(userId, email, tenant);
        var listed = await customer.GetFromJsonAsync<List<MyCharge>>("/me/usage-charges");
        var mine = Assert.Single(listed!, c => c.Reference == reference);
        Assert.Equal(("PastDue", 500L, "Failed", 2), (mine.Status, mine.AmountDueMinor, mine.FailureReason, mine.Kind)); // kind as a NUMBER

        using (var stranger = Client(Guid.NewGuid(), "stranger@example.com", tenant))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/me/usage-charges/{reference}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PayAsync(stranger, reference, "k-stranger")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/me/usage-charges/{reference}/pay", new { })).StatusCode);

        var paid = await PayAsync(customer, reference, "k-1");
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var answer = (await paid.Content.ReadFromJsonAsync<PayAnswer>())!;
        Assert.Equal((1, "Settled"), (answer.Outcome, answer.Status)); // Paid, as a NUMBER
        var replay = (await (await PayAsync(customer, reference, "k-1")).Content.ReadFromJsonAsync<PayAnswer>())!;
        Assert.Equal(answer.Outcome, replay.Outcome);
        Assert.Equal(HttpStatusCode.Conflict, (await PayAsync(customer, reference, "k-2")).StatusCode); // nothing left to pay

        await WaitAsync(async () => await BalanceAsync(tenant) == (100L, UsagePaymentHold.None), "Usage never re-granted the credit paid from the page");
        await using var payments = fixture.Payments.Services.CreateAsyncScope();
        var paymentsDb = payments.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var charge = await paymentsDb.UsageCharges.AsNoTracking().SingleAsync(c => c.Reference == reference);
        var attempts = await paymentsDb.UsageChargeAttempts.AsNoTracking().Where(a => a.UsageChargeId == charge.Id).OrderBy(a => a.Attempt).ToListAsync();
        Assert.Equal([UsageChargeTrigger.Initial, UsageChargeTrigger.Customer], attempts.Select(a => a.Trigger)); // the replay charged nothing
        Assert.Equal([reference, $"{reference}-a1"], attempts.Select(a => a.IdempotencyKey));
        Assert.All(attempts, a => Assert.Equal(500, a.AmountMinor));
        Assert.Equal(1, await paymentsDb.JournalEntries.CountAsync(e => e.Reference == reference));
    }

    private HttpClient Client(Guid userId, string email, Guid tenant)
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(userId, "customer", email, tenant.ToString()));
        return client;
    }

    private static Task<HttpResponseMessage> PayAsync(HttpClient client, string reference, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/me/usage-charges/{reference}/pay") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private async Task<(long Prepaid, UsagePaymentHold Hold)> BalanceAsync(Guid tenant)
    {
        await using var scope = fixture.Usage.Services.CreateAsyncScope();
        var balance = await scope.ServiceProvider.GetRequiredService<UsageDbContext>().UsageBalances.AsNoTracking().SingleAsync(b => b.TenantId == tenant);
        return (balance.PrepaidRemainingQuantity, balance.PaymentHold);
    }

    private static Task WaitAsync(Func<Task<bool>> settled, string what) =>
        Eventually.UntilAsync(settled, () => new TimeoutException($"{what}."), TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));

    private sealed record MyCharge(string Reference, int Kind, string Status, long? AmountDueMinor, string? FailureReason);

    private sealed record PayAnswer(int Outcome, string? PaymentIntentId, string Status);

    private sealed class DecliningProvider : IPaymentProvider
    {
        public string ProviderKey => "mock";

        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct) =>
            Task.FromResult(new PaymentResponse(
                $"pi_paypage_{Guid.NewGuid():N}", null, PaymentOutcome.Failed, new PaymentError(PaymentErrorCode.CardDeclined, "Card declined.", false)));

        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => throw new NotSupportedException();
        public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
    }

    private sealed class OneProvider(IPaymentProvider provider) : IPaymentProviderRegistry
    {
        public IPaymentProvider Resolve(PaymentAccountSnapshot account) => provider;
        public IPaymentProvider ResolveDefault() => provider;
        public IPaymentProvider ResolveByKey(string providerKey) => provider;
    }
}

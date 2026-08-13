using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The admin ledger-entries endpoint filters server-side by currency, storefront (encoded in the line
/// account codes) and date range — the Ledger page's Journal-entries filters. Entries are seeded with
/// distinctive currencies so they're isolated from the shared Phase-3 ledger.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class LedgerEntryFilterTests(Phase3Fixture fixture)
{
    private sealed record LineDto(string AccountCode, long DebitMinor, long CreditMinor);
    private sealed record EntryDto(Guid Id, string Description, string Reference, string Currency, DateTimeOffset CreatedAt, List<LineDto> Lines);

    private HttpClient AdminClient()
    {
        var client = fixture.Payments.CreateClient();
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));
        return client;
    }

    private static JournalEntry StoreSale(Guid sid, string currency, DateTimeOffset when) =>
        Ledger.Sale(
            Guid.CreateVersion7(), grossMinor: 11_900, taxMinor: 1_900, feeMinor: 350, currency, when,
            provider: "stripe",
            revenueAccount: $"revenue.store-{sid:N}",
            taxAccount: $"tax.store-{sid:N}",
            receivableAccount: $"receivable.store-{sid:N}",
            storefrontId: sid,
            reference: $"ledgerfilter-{Guid.NewGuid():N}");

    private async Task<List<EntryDto>> GetAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<List<EntryDto>>($"/admin/ledger/entries{query}"))!;

    [Fact]
    public async Task Entries_filter_by_currency_storefront_and_date()
    {
        var storeA = Guid.CreateVersion7();
        var storeB = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40);

        // Distinctive currencies ("QAA"/"QBB") so these four entries are the only matches for the filters.
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            db.JournalEntries.Add(StoreSale(storeA, "QAA", now));   // e1
            db.JournalEntries.Add(StoreSale(storeB, "QAA", now));   // e2
            db.JournalEntries.Add(StoreSale(storeA, "QAA", old));   // e3 (old)
            db.JournalEntries.Add(StoreSale(storeA, "QBB", now));   // e4 (other currency)
            await db.SaveChangesAsync();
        }

        var client = AdminClient();

        // Currency filter: the three QAA entries (case-insensitive), and the one QBB entry.
        Assert.Equal(3, (await GetAsync(client, "?currency=qaa")).Count);
        Assert.Single(await GetAsync(client, "?currency=QBB"));

        // Storefront filter (encoded in account codes): storeA has e1+e3 in QAA; storeB has e2.
        Assert.Equal(2, (await GetAsync(client, $"?currency=QAA&storefrontId={storeA}")).Count);
        Assert.Single(await GetAsync(client, $"?currency=QAA&storefrontId={storeB}"));

        // Date range: start=today drops the 40-day-old e3 (leaving e1 for storeA); end=old's day keeps only e3.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var oldDay = DateOnly.FromDateTime(old.UtcDateTime);
        Assert.Single(await GetAsync(client, $"?currency=QAA&storefrontId={storeA}&start={today:yyyy-MM-dd}"));
        var endOld = await GetAsync(client, $"?currency=QAA&end={oldDay:yyyy-MM-dd}");
        Assert.Single(endOld);
        Assert.All(endOld, e => Assert.Contains(e.Lines, l => l.AccountCode.Contains($"store-{storeA:N}")));

        client.Dispose();
    }
}

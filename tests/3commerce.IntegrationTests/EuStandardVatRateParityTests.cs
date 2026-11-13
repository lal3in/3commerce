using ThreeCommerce.Payments.Domain.Ledger;
using OrderingVat = ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane — ADR-0063 § 16, OSS renewals: Payments renews an OSS subscription at its destination's standard rate from
/// its OWN copy of the EU rate table (<see cref="EuStandardVatRates"/>; Payments cannot reference Ordering's domain), so
/// the copy must never drift from what checkout charged with (Ordering's <c>EuVatRateTable.Current</c> and
/// <c>EuMemberStates.LocalDate</c>). This is the one project that sees both: it compares the Member States, the standard
/// rate of every country on every day from the OSS start until a year after the last change, and the local date at the
/// midnight edges in every Member State's time zone. A rate added to one table and not the other fails here.
/// </summary>
public sealed class EuStandardVatRateParityTests
{
    [Fact]
    public void Both_copies_know_the_same_member_states()
    {
        Assert.Equal(
            OrderingVat.EuMemberStates.Codes.Append(OrderingVat.EuVatTerritories.NorthernIreland).OrderBy(c => c, StringComparer.Ordinal),
            EuStandardVatRates.Codes.OrderBy(c => c, StringComparer.Ordinal));
        Assert.Equal(OrderingVat.EuVatRateTable.OssStart, EuStandardVatRates.OssStart);
        foreach (var alias in new[] { "EL", "el", " gr ", "CH", "XI", "xi", "GB", "IC", "", null })
        {
            Assert.Equal(OrderingVat.EuVatArea.Normalize(alias), EuStandardVatRates.Normalize(alias));
        }
    }

    [Fact]
    public void Every_standard_rate_agrees_on_every_day()
    {
        var table = OrderingVat.EuVatRateTable.Current;
        var standardRows = table.Rows.Where(r => r.CategoryCode.Length == 0)
            .Select(r => (r.Country, r.EffectiveFrom, r.RateBps)).OrderBy(r => r.Country, StringComparer.Ordinal).ThenBy(r => r.EffectiveFrom);
        Assert.Equal(standardRows, EuStandardVatRates.Rows.OrderBy(r => r.Country, StringComparer.Ordinal).ThenBy(r => r.From));

        var last = table.Rows.Max(r => r.EffectiveFrom).AddYears(1);
        foreach (var country in EuStandardVatRates.Codes)
        {
            for (var day = EuStandardVatRates.OssStart.AddDays(-1); day <= last; day = day.AddDays(1))
            {
                Assert.True(
                    table.StandardRateFor(country, day) == EuStandardVatRates.StandardRateFor(country, day),
                    $"{country} on {day:yyyy-MM-dd}: Ordering {table.StandardRateFor(country, day)} vs Payments {EuStandardVatRates.StandardRateFor(country, day)}");
            }
        }
    }

    [Fact]
    public void The_local_date_agrees_at_the_midnight_edges()
    {
        foreach (var country in EuStandardVatRates.Codes)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                foreach (var instant in new[] { new DateTimeOffset(2025, 6, 30, hour, 30, 0, TimeSpan.Zero), new DateTimeOffset(2025, 12, 31, hour, 59, 0, TimeSpan.Zero) })
                {
                    Assert.Equal(OrderingVat.EuVatArea.LocalDate(country, instant), EuStandardVatRates.LocalDate(country, instant));
                }
            }
        }
    }
}

using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using Facts = ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 P5 — <see cref="OrderConfirmed"/> publishes the invoicing facts the order already holds (P1 per-line and
/// shipping tax, P1b jurisdiction / destination / scheme, P3 bill-to and buyer), verbatim, plus the storefront, its order
/// number, the totals, each line's id and the capture time. Nothing is recomputed: a document built from the event shows
/// what was charged.
/// </summary>
public class OrderConfirmedInvoicingFactsTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 10, 29, 4, 15, 0, TimeSpan.Zero);
    private static readonly Guid Keyboard = Guid.Parse("01920000-0000-7000-8000-0000000000a1");
    private static readonly Guid Download = Guid.Parse("01920000-0000-7000-8000-0000000000a2");

    [Fact]
    public void The_event_carries_every_invoicing_fact_of_the_order()
    {
        var order = Attempt().ToOrder(1042, CapturedAt);

        var e = OrderConfirmedFactory.From(order);

        Assert.Equal(order.StorefrontId, e.StorefrontId);
        Assert.Equal(1042, e.PublicOrderNumber);
        Assert.Equal((2500L, 200L, 300L, 210L, false), (e.NetMinor, e.DiscountMinor, e.ShippingMinor, e.TaxMinor, e.TaxInclusive));
        Assert.Equal(("DE", "FR", Facts.TaxScheme.Oss), (e.TaxJurisdiction, e.TaxDestinationCountry, e.TaxScheme));
        Assert.Equal(new BillToInfo("Acme GmbH Accounts", "1 Rechnungsweg", "Berlin", "10115", "DE", "BE"), e.BillTo);
        Assert.Equal(new BuyerInfo("Acme GmbH", "DE123456789", TaxIdScheme.EuVat, "DE"), e.Buyer);
        Assert.Equal(new LineTaxInfo(Facts.TaxCategory.Standard, 1000, 300, 30), e.ShippingTax);
        Assert.Equal(CapturedAt, e.CapturedAt);

        var keyboard = e.Lines.Single(l => l.ProductId == Keyboard);
        var download = e.Lines.Single(l => l.ProductId == Download);
        Assert.Equal(order.Lines.Single(l => l.ProductId == Keyboard).Id, keyboard.LineId);
        Assert.Equal(order.Lines.Single(l => l.ProductId == Download).Id, download.LineId);
        Assert.NotEqual(Guid.Empty, keyboard.LineId);
        Assert.Equal((Facts.TaxCategory.Standard, 1000, 1800L, 180L, Facts.TaxExemptionReason.None),
            (keyboard.TaxCategory, keyboard.TaxRateBps, keyboard.TaxableBaseMinor, keyboard.TaxMinor, keyboard.ExemptionReason));
        Assert.Equal((Facts.TaxCategory.Exempt, 0, 500L, 0L, Facts.TaxExemptionReason.DestinationShipRule),
            (download.TaxCategory, download.TaxRateBps, download.TaxableBaseMinor, download.TaxMinor, download.ExemptionReason));
        // The pre-P5 fields are unchanged.
        Assert.Equal((200L, 2, 1000L), (keyboard.DiscountMinor, keyboard.Quantity, keyboard.UnitPriceMinor));
    }

    [Fact]
    public void The_published_facts_satisfy_the_money_identities()
    {
        var e = OrderConfirmedFactory.From(Attempt().ToOrder(1042, CapturedAt));

        // Σ line tax + shipping tax == TaxMinor (P1), and net − discount + shipping (+ tax on an exclusive store) == gross.
        Assert.Equal(e.TaxMinor, e.Lines.Sum(l => l.TaxMinor) + e.ShippingTax!.TaxMinor);
        Assert.Equal(e.AmountMinor, e.NetMinor - e.DiscountMinor + e.ShippingMinor + (e.TaxInclusive ? 0 : e.TaxMinor));
        Assert.Equal(e.DiscountMinor, e.Lines.Sum(l => l.DiscountMinor));
    }

    [Fact]
    public void An_order_without_recorded_facts_publishes_them_as_not_recorded_never_guessed()
    {
        // Negative control: an order confirmed before P1/P3 has no bill-to, no buyer and no tax facts. The bill-to is NOT
        // filled in from the ship-to, and the categories read NotRecorded — not a zero-rated Standard line.
        var order = Attempt(recorded: false).ToOrder(7, CapturedAt);

        var e = OrderConfirmedFactory.From(order);

        Assert.Null(e.BillTo);
        Assert.Null(e.Buyer);
        Assert.Equal(Facts.TaxScheme.NotRecorded, e.TaxScheme);
        Assert.Equal(Facts.TaxCategory.NotRecorded, e.ShippingTax!.Category);
        Assert.All(e.Lines, l => Assert.Equal(Facts.TaxCategory.NotRecorded, l.TaxCategory));
        Assert.All(e.Lines, l => Assert.NotEqual(Guid.Empty, l.LineId)); // the line id is always known
    }

    [Fact]
    public void Orderings_tax_vocabulary_crosses_by_the_same_numbers()
    {
        AssertSameMembers<Facts.TaxCategory, TaxCategory>();
        AssertSameMembers<Facts.TaxExemptionReason, TaxExemptionReason>();
        AssertSameMembers<Facts.TaxScheme, TaxScheme>();
    }

    private static void AssertSameMembers<TContract, TOrdering>()
        where TContract : struct, Enum
        where TOrdering : struct, Enum
    {
        var contract = Enum.GetValues<TContract>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));
        var ordering = Enum.GetValues<TOrdering>().Select(v => (v.ToString(), Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(ordering.OrderBy(m => m.Item2), contract.OrderBy(m => m.Item2));
    }

    /// <summary>
    /// An EU (DE) exclusive-priced storefront selling to France under OSS at 10 % (the rate is illustrative — the facts are
    /// copied, not computed): keyboard 2 × 1000 less a 200 promotion → base 1800, tax 180; an exempt download 500;
    /// shipping 300, tax 30. Gross 2500 − 200 + 300 + 210 = 2810.
    /// </summary>
    private static CheckoutAttempt Attempt(bool recorded = true)
    {
        var id = Guid.CreateVersion7();
        return new CheckoutAttempt
        {
            Id = id,
            TenantId = Guid.CreateVersion7(),
            StorefrontId = Guid.CreateVersion7(),
            Email = "buyer@example.test",
            Status = CheckoutAttemptStatus.AwaitingPayment,
            NetMinor = 2500,
            DiscountMinor = 200,
            PromotionDiscountMinor = 200,
            ShippingMinor = 300,
            TaxMinor = 210,
            GrossMinor = 2810,
            Currency = "EUR",
            PaymentIntentId = "pi_fake",
            ShipName = "Acme GmbH",
            ShipLine1 = "1 Lieferweg",
            ShipCity = "Paris",
            ShipPostcode = "75001",
            ShipCountry = "FR",
            BillName = recorded ? "Acme GmbH Accounts" : null,
            BillLine1 = recorded ? "1 Rechnungsweg" : null,
            BillCity = recorded ? "Berlin" : null,
            BillRegion = recorded ? "BE" : null,
            BillPostcode = recorded ? "10115" : null,
            BillCountry = recorded ? "DE" : null,
            BuyerCompanyName = recorded ? "Acme GmbH" : null,
            BuyerTaxId = recorded ? "DE123456789" : null,
            BuyerTaxIdScheme = recorded ? TaxIdScheme.EuVat : null,
            BuyerTaxIdJurisdiction = recorded ? "DE" : null,
            TaxScheme = recorded ? TaxScheme.Oss : TaxScheme.NotRecorded,
            TaxInclusive = false,
            TaxJurisdiction = recorded ? "DE" : string.Empty,
            TaxDestinationCountry = recorded ? "FR" : string.Empty,
            ShippingTaxCategory = recorded ? TaxCategory.Standard : TaxCategory.NotRecorded,
            ShippingTaxRateBps = recorded ? 1000 : 0,
            ShippingTaxableBaseMinor = recorded ? 300 : 0,
            ShippingTaxMinor = recorded ? 30 : 0,
            CreatedAt = CapturedAt.AddMinutes(-2),
            Lines =
            [
                new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = id,
                    ProductId = Keyboard,
                    Title = "Keyboard",
                    Quantity = 2,
                    UnitPriceMinor = 1000,
                    DiscountMinor = 200,
                    FulfilmentType = FulfilmentType.Warehouse,
                    TaxCategory = recorded ? TaxCategory.Standard : TaxCategory.NotRecorded,
                    TaxRateBps = recorded ? 1000 : 0,
                    TaxableBaseMinor = recorded ? 1800 : 0,
                    TaxMinor = recorded ? 180 : 0,
                },
                new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = id,
                    ProductId = Download,
                    Title = "Download",
                    Quantity = 1,
                    UnitPriceMinor = 500,
                    FulfilmentType = FulfilmentType.DigitalDownload,
                    TaxCategory = recorded ? TaxCategory.Exempt : TaxCategory.NotRecorded,
                    TaxableBaseMinor = recorded ? 500 : 0,
                    ExemptionReason = recorded ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None,
                },
            ],
        };
    }
}

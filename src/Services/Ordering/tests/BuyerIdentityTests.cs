using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.Ordering.Api.Endpoints;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 P3 — buyer identity at checkout: the bill-to address defaults to the ship-to address, the optional
/// business-purchase block is validated (company name; tax ID + scheme together; per-scheme format mirrored from
/// Entity's P2 rules), each refusal names the request field at fault, and the confirmed order carries all of it
/// verbatim. Format only: no registry lookup, and nothing here can change an amount.
/// </summary>
public class BuyerIdentityTests
{
    private static readonly BillToAddress ShipTo = new("Ship Name", "1 Ship St", "Sydney", "NSW", "2000", "AU");

    [Fact]
    public void Bill_to_defaults_to_the_ship_to_address()
    {
        var billTo = BuyerIdentity.ResolveBillTo(ShipTo, supplied: null);

        Assert.Equal(ShipTo, billTo);
    }

    [Fact]
    public void A_supplied_bill_to_wins_and_is_normalized()
    {
        var billTo = BuyerIdentity.ResolveBillTo(ShipTo, new BillToAddress(" Acme Billing ", " 9 Ledger Rd ", " Berlin ", "  ", " 10115 ", " de "));

        Assert.Equal(new BillToAddress("Acme Billing", "9 Ledger Rd", "Berlin", null, "10115", "DE"), billTo);
    }

    [Theory]
    [InlineData(" ", "9 Ledger Rd", "Berlin", "10115", "DE")]
    [InlineData("Acme", "", "Berlin", "10115", "DE")]
    [InlineData("Acme", "9 Ledger Rd", null, "10115", "DE")]
    [InlineData("Acme", "9 Ledger Rd", "Berlin", "  ", "DE")]
    [InlineData("Acme", "9 Ledger Rd", "Berlin", "10115", "DEU")]
    public void A_supplied_bill_to_with_a_blank_part_is_refused_naming_the_billing_address(
        string? name, string? line1, string? city, string? postcode, string? country)
    {
        // Defence in depth behind the request validator: a bill-to is never stored blank (and never throws a 500).
        var ex = Assert.Throws<BuyerIdentityException>(() =>
            BuyerIdentity.ResolveBillTo(ShipTo, new BillToAddress(name!, line1!, city!, null, postcode!, country!)));

        Assert.Equal("billingAddress", CheckoutEndpoints.BuyerFieldPath(ex.Field));
    }

    [Theory]
    [InlineData(TaxIdScheme.EuVat, "de 123 456 789", null, "DE123456789", "DE")]
    [InlineData(TaxIdScheme.EuVat, "EL123456789", null, "EL123456789", "GR")]
    [InlineData(TaxIdScheme.EuVat, "XI123456789", null, "XI123456789", "GB-NIR")]
    [InlineData(TaxIdScheme.Abn, "51 824 753 556", null, "51824753556", "AU")]
    [InlineData(TaxIdScheme.Acn, "123 456 789", null, "123456789", "AU")]
    [InlineData(TaxIdScheme.GbVrn, "123456789", null, "GB123456789", "GB")]
    [InlineData(TaxIdScheme.NzGst, "123-456-789", null, "123456789", "NZ")]
    [InlineData(TaxIdScheme.CaBn, "123456789 RT 0001", "ca-qc", "123456789RT0001", "CA-QC")]
    [InlineData(TaxIdScheme.ChUid, "CHE-123.456.789 MWST", null, "CHE123456789MWST", "CH")]
    [InlineData(TaxIdScheme.UsStateSalesTaxId, "sr ab 12-345678", "us-ca", "SRAB12-345678", "US-CA")]
    [InlineData(TaxIdScheme.Gst, "gst-registered", null, "GST-REGISTERED", null)]
    [InlineData(TaxIdScheme.OtherTaxRegistration, "x-99", "jp", "X-99", "JP")]
    public void A_well_formed_tax_id_is_normalized_with_its_jurisdiction(
        TaxIdScheme scheme, string taxId, string? jurisdiction, string expectedTaxId, string? expectedJurisdiction)
    {
        var buyer = BuyerIdentity.Resolve(" Acme Pty Ltd ", taxId, scheme, jurisdiction, ShipTo);

        Assert.Equal(new BusinessBuyer("Acme Pty Ltd", expectedTaxId, scheme, expectedJurisdiction), buyer);
    }

    [Fact]
    public void A_company_without_a_tax_id_is_a_business_purchase_with_no_registration()
    {
        var buyer = BuyerIdentity.Resolve("Acme Pty Ltd", "  ", scheme: null, taxIdJurisdiction: null, ShipTo);

        Assert.Equal(new BusinessBuyer("Acme Pty Ltd", null, null, null), buyer);
    }

    [Fact]
    public void A_us_state_tax_id_takes_its_state_from_a_us_bill_to_address()
    {
        var usBillTo = new BillToAddress("Acme Inc", "1 Main St", "Austin", "tx", "73301", "US");

        var buyer = BuyerIdentity.Resolve("Acme Inc", "12-3456789", TaxIdScheme.UsStateSalesTaxId, null, usBillTo);

        Assert.Equal("US-TX", buyer.TaxIdJurisdiction);
        // Negative control: the same ID with a non-US bill-to has no state to take, so the jurisdiction is missing.
        var ex = Assert.Throws<BuyerIdentityException>(() =>
            BuyerIdentity.Resolve("Acme Inc", "12-3456789", TaxIdScheme.UsStateSalesTaxId, null, ShipTo));
        Assert.Equal(BuyerIdentity.TaxIdJurisdictionField, ex.Field);
    }

    [Theory]
    [InlineData(null, "DE123456789", TaxIdScheme.EuVat, null, "buyer.companyName")] // no company
    [InlineData("   ", null, null, null, "buyer.companyName")] // blank company
    [InlineData("Acme", "DE123456789", null, null, "buyer.taxIdScheme")] // tax ID without its scheme
    [InlineData("Acme", null, TaxIdScheme.EuVat, null, "buyer.taxId")] // scheme without its tax ID
    [InlineData("Acme", "DE12345678", TaxIdScheme.EuVat, null, "buyer.taxId")] // DE needs 9 digits
    [InlineData("Acme", "GR123456789", TaxIdScheme.EuVat, null, "buyer.taxId")] // Greece is EL
    [InlineData("Acme", "US123456789", TaxIdScheme.EuVat, null, "buyer.taxId")] // not a Member State
    [InlineData("Acme", "DE123456789", TaxIdScheme.EuVat, "FR", "buyer.taxIdJurisdiction")] // prefix and jurisdiction disagree
    [InlineData("Acme", "1234567890", TaxIdScheme.Abn, null, "buyer.taxId")] // ABN is 11 digits
    [InlineData("Acme", "GB12345678", TaxIdScheme.GbVrn, null, "buyer.taxId")]
    [InlineData("Acme", "1234567", TaxIdScheme.NzGst, null, "buyer.taxId")]
    [InlineData("Acme", "12345678RT0001", TaxIdScheme.CaBn, null, "buyer.taxId")]
    [InlineData("Acme", "CH123456789", TaxIdScheme.ChUid, null, "buyer.taxId")]
    [InlineData("Acme", "12345", TaxIdScheme.UsStateSalesTaxId, "US-ZZ", "buyer.taxIdJurisdiction")] // not a state
    [InlineData("Acme", "12$45", TaxIdScheme.UsStateSalesTaxId, "US-NY", "buyer.taxId")]
    [InlineData("Acme", "x", TaxIdScheme.Gst, null, "buyer.taxId")]
    [InlineData("Acme", "x-99", TaxIdScheme.OtherTaxRegistration, "Japan", "buyer.taxIdJurisdiction")]
    [InlineData("Acme", "DE123456789", (TaxIdScheme)42, null, "buyer.taxIdScheme")] // unknown wire value
    public void An_invalid_business_block_is_refused_naming_the_request_field(
        string? companyName, string? taxId, TaxIdScheme? scheme, string? jurisdiction, string expectedField)
    {
        var ex = Assert.Throws<BuyerIdentityException>(() => BuyerIdentity.Resolve(companyName, taxId, scheme, jurisdiction, ShipTo));

        Assert.Equal(expectedField, CheckoutEndpoints.BuyerFieldPath(ex.Field));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Oversize_values_are_refused_before_they_reach_the_bounded_columns()
    {
        var longName = Assert.Throws<BuyerIdentityException>(() =>
            BuyerIdentity.Resolve(new string('A', BuyerIdentity.CompanyNameMaxLength + 1), null, null, null, ShipTo));
        Assert.Equal(BuyerIdentity.CompanyNameField, longName.Field);

        var longId = Assert.Throws<BuyerIdentityException>(() =>
            BuyerIdentity.Resolve("Acme", new string('9', BuyerIdentity.TaxIdMaxLength + 1), TaxIdScheme.OtherTaxRegistration, null, ShipTo));
        Assert.Equal(BuyerIdentity.TaxIdField, longId.Field);

        // Positive control: exactly at the bounds is accepted.
        var atBounds = BuyerIdentity.Resolve(new string('A', BuyerIdentity.CompanyNameMaxLength), new string('9', 80), TaxIdScheme.OtherTaxRegistration, null, ShipTo);
        Assert.Equal(BuyerIdentity.CompanyNameMaxLength, atBounds.CompanyName.Length);
    }

    [Fact]
    public void The_checkout_request_defaults_bill_to_and_carries_no_business_block_when_none_is_sent()
    {
        var request = new CheckoutRequest("shopper@example.test", new AddressRequest("Ship Name", "1 Ship St", "Sydney", "2000", "AU", "NSW"));

        var (billTo, buyer) = CheckoutEndpoints.ResolveBuyerIdentity(request);

        Assert.Equal(ShipTo, billTo);
        Assert.Null(buyer);
    }

    [Fact]
    public void The_checkout_request_resolves_a_separate_bill_to_and_a_business_block()
    {
        var request = new CheckoutRequest(
            "buyer@example.test",
            new AddressRequest("Ship Name", "1 Ship St", "Sydney", "2000", "AU"),
            BillingAddress: new AddressRequest("Acme Accounts", "9 Ledger Rd", "Berlin", "10115", "de"),
            Buyer: new BuyerRequest("Acme GmbH", "DE 123456789", TaxIdScheme.EuVat));

        var (billTo, buyer) = CheckoutEndpoints.ResolveBuyerIdentity(request);

        Assert.Equal(new BillToAddress("Acme Accounts", "9 Ledger Rd", "Berlin", null, "10115", "DE"), billTo);
        Assert.Equal(new BusinessBuyer("Acme GmbH", "DE123456789", TaxIdScheme.EuVat, "DE"), buyer);
    }

    [Fact]
    public void The_order_carries_the_attempts_bill_to_and_business_block_verbatim()
    {
        var attempt = NewAttempt(billTo: new BillToAddress("Acme Accounts", "9 Ledger Rd", "Berlin", "BE", "10115", "DE"),
            buyer: new BusinessBuyer("Acme GmbH", "DE123456789", TaxIdScheme.EuVat, "DE"));

        var order = attempt.ToOrder(1000, DateTimeOffset.UtcNow);

        Assert.Equal(
            (attempt.BillName, attempt.BillLine1, attempt.BillCity, attempt.BillRegion, attempt.BillPostcode, attempt.BillCountry),
            (order.BillName, order.BillLine1, order.BillCity, order.BillRegion, order.BillPostcode, order.BillCountry));
        Assert.Equal(
            ("Acme GmbH", "DE123456789", (TaxIdScheme?)TaxIdScheme.EuVat, "DE"),
            (order.BuyerCompanyName, order.BuyerTaxId, order.BuyerTaxIdScheme, order.BuyerTaxIdJurisdiction));
        // The block is captured, never priced: the order's money is the attempt's, untouched.
        Assert.Equal((attempt.NetMinor, attempt.TaxMinor, attempt.GrossMinor), (order.NetMinor, order.TaxMinor, order.GrossMinor));
    }

    [Fact]
    public void A_consumer_order_carries_no_business_block()
    {
        var order = NewAttempt(billTo: ShipTo, buyer: null).ToOrder(1000, DateTimeOffset.UtcNow);

        Assert.Equal("Ship Name", order.BillName);
        Assert.Null(order.BuyerCompanyName);
        Assert.Null(order.BuyerTaxId);
        Assert.Null(order.BuyerTaxIdScheme);
        Assert.Null(order.BuyerTaxIdJurisdiction);
    }

    private static CheckoutAttempt NewAttempt(BillToAddress billTo, BusinessBuyer? buyer) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        StorefrontId = Guid.CreateVersion7(),
        Email = "buyer@example.test",
        NetMinor = 1000,
        TaxMinor = 91,
        GrossMinor = 1000,
        Currency = "AUD",
        PaymentIntentId = "pi_fake",
        ShipName = "Ship Name",
        ShipLine1 = "1 Ship St",
        ShipCity = "Sydney",
        ShipRegion = "NSW",
        ShipPostcode = "2000",
        ShipCountry = "AU",
        BillName = billTo.Name,
        BillLine1 = billTo.Line1,
        BillCity = billTo.City,
        BillRegion = billTo.Region,
        BillPostcode = billTo.Postcode,
        BillCountry = billTo.Country,
        BuyerCompanyName = buyer?.CompanyName,
        BuyerTaxId = buyer?.TaxId,
        BuyerTaxIdScheme = buyer?.TaxIdScheme,
        BuyerTaxIdJurisdiction = buyer?.TaxIdJurisdiction,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}

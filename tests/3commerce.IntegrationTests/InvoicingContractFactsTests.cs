using System.Text.Json;
using MassTransit.Serialization;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure.Consumers;
using ThreeCommerce.Support.Infrastructure.Sagas;
using ThreeCommerce.Workers.Notifications.Consumers;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane — ADR-0063 P5 contract facts, on the ADR-0055 back-compat pattern: an <see cref="OrderConfirmed"/>,
/// <see cref="RefundRequested"/> or <see cref="RefundCompleted"/> published BEFORE the invoicing facts were appended
/// still deserializes with MassTransit's own serializer options, every new field takes its default, and the message
/// still applies (Support's snapshot projects it and its RMA refunds go out amount-only, as before; Notifications still
/// sends the order email). Positive controls prove the same path carries the facts — enums as NUMBERS — when they are
/// sent.
/// </summary>
public sealed class InvoicingContractFactsTests
{
    private static readonly Guid OrderId = Guid.Parse("01920000-0000-7000-8000-0000000000f1");
    private static readonly Guid ProductId = Guid.Parse("01920000-0000-7000-8000-0000000000f2");

    // Exactly the shape a pre-P5 Ordering published (rma_disc era): every field up to the line's discountMinor, nothing after.
    private const string PreP5OrderConfirmed = """
        {
          "orderId": "01920000-0000-7000-8000-0000000000f1",
          "tenantId": "00000000-0000-0000-0000-000000000001",
          "email": "legacy@example.com",
          "amountMinor": 2810,
          "currency": "EUR",
          "shipTo": { "name": "Legacy", "line1": "1 Old St", "city": "Paris", "postcode": "75001", "country": "FR", "region": null },
          "lines": [
            { "productId": "01920000-0000-7000-8000-0000000000f2", "variantId": null, "supplierId": null, "title": "Keyboard",
              "quantity": 2, "fulfilmentType": 1, "billingMode": 1, "unitPriceMinor": 1000, "discountMinor": 200 }
          ]
        }
        """;

    private const string PreP5RefundRequested = """
        { "refundId": "01920000-0000-7000-8000-0000000000e1", "orderId": "01920000-0000-7000-8000-0000000000f1",
          "amountMinor": 1800, "reason": "damaged", "requestedBy": "rma" }
        """;

    private const string PreP5RefundCompleted = """
        { "refundId": "01920000-0000-7000-8000-0000000000e1", "orderId": "01920000-0000-7000-8000-0000000000f1",
          "amountMinor": 1800, "fullyRefunded": false }
        """;

    [Fact]
    public void A_pre_p5_order_confirmed_deserializes_with_default_invoicing_facts()
    {
        var m = Deserialize<OrderConfirmed>(PreP5OrderConfirmed);

        // Everything the old message carried...
        Assert.Equal((OrderId, "legacy@example.com", 2810L, "EUR"), (m.OrderId, m.Email, m.AmountMinor, m.Currency));
        var line = Assert.Single(m.Lines);
        Assert.Equal((ProductId, 2, 1000L, 200L, FulfilmentType.Warehouse), (line.ProductId, line.Quantity, line.UnitPriceMinor, line.DiscountMinor, line.FulfilmentType));
        // ...and every appended fact at its default: "not recorded", never a zero-tax sale.
        Assert.Null(m.StorefrontId);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, false), (m.PublicOrderNumber, m.NetMinor, m.DiscountMinor, m.ShippingMinor, m.TaxMinor, m.TaxInclusive));
        Assert.Equal((string.Empty, string.Empty, TaxScheme.NotRecorded), (m.TaxJurisdiction, m.TaxDestinationCountry, m.TaxScheme));
        Assert.Null(m.BillTo);
        Assert.Null(m.Buyer);
        Assert.Null(m.ShippingTax);
        Assert.Null(m.CapturedAt);
        Assert.Equal((Guid.Empty, TaxCategory.NotRecorded, 0, 0L, 0L, TaxExemptionReason.None),
            (line.LineId, line.TaxCategory, line.TaxRateBps, line.TaxableBaseMinor, line.TaxMinor, line.ExemptionReason));
    }

    [Fact]
    public async Task A_pre_p5_order_confirmed_still_applies_and_its_rma_refund_goes_out_amount_only()
    {
        var m = Deserialize<OrderConfirmed>(PreP5OrderConfirmed);

        // Support projects it exactly as before (the discounted refund basis included), with no order-line id...
        var snapshot = OrderSnapshotConsumer.Snapshot(m);
        var snapLine = Assert.Single(snapshot.Lines);
        Assert.Equal((2810L, 1000L, 200L, 2, Guid.Empty), (snapshot.GrossMinor, snapLine.UnitPriceMinor, snapLine.DiscountMinor, snapLine.Quantity, snapLine.OrderLineId));
        Assert.Equal(1800, snapLine.RefundableMinor(2));

        // ...so an RMA of that line refunds the same amount it always did, without line attribution.
        var request = RequestRmaRefundActivity.Build(Saga(1800), [RmaLine(snapLine, 2)]);
        Assert.Equal(1800, request.AmountMinor);
        Assert.Null(request.Lines);
        Assert.Null(request.ShippingMinor);

        // Notifications still sends the confirmation email from the old message.
        var sender = new CollectingSender();
        await new OrderConfirmedConsumer(sender, new EmailTemplates("https://shop.example"), new InMemoryOrderEmailLookup())
            .Consume(SubscriptionRenewalActionEmailTests.StubContext<OrderConfirmed>.For(m));
        Assert.Equal("legacy@example.com", Assert.Single(sender.Sent).To);
    }

    [Fact]
    public void A_p5_order_confirmed_round_trips_its_facts_with_enums_as_numbers()
    {
        var lineId = Guid.CreateVersion7();
        var sent = new OrderConfirmed(
            OrderId, Guid.CreateVersion7(), "b2b@example.com", 2810, "EUR",
            new ShipToInfo("Acme", "1 Lieferweg", "Paris", "75001", "FR"),
            [new OrderLineInfo(ProductId, null, null, "Keyboard", 2, FulfilmentType.Warehouse, BillingMode.OneTime, 1000, 200,
                lineId, TaxCategory.Standard, 2000, 1800, 360)],
            StorefrontId: Guid.CreateVersion7(), PublicOrderNumber: 1042, NetMinor: 2000, DiscountMinor: 200, ShippingMinor: 300,
            TaxMinor: 420, TaxInclusive: false, TaxJurisdiction: "DE", TaxDestinationCountry: "FR", TaxScheme: TaxScheme.Oss,
            BillTo: new BillToInfo("Acme Accounts", "1 Rechnungsweg", "Berlin", "10115", "DE"),
            Buyer: new BuyerInfo("Acme GmbH", "DE123456789", TaxIdScheme.EuVat, "DE"),
            ShippingTax: new LineTaxInfo(TaxCategory.Standard, 2000, 300, 60),
            CapturedAt: new DateTimeOffset(2026, 10, 29, 4, 15, 0, TimeSpan.Zero));

        var json = Compact(JsonSerializer.Serialize(sent, SystemTextJsonMessageSerializer.Options));
        Assert.Contains("\"taxScheme\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"taxCategory\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"taxIdScheme\":5", json, StringComparison.Ordinal);

        var received = Deserialize<OrderConfirmed>(json);
        Assert.Equal(sent with { Lines = [] }, received with { Lines = [] });
        Assert.Equal(sent.Lines[0], Assert.Single(received.Lines));

        // Support keeps the line id, so the RMA path can attribute a refund to it.
        Assert.Equal(lineId, Assert.Single(OrderSnapshotConsumer.Snapshot(received).Lines).OrderLineId);
    }

    [Fact]
    public void Pre_p5_refund_messages_deserialize_amount_only()
    {
        var requested = Deserialize<RefundRequested>(PreP5RefundRequested);
        var completed = Deserialize<RefundCompleted>(PreP5RefundCompleted);

        Assert.Equal((1800L, "damaged", "rma"), (requested.AmountMinor, requested.Reason, requested.RequestedBy));
        Assert.Equal((1800L, false), (completed.AmountMinor, completed.FullyRefunded));
        Assert.Equal((null, null, null), (requested.Lines, requested.ShippingMinor, requested.Kind));
        Assert.Equal((null, null, null), (completed.Lines, completed.ShippingMinor, completed.Kind));
    }

    [Fact]
    public void Refund_attribution_round_trips_with_its_kind_as_a_number()
    {
        var lineId = Guid.CreateVersion7();
        var requested = new RefundRequested(
            Guid.CreateVersion7(), OrderId, 1800, "damaged", "rma",
            Lines: [new RefundLine(lineId, 2, 1800)], ShippingMinor: 0, Kind: RefundKind.Return);
        var completed = new RefundCompleted(
            requested.RefundId, OrderId, 1800, FullyRefunded: false,
            Lines: requested.Lines, ShippingMinor: requested.ShippingMinor, Kind: requested.Kind);

        var requestedJson = Compact(JsonSerializer.Serialize(requested, SystemTextJsonMessageSerializer.Options));
        var completedJson = Compact(JsonSerializer.Serialize(completed, SystemTextJsonMessageSerializer.Options));
        Assert.Contains("\"kind\":1", requestedJson, StringComparison.Ordinal);
        Assert.Contains("\"kind\":1", completedJson, StringComparison.Ordinal);

        var requestedBack = Deserialize<RefundRequested>(requestedJson);
        var completedBack = Deserialize<RefundCompleted>(completedJson);
        Assert.Equal(new RefundLine(lineId, 2, 1800), Assert.Single(requestedBack.Lines!));
        Assert.Equal(new RefundLine(lineId, 2, 1800), Assert.Single(completedBack.Lines!));
        Assert.Equal((0L, RefundKind.Return), (completedBack.ShippingMinor, completedBack.Kind));
        Assert.Equal(requested with { Lines = null }, requestedBack with { Lines = null });
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, SystemTextJsonMessageSerializer.Options)!;

    /// <summary>MassTransit's options indent; strip the layout whitespace so a field can be matched as <c>"name":value</c>.</summary>
    private static string Compact(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { WriteIndented = false });

    private static RmaState Saga(long amount) => new()
    {
        CorrelationId = Guid.CreateVersion7(),
        CurrentState = "Requested",
        OrderId = OrderId,
        RefundId = Guid.CreateVersion7(),
        AmountMinor = amount,
        Reason = "damaged",
    };

    private static RmaRequestLine RmaLine(OrderSnapshotLine line, int qty) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderLineId = line.OrderLineId,
        ProductId = line.ProductId,
        Title = line.Title,
        Quantity = qty,
        UnitPriceMinor = line.UnitPriceMinor,
        DiscountMinor = (line.UnitPriceMinor * qty) - line.RefundableMinor(qty),
    };

    private sealed class CollectingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}

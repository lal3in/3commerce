using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_7: add a package to a shipment, buy a label, refresh tracking (manual, automation off).</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class ShipmentPackageTests(Phase4Fixture fixture)
{
    private async Task<T> WithShipmentsAsync<T>(Func<ShipmentService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<ShipmentService>());
    }

    private async Task<Guid> SeedShipmentAsync(Guid tenant, Guid? orderId = null)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
        var shipment = new Shipment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            OrderId = orderId ?? Guid.NewGuid(),
            FulfillmentSource = "Warehouse",
            Status = ShipmentStatus.Created,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return shipment.Id;
    }

    [Fact]
    public async Task Add_package_then_buy_label_then_refresh_tracking()
    {
        var tenant = Guid.NewGuid();
        var shipmentId = await SeedShipmentAsync(tenant);

        var package = await WithShipmentsAsync(s => s.AddPackageAsync(tenant, shipmentId, new Parcel(1000, 200, 150, 100), default));
        Assert.NotNull(package);
        Assert.Equal(PackageStatus.Pending, package!.Status);

        var labelled = await WithShipmentsAsync(s => s.BuyLabelAsync(tenant, package.Id, null, default));
        Assert.Equal(PackageStatus.Labelled, labelled!.Status);
        Assert.False(string.IsNullOrWhiteSpace(labelled.TrackingNumber));
        Assert.Equal(CarrierCode.Fake, labelled.Carrier);

        var tracked = await WithShipmentsAsync(s => s.RefreshTrackingAsync(tenant, package.Id, default));
        Assert.Equal(PackageStatus.InTransit, tracked!.Status); // Fake tracking reports in_transit
    }

    [Fact]
    public async Task Add_package_to_an_unknown_shipment_returns_null()
    {
        var result = await WithShipmentsAsync(s =>
            s.AddPackageAsync(Guid.NewGuid(), Guid.NewGuid(), new Parcel(1, 1, 1, 1), default));
        Assert.Null(result);
    }

    /// <summary>
    /// phase 1: buying a (costed) label publishes ShippingLabelPurchased, which Payments' consumer
    /// turns into a balanced carrier-cost accrual — Dr the shared expense.shipping_carrier (this
    /// order has no storefront attribution) / Cr liability.carrier_payable.
    /// </summary>
    [Fact]
    public async Task Buying_a_label_publishes_ShippingLabelPurchased_and_books_the_carrier_cost_accrual()
    {
        var tenant = Guid.NewGuid();
        var shipmentId = await SeedShipmentAsync(tenant);

        var package = await WithShipmentsAsync(s => s.AddPackageAsync(tenant, shipmentId, new Parcel(1000, 200, 150, 100), default));
        Assert.NotNull(package);

        var labelled = await WithShipmentsAsync(s => s.BuyLabelAsync(tenant, package!.Id, null, default));
        Assert.NotNull(labelled);
        Assert.Equal(PackageStatus.Labelled, labelled!.Status);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var entry = await db.JournalEntries.Include(e => e.Lines)
                .SingleOrDefaultAsync(e => e.Reference == package!.Id.ToString());
            if (entry is not null)
            {
                Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));
                Assert.Contains(entry.Lines, l => l.AccountCode == Accounts.ExpenseShippingCarrier && l.DebitMinor > 0);
                Assert.Contains(entry.Lines, l => l.AccountCode == Accounts.LiabilityCarrierPayable && l.CreditMinor > 0);
                return;
            }

            await Task.Delay(300);
        }

        Assert.Fail("Carrier cost accrual was not booked in Payments after buying a label.");
    }

    /// <summary>
    /// FIX (cross-model review of Batch B): the accrual must post in the payment's settlement
    /// currency, not the carrier's cost currency (the fake carrier always quotes AUD) — otherwise a
    /// EUR-storefront order's carrier cost lands in AUD and Financials' by-storefront Margin column
    /// (which sums account amounts across currencies) subtracts AUD minor units from EUR revenue.
    /// </summary>
    [Fact]
    public async Task Buying_a_label_for_a_eur_storefront_order_books_the_accrual_in_eur_to_the_stores_own_account()
    {
        var tenant = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var storefrontId = Guid.NewGuid();
        var shipmentId = await SeedShipmentAsync(tenant, orderId);

        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            db.Payments.Add(new Payment
            {
                Id = Guid.CreateVersion7(),
                OrderId = orderId,
                StorefrontId = storefrontId,
                PaymentIntentId = $"pi_seed_{orderId:N}",
                AmountMinor = 10_000,
                Currency = "EUR",
                Status = PaymentStatus.Succeeded,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var package = await WithShipmentsAsync(s => s.AddPackageAsync(tenant, shipmentId, new Parcel(1000, 200, 150, 100), default));
        Assert.NotNull(package);

        var labelled = await WithShipmentsAsync(s => s.BuyLabelAsync(tenant, package!.Id, null, default));
        Assert.NotNull(labelled);

        var storeAccount = Accounts.ShippingCostStoreFor(storefrontId);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var entry = await db.JournalEntries.Include(e => e.Lines)
                .SingleOrDefaultAsync(e => e.Reference == package!.Id.ToString());
            if (entry is not null)
            {
                Assert.Equal("EUR", entry.Currency);
                Assert.Contains(entry.Lines, l => l.AccountCode == storeAccount && l.DebitMinor > 0);
                Assert.DoesNotContain(entry.Lines, l => l.AccountCode == Accounts.ExpenseShippingCarrier);
                return;
            }

            await Task.Delay(300);
        }

        Assert.Fail("Attributed carrier cost accrual was not booked in EUR to the store's own account.");
    }
}

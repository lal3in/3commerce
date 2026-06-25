using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_2: reserve / confirm / release warehouse stock with the movement ledger.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class ReservationServiceTests(Phase4Fixture fixture)
{
    private async Task<T> WithReservationAsync<T>(Func<ReservationService, InventoryService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(
            scope.ServiceProvider.GetRequiredService<ReservationService>(),
            scope.ServiceProvider.GetRequiredService<InventoryService>());
    }

    private async Task WithReservationAsync(Func<ReservationService, InventoryService, Task> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        await work(
            scope.ServiceProvider.GetRequiredService<ReservationService>(),
            scope.ServiceProvider.GetRequiredService<InventoryService>());
    }

    private async Task<int> AvailableAsync(Guid tenant, Guid product) =>
        await WithReservationAsync((_, inv) => inv.AvailableAsync(tenant, product, null, default));

    [Fact]
    public async Task Reserve_holds_stock_then_confirm_consumes_on_hand()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var order = Guid.NewGuid();

        var loc = await WithReservationAsync((_, inv) =>
            inv.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default));
        await WithReservationAsync((_, inv) => inv.SetStockAsync(tenant, loc.Id, product, null, 10, default));

        await WithReservationAsync((res, _) =>
            res.ReserveAsync(tenant, order, [new ReservationLine(product, null, 4)], default));
        Assert.Equal(6, await AvailableAsync(tenant, product)); // 10 on-hand - 4 reserved

        await WithReservationAsync((res, _) =>
            res.ConfirmAsync(tenant, order, [new ReservationLine(product, null, 4)], default));
        Assert.Equal(6, await AvailableAsync(tenant, product)); // on-hand now 6, reserved 0
    }

    [Fact]
    public async Task Reserve_is_idempotent_for_a_redelivered_order()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var order = Guid.NewGuid();
        var loc = await WithReservationAsync((_, inv) =>
            inv.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default));
        await WithReservationAsync((_, inv) => inv.SetStockAsync(tenant, loc.Id, product, null, 10, default));

        await WithReservationAsync((res, _) => res.ReserveAsync(tenant, order, [new ReservationLine(product, null, 3)], default));
        await WithReservationAsync((res, _) => res.ReserveAsync(tenant, order, [new ReservationLine(product, null, 3)], default));

        Assert.Equal(7, await AvailableAsync(tenant, product)); // reserved once, not twice
    }

    [Fact]
    public async Task Confirm_without_prior_reservation_decrements_on_hand()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var order = Guid.NewGuid();
        var loc = await WithReservationAsync((_, inv) =>
            inv.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default));
        await WithReservationAsync((_, inv) => inv.SetStockAsync(tenant, loc.Id, product, null, 8, default));

        await WithReservationAsync((res, _) => res.ConfirmAsync(tenant, order, [new ReservationLine(product, null, 5)], default));
        Assert.Equal(3, await AvailableAsync(tenant, product));
    }

    [Fact]
    public async Task Release_restores_a_hold()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var order = Guid.NewGuid();
        var loc = await WithReservationAsync((_, inv) =>
            inv.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default));
        await WithReservationAsync((_, inv) => inv.SetStockAsync(tenant, loc.Id, product, null, 10, default));

        await WithReservationAsync((res, _) => res.ReserveAsync(tenant, order, [new ReservationLine(product, null, 6)], default));
        await WithReservationAsync((res, _) => res.ReleaseAsync(order, default));
        Assert.Equal(10, await AvailableAsync(tenant, product));
    }

    [Fact]
    public async Task Reserve_spreads_across_multiple_active_locations()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var order = Guid.NewGuid();
        var a = await WithReservationAsync((_, inv) => inv.CreateLocationAsync(tenant, Guid.NewGuid(), null, "A", LocationKind.TenantWarehouse, default));
        var b = await WithReservationAsync((_, inv) => inv.CreateLocationAsync(tenant, Guid.NewGuid(), null, "B", LocationKind.TenantWarehouse, default));
        await WithReservationAsync((_, inv) => inv.SetStockAsync(tenant, a.Id, product, null, 3, default));
        await WithReservationAsync((_, inv) => inv.SetStockAsync(tenant, b.Id, product, null, 5, default));

        await WithReservationAsync((res, _) => res.ReserveAsync(tenant, order, [new ReservationLine(product, null, 7)], default));
        Assert.Equal(1, await AvailableAsync(tenant, product)); // 8 on-hand - 7 reserved across both
    }
}

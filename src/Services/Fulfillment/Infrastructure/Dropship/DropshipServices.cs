using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain.Dropship;

namespace ThreeCommerce.Fulfillment.Infrastructure.Dropship;

/// <summary>Deterministic keyless dropship provider for tests/dev (mt4_4b): accepts + returns tracking.</summary>
public sealed class FakeSupplierOrderProvider : ISupplierOrderProvider
{
    public string Key => "fake";

    public Task<SupplierOrderResult> SubmitAsync(SupplierOrderRequest request, CancellationToken ct) =>
        Task.FromResult(new SupplierOrderResult(
            Accepted: true,
            ExternalReference: "EXT-" + request.OrderId.ToString("N")[..8].ToUpperInvariant(),
            TrackingNumber: "FDS" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Carrier: "FakeDropshipCarrier",
            FailureReason: null));
}

/// <summary>
/// Forwards dropship orders to a supplier (mt4_4b): create a SupplierOrder, submit via the provider,
/// record acceptance/tracking. Idempotent per (order, supplier) so a redelivered confirm is safe.
/// </summary>
public sealed class SupplierOrderService(FulfillmentDbContext db, TimeProvider clock, ISupplierOrderProvider provider)
{
    public async Task<SupplierOrder> ForwardAsync(Guid tenantId, SupplierOrderRequest request, CancellationToken ct)
    {
        var existing = await db.SupplierOrders
            .FirstOrDefaultAsync(o => o.OrderId == request.OrderId && o.SupplierId == request.SupplierId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var supplierOrder = SupplierOrder.Request(tenantId, request.OrderId, request.SupplierId, clock.GetUtcNow());
        db.SupplierOrders.Add(supplierOrder);
        var result = await provider.SubmitAsync(request, ct);
        supplierOrder.Apply(result, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return supplierOrder;
    }

    public Task<List<SupplierOrder>> ListAsync(Guid tenantId, Guid? orderId, CancellationToken ct)
    {
        var query = db.SupplierOrders.AsNoTracking().Where(o => o.TenantId == tenantId);
        if (orderId is { } oid)
        {
            query = query.Where(o => o.OrderId == oid);
        }

        return query.OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
    }
}

/// <summary>Supplier availability feed (mt4_4b): find-or-create per (supplier, product, variant).</summary>
public sealed class SupplierAvailabilityService(FulfillmentDbContext db, TimeProvider clock)
{
    public async Task<SupplierAvailability> SetAsync(
        Guid tenantId, Guid supplierId, Guid productId, Guid? variantId,
        SupplierStockStatus status, int? externalQuantity, string? supplierSku, CancellationToken ct)
    {
        var item = await db.SupplierAvailabilities.SingleOrDefaultAsync(
            a => a.TenantId == tenantId && a.SupplierId == supplierId
                && a.ProductId == productId && a.VariantId == variantId, ct);
        if (item is null)
        {
            item = SupplierAvailability.Create(tenantId, supplierId, productId, variantId, clock.GetUtcNow());
            db.SupplierAvailabilities.Add(item);
        }

        item.Update(status, externalQuantity, supplierSku, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return item;
    }

    public Task<List<SupplierAvailability>> ListAsync(Guid tenantId, Guid? supplierId, Guid? productId, CancellationToken ct)
    {
        var query = db.SupplierAvailabilities.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (supplierId is { } sid)
        {
            query = query.Where(a => a.SupplierId == sid);
        }

        if (productId is { } pid)
        {
            query = query.Where(a => a.ProductId == pid);
        }

        return query.ToListAsync(ct);
    }
}

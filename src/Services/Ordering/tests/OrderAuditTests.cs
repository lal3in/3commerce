using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Consumers;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// A confirmed purchase must land on the Mission Control activity timeline (mt6_1): the
/// order-confirmation path records an audit entry, attributed to the shopper, with no PII
/// in the summary.
/// </summary>
public class OrderAuditTests
{
    private static Order MakeOrder(Guid? userId = null) => new()
    {
        Id = Guid.Parse("019f74dc-5bc1-7135-9dbd-176f10ba4684"),
        TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        PublicOrderNumber = 1012,
        UserId = userId,
        Email = "shopper@example.test",
        Status = OrderStatus.Confirmed,
        GrossMinor = 362966,
        Currency = "AUD",
        ShipName = "S",
        ShipLine1 = "1 St",
        ShipCity = "C",
        ShipPostcode = "0000",
        ShipCountry = "AU",
    };

    [Fact]
    public void Purchase_audit_records_a_confirmed_order_mutation()
    {
        var order = MakeOrder();
        var draft = OrderStatusConsumer.PurchaseAudit(order);

        Assert.Equal(order.TenantId, draft.TenantId);
        Assert.Equal("ordering.order.confirm", draft.Action);
        Assert.Equal("Order", draft.ResourceType);
        Assert.Equal(order.Id.ToString(), draft.ResourceId);
        Assert.Equal(AuditOutcome.Success, draft.Outcome);
    }

    [Fact]
    public void Purchase_audit_attributes_a_guest_purchase_to_guest()
    {
        var draft = OrderStatusConsumer.PurchaseAudit(MakeOrder(userId: null));
        Assert.Null(draft.ActorId);
        Assert.Equal("guest", draft.ActorRole);
    }

    [Fact]
    public void Purchase_audit_attributes_an_owned_purchase_to_the_customer()
    {
        var uid = Guid.CreateVersion7();
        var draft = OrderStatusConsumer.PurchaseAudit(MakeOrder(userId: uid));
        Assert.Equal(uid, draft.ActorId);
        Assert.Equal("customer", draft.ActorRole);
    }

    [Fact]
    public void Purchase_audit_summary_is_order_number_and_amount_without_pii()
    {
        var draft = OrderStatusConsumer.PurchaseAudit(MakeOrder());
        Assert.Equal("#1012 3629.66 AUD", draft.Summary);
        Assert.DoesNotContain("@", draft.Summary); // the shopper's email must never reach the audit store
    }
}

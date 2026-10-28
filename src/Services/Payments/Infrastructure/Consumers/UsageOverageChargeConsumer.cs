using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Charges a metered overage via the rail (mt7_5) and, on a settled charge, records the revenue + tax in the
/// ledger attributed to the usage balance's storefront (pay_usage_charges_post_revenue, ADR-0063 P4) and publishes
/// <see cref="UsageChargeSettled"/> (P4b); an unsettled one is dunned and reported back (<see cref="UsageChargeFailed"/>,
/// usage_charge_failure_feedback). On an exclusive (US) store the tax is added to the charge. Idempotent by Reference
/// (the intent key) — the same overage never double-charges or double-posts. See <see cref="UsageChargeBilling"/>.
/// </summary>
public sealed class UsageOverageChargeConsumer(UsageChargeBilling billing) : IConsumer<UsageOverageCharge>
{
    public Task Consume(ConsumeContext<UsageOverageCharge> context)
    {
        var m = context.Message;
        return billing.ChargeAsync(
            new UsageChargeRequest(
                m.TenantId, m.StorefrontId, m.CustomerEmail, UsageChargeKind.Overage, m.Meter, m.OverageQuantity, m.UnitPriceMinor,
                m.ChargeMinor, m.Currency, m.Reference, $"Usage overage — {m.Meter} × {m.OverageQuantity}"),
            context.CancellationToken);
    }
}

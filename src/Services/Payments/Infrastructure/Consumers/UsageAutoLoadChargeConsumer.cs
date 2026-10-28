using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>Charges a prepaid usage auto-load top-up via the rail (jobmgr_3) and, on a settled charge,
/// records the revenue + tax in the ledger attributed to the balance's storefront
/// (pay_usage_charges_post_revenue, ADR-0063 P4) and publishes <see cref="UsageChargeSettled"/> (P4b); an unsettled one
/// is dunned and reported back (<see cref="UsageChargeFailed"/> — Usage reverses the top-up's credit,
/// usage_charge_failure_feedback). Mirrors the arrears <see cref="UsageOverageChargeConsumer"/> through the shared
/// <see cref="UsageChargeBilling"/> (tax added on an exclusive store). Idempotent by Reference (the intent key).</summary>
public sealed class UsageAutoLoadChargeConsumer(UsageChargeBilling billing) : IConsumer<UsageAutoLoadCharge>
{
    public Task Consume(ConsumeContext<UsageAutoLoadCharge> context)
    {
        var m = context.Message;
        return billing.ChargeAsync(
            new UsageChargeRequest(
                m.TenantId, m.StorefrontId, m.CustomerEmail, UsageChargeKind.AutoLoad, m.Meter, m.ReloadQuantity, m.UnitPriceMinor,
                m.ChargeMinor, m.Currency, m.Reference, $"Usage auto-load — {m.Meter} × {m.ReloadQuantity}"),
            context.CancellationToken);
    }
}

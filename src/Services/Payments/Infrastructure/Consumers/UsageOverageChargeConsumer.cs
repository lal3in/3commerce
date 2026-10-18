using MassTransit;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Charges a metered overage via the rail (mt7_5) and, on a settled charge, records the revenue + tax in the
/// ledger attributed to the usage balance's storefront (pay_usage_charges_post_revenue, ADR-0063 P4). On an
/// exclusive (US) store the tax is added to the charge. Idempotent by Reference (the intent key) — the same
/// overage never double-charges or double-posts. See <see cref="UsageChargeBilling"/>.
/// </summary>
public sealed class UsageOverageChargeConsumer(
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver,
    PaymentsDbContext db,
    TimeProvider time,
    ILogger<UsageOverageChargeConsumer> logger) : IConsumer<UsageOverageCharge>
{
    public Task Consume(ConsumeContext<UsageOverageCharge> context)
    {
        var m = context.Message;
        return UsageChargeBilling.ChargeAndPostAsync(
            db, registry, modeResolver, time, logger, m.StorefrontId, m.ChargeMinor, m.Currency, m.Reference,
            $"Usage overage — {m.Meter} × {m.OverageQuantity}", context.CancellationToken);
    }
}

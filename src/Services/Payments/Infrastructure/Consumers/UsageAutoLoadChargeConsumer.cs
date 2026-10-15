using MassTransit;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>Charges a prepaid usage auto-load top-up via the rail (jobmgr_3) and, on a settled charge,
/// records the revenue + tax in the ledger attributed to the balance's storefront
/// (pay_usage_charges_post_revenue, ADR-0063 P4), mirroring the arrears <see cref="UsageOverageChargeConsumer"/>
/// through the shared <see cref="UsageChargeBilling"/> (tax added on an exclusive store).
/// Idempotent by Reference (the intent key).</summary>
public sealed class UsageAutoLoadChargeConsumer(
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver,
    PaymentsDbContext db,
    TimeProvider time,
    ILogger<UsageAutoLoadChargeConsumer> logger) : IConsumer<UsageAutoLoadCharge>
{
    public Task Consume(ConsumeContext<UsageAutoLoadCharge> context)
    {
        var m = context.Message;
        return UsageChargeBilling.ChargeAndPostAsync(
            db, registry, modeResolver, time, logger, m.StorefrontId, m.ChargeMinor, m.Currency, m.Reference,
            $"Usage auto-load — {m.Meter} × {m.ReloadQuantity}", context.CancellationToken);
    }
}

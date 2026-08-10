using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>
/// Charges a metered overage via the rail (mt7_5) and, on a settled charge, records the revenue in the
/// ledger attributed to the usage balance's storefront (pay_usage_charges_post_revenue). Idempotent by
/// Reference (the intent key) — the same overage never double-charges or double-posts.
/// </summary>
public sealed class UsageOverageChargeConsumer(
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver,
    PaymentsDbContext db,
    TimeProvider time) : IConsumer<UsageOverageCharge>
{
    public async Task Consume(ConsumeContext<UsageOverageCharge> context)
    {
        var m = context.Message;
        if (await db.JournalEntries.AnyAsync(e => e.Reference == m.Reference, context.CancellationToken))
        {
            return; // already charged + posted for this overage
        }

        var account = modeResolver.DefaultAccountForHost();
        var response = await registry.Resolve(account).AuthorizeAsync(
            new PaymentRequest(Guid.Empty, m.ChargeMinor, m.Currency, m.Reference, PaymentMethodKind.Card, account),
            context.CancellationToken);
        if (response.Outcome != PaymentOutcome.Succeeded)
        {
            return;
        }

        await UsageRevenuePoster.PostAsync(db, time, m.StorefrontId, m.ChargeMinor, m.Currency, account.Provider, m.Reference,
            $"Usage overage — {m.Meter} × {m.OverageQuantity}", context.CancellationToken);
    }
}

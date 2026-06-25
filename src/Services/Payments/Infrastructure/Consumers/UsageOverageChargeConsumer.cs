using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>Charges a metered overage via the rail (mt7_5). Idempotent by Reference (the intent key).</summary>
public sealed class UsageOverageChargeConsumer(IPaymentProvider provider) : IConsumer<UsageOverageCharge>
{
    public Task Consume(ConsumeContext<UsageOverageCharge> context)
    {
        var m = context.Message;
        return provider.CreateIntentAsync(
            Guid.Empty, m.ChargeMinor, m.Currency, m.Reference, null, null, false, context.CancellationToken);
    }
}

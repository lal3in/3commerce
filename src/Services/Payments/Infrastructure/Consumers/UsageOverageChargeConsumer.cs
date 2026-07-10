using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>Charges a metered overage via the rail (mt7_5). Idempotent by Reference (the intent key).</summary>
public sealed class UsageOverageChargeConsumer(
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver) : IConsumer<UsageOverageCharge>
{
    public Task Consume(ConsumeContext<UsageOverageCharge> context)
    {
        var m = context.Message;
        var account = modeResolver.DefaultAccountForHost();
        return registry.Resolve(account).AuthorizeAsync(
            new PaymentRequest(Guid.Empty, m.ChargeMinor, m.Currency, m.Reference, PaymentMethodKind.Card, account),
            context.CancellationToken);
    }
}

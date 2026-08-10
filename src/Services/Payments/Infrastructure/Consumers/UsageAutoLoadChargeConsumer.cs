using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Infrastructure.Consumers;

/// <summary>Charges a prepaid usage auto-load top-up via the rail (jobmgr_3), mirroring the arrears
/// <see cref="UsageOverageChargeConsumer"/>. Idempotent by Reference (the intent key).</summary>
public sealed class UsageAutoLoadChargeConsumer(
    IPaymentProviderRegistry registry,
    PaymentModeResolver modeResolver) : IConsumer<UsageAutoLoadCharge>
{
    public Task Consume(ConsumeContext<UsageAutoLoadCharge> context)
    {
        var m = context.Message;
        var account = modeResolver.DefaultAccountForHost();
        return registry.Resolve(account).AuthorizeAsync(
            new PaymentRequest(Guid.Empty, m.ChargeMinor, m.Currency, m.Reference, PaymentMethodKind.Card, account),
            context.CancellationToken);
    }
}

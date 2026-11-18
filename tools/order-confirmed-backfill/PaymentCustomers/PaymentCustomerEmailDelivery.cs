using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.PaymentCustomers;

/// <summary>Payments' vaults, read inside the RLS scope (ADR-0024): tenants in platform scope, vaults per tenant.</summary>
public sealed class PaymentsVaultSource(PaymentsDbContext db, ITenantScope scope)
{
    /// <summary>Every tenant with a vault. Enumerating across tenants is a platform-scope read.</summary>
    public Task<List<Guid>> TenantsAsync(CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.Platform(), () => db.PaymentCustomers.AsNoTracking()
            .Select(c => c.TenantId)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct), ct);

    public Task<List<VaultFact>> VaultsAsync(Guid tenantId, CancellationToken ct) =>
        scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.PaymentCustomers.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new VaultFact(c.Id, c.TenantId, c.UserId, c.Provider, c.Email, c.CreatedAt))
            .ToListAsync(ct), ct);
}

/// <summary>Identity's accounts of one tenant, read inside its RLS scope (identity."Users" is FORCE RLS).</summary>
public sealed class IdentityAccountSource(IdentityDbContext db, ITenantScope scope)
{
    public Task<List<AccountFact>> AccountsAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return scope.RunAsync(db, TenantContext.ForTenant(tenantId), () => db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && ids.Contains(u.Id))
            .Select(u => new AccountFact(u.Id, tenantId, u.Email))
            .ToListAsync(ct), ct);
    }
}

/// <summary>
/// Where a <see cref="RecordPaymentCustomerEmail"/> goes: Payments' own queue, its name DERIVED with the shared formatter
/// from the real consumer type (ADR-0060), never typed by hand.
/// </summary>
public static class PaymentCustomerEmailQueues
{
    public const string BackfillHeaderValue = "payment-customer-email (payment_customer_email_backfill)";

    public const string ExpectedConnectionName = "3commerce.Payments.Api";

    public static string QueueName => MassTransitExtensions.EndpointNameFormatter.Consumer<PaymentCustomerEmailConsumer>();

    /// <summary>A <c>queue:</c> address: <c>Send</c> to it reaches that one queue — no exchange fan-out, no other consumer.</summary>
    public static Uri Address => new($"queue:{QueueName}");

    /// <summary>A deterministic message id per member: a repeat send is the SAME message (Payments' inbox drops it).</summary>
    public static Guid MessageId(Guid tenantId, Guid userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"payment-customer-email-backfill:{tenantId:D}:{userId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>Sends the commands point-to-point (<c>Send</c>, never <c>Publish</c>).</summary>
public sealed class PaymentCustomerEmailSender(ISendEndpointProvider endpoints)
{
    public async Task<int> SendAsync(IEnumerable<RecordPaymentCustomerEmail> commands, CancellationToken ct)
    {
        var endpoint = await endpoints.GetSendEndpoint(PaymentCustomerEmailQueues.Address);
        var sent = 0;
        foreach (var command in commands)
        {
            await endpoint.Send(command, context =>
            {
                context.MessageId = PaymentCustomerEmailQueues.MessageId(command.TenantId, command.UserId);
                context.Headers.Set(BackfillQueues.BackfillHeader, PaymentCustomerEmailQueues.BackfillHeaderValue);
            }, ct);
            sent++;
        }

        return sent;
    }
}

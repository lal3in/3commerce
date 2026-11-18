using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>What recording a backfilled vault email did.</summary>
public enum PaymentCustomerEmailOutcome
{
    /// <summary>The email was learnt; the member has no default card yet.</summary>
    Recorded = 1,

    /// <summary>The email was learnt and the member has a default card: <see cref="CustomerPaymentMethodReady"/> published.</summary>
    RecordedAndReady = 2,

    /// <summary>Every vault of the member already knows an email — nothing changed (a redelivery, a re-run, a later visit).</summary>
    AlreadyKnown = 3,

    /// <summary>The member has no vault in this tenant.</summary>
    NoVault = 4,

    /// <summary>The command names no tenant, user or email.</summary>
    Invalid = 5,
}

/// <summary>
/// Applies <see cref="RecordPaymentCustomerEmail"/> (<c>payment_customer_email_backfill</c>): records the member's account
/// email on each of their vaults in the tenant that has NONE, so a usage auto top-up can find their default saved card
/// (<see cref="DefaultSavedCard.FindAsync"/>, keyed by tenant + email). A vault that already knows an email keeps it —
/// it was learnt from the member's own claims, which are at least as current as the backfill's read of Identity.
/// <para>
/// When a vault learns its email and the member already has a default card, <see cref="CustomerPaymentMethodReady"/> is
/// published (bus outbox, committed with the email), exactly as the saved-cards page does when it learns the email:
/// Usage resumes the top-ups it paused for want of a card. Vaults and cards are read and written in the command's tenant
/// scope (<see cref="PaymentsTenantScope"/>, ADR-0024): inside the consumer's outbox transaction the scope applies to it.
/// </para>
/// </summary>
public sealed class PaymentCustomerEmailBackfill(PaymentsDbContext db, IPublishEndpoint publisher, TimeProvider time)
{
    public async Task<PaymentCustomerEmailOutcome> RecordAsync(RecordPaymentCustomerEmail command, CancellationToken ct)
    {
        if (command.TenantId == Guid.Empty || command.UserId == Guid.Empty || string.IsNullOrWhiteSpace(command.Email))
        {
            return PaymentCustomerEmailOutcome.Invalid;
        }

        return await PaymentsTenantScope.RunAsync(db, command.TenantId, async () =>
        {
            var vaults = await db.PaymentCustomers
                .Where(c => c.TenantId == command.TenantId && c.UserId == command.UserId)
                .ToListAsync(ct);
            if (vaults.Count == 0)
            {
                return PaymentCustomerEmailOutcome.NoVault;
            }

            var learnt = new List<PaymentCustomer>();
            foreach (var vault in vaults)
            {
                if (vault.Email is null && vault.RecordEmail(command.Email))
                {
                    learnt.Add(vault);
                }
            }

            if (learnt.Count == 0)
            {
                return PaymentCustomerEmailOutcome.AlreadyKnown;
            }

            var ready = false;
            foreach (var vault in learnt)
            {
                ready |= await DefaultSavedCard.ExistsForCustomerAsync(db, command.TenantId, vault.Id, ct);
            }

            if (ready)
            {
                await publisher.Publish(
                    new CustomerPaymentMethodReady(command.TenantId, learnt[0].Email!, StoredTime.Of(time.GetUtcNow())), ct);
            }

            await db.SaveChangesAsync(ct); // the email and CustomerPaymentMethodReady commit together (bus outbox)
            return ready ? PaymentCustomerEmailOutcome.RecordedAndReady : PaymentCustomerEmailOutcome.Recorded;
        }, ct, command.UserId);
    }
}

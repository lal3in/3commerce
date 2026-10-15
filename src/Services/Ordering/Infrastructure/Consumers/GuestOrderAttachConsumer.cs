using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// FR-7: when a user verifies their email, attach their prior guest orders (placed with
/// that email, no user yet) to the account so they appear in order history. Also records
/// the verified email as a <see cref="VerifiedCustomerCopy"/> so orders that materialize
/// AFTER verification (payment settles later than the account conversion) attach at
/// creation time in <see cref="OrderStatusConsumer"/>. Idempotent — re-running just
/// re-matches the same orders and upserts the same copy row.
/// <para>
/// It never writes an order row itself. <see cref="EmailVerified"/> is keyed by email, not by order, so it
/// cannot share the order partition; updating the matching orders here ran concurrently with their status
/// events (a refund or dispute landing while the shopper verified) and failed with 40001 under the outbox's
/// REPEATABLE READ transaction. Instead it publishes one <see cref="GuestOrderOwnerVerified"/> per matching
/// order (through the outbox, so only once this commits), which <see cref="OrderStatusConsumer"/> applies on
/// that order's partition, in line with the order's other events.
/// </para>
/// </summary>
public sealed class GuestOrderAttachConsumer(OrderingDbContext db) : IConsumer<EmailVerified>
{
    public async Task Consume(ConsumeContext<EmailVerified> context)
    {
        var email = context.Message.Email.Trim().ToLowerInvariant();

        // Remember the verified account: closes the race where the order row is created
        // (on payment confirmation) after EmailVerified already fired.
        var copy = await db.VerifiedCustomerCopies.SingleOrDefaultAsync(c => c.Email == email, context.CancellationToken);
        if (copy is null)
        {
            db.VerifiedCustomerCopies.Add(new VerifiedCustomerCopy
            {
                Email = email,
                UserId = context.Message.UserId,
                VerifiedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            copy.UserId = context.Message.UserId;
            copy.VerifiedAt = DateTimeOffset.UtcNow;
        }

        // Guest orders that already exist with this (now verified) email: hand each to its own partition.
        var orphans = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == null && o.Email.ToLower() == email)
            .Select(o => o.Id)
            .ToListAsync(context.CancellationToken);

        foreach (var orderId in orphans)
        {
            await context.Publish(new GuestOrderOwnerVerified(orderId, context.Message.UserId), context.CancellationToken);
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}

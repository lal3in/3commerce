using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// FR-7: when a user verifies their email, attach their prior guest orders (placed with
/// that email, no user yet) to the account so they appear in order history. Also records
/// the verified email as a <see cref="VerifiedCustomerCopy"/> so orders that materialize
/// AFTER verification (payment settles later than the account conversion) attach at
/// creation time in <see cref="OrderStatusConsumer"/>. Idempotent — re-running just
/// re-matches the same orders and upserts the same copy row.
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

        // Sweep guest orders that already exist with this (now verified) email.
        var orphans = await db.Orders
            .Where(o => o.UserId == null && o.Email.ToLower() == email)
            .ToListAsync(context.CancellationToken);

        foreach (var order in orphans)
        {
            order.UserId = context.Message.UserId;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}

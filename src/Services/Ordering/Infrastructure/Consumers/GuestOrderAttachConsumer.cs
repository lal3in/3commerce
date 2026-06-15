using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// FR-7: when a user verifies their email, attach their prior guest orders (placed with
/// that email, no user yet) to the account so they appear in order history. Idempotent —
/// re-running just re-matches the same orders.
/// </summary>
public sealed class GuestOrderAttachConsumer(OrderingDbContext db) : IConsumer<EmailVerified>
{
    public async Task Consume(ConsumeContext<EmailVerified> context)
    {
        var email = context.Message.Email.ToLowerInvariant();
        var orphans = await db.Orders
            .Where(o => o.UserId == null && o.Email.ToLower() == email)
            .ToListAsync(context.CancellationToken);

        if (orphans.Count == 0)
        {
            return;
        }

        foreach (var order in orphans)
        {
            order.UserId = context.Message.UserId;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
